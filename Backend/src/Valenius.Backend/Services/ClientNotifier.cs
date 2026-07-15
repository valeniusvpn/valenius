using System.Collections.Concurrent;

namespace Valenius.Backend.Services;

/// <summary>
/// In-memory notification bus that lets admin actions instantly wake up a waiting
/// <c>GET /api/clients/poll</c> request instead of making the client wait for the
/// next scheduled heartbeat.
///
/// One <see cref="TaskCompletionSource{T}"/> is stored per <c>ClientKey</c>.  When a
/// poll request arrives it awaits that TCS via <see cref="Task.WhenAny"/> combined with
/// its own timeout task, so it never blocks a thread.  When an admin action calls
/// <see cref="Notify"/>, the TCS is completed and the waiting request unblocks within
/// milliseconds.
///
/// If the backend restarts the in-memory state is lost; any open poll connections drop
/// and clients reconnect on their next timeout (~55 s) or scheduled heartbeat.
/// </summary>
public sealed class ClientNotifier
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a task that completes when <see cref="Notify"/> is called for
    /// <paramref name="clientKey"/>.  Multiple callers for the same key share the same
    /// underlying <see cref="TaskCompletionSource{T}"/> and all unblock together.
    /// </summary>
    public Task WaitAsync(string clientKey) =>
        _waiters.GetOrAdd(
            clientKey,
            _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>
    /// Immediately unblocks any <see cref="WaitAsync"/> call waiting for
    /// <paramref name="clientKey"/>.  No-op if nobody is waiting.
    /// </summary>
    public void Notify(string clientKey)
    {
        if (_waiters.TryRemove(clientKey, out var tcs))
            tcs.TrySetResult(true);
    }
}
