using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>Creates the initial local administrator without changing existing accounts.</summary>
public static class BootstrapAdminSeeder
{
    public static async Task SeedAsync(ApplicationDbContext db, IConfiguration configuration)
    {
        var email = configuration["Valenius:AdminEmail"];
        if (string.IsNullOrEmpty(email))
            email = configuration["ADMIN_EMAIL"];
        var password = configuration["Valenius:AdminPassword"];
        if (string.IsNullOrEmpty(password))
            password = configuration["ADMIN_PASSWORD"];

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            return;

        // Never reset passwords/MFA, restore roles or recreate a removed bootstrap
        // account alongside existing users. Recovery belongs to an explicit admin action.
        if (await db.AppUsers.AnyAsync())
            return;

        db.AppUsers.Add(new AppUser
        {
            Subject = "local:bootstrap",
            DisplayName = "Admin",
            Email = email,
            Role = "Admin",
            PasswordHash = PasswordHasher.Hash(password)
        });
        await db.SaveChangesAsync();
    }
}
