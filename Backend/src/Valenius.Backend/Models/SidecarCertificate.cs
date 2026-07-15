namespace Valenius.Backend.Models;

/// <summary>Tracks every certificate the backend has issued to a sidecar.</summary>
public class SidecarCertificate
{
    public int Id { get; set; }

    public int CustomerId { get; set; }

    /// <summary>CN from the CSR, e.g. "wg-sidecar-wg0".</summary>
    public string SubjectCn { get; set; } = "";

    /// <summary>Full certificate PEM.</summary>
    public string CertPem { get; set; } = "";

    /// <summary>SHA-256 hex fingerprint of the DER-encoded certificate.</summary>
    public string Fingerprint { get; set; } = "";

    public DateTime IssuedAt  { get; set; }
    public DateTime ExpiresAt { get; set; }

    public bool      IsRevoked  { get; set; }
    public DateTime? RevokedAt  { get; set; }

    /// <summary>IP address of the HTTP client that submitted the enrollment CSR.</summary>
    public string? EnrollingIp { get; set; }

    public Customer? Customer { get; set; }
}
