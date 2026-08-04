namespace Valenius.Backend.Monitoring;

/// <summary>
/// Server-side snapshot cache for <c>/api/v1/metrics</c> — TTL 15 s, single-flight rebuild
/// (docs/valenius-zabbix-monitoring-concept.md §3.1: "Concurrent scrapes get the cached
/// copy"). Deliberately a plain TTL-on-read cache rather than a timer-driven background
/// service — there's no existing OSS precedent for the latter (see
/// <see cref="Services.ClientPresenceTracker"/> and Backend/CLAUDE.md), and a scrape-triggered
/// rebuild is simpler and just as correct at this feature's request rate (at most one rebuild
/// per 15 s per backend).
/// </summary>
public sealed class MetricsSnapshotCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    private readonly SemaphoreSlim _rebuildLock = new(1, 1);
    private MetricsSnapshot? _snapshot;
    private DateTime _generatedAt;

    public async Task<MetricsSnapshot> GetOrBuildAsync(
        Func<CancellationToken, Task<MetricsSnapshot>> build, CancellationToken ct)
    {
        if (IsFresh()) return _snapshot!;

        await _rebuildLock.WaitAsync(ct);
        try
        {
            if (IsFresh()) return _snapshot!;

            var fresh = await build(ct);
            _snapshot = fresh;
            _generatedAt = DateTime.UtcNow;
            return fresh;
        }
        finally
        {
            _rebuildLock.Release();
        }
    }

    private bool IsFresh() =>
        _snapshot is not null && DateTime.UtcNow - _generatedAt < Ttl;
}
