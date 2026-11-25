namespace Valenius.Backend.Services;

/// <summary>Notification waiters exist only for active polls; cancellation removes idle state.</summary>
public sealed class ClientNotifier
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Waiter> _waiters = new(StringComparer.Ordinal);
    private sealed class Waiter
    {
        public readonly TaskCompletionSource<bool> Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Users;
    }

    public async Task WaitAsync(string key, CancellationToken ct = default)
    {
        Waiter waiter;
        lock (_gate)
        {
            if (!_waiters.TryGetValue(key, out waiter!)) _waiters[key] = waiter = new();
            waiter.Users++;
        }
        try { await waiter.Signal.Task.WaitAsync(ct); }
        finally
        {
            lock (_gate)
                if (--waiter.Users == 0 && _waiters.TryGetValue(key, out var current) && ReferenceEquals(current, waiter))
                    _waiters.Remove(key);
        }
    }

    public void Notify(string key)
    {
        Waiter? waiter;
        lock (_gate) _waiters.Remove(key, out waiter);
        waiter?.Signal.TrySetResult(true);
    }
}
