using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>Committed before sidecar effects. Retired addresses remain quarantined.</summary>
public sealed class PeerAddressReservation
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public int ClientId { get; set; }
    public bool Foreign { get; set; }
    [MaxLength(100)] public string Owner { get; set; } = "";
    [MaxLength(45)] public string Address { get; set; } = "";
    [MaxLength(50)] public string PublicKey { get; set; } = "";
    [MaxLength(50)] public string PrivateKey { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
