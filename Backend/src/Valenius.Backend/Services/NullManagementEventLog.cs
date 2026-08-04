namespace Valenius.Backend.Services;

/// <summary>OSS default — no-op. No Management API exists in OSS to ever poll for events.</summary>
public sealed class NullManagementEventLog : IManagementEventLog
{
    public Task RecordAsync(string type, int? clientId, int? customerId, string? detail = null) => Task.CompletedTask;
}
