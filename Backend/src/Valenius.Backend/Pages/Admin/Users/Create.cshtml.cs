using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin.Users;

public class CreateModel(ApplicationDbContext db, IAuditService audit) : PageModel
{
    [BindProperty] public string  DisplayName { get; set; } = "";
    [BindProperty] public string  Email       { get; set; } = "";
    [BindProperty] public string  Role        { get; set; } = "User";
    [BindProperty] public int?    CustomerId  { get; set; }
    [BindProperty] public string? Password    { get; set; }

    public List<Customer> AllCustomers { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        AllCustomers = await db.Customers.OrderBy(c => c.Name).ToListAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        AllCustomers = await db.Customers.OrderBy(c => c.Name).ToListAsync();

        if (string.IsNullOrWhiteSpace(DisplayName))  { Error = "Display name is required.";  return Page(); }
        if (string.IsNullOrWhiteSpace(Email))         { Error = "Email is required.";         return Page(); }

        var emailLower = Email.Trim().ToLower();
        if (await db.AppUsers.AnyAsync(u => u.Email != null && u.Email.ToLower() == emailLower))
        {
            Error = "A user with that email already exists.";
            return Page();
        }

        if (!string.IsNullOrEmpty(Password) && Password.Length < 8)
        {
            Error = "Password must be at least 8 characters.";
            return Page();
        }

        var user = new AppUser
        {
            Subject     = $"local:pending:{Guid.NewGuid()}", // replaced after insert to get the stable Id
            DisplayName = DisplayName.Trim(),
            Email       = Email.Trim(),
            Role        = Role is "Admin" or "User" ? Role : "User",
            CustomerId  = CustomerId,
            PasswordHash = string.IsNullOrEmpty(Password) ? null : PasswordHasher.Hash(Password)
        };

        db.AppUsers.Add(user);
        await db.SaveChangesAsync();  // now user.Id is set

        // Replace temporary subject with the stable "local:{id}" identifier.
        user.Subject = $"local:{user.Id}";
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Audit.User, "Created", $"User: {user.DisplayName} ({user.Email})", $"Role: {user.Role}");
        TempData["Success"] = $"User \"{user.DisplayName}\" created.";
        return RedirectToPage("Index");
    }
}
