using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class ApplianceRelease
{
    public int Id { get; set; }

    [MaxLength(50)]
    public string Version { get; set; } = "";

    /// <summary>e.g. "sha256:abc123..." — verified by the agent after pull.</summary>
    [MaxLength(100)]
    public string ImageDigest { get; set; } = "";

    /// <summary>Full image reference, e.g. "git.stranto.com/valenius/server:1.5.0"</summary>
    [MaxLength(300)]
    public string ImageReference { get; set; } = "";

    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;

    /// <summary>False = held back (draft or rollout halted); True = agents can receive it.</summary>
    public bool IsPublished { get; set; }

    /// <summary>Appliances with RolloutRing >= this value receive the release.</summary>
    public int MinRolloutRing { get; set; } = 2;

    /// <summary>Set automatically when too many appliances report failure for this version.</summary>
    public bool RolloutHalted { get; set; }

    [MaxLength(2000)]
    public string? ReleaseNotes { get; set; }
}
