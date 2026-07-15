using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class Appliance
{
    public int Id { get; set; }

    [MaxLength(100)]
    public string DeviceId { get; set; } = "";

    [MaxLength(200)]
    public string DisplayName { get; set; } = "";

    /// <summary>PBKDF2 hash of the API key generated at provisioning time.</summary>
    [MaxLength(200)]
    public string ApiKeyHash { get; set; } = "";

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    // ── Version state ─────────────────────────────────────────────────────────

    [MaxLength(50)]
    public string? RunningVersion { get; set; }

    [MaxLength(50)]
    public string? TargetVersion { get; set; }

    [MaxLength(50)]
    public string? PreviousVersion { get; set; }

    // ── Rollout ───────────────────────────────────────────────────────────────

    /// <summary>0 = Test, 1 = Early, 2 = General</summary>
    public int RolloutRing { get; set; } = 2;

    /// <summary>Per-device emergency brake: pauses updates for this appliance.</summary>
    public bool UpdatesPaused { get; set; }

    // ── Health & heartbeat ────────────────────────────────────────────────────

    public ApplianceStatus Status { get; set; } = ApplianceStatus.Unknown;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? LastUpdateAttemptAt { get; set; }

    [MaxLength(30)]
    public string? LastUpdateResult { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // ── Navigation ────────────────────────────────────────────────────────────

    public List<ApplianceUpdateLog> UpdateLogs { get; set; } = [];
}

public enum ApplianceStatus
{
    Unknown,
    Online,
    Offline,
    Updating,
    UpdateFailed,
    RolledBack
}
