namespace Valenius.Backend.Services;

/// <summary>Event type constants for <see cref="IManagementEventLog"/> (docs/design/management-api.md
/// §14 phase 4 / §18.1). Dotted, namespaced strings — mirrors the shape a future real webhook
/// payload's <c>type</c> field would use, even though phase 4 shipped as a poll-only
/// <c>GET /api/mgmt/v1/events</c> feed rather than outbound webhooks.</summary>
public static class ManagementEventType
{
    public const string ClientConnected    = "client.connected";
    public const string ClientDisconnected = "client.disconnected";
    public const string ClientAlert        = "client.alert";
    public const string ClientProvisioned  = "client.provisioned";
    public const string ClientDeprovisioned = "client.deprovisioned";
}

/// <summary>
/// Records fleet events (connect/disconnect/alert/provisioned) for the Management API's
/// event feed. OSS registers <see cref="NullManagementEventLog"/> (no-op — nothing can ever
/// poll for events without the Pro Management API); the Pro project replaces it with a
/// DB-backed implementation via DI. Event *sources* live partly in OSS code (heartbeat
/// connect/disconnect, client alerts), so this interface — like <see cref="IAuditService"/> —
/// lives in OSS even though only Pro ever does real work with it.
/// </summary>
public interface IManagementEventLog
{
    /// <summary>Records an event. Fire-and-forget — do not await from a hot request path.
    /// <paramref name="detail"/> is a short human-readable string, same convention as
    /// <c>AuditLog.Details</c>.</summary>
    Task RecordAsync(string type, int? clientId, int? customerId, string? detail = null);
}
