using System.Collections.Concurrent;

namespace Valenius.Backend.Services;

/// <summary>
/// In-memory tracker for WireGuard tunnel state, updated immediately by client
/// Connect/Disconnect events.
///
/// A client can have more than one tunnel up at once — its own customer's native sidecar
/// profile plus zero or more foreign sidecar profiles (see root CLAUDE.md "Simultaneous
/// multi-VPN connections" and "foreign-sidecar-profiles") — so state is tracked per
/// (ClientKey, ProfileName), not per client.
///
/// The Pro <c>WgTunnelReconcilerService</c> periodically reconciles this state against
/// the sidecar's live peer list, dropping stale entries and adding any peers that are
/// active in WireGuard but whose event was missed (e.g. backend restart during a session).
///
/// State resets on restart; the reconciler repopulates it within one cycle (~30 s).
/// </summary>
public sealed class WgTunnelTracker
{
    // Must exceed the reconciler's ActiveHandshakeAge (250 s) so an explicit disconnect
    // is not undone by the reconciler seeing a stale-but-still-recent WG handshake.
    private static readonly TimeSpan DisconnectSuppressDuration = TimeSpan.FromSeconds(300);

    /// <summary>Per-tunnel connection status, exposed for admin display (e.g. Admin/Details'
    /// per-peer connected/verified breakdown).</summary>
    public sealed record TunnelInfo(string ProfileName, DateTime ConnectedAt, bool ExpectsGatewayVerification, bool Verified)
    {
        /// <summary>Same "stricter" rule as <see cref="IsDisplayConnected(string)"/>, scoped to this one tunnel.</summary>
        public bool DisplayConnected => !ExpectsGatewayVerification || Verified;
    }

    private sealed class Entry
    {
        public DateTime ConnectedAt;
        public string? WgPublicKey;
        public bool ExpectsGatewayVerification;
        public bool Verified;
    }

    // ClientKey -> ProfileName -> Entry.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Entry>> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks (client, profile) pairs that sent an explicit Disconnect event so the reconciler
    // cannot re-add just that tunnel while WireGuard still reports a recent handshake. Keyed by
    // TunnelKey(clientKey, profileName) — a Disconnect on one tunnel must never suppress another
    // simultaneously-connected tunnel on the same client.
    private readonly ConcurrentDictionary<string, DateTime> _recentlyDisconnected =
        new(StringComparer.OrdinalIgnoreCase);

    private static string TunnelKey(string clientKey, string profileName) => $"{clientKey}{profileName}";

