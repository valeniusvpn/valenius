using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A cross-device push-to-approve MFA challenge: a gated <see cref="RequesterClientId"/>
/// needs a session, and the <see cref="ApproverClientId"/> phone must approve it by
/// matching <see cref="MatchNumber"/> among <see cref="Choices"/>. One-shot.
/// </summary>
public class MfaChallenge
{
    public int Id { get; set; }

    public int RequesterClientId { get; set; }
    public int ApproverClientId  { get; set; }

    /// <summary>The number the requester displays; the approver must pick it among <see cref="Choices"/>.</summary>
    public int MatchNumber { get; set; }

    /// <summary>The three numbers shown to the approver, comma-separated (e.g. "42,17,88").</summary>
    [MaxLength(32)]
    public string Choices { get; set; } = "";

    [MaxLength(20)]
    public string Status { get; set; } = "Pending"; // Pending | Approved | Denied | Expired

    [MaxLength(45)]
    public string? RequesterIp { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
