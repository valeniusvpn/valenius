using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Helpers;
using Valenius.Backend.Models;

namespace Valenius.Backend.Pages.Admin;

public class AuditLogModel(ApplicationDbContext db) : PageModel
{
    public List<AuditLog> Entries    { get; private set; } = [];
    public int            TotalCount { get; private set; }

    [BindProperty(SupportsGet = true)] public string?  Category { get; set; }
    [BindProperty(SupportsGet = true)] public string?  Search   { get; set; }
    [BindProperty(SupportsGet = true)] public string?  Period   { get; set; } = "7d";

    public async Task OnGetAsync()
    {
        var cutoff = Period switch
        {
            "1h"  => DateTime.UtcNow.AddHours(-1),
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d"  => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _     => DateTime.MinValue
        };

        var q = db.AuditLogs.AsQueryable();

        if (cutoff > DateTime.MinValue)
            q = q.Where(l => l.Timestamp >= cutoff);

        if (!string.IsNullOrWhiteSpace(Category) && Category != "All")
            q = q.Where(l => l.Category == Category);

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var s = Search.Trim().ToLower();
            q = q.Where(l =>
                l.Actor.ToLower().Contains(s)  ||
                l.Target.ToLower().Contains(s) ||
                l.Action.ToLower().Contains(s) ||
                (l.Details != null && l.Details.ToLower().Contains(s)));
        }

        TotalCount = await q.CountAsync();
        Entries    = await q.OrderByDescending(l => l.Timestamp).Take(500).ToListAsync();
    }

    public async Task<IActionResult> OnPostClearAsync()
    {
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "AuditLogs" """);
        TempData["Success"] = "Audit log cleared.";
        return RedirectToPage();
    }
}
