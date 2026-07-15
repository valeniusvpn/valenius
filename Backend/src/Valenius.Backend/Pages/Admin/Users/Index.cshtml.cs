using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;

namespace Valenius.Backend.Pages.Admin.Users;

public class IndexModel(ApplicationDbContext db) : PageModel
{
    public record UserRow(int Id, string DisplayName, string? Email, string Role, string? CustomerName, bool IsLocal, bool HasPassword);
    public List<UserRow> Users { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var rows = await db.AppUsers
            .OrderBy(u => u.DisplayName)
            .Select(u => new
            {
                u.Id,
                u.DisplayName,
                u.Email,
                u.Role,
                CustomerName = u.Customer != null ? u.Customer.Name : null,
                IsLocal      = u.Subject.StartsWith("local:"),
                HasPassword  = u.PasswordHash != null
            })
            .ToListAsync();

        Users = rows.Select(r => new UserRow(
            r.Id, r.DisplayName, r.Email, r.Role, r.CustomerName, r.IsLocal, r.HasPassword)).ToList();
    }
}
