using System.Diagnostics.Metrics;

namespace Valenius.Backend.Services;

/// <summary>Process-local loss signal. Success does not erase previously lost events.</summary>
public sealed class AuditWriteStatus
{
    private static readonly Meter Meter = new("Valenius.Backend.Audit");
    private static readonly Counter<long> Writes = Meter.CreateCounter<long>("audit.write.success");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("audit.write.failure");
    private long _failures, _lastFailureTicks;
    public long FailureCount => Interlocked.Read(ref _failures);
    public DateTime? LastFailureUtc => Interlocked.Read(ref _lastFailureTicks) is var ticks && ticks != 0
        ? new DateTime(ticks, DateTimeKind.Utc) : null;

    internal void Succeeded() => Writes.Add(1);
    internal void Failed()
    {
        Interlocked.Increment(ref _failures);
        Interlocked.Exchange(ref _lastFailureTicks, DateTime.UtcNow.Ticks);
        Failures.Add(1);
    }
}

public sealed class AuditPersistenceException() : Exception(
    "The audit record could not be saved. Review the server audit failure alert before retrying this operation.");
