namespace Valenius.Backend.Services;

/// <summary>Fixed-memory onboarding buckets and authenticated poll admission.</summary>
public sealed class DeviceRequestBudget
{
    private readonly object _gate = new();
    private readonly (long Minute, int Used)[] _onboarding = new (long, int)[1024];
    private readonly Dictionary<string, int> _polls = new(StringComparer.Ordinal);
    private int _active, _controls;

    public bool TryRegister(string? address, DateTime utcNow)
    {
        var slot = (uint)StringComparer.Ordinal.GetHashCode(address ?? "unknown") % 1024;
        var minute = utcNow.Ticks / TimeSpan.TicksPerMinute;
        lock (_gate)
        {
            ref var bucket = ref _onboarding[slot];
            if (bucket.Minute != minute) bucket = (minute, 0);
            if (bucket.Used >= 30) return false;
            bucket.Used++;
            return true;
        }
    }

    public IDisposable? TryPoll(string key) => Acquire("poll:" + key, true);
    public IDisposable? TryControl(string key) => Acquire("control:" + key, false);

    private IDisposable? Acquire(string key, bool poll)
    {
        lock (_gate)
        {
            _polls.TryGetValue(key, out var count);
            if (count >= (poll ? 2 : 4) || (poll ? _active >= 2048 : _controls >= 256)) return null;
            _polls[key] = count + 1;
            if (poll) _active++; else _controls++;
            return new Lease(() =>
            {
                lock (_gate)
                {
                    if (--_polls[key] == 0) _polls.Remove(key);
                    if (poll) _active--; else _controls--;
                }
            });
        }
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
