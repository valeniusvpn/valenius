using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class ConnectionLog
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client Client { get; set; } = null!;

    [MaxLength(255)]
    public string Username { get; set; } = "";

    [MaxLength(100)]
    public string TunnelName { get; set; } = "";

    [MaxLength(20)]
    public string EventType { get; set; } = "";

    [MaxLength(45)]
    public string LanIp { get; set; } = "";

    [MaxLength(45)]
    public string WanIp { get; set; } = "";

    public DateTime OccurredAt { get; set; }
}
