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

    public List<AlertRow> UnacknowledgedAlerts { get; private set; } = [];
    public List<AlertRow> AcknowledgedAlerts   { get; private set; } = [];
    public string?        CaCertPem            { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadAsync();
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
}
