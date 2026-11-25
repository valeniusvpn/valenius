using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages;

[AllowAnonymous]
[EnableRateLimiting("login")]
public class LoginModel(
    ApplicationDbContext db,
    IAuthenticationSchemeProvider schemeProvider,
    IDataProtectionProvider dataProtection,
    IAuditService audit) : PageModel
{
    private const string PendingTotpCookie = ".Valenius.PendingTotp";
    private const string PendingTotpPurpose = "Valenius.PendingTotp.v2";

    public bool   OidcEnabled      { get; private set; }
    public bool   LocalAuthEnabled { get; private set; }
    public bool   IsTotpStep       { get; private set; }
    public string ReturnUrl        { get; private set; } = "/Admin/";
    public string? Error           { get; private set; }

    [BindProperty] public string Email    { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(
        [FromQuery(Name = "ReturnUrl")] string? returnUrl,
        [FromQuery(Name = "step")] string? step)
    {
        if (User.Identity?.IsAuthenticated == true)
            return Redirect(SafeReturn(returnUrl));

        ReturnUrl = SafeReturn(returnUrl);

        // ── TOTP challenge step ──────────────────────────────────────────────
        if (step == "totp")
        {
            if (TryGetPendingUserId(out _))
            {
                IsTotpStep = true;
                return Page();
            }
            // Cookie missing or tampered — restart login
            Response.Cookies.Delete(PendingTotpCookie);
            return RedirectToPage(new { returnUrl });
        }

        // ── Normal login page ────────────────────────────────────────────────
        var settings = await db.ServerSettings.FirstAsync();
        var oidcSchemeAvailable = await schemeProvider.GetSchemeAsync(
            OpenIdConnectDefaults.AuthenticationScheme) is not null;
        OidcEnabled = settings.OidcEnabled && oidcSchemeAvailable;

        var bootstrapExists = await db.AppUsers
            .AnyAsync(u => u.Subject == "local:bootstrap" && u.PasswordHash != null);
        LocalAuthEnabled = settings.LocalAuthEnabled || bootstrapExists;

        if (OidcEnabled && !LocalAuthEnabled)
        {
            return Challenge(
                new AuthenticationProperties { RedirectUri = ReturnUrl },
                OpenIdConnectDefaults.AuthenticationScheme);
        }

        return Page();
    }

    // ── Local email + password login ─────────────────────────────────────────

    public async Task<IActionResult> OnPostLocalAsync(string? returnUrl)
    {
        var settings   = await db.ServerSettings.FirstAsync();
        var safeReturn = SafeReturn(returnUrl);

        var user = await db.AppUsers.FirstOrDefaultAsync(
            u => u.Email != null && u.Email.ToLower() == Email.Trim().ToLower());

        var isBootstrap = user?.Subject == "local:bootstrap";
        if (!settings.LocalAuthEnabled && !isBootstrap)
            return Forbid();

        if (user is null || string.IsNullOrEmpty(user.PasswordHash)
            || !PasswordHasher.Verify(Password, user.PasswordHash))
        {
            await audit.LogAsync(Audit.Auth, "Login failed", $"Email: {Email.Trim()}");
            OidcEnabled      = settings.OidcEnabled;
            LocalAuthEnabled = true;
            ReturnUrl        = safeReturn;
            Error            = "Invalid email or password.";
            return Page();
        }

        // ── TOTP required? ────────────────────────────────────────────────────
        if (user.TotpEnabled && !string.IsNullOrEmpty(user.TotpSecret))
        {
            // Store userId in a short-lived protected cookie; redirect to TOTP step.
            var protector = dataProtection.CreateProtector(PendingTotpPurpose).ToTimeLimitedDataProtector();
            Response.Cookies.Append(PendingTotpCookie,
                protector.Protect($"{user.Id}|{CredentialStamp(user)}", TimeSpan.FromMinutes(5)),
                new CookieOptions
                {
                    HttpOnly  = true,
                    Secure    = true,
                    SameSite  = SameSiteMode.Strict,
                    Expires   = DateTimeOffset.UtcNow.AddMinutes(5)
                });
            return RedirectToPage(new { step = "totp", returnUrl = safeReturn });
        }

        return await SignInAndRedirect(user, safeReturn);
    }

    // ── TOTP second factor ────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostTotpAsync(string? returnUrl, string code)
    {
        var safeReturn = SafeReturn(returnUrl);

        if (!TryGetPendingChallenge(out var userId, out var stamp))
        {
            // Cookie missing or tampered — restart
            Response.Cookies.Delete(PendingTotpCookie);
            return RedirectToPage(new { returnUrl = safeReturn });
        }

        var user = await db.AppUsers.FindAsync(userId);
        if (user is null || !user.IsLocalUser || !user.TotpEnabled
            || string.IsNullOrEmpty(user.TotpSecret) || string.IsNullOrEmpty(user.PasswordHash)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stamp), Encoding.ASCII.GetBytes(CredentialStamp(user))))
        {
            Response.Cookies.Delete(PendingTotpCookie);
            return RedirectToPage(new { returnUrl = safeReturn });
        }

        var secret = user.TotpSecret;
        var passwordHash = user.PasswordHash;
        var subject = user.Subject;
        var eligible = db.AppUsers.Where(u => u.Id == userId && u.Subject == subject
            && u.TotpEnabled && u.TotpSecret == secret && u.PasswordHash == passwordHash
            && (u.Subject == "local:bootstrap" || db.ServerSettings.Any(s => s.LocalAuthEnabled)));
        var result = await TotpAttemptGuard.VerifyAsync(eligible, secret, code, nameof(AppUser.TotpLastUsedInterval));
        if (result != TotpAttemptResult.Accepted)
        {
            IsTotpStep = true;
            ReturnUrl = safeReturn;
            Error = result == TotpAttemptResult.Locked
                ? "Too many failed attempts. Please wait 15 minutes before trying again."
                : "Invalid authenticator code. Please try again.";
            return Page();
        }

        Response.Cookies.Delete(PendingTotpCookie);
        return await SignInAndRedirect(user, safeReturn);
    }

    // ── OIDC challenge ────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostOidcAsync(string? returnUrl)
    {
        var settings = await db.ServerSettings.FirstAsync();
        if (!settings.OidcEnabled) return Forbid();

        return Challenge(
            new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) },
            OpenIdConnectDefaults.AuthenticationScheme);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<IActionResult> SignInAndRedirect(Models.AppUser user, string returnUrl)
    {
        await audit.LogAsync(Audit.Auth, "Login", $"User: {user.DisplayName} ({user.Email})");
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Subject),
            new Claim("sub",                     user.Subject),
            new Claim(ClaimTypes.Name,           user.DisplayName),
            new Claim(ClaimTypes.Email,          user.Email!),
        };
        var identity  = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        return Redirect("/Admin/Overview");
    }

    private static string CredentialStamp(AppUser user) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{user.PasswordHash}\0{user.TotpSecret}")));

    private bool TryGetPendingUserId(out int userId) => TryGetPendingChallenge(out userId, out _);

    private bool TryGetPendingChallenge(out int userId, out string stamp)
    {
        userId = 0;
        stamp = "";
        if (!Request.Cookies.TryGetValue(PendingTotpCookie, out var token)) return false;
        try
        {
            var protector = dataProtection.CreateProtector(PendingTotpPurpose).ToTimeLimitedDataProtector();
            var parts = protector.Unprotect(token, out _).Split('|');
            if (parts.Length != 2 || !int.TryParse(parts[0], out userId) || userId <= 0
                || parts[1].Length != 64 || !parts[1].All(char.IsAsciiHexDigit)) return false;
            stamp = parts[1];
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException) { return false; }
    }

    private string SafeReturn(string? url)
    {
        if (string.IsNullOrEmpty(url) || !Url.IsLocalUrl(url))
            return "/Admin/";
        // Never return to the login page itself or to a POST-only Razor page handler
        // (e.g. /Login?handler=Oidc). After OIDC sign-in the middleware redirects there
        // with a GET, which has no matching handler and 404s ("site not found"). Land on
        // the admin home instead.
        var path = url.Split('?', '#')[0].TrimEnd('/');
        if (path.Equals("/login", StringComparison.OrdinalIgnoreCase)
            || url.Contains("handler=", StringComparison.OrdinalIgnoreCase))
            return "/Admin/";
        return url;
    }
}