    /// <summary>Records a Connect event from the client API for one specific tunnel.
    /// <paramref name="expectsGatewayVerification"/> is true for a Pro sidecar profile with a
    /// globally-unique transit network (the client will attempt the gateway /health probe, not
    /// just a handshake) — see <see cref="IsDisplayConnected(string)"/>.</summary>
    public void MarkConnected(string clientKey, string profileName, string? wgPublicKey, bool expectsGatewayVerification = false)
    {
        _recentlyDisconnected.TryRemove(TunnelKey(clientKey, profileName), out _);
        var tunnels = _entries.GetOrAdd(clientKey, _ => new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
        tunnels[profileName] = new Entry
        {
            ConnectedAt = DateTime.UtcNow,
            WgPublicKey = wgPublicKey,
            ExpectsGatewayVerification = expectsGatewayVerification,
        };
    }

    /// <summary>Records a Verified event from the client API (gateway /health probe succeeded)
    /// for one specific tunnel. No-op if that tunnel isn't currently tracked as connected
    /// (e.g. a race with a Disconnect).</summary>
    public void MarkVerified(string clientKey, string profileName)
    {
        if (_entries.TryGetValue(clientKey, out var tunnels) && tunnels.TryGetValue(profileName, out var entry))
            entry.Verified = true;
    }

    /// <summary>Records a Disconnect event from the client API for one specific tunnel. Other
    /// tunnels the same client has connected (native or foreign) are left untouched.</summary>
    public void MarkDisconnected(string clientKey, string profileName)
    {
        if (_entries.TryGetValue(clientKey, out var tunnels))
        {
            tunnels.TryRemove(profileName, out _);
            if (tunnels.IsEmpty) _entries.TryRemove(clientKey, out _);
        }
        _recentlyDisconnected[TunnelKey(clientKey, profileName)] = DateTime.UtcNow;
    }

    /// <summary>Number of clients with at least one tunnel currently tracked as connected (a
    /// client with two simultaneous tunnels still counts once) — used for the Overview
    /// "active WireGuard tunnels" stat.</summary>
    public int ActiveCount => _entries.Count;

    /// <summary>Returns true if the client has ANY tunnel currently tracked as connected. This is
    /// the raw peer-registered signal — used for functional gates (MFA policy changes, traffic
    /// accounting) where "some sidecar peer is registered, whether or not it's been verified yet"
    /// is the correct question. For a specific tunnel (e.g. Kill Tunnel / Force Disconnect, which
    /// only ever act on the native peer) use the two-argument overload instead.</summary>
    public bool IsConnected(string clientKey) =>
        _entries.TryGetValue(clientKey, out var tunnels) && !tunnels.IsEmpty;

    /// <summary>Returns true if this specific (client, profile) tunnel is currently tracked as
    /// connected.</summary>
    public bool IsConnected(string clientKey, string profileName) =>
        _entries.TryGetValue(clientKey, out var tunnels) && tunnels.ContainsKey(profileName);

    /// <summary>
    /// Stricter "connected" signal for admin-facing display (e.g. the client list's connected
    /// star): true when AT LEAST ONE of the client's tunnels (native or foreign) is display-
    /// connected — for a tunnel expected to gateway-verify (Pro sidecar, unique transit network),
    /// that means the gateway /health probe actually succeeded, not just the WireGuard interface
    /// being up; for a tunnel with no gateway target (OSS, External, or a non-unique transit
    /// network) there's no better signal to require, so it falls back to "peer registered".
    /// A client is shown connected whenever any one sidecar-based VPN of theirs is genuinely up,
    /// regardless of whether that happens to be their own assigned customer's native profile or
    /// a foreign one.
    /// </summary>
    public bool IsDisplayConnected(string clientKey) =>
        _entries.TryGetValue(clientKey, out var tunnels)
        && tunnels.Values.Any(e => !e.ExpectsGatewayVerification || e.Verified);

    /// <summary>Per-tunnel breakdown for a client, for admin display (Admin/Details). Empty if
    /// the client has no tunnel currently tracked as connected.</summary>
    public IReadOnlyList<TunnelInfo> GetTunnels(string clientKey) =>
        _entries.TryGetValue(clientKey, out var tunnels)
            ? tunnels.Select(kv => new TunnelInfo(kv.Key, kv.Value.ConnectedAt, kv.Value.ExpectsGatewayVerification, kv.Value.Verified)).ToList()
            : [];

    /// <summary>
    /// Synchronises tracker state with a WireGuard snapshot (called by the background reconciler).
    ///
    /// <paramref name="wgActivePubKeyToTunnel"/> maps every public key that WireGuard reports as
    /// having a recent handshake to the (ClientKey, ProfileName) tunnel it belongs to — a native
    /// peer's key resolves to the client's own native profile name, a foreign peer's key to that
    /// specific foreign profile's name.
    ///
    /// - Tunnels WireGuard shows as active but not yet tracked are added, unless they recently
    ///   received an explicit Disconnect event (suppressed for <see cref="DisconnectSuppressDuration"/>
    ///   to avoid undoing the disconnect while WireGuard still reports a stale-but-recent handshake).
    /// - Tracked tunnels older than <paramref name="grace"/> whose public key is not in the
    ///   WireGuard snapshot are removed (the grace period absorbs the race between connect event
    ///   arrival and the first WireGuard handshake completing).
    /// - Entries without a public key are never removed by reconciliation; they can only be
    ///   cleared by a Disconnect event.
    /// </summary>
    /// <returns>
    /// ClientKeys of clients that had at least one tracked tunnel before this call and have none
    /// afterward — i.e. clients the reconciler just determined are fully disconnected (ping-
    /// confirmed unreachable on every tunnel) despite no explicit Disconnect event. Matches the
    /// client-level granularity of the ClientConnected/ClientDisconnected event log: a client that
    /// merely loses one of several simultaneous tunnels is not reported here. Callers can use this
    /// to audit-log automatic disconnect detection.
    /// </returns>
    public IReadOnlyCollection<string> Reconcile(
        IReadOnlyDictionary<string, (string ClientKey, string ProfileName)> wgActivePubKeyToTunnel,
        TimeSpan grace)
    {
        var now = DateTime.UtcNow;
        var wgActiveTunnelKeys = new HashSet<string>(
            wgActivePubKeyToTunnel.Values.Select(t => TunnelKey(t.ClientKey, t.ProfileName)),
            StringComparer.OrdinalIgnoreCase);

        // Add WireGuard-active tunnels not yet tracked.
        foreach (var (pubKey, (clientKey, profileName)) in wgActivePubKeyToTunnel)
        {
            var tunnels = _entries.GetOrAdd(clientKey, _ => new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase));
            if (tunnels.ContainsKey(profileName)) continue;

            // Don't re-add a tunnel that explicitly disconnected recently
            if (_recentlyDisconnected.TryGetValue(TunnelKey(clientKey, profileName), out var disconnectedAt) &&
                now - disconnectedAt < DisconnectSuppressDuration) continue;

            // Reconciler-recovered entry (missed Connect event, e.g. a backend restart mid-session)
            // — no Verified event will ever arrive to confirm it, so default to the non-strict
            // display behavior (ExpectsGatewayVerification: false) rather than leaving the star
            // permanently off for a connection that's actually fine.
            tunnels[profileName] = new Entry
            {
                ConnectedAt = now - grace - TimeSpan.FromSeconds(1),
                WgPublicKey = pubKey,
                ExpectsGatewayVerification = false,
            };
        }

        // Remove tunnels that are past the grace window and no longer in WireGuard. A client
        // whose tracked-tunnel set becomes empty as a result is reported for auto-disconnect
        // audit logging.
        var fullyDisconnectedClients = new List<string>();
        var cutoff = now - grace;
        foreach (var (clientKey, tunnels) in _entries.ToList())
        {
            foreach (var (profileName, entry) in tunnels.ToList())
            {
                if (entry.ConnectedAt > cutoff) continue;                              // still within grace period
                if (entry.WgPublicKey is null) continue;                               // no key — can't reconcile
                if (wgActiveTunnelKeys.Contains(TunnelKey(clientKey, profileName))) continue;
                tunnels.TryRemove(profileName, out _);
            }
            if (tunnels.IsEmpty && _entries.TryRemove(clientKey, out _))
                fullyDisconnectedClients.Add(clientKey);
        }

        // Clean up expired disconnect suppressions
        foreach (var kv in _recentlyDisconnected.ToList())
        {
            if (now - kv.Value > DisconnectSuppressDuration)
                _recentlyDisconnected.TryRemove(kv.Key, out _);
        }

        return fullyDisconnectedClients;
    }
}
