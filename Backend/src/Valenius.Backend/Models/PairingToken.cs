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
    public Customer? Customer { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
