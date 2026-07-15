using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// One cross-customer WireGuard peer assignment: a client provisioned onto a sidecar
/// that belongs to a different customer than the client itself.
/// Each row holds its own key pair and VPN IP so the foreign profile is completely
/// independent from the client's primary provisioning.
/// </summary>
public class ClientForeignProfile
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    public int SourceCustomerId { get; set; }
    public Customer SourceCustomer { get; set; } = null!;

    [MaxLength(50)]
    public string? WgPublicKey { get; set; }

    [MaxLength(50)]
    public string? WgPrivateKey { get; set; }

    [MaxLength(45)]
    public string? VpnIpAddress { get; set; }

    [MaxLength(260)]
    public string? PendingConfigFileName { get; set; }

    public string? PendingConfigContent { get; set; }

    public DateTime? ConfigProvisionedAt { get; set; }

    /// <summary>
    /// The WireGuard profile name used when this config was staged on the client.
    /// Stored persistently so DeprovisionForeignAsync can queue the correct delete
    /// even after PendingConfigFileName has been cleared by the delivery poll.
    /// </summary>
    [MaxLength(50)]
    public string? ProfileName { get; set; }
}
