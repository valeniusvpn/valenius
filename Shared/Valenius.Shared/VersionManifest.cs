using System.Text.Json.Serialization;

namespace Valenius.Shared;

public class VersionManifest
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = "";
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";
    [JsonPropertyName("releaseNotes")]
    public string ReleaseNotes { get; set; } = "";
    [JsonPropertyName("linuxUrl")]
    public string? LinuxUrl { get; set; }
    [JsonPropertyName("linuxSha256")]
    public string? LinuxSha256 { get; set; }
}
