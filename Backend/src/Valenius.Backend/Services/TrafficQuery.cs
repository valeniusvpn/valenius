using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;

namespace Valenius.Backend.Services;

/// <summary>Shared traffic-sample aggregation for the dashboard pages (client / customer / global).</summary>
public static class TrafficQuery
{
    /// <summary>
    /// Rx/tx buckets over the selected <paramref name="range"/> — one per <see cref="TrafficRange.Bucket"/>
    /// interval ending at the current bucket boundary, gaps filled with 0 for a continuous x-axis —
    /// summed across <paramref name="clientIds"/>. Samples (5-min intervals) are aggregated in SQL to
    /// per-minute sums, then folded into the target buckets in memory (bounded by the window size).
    /// </summary>
    public static async Task<IReadOnlyList<TrafficBucket>> LoadRangeAsync(
        ApplicationDbContext db, IReadOnlyCollection<int> clientIds, TrafficRange range)
    {
        var bucketTicks = range.Bucket.Ticks;
        var nowTicks    = DateTime.UtcNow.Ticks;
        var endTicks    = nowTicks - nowTicks % bucketTicks;                 // start of the current bucket
        var count       = (int)(range.Window.Ticks / bucketTicks);
        var startTicks  = endTicks - (long)(count - 1) * bucketTicks;
        var start       = new DateTime(startTicks, DateTimeKind.Utc);

        var byBucket = new Dictionary<long, (long Rx, long Tx)>();
        if (clientIds.Count > 0)
        {
            var rows = await db.ClientTrafficSamples
                .Where(s => clientIds.Contains(s.ClientId) && s.SampledAt >= start)
                .GroupBy(s => new { s.SampledAt.Year, s.SampledAt.Month, s.SampledAt.Day,
                                    s.SampledAt.Hour, s.SampledAt.Minute })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.Minute,
                                   Rx = g.Sum(x => x.RxBytes), Tx = g.Sum(x => x.TxBytes) })
                .ToListAsync();
            foreach (var r in rows)
            {
                var t   = new DateTime(r.Year, r.Month, r.Day, r.Hour, r.Minute, 0, DateTimeKind.Utc);
                var idx = (t.Ticks - startTicks) / bucketTicks;
                if (idx < 0 || idx >= count) continue;
                var cur = byBucket.TryGetValue(idx, out var v) ? v : (0L, 0L);
                byBucket[idx] = (cur.Item1 + r.Rx, cur.Item2 + r.Tx);
            }
        }

        var buckets = new List<TrafficBucket>(count);
        for (var i = 0; i < count; i++)
        {
            var at = new DateTime(startTicks + (long)i * bucketTicks, DateTimeKind.Utc);
            buckets.Add(byBucket.TryGetValue(i, out var v) ? new TrafficBucket(at, v.Rx, v.Tx)
                                                           : new TrafficBucket(at, 0, 0));
        }
        return buckets;
    }
}
