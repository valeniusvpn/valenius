using Valenius.Shared;

namespace Valenius.Service;

/// <summary>
/// Tracks every WireGuard tunnel that is currently active. More than one tunnel may be
/// connected at once (the primary/server VPN plus any number of foreign-customer profiles),
/// so state is keyed by tunnel name rather than a single set of scalar fields.
/// </summary>
public class TunnelStateManager
{
    private readonly object _lock = new();

    private readonly Dictionary<string, Entry> _active =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Entry(string User, DateTime Since, bool Verified, bool VerifiedViaGateway = false, bool VerificationFailed = false, int FallbackPortUsed = 0);

    public void SetConnected(string tunnelName, string user)
    {
        lock (_lock)
        {
            _active[tunnelName] = new Entry(user, DateTime.UtcNow, Verified: false);
        }
    }

    /// <summary>
    /// Marks a tunnel as connectivity-verified. No-op if the tunnel is no longer active
    /// (guards against a race where the user disconnected before the background probe finished).
    /// <paramref name="viaGateway"/> records that the proof was an end-to-end gateway /health
    /// probe (not just a local handshake) — the network-change re-verifier trusts a failed
    /// gateway probe as a "dead" signal only for tunnels that were verified this way.
    /// </summary>
    public void SetVerified(string tunnelName, bool viaGateway = false)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(tunnelName, out var e))
                _active[tunnelName] = e with { Verified = true, VerifiedViaGateway = viaGateway, VerificationFailed = false };
        }
    }

    /// <summary>
    /// Marks that the initial fast verification window elapsed with no success. Not terminal —
    /// verification keeps retrying at a slower cadence afterward (see <c>PipeServer.RunVerificationLoopAsync</c>),
    /// and a later success still clears this via <see cref="SetVerified"/>. No-op if the tunnel
    /// disconnected in the meantime.
    /// </summary>
    public void MarkVerificationFailed(string tunnelName)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(tunnelName, out var e))
                _active[tunnelName] = e with { VerificationFailed = true };
        }
    }

    /// <summary>
    /// Records that <paramref name="tunnelName"/> switched to the UDP fallback port
    /// (docs/design/port-443-fallback.md) after the primary port produced no successful
    /// verification. Sticky for the life of the connection — surfaced to the tray via
    /// <see cref="ConnectedTunnelInfo.FallbackPortUsed"/> so the "Verified"/"Connected" tag can
    /// show which port the tunnel is actually on. No-op if the tunnel disconnected in the
    /// meantime.
    /// </summary>
    public void MarkUsedFallbackPort(string tunnelName, int port)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(tunnelName, out var e))
                _active[tunnelName] = e with { FallbackPortUsed = port };
        }
    }

    /// <summary>True if the tunnel's last verification was an end-to-end gateway /health probe.</summary>
    public bool IsVerifiedViaGateway(string tunnelName)
    {
        lock (_lock)
        {
            return _active.TryGetValue(tunnelName, out var e) && e.VerifiedViaGateway;
        }
    }

    /// <summary>
    /// Clears the connectivity-verified flag for a tunnel (no-op if it is no longer active).
    /// Used when a network change forces a re-verification: the tray drops the green check
    /// back to a plain "Connected" dot until liveness is re-confirmed.
    /// </summary>
    public void SetUnverified(string tunnelName)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(tunnelName, out var e))
                _active[tunnelName] = e with { Verified = false, VerificationFailed = false };
        }
    }

    /// <summary>Removes a single tunnel from the active set.</summary>
    public void SetDisconnected(string tunnelName)
    {
        lock (_lock)
        {
            _active.Remove(tunnelName);
        }
    }

    /// <summary>Clears every active tunnel (used by the auto-disconnect-all / no-profile disconnect path).</summary>
    public void SetDisconnected()
    {
        lock (_lock)
        {
            _active.Clear();
        }
    }

    public bool IsConnected(string tunnelName)
    {
        lock (_lock)
        {
            return _active.ContainsKey(tunnelName);
        }
    }

    /// <summary>Snapshot of all active tunnels for status reporting.</summary>
    public ConnectedTunnelInfo[] GetAllConnected()
    {
        lock (_lock)
        {
            return _active
                .Select(kv => new ConnectedTunnelInfo
                {
                    Name               = kv.Key,
                    IsVerified         = kv.Value.Verified,
                    VerificationFailed = kv.Value.VerificationFailed,
                    FallbackPortUsed   = kv.Value.FallbackPortUsed,
                    ConnectedUser      = kv.Value.User,
                    ConnectedSince     = kv.Value.Since
                })
                .ToArray();
        }
    }

    // ── Backward-compatible single-tunnel views ───────────────────────────────
    // Used by the Phase-2 verifier guard and a handful of legacy callers. They return
    // the server/primary profile entry when one is supplied, otherwise the first entry.

    public (bool IsConnected, string? TunnelName, string? User, DateTime? Since) GetState(string? preferredName = null)
    {
        lock (_lock)
        {
            var e = PickPrimary(preferredName);
            return e is null ? (false, null, null, null) : (true, e.Value.Key, e.Value.Value.User, e.Value.Value.Since);
        }
    }

    public (bool IsConnected, bool IsVerified, string? TunnelName, string? User, DateTime? Since) GetFullState(string? preferredName = null)
    {
        lock (_lock)
        {
            var e = PickPrimary(preferredName);
            return e is null
                ? (false, false, null, null, null)
                : (true, e.Value.Value.Verified, e.Value.Key, e.Value.Value.User, e.Value.Value.Since);
        }
    }

    private KeyValuePair<string, Entry>? PickPrimary(string? preferredName)
    {
        if (_active.Count == 0) return null;
        if (!string.IsNullOrEmpty(preferredName) && _active.TryGetValue(preferredName, out var match))
            return new KeyValuePair<string, Entry>(
                _active.Keys.First(k => string.Equals(k, preferredName, StringComparison.OrdinalIgnoreCase)), match);
        return _active.First();
    }
}
