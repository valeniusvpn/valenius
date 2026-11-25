using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class BackupModel(ApplicationDbContext db, IAuditService audit,
    DatabaseBackupService backups, ApiKeyStore apiKeys, HeartbeatSettingsStore heartbeat) : PageModel
{
    public int CustomerCount      { get; private set; }
    public int ClientCount        { get; private set; }
    public int UserCount          { get; private set; }
    public int ConnectionLogCount { get; private set; }

    [BindProperty] public bool       IncludeConnectionLogs         { get; set; }
    [BindProperty] public bool       EncIncludeConnectionLogs      { get; set; } = true;
    [BindProperty] public IFormFile? EncryptedFile                 { get; set; }
    [BindProperty] public string     EncryptedPassphrase           { get; set; } = "";

    private static readonly JsonSerializerOptions s_write = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions s_read  = new() { PropertyNameCaseInsensitive = true };

    public async Task OnGetAsync()
    {
        CustomerCount      = await db.Customers.CountAsync();
        ClientCount        = await db.Clients.CountAsync();
        UserCount          = await db.AppUsers.CountAsync();
        ConnectionLogCount = await db.ConnectionLogs.CountAsync();
    }

    public async Task<IActionResult> OnPostDownloadAsync()
    {
        var inventory = await RedactedInventoryExporter.ExportAsync(db, IncludeConnectionLogs);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(inventory, s_write));
        var name = $"valenius-redacted-inventory-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

        // An inventory is not a recovery backup: do not advance LastBackupAt.
        await audit.LogRequiredAsync(Audit.Backup, "Redacted inventory downloaded", $"File: {name}",
            $"Customers: {inventory.Customers.Count}, Clients: {inventory.Clients.Count}");
        Response.Headers["Cache-Control"] = "no-store";
        return File(bytes, "application/json", name);
    }

    // ── Encrypted download (returns JSON consumed by page JS) ─────────────────

    public async Task<IActionResult> OnPostDownloadEncryptedAsync()
    {
        var backup = await backups.CaptureAsync(EncIncludeConnectionLogs);

        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, s_write));
        var (key, displayKey) = BackupEncryption.GenerateKey();
        var encrypted  = BackupEncryption.Encrypt(key, plaintext);
        var filename   = $"valenius-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.enc";

        var settings = await db.ServerSettings.FirstAsync();
        settings.LastBackupAt = DateTime.UtcNow;
        await audit.SaveChangesWithAuditAsync(db, Audit.Backup, "Encrypted backup downloaded", $"File: {filename}",
            $"Tables: {backup.Tables.Count}");

        Response.Headers["Cache-Control"] = "no-store";
        return new ContentResult
        {
            Content     = JsonSerializer.Serialize(new
            {
                passphrase = displayKey,
                filename,
                data = Convert.ToBase64String(encrypted),
            }),
            ContentType = "application/json",
            StatusCode  = 200,
        };
    }

    // ── Encrypted restore ─────────────────────────────────────────────────────

    public async Task<IActionResult> OnPostRestoreEncryptedAsync()
    {
        if (EncryptedFile is null || EncryptedFile.Length == 0)
        {
            TempData["Error"] = "No backup file selected.";
            await OnGetAsync();
            return Page();
        }
        if (string.IsNullOrWhiteSpace(EncryptedPassphrase))
        {
            TempData["Error"] = "Passphrase is required to decrypt the backup.";
            await OnGetAsync();
            return Page();
        }

        byte[] encryptedBytes;
        using (var ms = new MemoryStream((int)EncryptedFile.Length))
        {
            await EncryptedFile.OpenReadStream().CopyToAsync(ms);
            encryptedBytes = ms.ToArray();
        }

        byte[] plaintext;
        try
        {
            var key = BackupEncryption.ParseDisplayKey(EncryptedPassphrase);
            plaintext = BackupEncryption.Decrypt(key, encryptedBytes);
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Decryption failed — wrong passphrase or corrupt file. ({ex.Message})";
            await OnGetAsync();
            return Page();
        }

        DatabaseBackup backup;
        try
        {
            backup = JsonSerializer.Deserialize<DatabaseBackup>(plaintext, s_read)
                     ?? throw new InvalidOperationException("Decrypted content is empty.");
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to parse decrypted backup: {ex.Message}";
            await OnGetAsync();
            return Page();
        }

        try
        {
            await backups.RestoreAsync(backup);
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Restore could not be confirmed. Check database state and server logs before retrying. {ex.Message}";
            return RedirectToPage();
        }

        // These hot-path caches can be refreshed immediately. OIDC and CA services
        // still require the operator's mandatory backend restart.
        var restoredSettings = await db.ServerSettings.AsNoTracking().SingleAsync(s => s.Id == 1);
        apiKeys.Key = restoredSettings.ApiKey ?? "";
        apiKeys.PreviousKey = restoredSettings.PreviousApiKey;
        heartbeat.DefaultIntervalMinutes = restoredSettings.DefaultHeartbeatIntervalMinutes is >= 1 and <= 60
            ? restoredSettings.DefaultHeartbeatIntervalMinutes : 1;
        const string message = "Encrypted database restore complete. Restart the backend before resuming operations " +
            "to reload authentication and CA settings. Temporary MFA grants, approval challenges and enrollment " +
            "codes were invalidated. Existing sidecar grants remain subject to their own expiry. " +
            "Restore release files and sidecar volumes separately.";
        await audit.LogAsync(Audit.Backup, "Encrypted restore completed", "Encrypted backup restore", message);
        TempData["Success"] = message;

        return RedirectToPage();
    }

    // Retain the handler so old forms/direct requests fail without deleting anything.
    public IActionResult OnPostRestore()
    {
        TempData["Error"] = "Legacy JSON backups are incomplete and cannot be restored automatically. " +
            "Use a compatible version 2 encrypted backup or a PostgreSQL database backup. " +
            "Keep older files for operator-assisted recovery.";
        return RedirectToPage();
    }
}
