using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A diagnostic log bundle uploaded by a client for error analysis. The client collects a
/// fixed, documented allowlist of Valenius-only logs, runs a redaction pass (strips the API
/// key, WireGuard private keys, TOTP secrets, tokens), gzips it, and POSTs it to
/// <c>/api/clients/logs</c>. Retained per client (newest N) and downloadable/deletable by an
/// admin on the client's Details page.
/// </summary>
public class ClientLogBundle
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public DateTime UploadedAt { get; set; }

    /// <summary>What triggered the upload: "Admin" (requested from the panel) or "User" (sent from the tray/app).</summary>
    [MaxLength(20)]
    public string Trigger { get; set; } = "Admin";

    [MaxLength(260)]
    public string FileName { get; set; } = "";

    [MaxLength(100)]
    public string ContentType { get; set; } = "application/gzip";

    public int SizeBytes { get; set; }

    /// <summary>The uploaded (gzipped, redacted) log bundle bytes.</summary>
    public byte[] Content { get; set; } = [];
}
