using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class ApplianceUpdateLog
{
    public int Id { get; set; }
    public int ApplianceId { get; set; }
    public Appliance Appliance { get; set; } = null!;

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [MaxLength(50)]
    public string FromVersion { get; set; } = "";

    [MaxLength(50)]
    public string ToVersion { get; set; } = "";

    /// <summary>"success" | "failed" | "rolled-back"</summary>
    [MaxLength(30)]
    public string Result { get; set; } = "";

    /// <summary>Container logs uploaded by the agent on failure (capped at 8 KB).</summary>
    [MaxLength(8192)]
    public string? LogDetail { get; set; }
}
