using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class AlertsModel(ApplicationDbContext db, ICaService ca) : PageModel
{
    public record AlertRow(
        int      Id,
        string   CustomerName,
        DateTime CreatedAt,
        string   EnrollingIp,
        string   OldFingerprint,
        string   NewFingerprint);

    public record ClientAlertRow(
        int      Id,
        int      ClientId,
        string   ClientName,
        string   Kind,
        string?  Detail,
        DateTime CreatedAt)
    {
        /// <summary>True for kinds where the client already fixed itself and this is purely FYI
        /// (e.g. "OwnershipReclaimed") — styled less alarmingly than a kind that still needs
        /// attention (e.g. "ConfigRepairFailed").</summary>
        public bool IsInformational => Kind == "OwnershipReclaimed";

        public string Label => Kind switch
        {
            "ConfigRepairFailed"  => "Config repair failed",
            "OwnershipReclaimed"  => "Ownership forcibly reclaimed (repair succeeded)",
            _ => Kind,
        };
    }

    public List<AlertRow> UnacknowledgedAlerts { get; private set; } = [];
    public List<AlertRow> AcknowledgedAlerts   { get; private set; } = [];
    public string?        CaCertPem            { get; private set; }

    public List<ClientAlertRow> UnacknowledgedClientAlerts { get; private set; } = [];
    public List<ClientAlertRow> AcknowledgedClientAlerts   { get; private set; } = [];

    public async Task OnGetAsync()
    {
        await LoadAsync();
        await LoadClientAlertsAsync();
        CaCertPem = await ca.GetCaCertPemAsync();
    }

    public async Task<IActionResult> OnPostAcknowledgeAsync(int id)
    {
        var alert = await db.SidecarAlerts.FindAsync(id);
        if (alert is null) return NotFound();

        alert.IsAcknowledged = true;
        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedBy = User.FindFirstValue(ClaimTypes.Email)
                            ?? User.FindFirstValue(ClaimTypes.Name)
                            ?? "unknown";
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostAcknowledgeClientAlertAsync(int id)
    {
        var alert = await db.ClientAlerts.FindAsync(id);
        if (alert is null) return NotFound();

        alert.IsAcknowledged = true;
        alert.AcknowledgedAt = DateTime.UtcNow;
        alert.AcknowledgedBy = User.FindFirstValue(ClaimTypes.Email)
                            ?? User.FindFirstValue(ClaimTypes.Name)
                            ?? "unknown";
        await db.SaveChangesAsync();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetDownloadCaAsync()
    {
        var pem = await ca.GetCaCertPemAsync();
        var bytes = System.Text.Encoding.UTF8.GetBytes(pem);
        return File(bytes, "application/x-pem-file", "valenius-ca.crt");
    }

    private async Task LoadAsync()
    {
        var all = await db.SidecarAlerts
            .Include(a => a.Customer)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        static AlertRow ToRow(SidecarAlert a) => new(
            a.Id,
            a.Customer?.Name ?? $"Customer #{a.CustomerId}",
            a.CreatedAt,
            a.EnrollingIp,
            a.OldCertFingerprint.Length >= 16 ? a.OldCertFingerprint[..16] + "…" : a.OldCertFingerprint,
            a.NewCertFingerprint.Length >= 16 ? a.NewCertFingerprint[..16] + "…" : a.NewCertFingerprint);

        UnacknowledgedAlerts = all.Where(a => !a.IsAcknowledged).Select(ToRow).ToList();
        AcknowledgedAlerts   = all.Where(a =>  a.IsAcknowledged).Select(ToRow).ToList();
    }

    private async Task LoadClientAlertsAsync()
    {
        var all = await db.ClientAlerts
            .Include(a => a.Client)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync();

        static ClientAlertRow ToRow(ClientAlert a) => new(
            a.Id,
            a.ClientId,
            a.Client?.EffectiveName ?? $"Client #{a.ClientId}",
            a.Kind,
            a.Detail,
            a.CreatedAt);

        UnacknowledgedClientAlerts = all.Where(a => !a.IsAcknowledged).Select(ToRow).ToList();
        AcknowledgedClientAlerts   = all.Where(a =>  a.IsAcknowledged).Select(ToRow).ToList();
    }
}
