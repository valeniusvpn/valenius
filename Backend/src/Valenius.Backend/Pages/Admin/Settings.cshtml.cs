using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class SettingsModel(ApplicationDbContext db, IAuthenticationSchemeProvider schemeProvider, IEmailService email, IAuditService audit, ApiKeyStore apiKeyStore, HeartbeatSettingsStore heartbeatSettings) : PageModel
{
    [BindProperty]
    public ServerSettings Settings { get; set; } = default!;

    /// <summary>True if the OIDC scheme was actually registered at startup (i.e. OIDC settings were present).</summary>
    public bool OidcConfiguredAtRuntime { get; private set; }

    public string    ClientApiKey       { get; private set; } = "";
    public string    BackendUrl         { get; private set; } = "";
    /// <summary>True while a rotation grace window is open (a previous key is still accepted).</summary>
    public bool      RotationInProgress { get; private set; }
    public DateTime? ApiKeyRotatedAt    { get; private set; }
    public int ClientsWithoutUpdatePolicy { get; private set; }

    public async Task OnGetAsync()
    {
        Settings                = await db.ServerSettings.FirstAsync();
        OidcConfiguredAtRuntime = await schemeProvider.GetSchemeAsync(OpenIdConnectDefaults.AuthenticationScheme) is not null;
        ClientApiKey            = apiKeyStore.Key;
        RotationInProgress      = !string.IsNullOrEmpty(apiKeyStore.PreviousKey);
        ApiKeyRotatedAt         = Settings.ApiKeyRotatedAt;
        BackendUrl              = $"{Request.Scheme}://{Request.Host}";
        var stableClients = await db.Clients.AsNoTracking()
            .Where(c => c.IsActive && !c.IsDeleted && c.UpdateChannel.ToLower() != ReleaseChannel.Beta)
            .Select(c => new Client { ClientKey = c.ClientKey, CustomerId = c.CustomerId, LastDeliveredPolicyJson = c.LastDeliveredPolicyJson })
            .ToListAsync();
        ClientsWithoutUpdatePolicy = stableClients.Count(c => ClientUpdateHalt.PreviousPolicy(c) is null);
    }

    public async Task<IActionResult> OnPostSaveUpdateHaltAsync()
    {
        var current = await db.ServerSettings.FirstAsync();
        current.ClientUpdateHaltEnabled = Settings.ClientUpdateHaltEnabled;
        await audit.SaveChangesWithAuditAsync(db, Audit.Settings,
            current.ClientUpdateHaltEnabled ? "Client update halt enabled" : "Client update halt disabled",
            "Beta clients are exempt; authentication, revocation and MFA remain active.");
        TempData["Success"] = current.ClientUpdateHaltEnabled
            ? "Client updates halted. Beta clients continue receiving updates."
            : "Client updates resumed. Queued changes will be delivered.";
        TempData["SettingsTab"] = "#tab-connectivity";
        return RedirectToPage();
    }

    // Old automation must fail explicitly instead of claiming fleet keys still authorize devices.
    public IActionResult OnPostRotateApiKey() => BadRequest("Installation-wide device keys are retired. Rotate a credential from Client Details / Access.");

    /// <summary>Save the traffic-sample retention window (days).</summary>
    public async Task<IActionResult> OnPostSaveTrafficAsync()
    {
        var current = await db.ServerSettings.FirstAsync();
        current.TrafficRetentionDays = Math.Clamp(Settings.TrafficRetentionDays, 1, 365);
        await audit.SaveChangesWithAuditAsync(db, Audit.Settings, "Traffic retention saved", $"{current.TrafficRetentionDays} days");
        TempData["Success"]     = "Traffic retention saved.";
        TempData["SettingsTab"] = "#tab-connectivity";
        return RedirectToPage();
    }

    /// <summary>
    /// Save the system-wide default heartbeat interval (used by any customer that doesn't set
    /// its own override). Updates the in-memory HeartbeatSettingsStore too, so it takes effect
    /// immediately without a restart.
    /// </summary>
    public async Task<IActionResult> OnPostSaveHeartbeatAsync()
    {
        var current = await db.ServerSettings.FirstAsync();
        current.DefaultHeartbeatIntervalMinutes = Math.Clamp(Settings.DefaultHeartbeatIntervalMinutes, 1, 60);
        await db.SaveChangesAsync();
        heartbeatSettings.DefaultIntervalMinutes = current.DefaultHeartbeatIntervalMinutes;
        await audit.LogAsync(Audit.Settings, "Default heartbeat interval saved", $"{current.DefaultHeartbeatIntervalMinutes} min");
        TempData["Success"]     = "Default heartbeat interval saved.";
        TempData["SettingsTab"] = "#tab-connectivity";
        return RedirectToPage();
    }

    public IActionResult OnPostRetireApiKey() => BadRequest("Installation-wide device keys are retired. Rotate a credential from Client Details / Access.");

    public async Task<IActionResult> OnPostSaveOidcConfigAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var current = await db.ServerSettings.FirstAsync();
        current.OidcAuthority   = string.IsNullOrWhiteSpace(Settings.OidcAuthority)   ? null : Settings.OidcAuthority.Trim();
        current.OidcClientId    = string.IsNullOrWhiteSpace(Settings.OidcClientId)    ? null : Settings.OidcClientId.Trim();
        current.OidcRedirectUri = string.IsNullOrWhiteSpace(Settings.OidcRedirectUri) ? null : Settings.OidcRedirectUri.Trim();
        // Only update the secret if a non-empty value was submitted (blank = keep existing).
        if (!string.IsNullOrWhiteSpace(Settings.OidcClientSecret))
            current.OidcClientSecret = Settings.OidcClientSecret.Trim();

        await audit.SaveChangesWithAuditAsync(db, Audit.Settings, "OIDC settings saved", "Settings: Authentication");
        TempData["Success"]     = "OIDC settings saved. Restart the container to apply.";
        TempData["SettingsTab"] = "#tab-auth";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveSmtpAsync()
    {
        var current = await db.ServerSettings.FirstAsync();
        current.SmtpEnabled = Settings.SmtpEnabled;
        current.SmtpHost    = string.IsNullOrWhiteSpace(Settings.SmtpHost) ? null : Settings.SmtpHost.Trim();
        current.SmtpPort    = Settings.SmtpPort is > 0 and <= 65535 ? Settings.SmtpPort : 587;
        current.SmtpUsername = string.IsNullOrWhiteSpace(Settings.SmtpUsername) ? null : Settings.SmtpUsername.Trim();
        // Only overwrite the password when a non-empty value was submitted.
        if (!string.IsNullOrWhiteSpace(Settings.SmtpPassword))
            current.SmtpPassword = Settings.SmtpPassword.Trim();
        current.SmtpUseSsl = Settings.SmtpUseSsl;
        current.EmailFrom  = string.IsNullOrWhiteSpace(Settings.EmailFrom) ? null : Settings.EmailFrom.Trim();
        await audit.SaveChangesWithAuditAsync(db, Audit.Settings, "SMTP settings saved", "Settings: E-mail");
        TempData["Success"]     = "SMTP settings saved.";
        TempData["SettingsTab"] = "#tab-email";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostTestSmtpAsync(string testRecipient)
    {
        if (string.IsNullOrWhiteSpace(testRecipient))
        {
            TempData["SmtpError"] = "Enter a recipient address.";
            return RedirectToPage();
        }
        try
        {
            await email.SendAsync(
                testRecipient.Trim(),
                "Valenius — SMTP test",
                "<p>This is a test e-mail from <strong>Valenius</strong>. " +
                "If you received it, your SMTP settings are working correctly.</p>");
            TempData["Success"] = $"Test e-mail sent to {testRecipient}.";
        }
        catch (Exception ex)
        {
            TempData["SmtpError"] = ex.Message;
        }
        TempData["SettingsTab"] = "#tab-email";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostSaveAuthAsync()
    {
        if (!ModelState.IsValid)
            return Page();

        var current = await db.ServerSettings.FirstAsync();

        // At least one method must stay enabled — enforce server-side too.
        if (!Settings.OidcEnabled && !Settings.LocalAuthEnabled)
        {
            ModelState.AddModelError("", "At least one authentication method must be enabled.");
            Settings = current;
            return Page();
        }

        current.OidcEnabled      = Settings.OidcEnabled;
        current.LocalAuthEnabled = Settings.LocalAuthEnabled;
        await audit.SaveChangesWithAuditAsync(db, Audit.Settings, "Auth methods saved",
            $"Settings: OIDC={current.OidcEnabled}, Local={current.LocalAuthEnabled}");
        TempData["Success"]     = "Authentication settings saved.";
        TempData["SettingsTab"] = "#tab-auth";
        return RedirectToPage();
    }

}
