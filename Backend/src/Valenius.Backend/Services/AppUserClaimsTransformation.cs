using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

public class AppUserClaimsTransformation(IServiceProvider services) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true)
            return principal;

        // Already transformed this request
        if (principal.HasClaim(c => c.Type == "AppRole"))
            return principal;

        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? principal.FindFirstValue("sub");
        if (string.IsNullOrEmpty(subject))
            return principal;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Subject == subject);
        if (user is null)
        {
            // Local users (Subject "local:{id}" or temp "local:pending:...") are created
            // explicitly via the admin UI — never auto-create them here.
            if (subject.StartsWith("local:", StringComparison.Ordinal))
                return principal;

            // Auto-create OIDC users: first one in the system becomes Admin.
            var isFirst   = !await db.AppUsers.AnyAsync();
            var displayName = principal.FindFirstValue("name")
                           ?? principal.FindFirstValue(ClaimTypes.Name)
                           ?? subject;
            var email = principal.FindFirstValue(ClaimTypes.Email)
                     ?? principal.FindFirstValue("email");
            user = new AppUser
            {
                Subject     = subject,
                DisplayName = displayName,
                Email       = email,
                Role        = isFirst ? "Admin" : "User"
            };
            db.AppUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("AppRole", user.Role));
        identity.AddClaim(new Claim(ClaimTypes.Role, user.Role));
        if (!identity.HasClaim(c => c.Type == ClaimTypes.Name))
            identity.AddClaim(new Claim(ClaimTypes.Name, user.DisplayName));
        if (user.CustomerId.HasValue)
            identity.AddClaim(new Claim("CustomerId", user.CustomerId.Value.ToString()));

        return principal;
    }
}
