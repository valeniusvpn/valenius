using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A short-lived code (shown as a QR in the admin panel) that a mobile device
/// redeems to activate itself and join a customer in one step. One-shot:
/// <see cref="UsedAt"/> is stamped on redemption.
/// </summary>
public class PairingToken
{
    public int Id { get; set; }

    [MaxLength(32)]
    public string Token { get; set; } = "";

    public int CustomerId { get; set; }

    /// <summary>Optional exact-device recovery binding. A customer-wide code cannot
    /// claim or replace an existing device merely by naming its public identifier.</summary>
    [MaxLength(36)]
    public string? ClientKey { get; set; }
    public Customer? Customer { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
