using System.Collections.Concurrent;

namespace Valenius.Backend.Monitoring;

/// <summary>
/// In-memory, in-process counters backing <c>valenius_http_requests_total</c> and
/// <c>valenius_http_request_duration_seconds</c> (docs/valenius-zabbix-monitoring-concept.md
/// §4.1). Labelled only by method + status_class — never by path, which is the single most
/// common cardinality mistake for this exact metric (root CLAUDE.md, Appendix B). Counts are
/// since-process-start (reset on restart, same as every other in-memory tracker in this
/// codebase, e.g. <see cref="Services.ClientPresenceTracker"/>); this is single-node only —
/// see §11 Q4 for the multi-node caveat, not addressed in Phase 1 (root CLAUDE.md "Backend
/// scale assumption": single-replica, &lt;=500 clients).
/// </summary>
public sealed class RequestMetricsCollector
{
    private static readonly TimeSpan DurationWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string Method, string StatusClass), long> _counts = new();
    private readonly ConcurrentQueue<(DateTime AtUtc, double Seconds)> _recentDurations = new();

    public void Record(string method, int statusCode, double durationSeconds)
    {
        var statusClass = statusCode switch
        {
            >= 200 and < 300 => "2xx",
            >= 300 and < 400 => "3xx",
            >= 400 and < 500 => "4xx",
            >= 500 and < 600 => "5xx",
            _ => "other",
        };
        _counts.AddOrUpdate((method.ToUpperInvariant(), statusClass), 1, (_, count) => count + 1);

        var now = DateTime.UtcNow;
        _recentDurations.Enqueue((now, durationSeconds));
        Prune(now);
    }

    public IReadOnlyList<HttpRequestCount> SnapshotCounts() =>
        _counts.Select(kv => new HttpRequestCount(kv.Key.Method, kv.Key.StatusClass, kv.Value)).ToList();

    /// <summary>p50/p95/p99 over the trailing 5-minute window. Empty once 5 minutes pass
    /// with no traffic (e.g. right after startup) rather than reporting a stale number.</summary>
    public IReadOnlyList<HttpRequestDurationQuantile> SnapshotDurationQuantiles()
    {
        Prune(DateTime.UtcNow);

        var sorted = _recentDurations.Select(d => d.Seconds).OrderBy(s => s).ToArray();
        if (sorted.Length == 0) return [];

        return
        [
            new("0.5", Percentile(sorted, 0.5)),
            new("0.95", Percentile(sorted, 0.95)),
            new("0.99", Percentile(sorted, 0.99)),
        ];
    }

    private void Prune(DateTime nowUtc)
    {
        while (_recentDurations.TryPeek(out var oldest) && nowUtc - oldest.AtUtc > DurationWindow)
            _recentDurations.TryDequeue(out _);
    }

    private static double Percentile(double[] sortedAscending, double p)
    {
        var index = (int)Math.Ceiling(p * sortedAscending.Length) - 1;
        return sortedAscending[Math.Clamp(index, 0, sortedAscending.Length - 1)];
    }
}
