using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Valenius.Backend.Services;

/// <summary>
/// Optional <c>Idempotency-Key</c> support for Management API action endpoints
/// (docs/design/management-api.md §8.4). The queued actions are flag flips — a retried
/// <c>force-update</c> after a network blip must not queue a second update round, so a
/// (token, key) pair replays the exact stored response instead of re-running the action.
/// Only successful (2xx) results are cached — a transient failure should still be retried
/// for real, not permanently pinned.
/// </summary>
public static class IdempotencyCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public static async Task<IActionResult> ExecuteAsync(
        IMemoryCache cache, string tokenId, string? idempotencyKey, Func<Task<IActionResult>> action)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return await action();

        var cacheKey = $"mgmt-idem:{tokenId}:{idempotencyKey}";
        if (cache.TryGetValue(cacheKey, out CachedResponse? cached) && cached is not null)
            return new ObjectResult(cached.Body) { StatusCode = cached.StatusCode };

        var result = await action();
        if (result is ObjectResult { StatusCode: >= 200 and < 300 } objResult)
            cache.Set(cacheKey, new CachedResponse(objResult.StatusCode ?? StatusCodes.Status200OK, objResult.Value), Ttl);

        return result;
    }

    private sealed record CachedResponse(int StatusCode, object? Body);
}
