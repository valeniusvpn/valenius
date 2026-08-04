namespace Valenius.Backend.Models;

/// <summary>
/// One row per fleet event (docs/design/management-api.md §14 phase 4), polled via
/// <c>GET /api/mgmt/v1/events?since=&lt;cursor&gt;</c>. <see cref="Id"/> doubles as the
/// opaque monotonic cursor — a bigserial primary key is strictly increasing across the
/// table's insert order, which is exactly what a "give me everything after X" poll needs.
/// Pro-only: OSS never writes a row (see <see cref="Services.NullManagementEventLog"/>), so
/// this table exists but stays empty in OSS installs.
/// </summary>
public class ManagementEvent
{
    public long Id { get; set; }

    /// <summary>One of the <see cref="Services.ManagementEventType"/> constants.</summary>
    public string Type { get; set; } = "";

    public int? ClientId { get; set; }

    /// <summary>Denormalised at write time (not a live FK lookup) so tenant-scoped polling
    /// doesn't need a join, and the event still carries its customer after the client is
    /// deleted or reassigned.</summary>
    public int? CustomerId { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>Short human-readable detail, same convention as <c>AuditLog.Details</c>.</summary>
    public string? Detail { get; set; }
}
