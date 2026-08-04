using System.Collections.Concurrent;

namespace Valenius.Backend.Services;

/// <summary>
/// Singleton, in-memory write-coalescer for <see cref="Models.ApiToken.LastUsedAt"/> /
/// <see cref="Models.ApiToken.LastUsedIp"/>. A polling integration can call the Management
/// API constantly; writing those two columns on every request would turn every GET into a
/// write transaction. This throttles that to at most once per token per minute — a
/// deliberately simpler mechanism than caching the token itself (see
/// docs/design/management-api.md §4.3): unlike a token cache, a stale entry here can never
/// produce a wrong authorization answer, only a slightly-stale "last used" display.
/// </summary>
public sealed class ApiTokenUsageThrottle
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<int, DateTime> lastWrittenAtUtc = new();

    /// <summary>True (and records "now" as the new baseline) if at least a minute has
    /// passed since the last recorded write for this token row. Callers should update
    /// LastUsedAt/LastUsedIp only when this returns true.</summary>
    public bool ShouldWrite(int tokenRowId)
    {
        var now = DateTime.UtcNow;
        var recorded = lastWrittenAtUtc.AddOrUpdate(
            tokenRowId,
            _ => now,
            (_, last) => now - last >= Interval ? now : last);
        return recorded == now;
    }
}
