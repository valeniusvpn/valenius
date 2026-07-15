using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OtpNet;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages;

[AllowAnonymous]
public class LoginModel(
    ApplicationDbContext db,
    IAuthenticationSchemeProvider schemeProvider,
    IDataProtectionProvider dataProtection,
    IMemoryCache cache,
    IAuditService audit) : PageModel
{
    private const string PendingTotpCookie = ".Valenius.PendingTotp";

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

    [EnableRateLimiting("login")]
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
            _ = audit.LogAsync(Audit.Auth, "Login failed", $"Email: {Email.Trim()}");
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
            var protector = dataProtection.CreateProtector("Valenius.PendingTotp");
            Response.Cookies.Append(PendingTotpCookie, protector.Protect(user.Id.ToString()),
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

    [EnableRateLimiting("login")]
    public async Task<IActionResult> OnPostTotpAsync(string? returnUrl, string code)
    {
        var safeReturn = SafeReturn(returnUrl);

        if (!TryGetPendingUserId(out var userId))
        {
            // Cookie missing or tampered — restart
            Response.Cookies.Delete(PendingTotpCookie);
            return RedirectToPage(new { returnUrl = safeReturn });
        }

        var user = await db.AppUsers.FindAsync(userId);
        if (user is null || string.IsNullOrEmpty(user.TotpSecret))
        {
            Response.Cookies.Delete(PendingTotpCookie);
            return RedirectToPage(new { returnUrl = safeReturn });
        }

        // Validate the 6-digit code (RFC 4226 / RFC 6238).
        // VerificationWindow.RfcSpecifiedNetworkDelay = ±1 time step (±30 s) for clock drift.
        var totp  = new Totp(Base32Encoding.ToBytes(user.TotpSecret));
        bool valid = totp.VerifyTotp(
            code?.Trim() ?? "",
            out long usedInterval,
            VerificationWindow.RfcSpecifiedNetworkDelay);

        // Replay protection: reject a code whose time-step was already used.
        if (valid && user.TotpLastUsedInterval.HasValue && usedInterval <= user.TotpLastUsedInterval.Value)
            valid = false;

        // Track TOTP failures per user to prevent brute-force of the 6-digit code.
        var totpKey = $"totp_fail:{user.Id}";
        if (!valid)
        {
            var fails = (cache.GetOrCreate<int>(totpKey, e => { e.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15); return 0; })) + 1;
            cache.Set(totpKey, fails, TimeSpan.FromMinutes(15));
            if (fails >= 5)
                Error = "Too many failed attempts. Please wait 15 minutes before trying again.";
            else
            {
                IsTotpStep = true;
                ReturnUrl  = safeReturn;
                Error      = "Invalid authenticator code. Please try again.";
            }
            return Page();
        }

        if (cache.TryGetValue<int>(totpKey, out var currentFails) && currentFails >= 5)
        {
            IsTotpStep = true;
            ReturnUrl  = safeReturn;
            Error      = "Too many failed attempts. Please wait 15 minutes before trying again.";
            return Page();
        }

        cache.Remove(totpKey);
        user.TotpLastUsedInterval = usedInterval;
        await db.SaveChangesAsync();

        Response.Cookies.Delete(PendingTotpCookie);
        return await SignInAndRedirect(user, safeReturn);
    }

    // ── OIDC challenge ────────────────────────────────────────────────────────

    [EnableRateLimiting("login")]
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
        _ = audit.LogAsync(Audit.Auth, "Login", $"User: {user.DisplayName} ({user.Email})");
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

    private bool TryGetPendingUserId(out int userId)
    {
        userId = 0;
        if (!Request.Cookies.TryGetValue(PendingTotpCookie, out var token))
            return false;
        try
        {
            var protector = dataProtection.CreateProtector("Valenius.PendingTotp");
            userId = int.Parse(protector.Unprotect(token));
            return true;
        }
        catch { return false; }
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
