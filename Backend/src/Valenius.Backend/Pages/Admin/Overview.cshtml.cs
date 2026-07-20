using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Licensing;
using Valenius.Backend.Services;
using Valenius.Shared;

namespace Valenius.Backend.Pages.Admin;

[Authorize(Policy = "AdminOnly")]
public class OverviewModel(
    ApplicationDbContext          db,
    ClientPresenceTracker         presence,
    WgTunnelTracker               wgTunnels,
    IConfiguration                config,
    ILicenseContext               license,
    ISidecarService               sidecar,
    IHttpClientFactory            httpClientFactory,
    IInstallationLicenseMonitor   installLicense) : PageModel
{
    public int          TotalClients     { get; private set; }
    public int          ActiveClients    { get; private set; }
    public int          PendingClients   { get; private set; }
    public int          OnlineClients    { get; private set; }
    public int          TotalCustomers   { get; private set; }
    public DateTime?    LastBackupAt     { get; private set; }

    /// <summary>Published stable release of each desktop OS (Windows / macOS / Linux) for the
    /// version + download card. Source of truth is the ClientRelease table, not versions.json.</summary>
    public IReadOnlyList<DownloadItem> ClientDownloads { get; private set; } = [];
    public ILicenseContext              License        => license;
    public IInstallationLicenseMonitor  InstallLicense => installLicense;

    // Fleet WireGuard traffic.
    public long FleetRx { get; private set; }
    public long FleetTx { get; private set; }
    public IReadOnlyList<Valenius.Backend.Services.TrafficBucket> FleetTrafficBuckets { get; private set; } = [];
    public IReadOnlyList<TrafficRow> TopTalkers { get; private set; } = [];
    public record TrafficRow(int Id, string Name, long Rx, long Tx);

    public async Task OnGetAsync()
    {
        var clients = await db.Clients
            .Where(c => !c.IsDeleted)
            .Select(c => new { c.IsActive, c.ClientKey })
            .ToListAsync();

        TotalClients   = clients.Count;
        ActiveClients  = clients.Count(c => c.IsActive);
        PendingClients = clients.Count(c => !c.IsActive);
        OnlineClients  = clients.Count(c => presence.IsOnline(c.ClientKey));
        TotalCustomers   = await db.Customers.CountAsync();
        var settings     = await db.ServerSettings.FirstOrDefaultAsync();
        LastBackupAt     = settings?.LastBackupAt;
        ClientDownloads  = await ReleaseQuery.PublishedStableDesktopAsync(db);

        // Fleet traffic: lifetime totals + top talkers + a daily chart across every client.
        var totals = await db.Clients
            .Where(c => !c.IsDeleted && (c.WgRxLifetime > 0 || c.WgTxLifetime > 0))
            .Select(c => new { c.Id, c.Hostname, c.DisplayName, c.WgRxLifetime, c.WgTxLifetime })
            .ToListAsync();
        FleetRx = totals.Sum(c => c.WgRxLifetime);
        FleetTx = totals.Sum(c => c.WgTxLifetime);
        TopTalkers = totals
            .OrderByDescending(c => c.WgRxLifetime + c.WgTxLifetime)
            .Take(10)
            .Select(c => new TrafficRow(c.Id,
                string.IsNullOrWhiteSpace(c.DisplayName) ? c.Hostname : c.DisplayName!,
                c.WgRxLifetime, c.WgTxLifetime))
            .ToList();

        var allIds = await db.Clients.Where(c => !c.IsDeleted).Select(c => c.Id).ToListAsync();
        FleetTrafficBuckets = await Valenius.Backend.Services.TrafficQuery.LoadRangeAsync(
            db, allIds, Valenius.Backend.Services.TrafficRange.Default);
    }

    /// <summary>JSON for the auto-refreshing fleet traffic panel (totals + chart).</summary>
    public async Task<IActionResult> OnGetTrafficAsync(string? range)
    {
        var q  = db.Clients.Where(c => !c.IsDeleted);
        var rx = await q.SumAsync(c => c.WgRxLifetime);
        var tx = await q.SumAsync(c => c.WgTxLifetime);
        var ids = await q.Select(c => c.Id).ToListAsync();
        var tr  = Valenius.Backend.Services.TrafficRange.Parse(range);
        var buckets = await Valenius.Backend.Services.TrafficQuery.LoadRangeAsync(db, ids, tr);
        return new JsonResult(new
        {
            total = Valenius.Backend.Helpers.TrafficFormat.Bytes(rx + tx),
            rx    = Valenius.Backend.Helpers.TrafficFormat.Bytes(rx),
            tx    = Valenius.Backend.Helpers.TrafficFormat.Bytes(tx),
            chart = Valenius.Backend.Services.TrafficChart.Render(buckets, tr),
        });
    }

    public async Task<IActionResult> OnGetCheckLicenseAsync()
    {
        var licenseId = license.LicenseId;
        if (string.IsNullOrEmpty(licenseId))
            return new JsonResult(new { reachable = true, lastCheckedIso = (string?)null });

        // The server URL is not a secret and never changes, so it's a plain constant here too
        // (kept identical to LicenseRevocationChecker's, the Pro-side background version of this
        // same check) — never read from config, so the appsettings.json empty-string-placeholder
        // trap (see LicenseRevocationChecker.cs) can't recur for it. The API key IS a real
        // credential and this file ships in the public OSS mirror, so it must stay config/env-only
        // (LICENSING_CLIENT_API_KEY or Licensing:ClientApiKey) — never hardcoded here. Empty-string
        // safe: an appsettings.json placeholder must not silently satisfy the first check and skip
        // the env var (same trap as the server URL used to have).
        var clientApiKey = config["Licensing:ClientApiKey"] is { Length: > 0 } k1 ? k1 : config["LICENSING_CLIENT_API_KEY"];
        if (string.IsNullOrEmpty(clientApiKey))
            return new JsonResult(new { reachable = true, lastCheckedIso = (string?)null });

        bool reachable;
        bool revoked = false;
        try
        {
            var http = httpClientFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(5);
            var url = $"https://license.valenius.stranto.com/api/v1/license/{Uri.EscapeDataString(licenseId)}/status";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-Api-Key", clientApiKey);
            using var resp = await http.SendAsync(req);
            reachable = resp.IsSuccessStatusCode;
            if (reachable)
            {
                var body   = await resp.Content.ReadAsStringAsync();
                var result = System.Text.Json.JsonSerializer.Deserialize<RevocationResult>(body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                revoked = result?.Revoked == true;
            }
        }
        catch
        {
            reachable = false;
        }

        DateTime? lastChecked = reachable && !revoked ? DateTime.UtcNow : null;

        // Persist to DB.
        var s = await db.ServerSettings.FirstAsync();
        if (reachable && !revoked)
        {
            s.LicenseStatus          = nameof(InstallationLicenseStatus.Valid);
            s.LicenseLastValidatedAt = lastChecked;
        }
        else if (revoked)
        {
            s.LicenseStatus = nameof(InstallationLicenseStatus.OssMode);
        }
        // Unreachable: keep existing LicenseLastValidatedAt, recompute status from age.
        await db.SaveChangesAsync();

        // Update in-memory monitor.
        installLicense.RecordCheckResult(reachable && !revoked, s.LicenseLastValidatedAt);

        return new JsonResult(new
        {
            reachable    = reachable && !revoked,
            lastCheckedIso = s.LicenseLastValidatedAt?.ToString("o")
        });
    }

    private sealed record RevocationResult(bool Revoked);

    /// <summary>
    /// Fast DB-only snapshot: client counts + online count.
    /// Called every 60 s by the dashboard JS to refresh the stat boxes without a full page reload.
    /// </summary>
    public async Task<IActionResult> OnGetStatsJsonAsync()
    {
        var clients = await db.Clients
            .Where(c => !c.IsDeleted)
            .Select(c => new { c.IsActive, c.ClientKey })
            .ToListAsync();

        var lastBackup = (await db.ServerSettings.FirstOrDefaultAsync())?.LastBackupAt;

        return new JsonResult(new
        {
            total     = clients.Count,
            active    = clients.Count(c => c.IsActive),
            pending   = clients.Count(c => !c.IsActive),
            online    = clients.Count(c => presence.IsOnline(c.ClientKey)),
            customers = await db.Customers.CountAsync(),
            lastBackupIso = lastBackup?.ToString("o"),
            wgActive  = wgTunnels.ActiveCount,
            licenseLastCheckedIso = installLicense.LastValidatedAt?.ToString("o")
        });
    }

    /// <summary>
    /// Polls all Valenius sidecars and returns a traffic snapshot.
    /// Called every 60 s by the dashboard chart JS; the JS computes deltas between
    /// consecutive snapshots to derive a data-rate series.
    /// </summary>
    public async Task<IActionResult> OnGetTrafficJsonAsync()
    {
        var wtCustomers = await db.Customers
            .Where(c => c.ServerMode == "Valenius" && c.SidecarUrl != null)
            .Select(c => new { c.SidecarUrl, c.SidecarManagementPort })
            .ToListAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var tasks = wtCustomers
            .Select(c => sidecar.GetPeersAsync($"https://{c.SidecarUrl}:{c.SidecarManagementPort}"))
            .ToArray();

        try { await Task.WhenAll(tasks); } catch { /* partial failures are ok */ }

        long bytesDown = 0, bytesUp = 0;
        foreach (var t in tasks)
        {
            if (!t.IsCompletedSuccessfully || t.Result is null) continue;
            foreach (var p in t.Result)
            {
                bytesDown += p.BytesReceived;
                bytesUp   += p.BytesSent;
            }
        }

        return new JsonResult(new
        {
            timestampIso = DateTime.UtcNow.ToString("o"),
            bytesDown,
            bytesUp,
            wgActive = wgTunnels.ActiveCount
        });
    }

}
