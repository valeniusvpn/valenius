using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class LogoutModel(IWebHostEnvironment env, IAuthenticationSchemeProvider schemeProvider, IAuditService audit) : PageModel
{
    public async Task<IActionResult> OnPostAsync()
    {
        _ = audit.LogAsync(Audit.Auth, "Logout",
            $"User: {User.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? User.FindFirst("sub")?.Value ?? "Unknown"}");

        if (env.IsDevelopment())
            return Redirect("/login");

        // Always clear the local session cookie first.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        // For OIDC users: also end the provider session so Authentik doesn't
        // silently re-authenticate on the next challenge.
        // Local users (sub = "local:...") have no OIDC session -- skip.
        var sub = User.FindFirstValue("sub")
               ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
               ?? "";

        if (!sub.StartsWith("local:", StringComparison.Ordinal))
        {
            var oidcScheme = await schemeProvider.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme);
            if (oidcScheme is not null)
            {
                // Redirects to Authentik end_session_endpoint, which redirects back to
                // /signout-callback-oidc (OIDC middleware default), then to /login.
                // NOTE: for the redirect-back to work, add
                //   https://<your-domain>/signout-callback-oidc
                // to the allowed Post-logout redirect URIs in Authentik.
                return SignOut(
                    new AuthenticationProperties { RedirectUri = "/login" },
                    OpenIdConnectDefaults.AuthenticationScheme);
            }
        }

        return Redirect("/login");
    }
}
