namespace Valenius.Backend.Models;

/// <summary>
/// Created when a sidecar re-enrolls (submits a new CSR for a customer that
/// already has an issued certificate).  Shown to all admins until one dismisses it.
/// Dismissal is purely informational — it has no functional effect.
/// </summary>
public class SidecarAlert
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>IP that submitted the re-enrollment CSR.</summary>
    public string EnrollingIp { get; set; } = "";

    /// <summary>SHA-256 fingerprint of the certificate that was superseded.</summary>
    public string OldCertFingerprint { get; set; } = "";

    /// <summary>SHA-256 fingerprint of the newly issued certificate.</summary>
    public string NewCertFingerprint { get; set; } = "";

    public bool      IsAcknowledged { get; set; }
    public DateTime? AcknowledgedAt { get; set; }

    /// <summary>Subject of the admin who dismissed the alert.</summary>
    public string? AcknowledgedBy { get; set; }

    public Customer? Customer { get; set; }
}
