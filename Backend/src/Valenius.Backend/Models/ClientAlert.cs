namespace Valenius.Backend.Models;

/// <summary>
/// A client-reported problem it could not self-resolve — e.g. the Windows data-directory ACL
/// self-heal ran and failed, which can mean the machine's ProgramData tree was tampered with
/// (ownership moved away from SYSTEM) rather than just an ordinary corrupted ACL. Shown to all
/// admins on Admin/Alerts until one dismisses it. <see cref="Kind"/> is a generic discriminator
/// so future client-side repair failures can reuse this same table without a schema change.
/// Dismissal is purely informational — it has no functional effect.
/// </summary>
public class ClientAlert
{
    public int Id { get; set; }

    public int ClientId { get; set; }

    /// <summary>What kind of problem this is, e.g. "ConfigRepairFailed". Lets future client-side
    /// failure reports reuse this table instead of adding a new one per kind.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Free-text detail from the client (e.g. the exact repair command still needed).</summary>
    public string? Detail { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool      IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Subject/email of the admin who dismissed the alert.</summary>
    public string? AcknowledgedBy { get; set; }

    public Client? Client { get; set; }
}
