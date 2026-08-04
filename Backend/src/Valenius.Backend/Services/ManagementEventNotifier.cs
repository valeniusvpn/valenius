namespace Valenius.Backend.Services;

/// <summary>
/// In-memory wake-up signal for <c>GET /api/mgmt/v1/events</c>'s long-poll, same shape as
/// <see cref="ClientNotifier"/> but broadcast rather than per-key — a management API caller
/// wants "any new event I can see", not one keyed to a single client. A single shared
/// <see cref="TaskCompletionSource{T}"/> is replaced on every <see cref="NotifyNew"/> call, so
/// every concurrently-waiting poller unblocks together.
///
/// If the backend restarts, in-memory state is lost; any open poll connections drop and
/// resume on their own client-side timeout/retry loop — same recovery story as
/// <see cref="ClientNotifier"/>.
/// </summary>
public sealed class ManagementEventNotifier
{
    private TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Returns a task that completes the next time <see cref="NotifyNew"/> is called.</summary>
    public Task WaitAsync() => _tcs.Task;

    /// <summary>Unblocks every current <see cref="WaitAsync"/> caller and arms a fresh signal
    /// for the next one.</summary>
    public void NotifyNew()
    {
        var previous = Interlocked.Exchange(
            ref _tcs, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult(true);
    }
}
