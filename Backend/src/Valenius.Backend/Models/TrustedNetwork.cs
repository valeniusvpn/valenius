using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class TrustedNetwork
{
    public int Id { get; set; }

    public int CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>CIDR subnet, e.g. "10.100.1.0/24"</summary>
    [MaxLength(50)]
    public string Subnet { get; set; } = "";

    /// <summary>Optional primary DNS to verify, e.g. "10.100.1.10"</summary>
    [MaxLength(45)]
    public string? PrimaryDns { get; set; }
}
