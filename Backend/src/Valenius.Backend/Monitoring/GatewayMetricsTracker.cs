using System.Collections.Concurrent;
using Valenius.Backend.Services;

namespace Valenius.Backend.Monitoring;

/// <summary>One customer's ("gateway", see MetricsSnapshot's doc comment on why gateway==customer
/// in this codebase) latest polled state, for the Zabbix/Prometheus monitoring endpoint
/// (docs/valenius-zabbix-monitoring-concept.md Phase 2, §4.4).</summary>
public sealed record GatewaySnapshot(
    int CustomerId,
    bool Up,
    DateTime LastSeenUtc,
    int PeersTotal,
    int PeersConnected,
    double? HandshakeAgeMaxSeconds,
    long EndpointChangesTotal,
    double? CpuUsageRatio,
    double? MemoryUsageRatio,
    double? Load1,
    long? ConntrackUsed,
    long? ConntrackMax);

/// <summary>
/// In-memory, per-customer gateway state — same shape of tracker as
/// <see cref="Services.ClientPresenceTracker"/> (mutated inline by a poller, read
/// synchronously, resets on restart). Registered in OSS so it exists harmlessly (always
/// empty) in the Community build; only Backend.Pro's <c>WgTunnelReconcilerService</c> ever
/// calls <see cref="RecordSuccess"/>/<see cref="RecordFailure"/>, since only Pro talks to a
/// sidecar at all.
///
/// Deliberately takes the already-resolved <c>activePublicKeys</c> set rather than computing
/// peer liveness itself — the ping-confirmed-vs-handshake-age fallback judgment call
/// (<c>PeerStats.Active ?? handshake-age heuristic</c>) has exactly one implementation, in
/// <c>WgTunnelReconcilerService</c>, and this tracker must not grow a second one.
/// </summary>
public sealed class GatewayMetricsTracker
{
    private readonly ConcurrentDictionary<int, GatewaySnapshot> _snapshots = new();

    // Last observed WireGuard endpoint per peer public key, for endpoint_changes_total
    // (roaming/NAT-churn indicator, §4.4). Keyed globally, not per customer — a public key
    // is effectively globally unique per client.
    private readonly ConcurrentDictionary<string, string> _lastEndpointByPeer =
        new(StringComparer.Ordinal);

    public void RecordSuccess(
        int customerId,
        IReadOnlyList<PeerStats> peers,
        ISet<string> activePublicKeys,
        double? cpuUsageRatio,
        double? memoryUsageRatio,
        double? load1,
        long? conntrackUsed,
        long? conntrackMax)
    {
        var now = DateTime.UtcNow;
        double? maxAgeSeconds = null;
        var newEndpointChanges = 0L;
        var connected = 0;

        foreach (var p in peers)
        {
            if (activePublicKeys.Contains(p.PublicKey))
                connected++;

            if (p.LatestHandshake is { } handshake)
            {
                var age = (now - handshake).TotalSeconds;
                if (age > (maxAgeSeconds ?? double.NegativeInfinity))
                    maxAgeSeconds = age;
            }

            if (!string.IsNullOrEmpty(p.Endpoint))
            {
                // GetOrAdd seeds the first-ever-seen endpoint as itself, so a peer's first
                // appearance is never counted as a "change".
                var previous = _lastEndpointByPeer.GetOrAdd(p.PublicKey, p.Endpoint);
                if (!string.Equals(previous, p.Endpoint, StringComparison.Ordinal))
                {
                    newEndpointChanges++;
                    _lastEndpointByPeer[p.PublicKey] = p.Endpoint;
                }
            }
        }

        var previousTotal = _snapshots.TryGetValue(customerId, out var prev) ? prev.EndpointChangesTotal : 0;

        _snapshots[customerId] = new GatewaySnapshot(
            CustomerId: customerId,
            Up: true,
            LastSeenUtc: now,
            PeersTotal: peers.Count,
            PeersConnected: connected,
            HandshakeAgeMaxSeconds: maxAgeSeconds,
            EndpointChangesTotal: previousTotal + newEndpointChanges,
            CpuUsageRatio: cpuUsageRatio,
            MemoryUsageRatio: memoryUsageRatio,
            Load1: load1,
            ConntrackUsed: conntrackUsed,
            ConntrackMax: conntrackMax);
    }

    /// <summary>Marks a customer's sidecar as unreachable this cycle. Keeps the rest of the
    /// last-known snapshot (so <c>stats_age_seconds</c> reflects real staleness) rather than
    /// dropping the row entirely.</summary>
    public void RecordFailure(int customerId)
    {
        _snapshots.AddOrUpdate(
            customerId,
            addValueFactory: _ => new GatewaySnapshot(
                customerId, Up: false, LastSeenUtc: DateTime.MinValue,
                PeersTotal: 0, PeersConnected: 0, HandshakeAgeMaxSeconds: null, EndpointChangesTotal: 0,
                CpuUsageRatio: null, MemoryUsageRatio: null, Load1: null, ConntrackUsed: null, ConntrackMax: null),
            updateValueFactory: (_, prev) => prev with { Up = false });
    }

    public IReadOnlyCollection<GatewaySnapshot> Snapshots => _snapshots.Values.ToList();
}
