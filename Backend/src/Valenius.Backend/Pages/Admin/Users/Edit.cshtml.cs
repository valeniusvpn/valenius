using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using OtpNet;
using QRCoder;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin.Users;

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public class EditModel(ApplicationDbContext db, IAuditService audit) : PageModel
{
    public AppUser        AppUser      { get; private set; } = null!;
    public List<Customer> AllCustomers { get; private set; } = [];

    /// <summary>Pending disabled factor, shown after "Generate" through protected TempData.</summary>
    public string? PendingTotpSecret  { get; private set; }
    /// <summary>Base64 PNG of the QR code for the pending secret.</summary>
    public string? TotpQrBase64       { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        AppUser      = user;
        AllCustomers = await db.Customers.OrderBy(c => c.Name).ToListAsync();

        // Restore pending secret after "Generate" redirect (TempData is one-shot).
        if (TempData[$"PendingTotpSecret:{id}"] is string secret && !user.TotpEnabled
            && secret == user.TotpSecret && !string.IsNullOrEmpty(user.Email))
        {
            PendingTotpSecret = secret;
            TotpQrBase64      = BuildQrBase64(secret, user.Email, user.DisplayName);
        }

        return Page();
    }

    // ── Save role + customer (both local and OIDC) ───────────────────────────

    public async Task<IActionResult> OnPostAsync(int id, string role, int? customerId)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();

        if (role is "Admin" or "User")
            user.Role = role;
        user.CustomerId = customerId;

        await audit.SaveChangesWithAuditAsync(db, Audit.User, "Role updated", $"User: {user.DisplayName}", $"Role: {user.Role}");
        TempData["Success"] = "User saved.";
        return RedirectToPage(new { id });
    }

    // ── Edit display name + email (local users only) ─────────────────────────

    public async Task<IActionResult> OnPostEditDetailsAsync(int id, string displayName, string email)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        if (!user.IsLocalUser) return Forbid();

        if (string.IsNullOrWhiteSpace(displayName))
        {
            TempData["Error"] = "Display name is required.";
            return RedirectToPage(new { id });
        }
        if (string.IsNullOrWhiteSpace(email))
        {
            TempData["Error"] = "Email is required.";
            return RedirectToPage(new { id });
        }

        // Email must be unique across all users
        var conflict = await db.AppUsers.AnyAsync(u => u.Id != id
            && u.Email != null
            && u.Email.ToLower() == email.Trim().ToLower());
        if (conflict)
        {
            TempData["Error"] = "That email address is already in use.";
            return RedirectToPage(new { id });
        }

        user.DisplayName = displayName.Trim();
        user.Email       = email.Trim();
        await db.SaveChangesAsync();
        TempData["Success"] = "Details updated.";
        return RedirectToPage(new { id });
    }

    // ── Set password (local users only) ──────────────────────────────────────

    public async Task<IActionResult> OnPostSetPasswordAsync(int id, string newPassword, string confirmPassword)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        if (!user.IsLocalUser) return Forbid();

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            TempData["Error"] = "Password must be at least 8 characters.";
            return RedirectToPage(new { id });
        }
        if (newPassword != confirmPassword)
        {
            TempData["Error"] = "Passwords do not match.";
            return RedirectToPage(new { id });
        }

        user.PasswordHash = PasswordHasher.Hash(newPassword);
        await audit.SaveChangesWithAuditAsync(db, Audit.User, "Password changed", $"User: {user.DisplayName}");
        TempData["Success"] = "Password updated.";
        return RedirectToPage(new { id });
    }

    // ── Clear password ────────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostClearPasswordAsync(int id)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        if (!user.IsLocalUser) return Forbid();

        user.PasswordHash = null;
        await db.SaveChangesAsync();
        TempData["Success"] = "Password cleared. The user cannot log in until a new password is set.";
        return RedirectToPage(new { id });
    }

    // ── TOTP / authenticator app setup ────────────────────────────────────────

    /// <summary>Generates a new TOTP secret and shows the QR code for scanning.</summary>
    public async Task<IActionResult> OnPostGenerateTotpAsync(int id)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        if (!user.IsLocalUser) return Forbid();

        var secret = Base32Encoding.ToString(KeyGeneration.GenerateRandomKey(20));
        var changed = await db.AppUsers.Where(u => u.Id == id && !u.TotpEnabled).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.TotpSecret, secret)
            .SetProperty(u => u.TotpLastUsedInterval, (long?)null)
            .SetProperty(u => u.TotpFailureCount, 0)
            .SetProperty(u => u.TotpFailureWindowEndsAt, (DateTime?)null));
        if (changed != 1)
        {
            TempData["Error"] = "Disable the existing authenticator before generating a new one.";
            return RedirectToPage(new { id });
        }
        TempData[$"PendingTotpSecret:{id}"] = secret;
        return RedirectToPage(new { id });
    }

    /// <summary>
    /// Verifies the first TOTP code against the pending secret.
    /// Saves the secret and enables TOTP only when the code is correct.
    /// </summary>
    public async Task<IActionResult> OnPostEnableTotpAsync(int id, string secret, string code)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        if (!user.IsLocalUser) return Forbid();

        if (string.IsNullOrWhiteSpace(secret) || secret != user.TotpSecret || user.TotpEnabled)
        {
            TempData["Error"] = "This setup is no longer current. Generate a new QR code.";
            return RedirectToPage(new { id });
        }

        var eligible = db.AppUsers.Where(u => u.Id == id && !u.TotpEnabled && u.TotpSecret == secret);
        var result = await TotpAttemptGuard.VerifyAsync(eligible, secret, code,
            nameof(Models.AppUser.TotpLastUsedInterval), set => set.SetProperty(u => u.TotpEnabled, true));
        if (result != TotpAttemptResult.Accepted)
        {
            // Return the QR code so the user can try again without regenerating.
            TempData[$"PendingTotpSecret:{id}"] = secret;
            TempData["Error"] = result == TotpAttemptResult.Locked
                ? "Too many failed attempts. Please wait 15 minutes before trying again."
                : "Code incorrect — please check the time on your device and try again.";
            return RedirectToPage(new { id });
        }

        await audit.LogAsync(Audit.User, "2FA enabled", $"User: {user.DisplayName}");
        TempData["Success"] = "Authenticator app enabled. Two-factor authentication is now active for this user.";
        return RedirectToPage(new { id });
    }

    /// <summary>Disables TOTP and clears the secret for this user.</summary>
    public async Task<IActionResult> OnPostDisableTotpAsync(int id)
    {
        var user = await db.AppUsers.FindAsync(id);
        if (user is null) return NotFound();
        if (!user.IsLocalUser) return Forbid();

        await db.AppUsers.Where(u => u.Id == id).ExecuteUpdateAsync(set => set
            .SetProperty(u => u.TotpSecret, (string?)null)
            .SetProperty(u => u.TotpEnabled, false));
        TempData.Remove($"PendingTotpSecret:{id}");
        await audit.LogAsync(Audit.User, "2FA disabled", $"User: {user.DisplayName}");
        TempData["Success"] = "Two-factor authentication disabled.";
        return RedirectToPage(new { id });
    }

    // ── QR code generation ────────────────────────────────────────────────────

    private static string BuildQrBase64(string secret, string email, string displayName)
    {
        var issuer  = "Valenius";
        var account = Uri.EscapeDataString(email);
        var uri     = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{account}" +
                      $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}" +
                      $"&algorithm=SHA1&digits=6&period=30";

        using var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        return Convert.ToBase64String(new PngByteQRCode(qrData).GetGraphic(6));
    }
}
