namespace Valenius.Backend.Services;

/// <summary>
/// Reset-aware accounting for WireGuard's cumulative byte counters. Pure/static so it can be
/// unit-tested without a sidecar or DB. Shared by the Pro TrafficSamplingService.
/// </summary>
public static class TrafficAccounting
{
    /// <summary>
    /// Given the last-seen raw counter (<paramref name="lastRaw"/>, null if never observed) and
    /// the current raw counter (<paramref name="currentRaw"/>), returns the bytes transferred
    /// since the last observation and the new last-raw to persist.
    ///
    /// - First observation (lastRaw null): delta 0, baseline set — the pre-sampling backlog is
    ///   NOT counted as one huge interval.
    /// - Normal (current &gt;= last): delta = current - last.
    /// - Counter reset (current &lt; last, i.e. the peer was re-added / interface restarted):
    ///   the new counter value IS the traffic since the reset, so delta = current.
    /// - Negative current (garbage) is clamped to 0.
    /// </summary>
    public static (long Delta, long NewLastRaw) Delta(long? lastRaw, long currentRaw)
    {
        if (currentRaw < 0) currentRaw = 0;
        if (lastRaw is null)
            return (0, currentRaw);                 // baseline only
        return currentRaw >= lastRaw.Value
            ? (currentRaw - lastRaw.Value, currentRaw)
            : (currentRaw, currentRaw);             // reset: counter rolled to a lower value
    }
}
