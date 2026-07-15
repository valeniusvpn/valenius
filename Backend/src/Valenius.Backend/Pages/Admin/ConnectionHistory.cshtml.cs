using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Pages.Admin;

public class ConnectionHistoryModel(ApplicationDbContext db) : PageModel
{
    public List<ConnectionLog> Logs { get; private set; } = [];

    public IReadOnlyList<string> CustomerNames =>
        Logs.Select(l => l.Client.Customer?.Name ?? "")
            .Where(n => n.Length > 0)
            .Distinct()
            .OrderBy(n => n)
            .ToList();

    public async Task OnGetAsync()
    {
        var customerId = GetCustomerId();

        if (!User.IsInRole("Admin") && !customerId.HasValue)
            return;

        IQueryable<ConnectionLog> query = db.ConnectionLogs
            .Include(l => l.Client)
                .ThenInclude(c => c!.Customer);

        if (!User.IsInRole("Admin"))
            query = query.Where(l => l.Client.CustomerId == customerId);

        Logs = await query
            .OrderByDescending(l => l.OccurredAt)
            .Take(500)
            .ToListAsync();
    }

    private int? GetCustomerId()
    {
        var val = User.FindFirstValue("CustomerId");
        return int.TryParse(val, out var id) ? id : null;
    }
}
