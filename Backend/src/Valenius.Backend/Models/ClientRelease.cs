using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// One uploaded client release for a specific OS stream (windows / linux / macos / android).
/// Each OS keeps its own independent history; at most one row per OS has <see cref="IsPublished"/>
/// = true — that's the version clients auto-update to (served by <c>GET /api/version/{os}</c>, and
/// for windows+linux also folded into the legacy <c>GET /api/version</c>). Rollback = publish an
/// older row. The installer file itself lives in the downloads folder and is served by
/// <c>/api/download/{fileName}</c>.
/// </summary>
public class ClientRelease
{
    public int Id { get; set; }

    /// <summary>Lowercase OS stream: "windows", "linux", "macos", "android".</summary>
    [MaxLength(20)]
    public string Os { get; set; } = "";

    /// <summary>Release channel: "stable" (default) or "beta". Published is per (Os, Channel).</summary>
    [MaxLength(10)]
    public string Channel { get; set; } = ReleaseChannel.Stable;

    [MaxLength(40)]
    public string Version { get; set; } = "";

    /// <summary>The installer file name in the downloads folder.</summary>
    [MaxLength(260)]
    public string FileName { get; set; } = "";

    [MaxLength(64)]
    public string Sha256 { get; set; } = "";

    public long SizeBytes { get; set; }

    public string ReleaseNotes { get; set; } = "";

    public DateTime UploadedAt { get; set; }

    /// <summary>True for the single active release of this OS that clients update to.</summary>
    public bool IsPublished { get; set; }
}

/// <summary>The OS streams the Releases page manages.</summary>
public static class ReleaseOs
{
    public const string Windows = "windows";
    public const string Linux   = "linux";
    public const string MacOs   = "macos";
    public const string Android = "android";

    public static readonly string[] All = [Windows, Linux, MacOs, Android];

    public static bool IsValid(string? os) => os is not null && Array.IndexOf(All, os) >= 0;
}

/// <summary>Update channels a client can track. Clients default to <see cref="Stable"/>.</summary>
public static class ReleaseChannel
{
    public const string Stable = "stable";
    public const string Beta   = "beta";

    public static readonly string[] All = [Stable, Beta];

    /// <summary>Coerces any input to a valid channel, defaulting to stable.</summary>
    public static string Normalize(string? channel) =>
        string.Equals(channel, Beta, StringComparison.OrdinalIgnoreCase) ? Beta : Stable;
}
