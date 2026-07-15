using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Pages.Admin.Customers;

public class IndexModel(ApplicationDbContext db) : PageModel
{
    public record CustomerRow(int Id, string Name, string? ClientPrefix, int ClientCount, int UserCount);
    public List<CustomerRow> Customers { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var rows = await db.Customers
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.ClientPrefix,
                ClientCount = c.Clients.Count(cl => !cl.IsDeleted),
                UserCount   = c.AppUsers.Count
            })
            .ToListAsync();

        Customers = rows.Select(r => new CustomerRow(r.Id, r.Name, r.ClientPrefix, r.ClientCount, r.UserCount)).ToList();
    }

    public async Task<IActionResult> OnPostCreateAsync(string name, string? clientPrefix, string? notes)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Page();

        db.Customers.Add(new Customer
        {
            Name         = name.Trim(),
            ClientPrefix = string.IsNullOrWhiteSpace(clientPrefix) ? null : clientPrefix.Trim().ToUpperInvariant()[..Math.Min(3, clientPrefix.Trim().Length)],
            Notes        = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim()
        });
        await db.SaveChangesAsync();
        return RedirectToPage();
    }
}
