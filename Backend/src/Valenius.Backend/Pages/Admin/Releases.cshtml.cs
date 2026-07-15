using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

// Increase limits so large self-contained installers (60–150 MB) can be uploaded.
[RequestSizeLimit(200_000_000)]
[RequestFormLimits(MultipartBodyLengthLimit = 200_000_000)]
public class ReleasesModel(ApplicationDbContext db, IConfiguration config, IAuditService audit) : PageModel
{
    /// <summary>The OS streams, in tab order, with display labels + accepted file extensions.</summary>
    public static readonly (string Os, string Label, string[] Ext, string Accept)[] Streams =
    [
        (ReleaseOs.Windows, "Windows", [".exe"],          ".exe"),
        (ReleaseOs.Linux,   "Linux",   [".deb", ".gz"],   ".deb,.gz"),
        (ReleaseOs.MacOs,   "macOS",   [".pkg", ".dmg"],  ".pkg,.dmg"),
        (ReleaseOs.Android, "Android", [".apk"],          ".apk"),
    ];

    /// <summary>All releases grouped by OS, newest first.</summary>
    public Dictionary<string, List<ClientRelease>> ReleasesByOs { get; private set; } = [];
    public string PreviewBaseUrl { get; private set; } = "";

    [BindProperty] public string     Os           { get; set; } = "";
    [BindProperty] public string     Channel      { get; set; } = ReleaseChannel.Stable;
    [BindProperty] public string     Version      { get; set; } = "";
    [BindProperty] public string     ReleaseNotes { get; set; } = "";
    [BindProperty] public IFormFile? Installer    { get; set; }

    private string DownloadsPath()
    {
        var raw = config["Valenius:DownloadsPath"] ?? "downloads";
        return Path.IsPathRooted(raw) ? raw : Path.Combine(AppContext.BaseDirectory, raw);
    }

    public async Task OnGetAsync()
    {
        PreviewBaseUrl = $"{Request.Scheme}://{Request.Host}";
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var all = await db.ClientReleases
            .OrderByDescending(r => r.UploadedAt)
            .ToListAsync();
        ReleasesByOs = Streams.ToDictionary(
            s => s.Os,
            s => all.Where(r => r.Os == s.Os).ToList());
    }

    // ── POST: upload + publish a new release ──────────────────────────────────
    public async Task<IActionResult> OnPostUploadAsync()
    {
        var stream = Array.Find(Streams, s => s.Os == Os);
        if (stream.Os is null)
        {
            TempData["Error"] = "Unknown OS stream.";
            return RedirectToPage();
        }
        if (string.IsNullOrWhiteSpace(Version))
        {
            TempData["Error"] = "Version number is required.";
            return RedirectToPage();
        }
        if (Installer is null || Installer.Length == 0)
        {
            TempData["Error"] = $"Please select a {stream.Label} installer file.";
            return RedirectToPage();
        }
        var ext = Path.GetExtension(Installer.FileName).ToLowerInvariant();
        if (Array.IndexOf(stream.Ext, ext) < 0)
        {
            TempData["Error"] = $"{stream.Label} installer must be one of: {string.Join(", ", stream.Ext)}.";
            return RedirectToPage();
        }

        var channel = ReleaseChannel.Normalize(Channel);

        var dPath = DownloadsPath();
        Directory.CreateDirectory(dPath);

        var safeName = Path.GetFileName(Installer.FileName);
        var destPath = Path.Combine(dPath, safeName);
        await using (var fs = System.IO.File.Create(destPath))
            await Installer.CopyToAsync(fs);

        string sha256;
        await using (var fs = System.IO.File.OpenRead(destPath))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(fs)).ToLowerInvariant();

        // New upload becomes the published release for its (OS, channel); unpublish the others in it.
        foreach (var r in await db.ClientReleases.Where(r => r.Os == Os && r.Channel == channel && r.IsPublished).ToListAsync())
            r.IsPublished = false;

