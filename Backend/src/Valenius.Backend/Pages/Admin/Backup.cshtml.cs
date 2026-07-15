using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;
using Valenius.Backend.Services;

namespace Valenius.Backend.Pages.Admin;

public class BackupModel(ApplicationDbContext db, IAuditService audit) : PageModel
{
    public int CustomerCount      { get; private set; }
    public int ClientCount        { get; private set; }
    public int UserCount          { get; private set; }
    public int ConnectionLogCount { get; private set; }

    [BindProperty] public bool       IncludeConnectionLogs         { get; set; } = true;
    [BindProperty] public bool       EncIncludeConnectionLogs      { get; set; } = true;
    [BindProperty] public IFormFile? BackupFile                    { get; set; }
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
        var serverSettings = await db.ServerSettings.AsNoTracking().FirstAsync();
        // Strip private key material — CA cert (public) is kept so re-enrollment can verify the trust chain.
        serverSettings.CaPrivateKeyPem = null;
        serverSettings.BackendCertPem  = null;
        serverSettings.BackendKeyPem   = null;

        var clients = await db.Clients.AsNoTracking().ToListAsync();
        foreach (var c in clients) c.WgPrivateKey = null;

        var backup = new BackupRoot
        {
            BackupVersion          = 1,
            CreatedAt              = DateTime.UtcNow,
            IncludesConnectionLogs = IncludeConnectionLogs,
            ServerSettings         = serverSettings,
            Customers              = await db.Customers.AsNoTracking().ToListAsync(),
            Clients                = clients,
            AppUsers               = await db.AppUsers.AsNoTracking().ToListAsync(),
            TrustedNetworks        = await db.TrustedNetworks.AsNoTracking().ToListAsync(),
            ConnectionLogs         = IncludeConnectionLogs
                ? await db.ConnectionLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync()
                : [],
        };

