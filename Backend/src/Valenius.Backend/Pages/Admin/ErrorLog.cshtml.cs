using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Pages.Admin;

public class ErrorLogModel(ApplicationDbContext db) : PageModel
{
    public List<ErrorLog> Errors { get; private set; } = [];

    public async Task OnGetAsync()
    {
        Errors = await db.ErrorLogs
            .OrderByDescending(e => e.OccurredAt)
            .Take(200)
            .ToListAsync();
    }
}