        db.ClientReleases.Add(new ClientRelease
        {
            Os           = Os,
            Channel      = channel,
            Version      = Version.Trim(),
            FileName     = safeName,
            Sha256       = sha256,
            SizeBytes    = Installer.Length,
            ReleaseNotes = ReleaseNotes?.Trim() ?? "",
            UploadedAt   = DateTime.UtcNow,
            IsPublished  = true,
        });
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Models.Audit.Release, "Release published",
            $"{stream.Label} {channel} {Version.Trim()}", safeName);
        TempData["Success"] = $"{stream.Label} ({channel}) {Version.Trim()} published — {safeName} (SHA-256: {sha256[..16]}…).";
        return RedirectToPage();
    }

    // ── POST: publish an existing (historical) release — rollback/switch active ─
    public async Task<IActionResult> OnPostPublishAsync(int id)
    {
        var rel = await db.ClientReleases.FindAsync(id);
        if (rel is null) return NotFound();

        foreach (var r in await db.ClientReleases.Where(r => r.Os == rel.Os && r.Channel == rel.Channel && r.IsPublished).ToListAsync())
            r.IsPublished = false;
        rel.IsPublished = true;
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Models.Audit.Release, "Release activated", $"{rel.Os} {rel.Channel} {rel.Version}", rel.FileName);
        TempData["Success"] = $"{rel.Os} ({rel.Channel}) {rel.Version} is now the published release.";
        return RedirectToPage();
    }

    // ── POST: promote a beta release to the stable channel (same file/hash/version) ─
    public async Task<IActionResult> OnPostPromoteAsync(int id)
    {
        var beta = await db.ClientReleases.FindAsync(id);
        if (beta is null) return NotFound();
        if (beta.Channel != ReleaseChannel.Beta)
        {
            TempData["Error"] = "Only beta releases can be promoted.";
            return RedirectToPage();
        }

        foreach (var r in await db.ClientReleases.Where(r => r.Os == beta.Os && r.Channel == ReleaseChannel.Stable && r.IsPublished).ToListAsync())
            r.IsPublished = false;

        db.ClientReleases.Add(new ClientRelease
        {
            Os           = beta.Os,
            Channel      = ReleaseChannel.Stable,
            Version      = beta.Version,
            FileName     = beta.FileName,
            Sha256       = beta.Sha256,
            SizeBytes    = beta.SizeBytes,
            ReleaseNotes = beta.ReleaseNotes,
            UploadedAt   = DateTime.UtcNow,
            IsPublished  = true,
        });
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Models.Audit.Release, "Beta promoted to stable", $"{beta.Os} {beta.Version}", beta.FileName);
        TempData["Success"] = $"{beta.Os} {beta.Version} promoted to stable.";
        return RedirectToPage();
    }

    // ── POST: delete a release (and its file if unreferenced) ─────────────────
    public async Task<IActionResult> OnPostDeleteAsync(int id)
    {
        var rel = await db.ClientReleases.FindAsync(id);
        if (rel is null) return NotFound();

        db.ClientReleases.Remove(rel);
        await db.SaveChangesAsync();

        // Remove the file only if no other release row still references it.
        var stillUsed = await db.ClientReleases.AnyAsync(r => r.FileName == rel.FileName);
        if (!stillUsed)
        {
            var filePath = Path.Combine(DownloadsPath(), Path.GetFileName(rel.FileName));
            if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath);
        }

        _ = audit.LogAsync(Models.Audit.Release, "Release deleted", $"{rel.Os} {rel.Version}", rel.FileName);
        TempData["Success"] = $"{rel.Os} {rel.Version} deleted.";
        return RedirectToPage();
    }

    // ── GET: Windows install package (installer + valenius-setup.ini) ──────────
    /// <summary>ZIP with the published Windows installer + a pre-filled valenius-setup.ini so the
    /// client connects to THIS backend out of the box (MSP deployments).</summary>
    public async Task<IActionResult> OnGetDownloadPackageAsync()
    {
        var win = await db.ClientReleases
            .Where(r => r.Os == ReleaseOs.Windows && r.IsPublished)
            .OrderByDescending(r => r.UploadedAt)
            .FirstOrDefaultAsync();
        if (win is null)
        {
            TempData["Error"] = "No published Windows release yet — upload one first.";
            return RedirectToPage();
        }

        var installerPath = Path.Combine(DownloadsPath(), Path.GetFileName(win.FileName));
        if (!System.IO.File.Exists(installerPath))
        {
            TempData["Error"] = $"Installer file '{win.FileName}' not found in downloads/.";
            return RedirectToPage();
        }

        // Force https: the client talks to the backend over TLS only.
        var backendUrl = $"https://{Request.Host}";
        var apiKey     = config["Valenius:ApiKey"] ?? "";
        var ini = $"""
            ; Valenius client setup
            ; Place this file in the same folder as the installer and run the installer.
            ; The client will connect to the backend below automatically.
            ; For silent/MDM installs you can also pass /BACKENDURL= and /APIKEY= on the command line.
            [Valenius]
            BackendUrl={backendUrl}
            ApiKey={apiKey}
            """;

        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var exeEntry = zip.CreateEntry(win.FileName, CompressionLevel.NoCompression);
            await using (var exeStream = exeEntry.Open())
            await using (var fileStream = System.IO.File.OpenRead(installerPath))
                await fileStream.CopyToAsync(exeStream);

            var iniEntry = zip.CreateEntry("valenius-setup.ini", CompressionLevel.SmallestSize);
            await using var iniStream = iniEntry.Open();
            await using var writer    = new StreamWriter(iniStream, System.Text.Encoding.UTF8);
            await writer.WriteAsync(ini);
        }

        ms.Seek(0, SeekOrigin.Begin);
        return File(ms, "application/zip", $"Valenius-{win.Version}-package.zip");
    }
}