        var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, s_write));
        var name  = $"wt-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";

        // Stamp the last-backup timestamp (tracked live data — not included in the backup payload itself).
        var settings = await db.ServerSettings.FirstAsync();
        settings.LastBackupAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Audit.Backup, "Backup downloaded", $"File: {name}",
            $"Customers: {backup.Customers.Count}, Clients: {backup.Clients.Count}");
        Response.Headers["Cache-Control"] = "no-store";
        return File(bytes, "application/json", name);
    }

    // ── Encrypted download (returns JSON consumed by page JS) ─────────────────

    public async Task<IActionResult> OnPostDownloadEncryptedAsync()
    {
        // Include ALL private key material — this is the full disaster-recovery backup.
        var serverSettings = await db.ServerSettings.AsNoTracking().FirstAsync();
        var clients        = await db.Clients.AsNoTracking().ToListAsync();

        var backup = new BackupRoot
        {
            BackupVersion          = 1,
            CreatedAt              = DateTime.UtcNow,
            IncludesConnectionLogs = EncIncludeConnectionLogs,
            ServerSettings         = serverSettings,
            Customers              = await db.Customers.AsNoTracking().ToListAsync(),
            Clients                = clients,
            AppUsers               = await db.AppUsers.AsNoTracking().ToListAsync(),
            TrustedNetworks        = await db.TrustedNetworks.AsNoTracking().ToListAsync(),
            ConnectionLogs         = EncIncludeConnectionLogs
                ? await db.ConnectionLogs.AsNoTracking().OrderBy(l => l.Id).ToListAsync()
                : [],
        };

        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(backup, s_write));
        var (key, displayKey) = BackupEncryption.GenerateKey();
        var encrypted  = BackupEncryption.Encrypt(key, plaintext);
        var filename   = $"valenius-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.enc";

        var settings = await db.ServerSettings.FirstAsync();
        settings.LastBackupAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        _ = audit.LogAsync(Audit.Backup, "Encrypted backup downloaded", $"File: {filename}",
            $"Customers: {backup.Customers.Count}, Clients: {backup.Clients.Count}");

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

        BackupRoot backup;
        try
        {
            backup = JsonSerializer.Deserialize<BackupRoot>(plaintext, s_read)
                     ?? throw new InvalidOperationException("Decrypted content is empty.");
            if (backup.ServerSettings is null)
                throw new InvalidOperationException("Missing ServerSettings block in backup.");
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to parse decrypted backup: {ex.Message}";
            await OnGetAsync();
            return Page();
        }

        if (backup.BackupVersion != 1)
        {
            TempData["Error"] = $"Unsupported backup version {backup.BackupVersion}.";
            await OnGetAsync();
            return Page();
        }

        try
        {
            await RestoreEncryptedAsync(backup);
            var logInfo = backup.IncludesConnectionLogs ? $", {backup.ConnectionLogs.Count} connection logs" : "";
            var msg = $"Encrypted restore complete: {backup.Customers.Count} customers, " +
                      $"{backup.Clients.Count} clients, {backup.AppUsers.Count} users{logInfo}. " +
                      "Restart the backend service to reload the CA certificate material.";
            _ = audit.LogAsync(Audit.Backup, "Encrypted restore completed", "Encrypted backup restore", msg);
            TempData["Success"] = msg;
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Restore failed — database was not changed. {ex.Message}";
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRestoreAsync()
    {
        if (BackupFile is null || BackupFile.Length == 0)
        {
            TempData["Error"] = "No backup file selected.";
            await OnGetAsync();
            return Page();
        }

        BackupRoot backup;
        try
        {
            using var stream = BackupFile.OpenReadStream();
            backup = await JsonSerializer.DeserializeAsync<BackupRoot>(stream, s_read)
                     ?? throw new InvalidOperationException("File is empty.");

            if (backup.ServerSettings is null)
                throw new InvalidOperationException("Missing ServerSettings block in backup file.");
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Failed to parse backup: {ex.Message}";
            await OnGetAsync();
            return Page();
        }

        if (backup.BackupVersion != 1)
        {
            TempData["Error"] = $"Unsupported backup version {backup.BackupVersion}.";
            await OnGetAsync();
            return Page();
        }

        try
        {
            await RestoreAsync(backup);
            var logInfo = backup.IncludesConnectionLogs
                ? $", {backup.ConnectionLogs.Count} connection logs"
                : "";
            var restoreMsg = $"Restore complete: {backup.Customers.Count} customers, " +
                             $"{backup.Clients.Count} clients, {backup.AppUsers.Count} users{logInfo}.";
            _ = audit.LogAsync(Audit.Backup, "Restore completed", "Backup restore", restoreMsg);
            TempData["Success"] = restoreMsg;
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Restore failed — database was not changed. {ex.Message}";
        }

        return RedirectToPage();
    }

    private async Task RestoreAsync(BackupRoot b)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        // Delete in reverse FK dependency order so constraints are not violated
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "ConnectionLogs" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "TrustedNetworks" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "AppUsers" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Clients" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Customers" """);

        foreach (var c in b.Customers)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Customers"
                    ("Id","Name","Notes","ClientPrefix","HeartbeatIntervalMinutes",
                     "ServerMode","SidecarUrl","SidecarManagementPort",
                     "SidecarVpnIp","EnrollmentToken","ServerEndpoint",
                     "VpnPrefix","DnsServer","AllowedIPs","AllowExternalConfig",
                     "MaxEndpoints")
                VALUES
                    ({c.Id},{c.Name},{c.Notes},{c.ClientPrefix},{c.HeartbeatIntervalMinutes},
                     {c.ServerMode},{c.SidecarUrl},{c.SidecarManagementPort},
                     {c.SidecarVpnIp},{c.EnrollmentToken},{c.ServerEndpoint},
                     {c.VpnPrefix},{c.DnsServer},{c.AllowedIPs},{c.AllowExternalConfig},
                     {c.MaxEndpoints})
                """);

        foreach (var c in b.Clients)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Clients"
                    ("Id","ClientKey","Hostname","HostSid","Username","UserSid",
                     "RegisteredAt","LastSeenAt","IsActive","IsDeleted","ClientVersion","Notes",
                     "PendingConfigFileName","PendingConfigContent","AutoConnectEnabled",
                     "AutoConnectProfileName","KnownProfiles","PendingDeleteProfileName","PendingConnect",
                     "WgPublicKey","WgPrivateKey","VpnIpAddress","AllowedIPs","DnsServer","CustomerId")
                VALUES
                    ({c.Id},{c.ClientKey},{c.Hostname},{c.HostSid},{c.Username},{c.UserSid},
                     {Utc(c.RegisteredAt)},{Utc(c.LastSeenAt)},{c.IsActive},{c.IsDeleted},{c.ClientVersion},{c.Notes},
                     {c.PendingConfigFileName},{c.PendingConfigContent},{c.AutoConnectEnabled},
                     {c.AutoConnectProfileName},{c.KnownProfiles},{c.PendingDeleteProfileName},{c.PendingConnect},
                     {c.WgPublicKey},{c.WgPrivateKey},{c.VpnIpAddress},{c.AllowedIPs},{c.DnsServer},{c.CustomerId})
                """);

        foreach (var u in b.AppUsers)
            // PasswordHash and TotpSecret are intentionally not restored — users must
            // re-set their passwords after a restore to prevent backdoor credential injection.
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AppUsers"
                    ("Id","Subject","DisplayName","Email","Role","CustomerId","PasswordHash",
                     "TotpSecret","TotpEnabled")
                VALUES
                    ({u.Id},{u.Subject},{u.DisplayName},{u.Email},{u.Role},{u.CustomerId},NULL,
                     NULL,FALSE)
                """);

        foreach (var t in b.TrustedNetworks)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "TrustedNetworks"
                    ("Id","CustomerId","Subnet","PrimaryDns")
                VALUES
                    ({t.Id},{t.CustomerId},{t.Subnet},{t.PrimaryDns})
                """);

        foreach (var l in b.ConnectionLogs)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ConnectionLogs"
                    ("Id","ClientId","Username","TunnelName","EventType","LanIp","WanIp","OccurredAt")
                VALUES
                    ({l.Id},{l.ClientId},{l.Username},{l.TunnelName},{l.EventType},{l.LanIp},{l.WanIp},{Utc(l.OccurredAt)})
                """);

        var s = b.ServerSettings;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "ServerSettings"
            SET "OidcEnabled"      = {s.OidcEnabled},
                "LocalAuthEnabled" = {s.LocalAuthEnabled},
                "OidcAuthority"    = {s.OidcAuthority},
                "OidcClientId"     = {s.OidcClientId},
                "OidcClientSecret" = {s.OidcClientSecret},
                "OidcRedirectUri"  = {s.OidcRedirectUri}
            WHERE "Id" = 1
            """);

        // Reset SERIAL sequences so future inserts don't collide with restored IDs.
        // Table names are hardcoded constants — no SQL injection risk.
#pragma warning disable EF1003
        foreach (var tbl in new[] { "Customers", "Clients", "AppUsers", "TrustedNetworks", "ConnectionLogs" })
            await db.Database.ExecuteSqlRawAsync(
                "SELECT setval(pg_get_serial_sequence('\"" + tbl + "\"', 'Id'), COALESCE((SELECT MAX(\"Id\") FROM \"" + tbl + "\"), 0) + 1, false)");
#pragma warning restore EF1003

        await tx.CommitAsync();
    }

    private async Task RestoreEncryptedAsync(BackupRoot b)
    {
        await using var tx = await db.Database.BeginTransactionAsync();

        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "ConnectionLogs" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "TrustedNetworks" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "AppUsers" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Clients" """);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Customers" """);

        foreach (var c in b.Customers)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Customers"
                    ("Id","Name","Notes","ClientPrefix","HeartbeatIntervalMinutes",
                     "ServerMode","SidecarUrl","SidecarManagementPort",
                     "SidecarVpnIp","EnrollmentToken","ServerEndpoint",
                     "VpnPrefix","DnsServer","AllowedIPs","AllowExternalConfig",
                     "MaxEndpoints")
                VALUES
                    ({c.Id},{c.Name},{c.Notes},{c.ClientPrefix},{c.HeartbeatIntervalMinutes},
                     {c.ServerMode},{c.SidecarUrl},{c.SidecarManagementPort},
                     {c.SidecarVpnIp},{c.EnrollmentToken},{c.ServerEndpoint},
                     {c.VpnPrefix},{c.DnsServer},{c.AllowedIPs},{c.AllowExternalConfig},
                     {c.MaxEndpoints})
                """);

        foreach (var c in b.Clients)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "Clients"
                    ("Id","ClientKey","Hostname","HostSid","Username","UserSid",
                     "RegisteredAt","LastSeenAt","IsActive","IsDeleted","ClientVersion","Notes",
                     "PendingConfigFileName","PendingConfigContent","AutoConnectEnabled",
                     "AutoConnectProfileName","KnownProfiles","PendingDeleteProfileName","PendingConnect",
                     "WgPublicKey","WgPrivateKey","VpnIpAddress","AllowedIPs","DnsServer","CustomerId")
                VALUES
                    ({c.Id},{c.ClientKey},{c.Hostname},{c.HostSid},{c.Username},{c.UserSid},
                     {Utc(c.RegisteredAt)},{Utc(c.LastSeenAt)},{c.IsActive},{c.IsDeleted},{c.ClientVersion},{c.Notes},
                     {c.PendingConfigFileName},{c.PendingConfigContent},{c.AutoConnectEnabled},
                     {c.AutoConnectProfileName},{c.KnownProfiles},{c.PendingDeleteProfileName},{c.PendingConnect},
                     {c.WgPublicKey},{c.WgPrivateKey},{c.VpnIpAddress},{c.AllowedIPs},{c.DnsServer},{c.CustomerId})
                """);

        // Encrypted backup is a trusted source — restore credentials as-is.
        foreach (var u in b.AppUsers)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "AppUsers"
                    ("Id","Subject","DisplayName","Email","Role","CustomerId","PasswordHash",
                     "TotpSecret","TotpEnabled")
                VALUES
                    ({u.Id},{u.Subject},{u.DisplayName},{u.Email},{u.Role},{u.CustomerId},{u.PasswordHash},
                     {u.TotpSecret},{u.TotpEnabled})
                """);

        foreach (var t in b.TrustedNetworks)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "TrustedNetworks"
                    ("Id","CustomerId","Subnet","PrimaryDns")
                VALUES
                    ({t.Id},{t.CustomerId},{t.Subnet},{t.PrimaryDns})
                """);

        foreach (var l in b.ConnectionLogs)
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO "ConnectionLogs"
                    ("Id","ClientId","Username","TunnelName","EventType","LanIp","WanIp","OccurredAt")
                VALUES
                    ({l.Id},{l.ClientId},{l.Username},{l.TunnelName},{l.EventType},{l.LanIp},{l.WanIp},{Utc(l.OccurredAt)})
                """);

        // Restore full ServerSettings including CA/mTLS key material.
        var s = b.ServerSettings;
        await db.Database.ExecuteSqlAsync($"""
            UPDATE "ServerSettings"
            SET "OidcEnabled"      = {s.OidcEnabled},
                "LocalAuthEnabled" = {s.LocalAuthEnabled},
                "OidcAuthority"    = {s.OidcAuthority},
                "OidcClientId"     = {s.OidcClientId},
                "OidcClientSecret" = {s.OidcClientSecret},
                "OidcRedirectUri"  = {s.OidcRedirectUri},
                "CaCertPem"        = {s.CaCertPem},
                "CaPrivateKeyPem"  = {s.CaPrivateKeyPem},
                "BackendCertPem"   = {s.BackendCertPem},
                "BackendKeyPem"    = {s.BackendKeyPem}
            WHERE "Id" = 1
            """);

#pragma warning disable EF1003
        foreach (var tbl in new[] { "Customers", "Clients", "AppUsers", "TrustedNetworks", "ConnectionLogs" })
            await db.Database.ExecuteSqlRawAsync(
                "SELECT setval(pg_get_serial_sequence('\"" + tbl + "\"', 'Id'), COALESCE((SELECT MAX(\"Id\") FROM \"" + tbl + "\"), 0) + 1, false)");
#pragma warning restore EF1003

        await tx.CommitAsync();
    }

    // JSON deserialisation produces Kind=Unspecified; Npgsql requires Kind=Utc for timestamptz.
    private static DateTime  Utc(DateTime  dt) => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
    private static DateTime? Utc(DateTime? dt) => dt is null ? null : Utc(dt.Value);
}

public sealed class BackupRoot
{
    public int                  BackupVersion          { get; init; }
    public DateTime             CreatedAt              { get; init; }
    public bool                 IncludesConnectionLogs { get; init; }
    public ServerSettings       ServerSettings         { get; init; } = null!;
    public List<Customer>       Customers              { get; init; } = [];
    public List<Client>         Clients                { get; init; } = [];
    public List<AppUser>        AppUsers               { get; init; } = [];
    public List<TrustedNetwork> TrustedNetworks        { get; init; } = [];
    public List<ConnectionLog>  ConnectionLogs         { get; init; } = [];
}
