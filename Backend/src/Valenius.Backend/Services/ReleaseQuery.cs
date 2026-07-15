using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>One desktop OS row for the download surfaces (Overview card, /Admin/Downloads, public
/// /downloads). <see cref="Release"/> is the currently published <b>stable</b> release for the OS,
/// or null when nothing is published yet.</summary>
public sealed record DownloadItem(string Os, string Label, string Icon, ClientRelease? Release)
{
    public bool    HasRelease  => Release is not null;
    public string? Version     => Release?.Version;
    /// <summary>Anonymous installer download URL (served by <c>/api/download/{fileName}</c>).</summary>
    public string? DownloadUrl => Release is null ? null : $"/api/download/{Uri.EscapeDataString(Release.FileName)}";
}

/// <summary>Shared read-side helpers for the published client releases (used by the Overview version
/// card and both downloads pages) so they agree on "the current stable version of each desktop OS".</summary>
public static class ReleaseQuery
{
    /// <summary>The desktop OS streams shown on the download surfaces, in display order.</summary>
    public static readonly (string Os, string Label, string Icon)[] DesktopStreams =
    [
        (ReleaseOs.Windows, "Windows", "bi-windows"),
        (ReleaseOs.MacOs,   "macOS",   "bi-apple"),
        (ReleaseOs.Linux,   "Linux",   "bi-ubuntu"),
    ];

    /// <summary>The published stable release of each desktop OS (Windows / macOS / Linux), one row
    /// per OS in display order — the <see cref="DownloadItem.Release"/> is null when unpublished.</summary>
    public static async Task<List<DownloadItem>> PublishedStableDesktopAsync(ApplicationDbContext db)
    {
        var published = await db.ClientReleases
            .Where(r => r.IsPublished && r.Channel == ReleaseChannel.Stable
                     && (r.Os == ReleaseOs.Windows || r.Os == ReleaseOs.MacOs || r.Os == ReleaseOs.Linux))
            .ToListAsync();

        return DesktopStreams
            .Select(s => new DownloadItem(s.Os, s.Label, s.Icon,
                published.FirstOrDefault(r => r.Os == s.Os)))
            .ToList();
    }
}
