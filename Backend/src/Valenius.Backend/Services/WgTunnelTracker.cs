using System.Collections.Concurrent;

namespace Valenius.Backend.Services;

/// <summary>
/// In-memory tracker for WireGuard tunnel state, updated immediately by client
/// Connect/Disconnect events.
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

    private record Entry(DateTime ConnectedAt, string? WgPublicKey);

    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    // Tracks clients that sent an explicit Disconnect event so the reconciler cannot
    // re-add them while WireGuard still reports a recent handshake.
    private readonly ConcurrentDictionary<string, DateTime> _recentlyDisconnected =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Records a Connect event from the client API.</summary>
    public void MarkConnected(string clientKey, string? wgPublicKey)
    {
        _recentlyDisconnected.TryRemove(clientKey, out _);
        _entries[clientKey] = new Entry(DateTime.UtcNow, wgPublicKey);
    }

    /// <summary>Records a Disconnect event from the client API.</summary>
    public void MarkDisconnected(string clientKey)
    {
        _entries.TryRemove(clientKey, out _);
        _recentlyDisconnected[clientKey] = DateTime.UtcNow;
    }

    /// <summary>Number of clients currently tracked as connected.</summary>
    public int ActiveCount => _entries.Count;

    /// <summary>Returns true if the given client is currently tracked as connected.</summary>
    public bool IsConnected(string clientKey) => _entries.ContainsKey(clientKey);

    /// <summary>
    /// Synchronises tracker state with a WireGuard snapshot (called by the background reconciler).
    ///
    /// <paramref name="wgActivePubKeyToClientKey"/> maps every public key that WireGuard
    /// reports as having a recent handshake to its <c>ClientKey</c>.
    ///
    /// - Clients that WireGuard shows as active but are not yet tracked are added, unless
    ///   they recently sent an explicit Disconnect event (suppressed for
    ///   <see cref="DisconnectSuppressDuration"/> to avoid undoing the disconnect while
    ///   WireGuard still reports a stale-but-recent handshake).
    /// - Tracker entries older than <paramref name="grace"/> whose public key is not in
    ///   the WireGuard snapshot are removed (the grace period absorbs the race between
    ///   connect event arrival and the first WireGuard handshake completing).
    /// - Entries without a public key are never removed by reconciliation; they can only
    ///   be cleared by a Disconnect event.
    /// </summary>
    /// <returns>
    /// ClientKeys removed in this call — i.e. clients the reconciler just determined are no
    /// longer connected (ping-confirmed unreachable) despite no explicit Disconnect event.
    /// Callers can use this to audit-log automatic disconnect detection.
    /// </returns>
    public IReadOnlyCollection<string> Reconcile(
        IReadOnlyDictionary<string, string> wgActivePubKeyToClientKey,
        TimeSpan grace)
    {
        var now            = DateTime.UtcNow;
        var wgActiveClientKeys = new HashSet<string>(
            wgActivePubKeyToClientKey.Values, StringComparer.OrdinalIgnoreCase);

        // Add WireGuard-active peers not yet in the tracker
        foreach (var (pubKey, clientKey) in wgActivePubKeyToClientKey)
        {
            if (_entries.ContainsKey(clientKey)) continue;

            // Don't re-add a client that explicitly disconnected recently
            if (_recentlyDisconnected.TryGetValue(clientKey, out var disconnectedAt) &&
                now - disconnectedAt < DisconnectSuppressDuration) continue;

            _entries[clientKey] = new Entry(now - grace - TimeSpan.FromSeconds(1), pubKey);
        }

        // Remove entries that are past the grace window and no longer in WireGuard
        var removed = new List<string>();
        var cutoff  = now - grace;
        foreach (var kv in _entries.ToList())
        {
            if (kv.Value.ConnectedAt > cutoff) continue;   // still within grace period
            if (kv.Value.WgPublicKey is null)   continue;  // no key — can't reconcile
            if (!wgActiveClientKeys.Contains(kv.Key) && _entries.TryRemove(kv.Key, out _))
                removed.Add(kv.Key);
        }

        // Clean up expired disconnect suppressions
        foreach (var kv in _recentlyDisconnected.ToList())
        {
            if (now - kv.Value > DisconnectSuppressDuration)
                _recentlyDisconnected.TryRemove(kv.Key, out _);
        }

        return removed;
    }
}
