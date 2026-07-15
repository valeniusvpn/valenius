using System.Security.Cryptography;
using System.Text.Json;
using Valenius.Shared;

namespace Valenius.Service;

/// <summary>
/// Manages WireGuard .conf files for all users.
/// All configs are stored encrypted as .conf.enc via DPAPI (LocalMachine scope) so the
/// WireGuard private key cannot be extracted if the drive is removed.
/// On connect, <see cref="PrepareConfigPath"/> decrypts to a temp .conf in <see cref="TempDir"/>
/// for wireguard.exe; the temp file is deleted on disconnect.
/// </summary>
public class ConfigManager
{
    private static readonly string BaseDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Valenius", "users");

    private static readonly string StagedDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Valenius", "staged");

    public static readonly string TempDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Valenius", "temp");

    private const string EncExt  = ".conf.enc";
    private const string PlainExt = ".conf";

    public ConfigManager()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(StagedDir);
        InitTempDir();
        MigratePlainConfigs();
    }

    // ── Profile enumeration ───────────────────────────────────────────────────

    public string[] GetProfiles(string username)
    {
        var userDir = GetUserDir(username);
        if (!Directory.Exists(userDir)) return [];
        return Directory.EnumerateFiles(userDir)
            .Where(IsConfigFile)
            .Select(ProfileNameFromPath)
            .OfType<string>()
            .OrderBy(n => n)
            .ToArray();
    }

    /// <summary>
    /// Union of all profile names across every user directory AND the staged directory.
    /// Staged profiles are included so the backend does not prematurely clear
    /// PendingDeleteProfileName before the TrayApp has had a chance to claim them.
    /// </summary>
    public string[] GetAllProfiles()
    {
        var files = new List<string>();
        if (Directory.Exists(BaseDir))
            files.AddRange(Directory.EnumerateDirectories(BaseDir)
                .SelectMany(dir => Directory.EnumerateFiles(dir).Where(IsConfigFile)));
        if (Directory.Exists(StagedDir))
            files.AddRange(Directory.EnumerateFiles(StagedDir).Where(IsConfigFile));
        return files.Select(ProfileNameFromPath).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // ── Config path resolution ────────────────────────────────────────────────

    /// <summary>Returns the stored path (.conf or .conf.enc) for a profile, or null if not found.</summary>
    public string? GetConfigPath(string username, string profileName)
    {
        var userDir = GetUserDir(username);
        var enc   = Path.Combine(userDir, profileName + EncExt);
        var plain = Path.Combine(userDir, profileName + PlainExt);
        if (File.Exists(enc))   return enc;
        if (File.Exists(plain)) return plain;
        return null;
    }

    /// <summary>Returns the first config path found for a user (.conf.enc preferred).</summary>
    public string? GetFirstConfigPath(string username)
    {
        var userDir = GetUserDir(username);
        if (!Directory.Exists(userDir)) return null;
        return Directory.EnumerateFiles(userDir).Where(IsConfigFile).FirstOrDefault();
    }

    /// <summary>
    /// Searches all user directories for a profile by name, or returns the first config found.
    /// Returns the stored path (.conf or .conf.enc).
    /// </summary>
    public string? GetAnyConfigPath(string? profileName)
    {
        if (!Directory.Exists(BaseDir)) return null;
        foreach (var userDir in Directory.EnumerateDirectories(BaseDir))
        {
            if (profileName is not null)
            {
                var enc   = Path.Combine(userDir, profileName + EncExt);
                var plain = Path.Combine(userDir, profileName + PlainExt);
                if (File.Exists(enc))   return enc;
                if (File.Exists(plain)) return plain;
            }
            else
            {
                var first = Directory.EnumerateFiles(userDir).Where(IsConfigFile).FirstOrDefault();
                if (first is not null) return first;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the WireGuard tunnel name (profile name without any extension) for an
    /// arbitrary stored config path, handling both .conf and .conf.enc correctly.
    /// </summary>
    public static string? GetTunnelNameFromPath(string path) => ProfileNameFromPath(path);

    public string? GetTunnelName(string username, string? profileName = null)
    {
        var path = profileName is not null
            ? GetConfigPath(username, profileName)
            : GetFirstConfigPath(username);
        return path is null ? null : ProfileNameFromPath(path);
    }

    public bool HasConfig(string username) => GetFirstConfigPath(username) is not null;

    // ── Prepare for WireGuard (decrypt if needed) ─────────────────────────────

    /// <summary>
    /// Given a stored config path (.conf or .conf.enc), returns the path wireguard.exe
    /// can use.  For encrypted files, decrypts to <see cref="TempDir"/> and returns the
    /// temp path.  The plaintext temp file is cleaned up by
    /// <see cref="CleanupTempConfig"/> after disconnect.
    /// </summary>
    public string PrepareConfigPath(string storedPath)
    {
        if (!storedPath.EndsWith(EncExt, StringComparison.OrdinalIgnoreCase))
            return storedPath;   // plaintext — no action needed

        var profileName = ProfileNameFromPath(storedPath)
            ?? throw new InvalidOperationException($"Cannot derive profile name from: {storedPath}");

        return DecryptToTemp(profileName, storedPath);
    }

    /// <summary>
    /// Reads the AllowedIPs CIDRs from a stored config (.conf or .conf.enc).
    /// Encrypted configs are decrypted in-memory — no temp file is written, so this
    /// does not interfere with the temp file that <see cref="PrepareConfigPath"/> creates
    /// for wireguard.exe. Returns every comma-separated entry across all [Peer] AllowedIPs
    /// lines, trimmed. Returns an empty array on any failure (fail-open for callers).
    /// </summary>
    public string[] ReadAllowedIPs(string storedPath)
    {
        try
        {
            string content;
            if (storedPath.EndsWith(EncExt, StringComparison.OrdinalIgnoreCase))
            {
                var encBytes   = File.ReadAllBytes(storedPath);
                var plainBytes = ProtectedData.Unprotect(encBytes, null, DataProtectionScope.LocalMachine);
                content = System.Text.Encoding.UTF8.GetString(plainBytes);
            }
            else
            {
                content = File.ReadAllText(storedPath);
            }

            var result = new List<string>();
            foreach (var raw in content.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("AllowedIPs", StringComparison.OrdinalIgnoreCase)) continue;
                var eq = line.IndexOf('=');
                if (eq < 0) continue;
                foreach (var part in line[(eq + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Add(part);
            }
            return result.ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Deletes the temp plaintext file(s) for <paramref name="tunnelName"/> after disconnect.
    /// Checks every per-connect subdirectory (plus the legacy flat path from older versions)
    /// and prunes emptied subdirectories. All best-effort: anything left behind is swept by
    /// <see cref="InitTempDir"/> on the next service start.
    /// </summary>
    public void CleanupTempConfig(string tunnelName)
    {
        var fileName = tunnelName + PlainExt;
        try { if (File.Exists(Path.Combine(TempDir, fileName))) File.Delete(Path.Combine(TempDir, fileName)); }
        catch { /* best-effort */ }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(TempDir))
            {
                var path = Path.Combine(dir, fileName);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                // Non-recursive: only removes the dir once empty, so a subdirectory still
                // holding another connected tunnel's config is left alone.
                try { Directory.Delete(dir); } catch { }
            }
        }
        catch { /* best-effort */ }
    }

    // ── Write / save ──────────────────────────────────────────────────────────

    public void SaveConfig(string username, string profileName, string content)
    {
        var userDir = GetUserDir(username);
        Directory.CreateDirectory(userDir);
        var encPath  = Path.Combine(userDir, profileName + EncExt);
        var oldPlain = Path.Combine(userDir, profileName + PlainExt);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(content);
        var encBytes   = ProtectedData.Protect(plainBytes, null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(encPath, encBytes);
        if (File.Exists(oldPlain))
            try { File.Delete(oldPlain); } catch { }
    }

    public void SaveStagedConfig(string profileName, string content)
    {
        Directory.CreateDirectory(StagedDir);
        var path = Path.Combine(StagedDir, profileName + PlainExt);
        File.WriteAllText(path, content);
        RestrictToSystem(path);
    }

    // ── Staged config lifecycle ───────────────────────────────────────────────

    public bool HasStagedConfig() =>
        Directory.Exists(StagedDir) &&
        Directory.EnumerateFiles(StagedDir, "*" + PlainExt).Any();

    public string[] GetStagedProfileNames()
    {
        if (!Directory.Exists(StagedDir)) return [];
        return Directory.EnumerateFiles(StagedDir, "*" + PlainExt)
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .ToArray();
    }

    /// <summary>Moves staged configs to the user's config directory, encrypted.</summary>
    public void ClaimStagedConfig(string username)
    {
        if (!Directory.Exists(StagedDir)) return;

        foreach (var stagedPath in Directory.EnumerateFiles(StagedDir, "*" + PlainExt).ToList())
        {
            try
            {
                var profileName = Path.GetFileNameWithoutExtension(stagedPath);
                if (profileName is null || !ProfileNameHelper.IsValid(profileName)) continue;

                var content = File.ReadAllText(stagedPath);
                SaveConfig(username, profileName, content);
                File.Delete(stagedPath);
                MarkAsManaged(username, profileName);
            }
            catch { /* leave staged file for next attempt */ }
        }
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    public void DeleteProfile(string profileName)
    {
        if (Directory.Exists(BaseDir))
        {
            foreach (var userDir in Directory.EnumerateDirectories(BaseDir))
            {
                foreach (var ext in new[] { PlainExt, EncExt })
                {
                    var path = Path.Combine(userDir, profileName + ext);
                    if (!File.Exists(path)) continue;
                    try { File.Delete(path); } catch { }
                }
            }
        }
        // Also remove any staged copy that was never claimed by the TrayApp,
        // otherwise the TrayApp would re-claim it after deletion.
        if (Directory.Exists(StagedDir))
        {
            var staged = Path.Combine(StagedDir, profileName + PlainExt);
            if (File.Exists(staged))
                try { File.Delete(staged); } catch { }
        }
    }

    public bool DeleteUserProfile(string username, string profileName)
    {
        if (IsManaged(username, profileName)) return false;
        bool deleted = false;
        foreach (var ext in new[] { PlainExt, EncExt })
        {
            var path = Path.Combine(GetUserDir(username), profileName + ext);
            if (!File.Exists(path)) continue;
            try { File.Delete(path); deleted = true; }
            catch { }
        }
        return deleted;
    }

    // ── Managed profile tracking ──────────────────────────────────────────────

    public string[] GetDeletableProfiles(string username)
    {
        var profiles = GetProfiles(username);
        if (profiles.Length == 0) return [];
        var managed = LoadManaged(username);
        return profiles.Where(p => !managed.Contains(p)).ToArray();
    }

    public bool IsManaged(string username, string profileName) =>
        LoadManaged(username).Contains(profileName);

    private void MarkAsManaged(string username, string profileName)
    {
        var managed = LoadManaged(username);
        if (!managed.Add(profileName)) return;
        SaveManaged(username, managed);
    }

    private static HashSet<string> LoadManaged(string username)
    {
        var path = ManagedPath(GetUserDir(username));
        if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var list = JsonSerializer.Deserialize(File.ReadAllText(path), ValeniusJsonContext.Default.StringArray);
            return new HashSet<string>(list ?? [], StringComparer.OrdinalIgnoreCase);
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveManaged(string username, HashSet<string> managed)
    {
        var userDir = GetUserDir(username);
        Directory.CreateDirectory(userDir);
        File.WriteAllText(ManagedPath(userDir),
            JsonSerializer.Serialize(managed.ToArray(), ValeniusJsonContext.Default.StringArray));
    }

    private static string ManagedPath(string userDir) => Path.Combine(userDir, "managed.json");

    // ── Encryption helpers ────────────────────────────────────────────────────

    private string DecryptToTemp(string profileName, string encPath)
    {
        var encBytes   = File.ReadAllBytes(encPath);
        var plainBytes = ProtectedData.Unprotect(encBytes, null, DataProtectionScope.LocalMachine);

        // Each connect writes into a fresh unique subdirectory rather than a fixed
        // temp\<profile>.conf. A leftover file at a fixed path can be held open by an AV
        // scanner or carry a hostile/empty DACL that even SYSTEM cannot delete or
        // overwrite (same failure mode as the old fixed-name run-update.cmd), which made
        // every reconnect fail with "access to the path ... is denied" until reboot.
        // The file NAME must remain <profile>.conf — wireguard.exe derives the tunnel
        // name from the basename — so only the directory varies.
        var tempPath = Path.Combine(TempDir, Path.GetRandomFileName(), profileName + PlainExt);
        StateFileWriter.WriteAllBytes(tempPath, plainBytes);
        return tempPath;
    }

    /// <summary>
    /// Ensures TempDir exists and is empty at service startup. If deleting the directory
    /// fails (a stale file from an old service version may have a hostile DACL that blocks
    /// delete), the whole directory is renamed out of the way — a rename only requires
    /// write access to the parent, not to the files inside. The abandoned directory is
    /// cleaned by the next installer run's icacls sweep.
    /// </summary>
    private static void InitTempDir()
    {
        if (Directory.Exists(TempDir))
        {
            try
            {
                Directory.Delete(TempDir, recursive: true);
            }
            catch
            {
                try { Directory.Move(TempDir, TempDir + "." + Environment.TickCount64); }
                catch { /* fallback: reuse the existing dir; ConnectAsync will overwrite files */ }
            }
        }
        Directory.CreateDirectory(TempDir);
    }

    // ── File-system helpers ───────────────────────────────────────────────────

    private static bool IsConfigFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(PlainExt,  StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(EncExt,    StringComparison.OrdinalIgnoreCase);
    }

    private static string? ProfileNameFromPath(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(EncExt, StringComparison.OrdinalIgnoreCase))
            return name[..^EncExt.Length];
        if (name.EndsWith(PlainExt, StringComparison.OrdinalIgnoreCase))
            return name[..^PlainExt.Length];
        return null;
    }

    /// <summary>Encrypts any plain .conf files left by older versions that did not encrypt all profiles.</summary>
    private static void MigratePlainConfigs()
    {
        if (!Directory.Exists(BaseDir)) return;
        foreach (var userDir in Directory.EnumerateDirectories(BaseDir))
        {
            foreach (var plainPath in Directory.EnumerateFiles(userDir)
                .Where(p => p.EndsWith(PlainExt, StringComparison.OrdinalIgnoreCase))
                .ToList())
            {
                try
                {
                    var profileName = ProfileNameFromPath(plainPath);
                    if (profileName is null || !ProfileNameHelper.IsValid(profileName)) continue;

                    var encPath = Path.Combine(userDir, profileName + EncExt);
                    if (File.Exists(encPath))
                    {
                        // Already has encrypted version — just remove the plain copy.
                        File.Delete(plainPath);
                        continue;
                    }

                    var content   = File.ReadAllText(plainPath);
                    var rawBytes  = System.Text.Encoding.UTF8.GetBytes(content);
                    var encBytes  = ProtectedData.Protect(rawBytes, null, DataProtectionScope.LocalMachine);
                    File.WriteAllBytes(encPath, encBytes);
                    File.Delete(plainPath);
                }
                catch { /* skip file; plain copy stays and will be retried next start */ }
            }
        }
    }

    private static void RestrictToSystem(string path)
    {
        // No-op. SetAccessRuleProtection(true, false) in .NET 10 can produce an empty DACL
        // that denies everyone including SYSTEM. Security is handled by the folder-level
        // SYSTEM:(OI)(CI)F ACL set by install-service.ps1, plus DPAPI encryption at rest.
        _ = path;
    }

    private static string GetUserDir(string username)
    {
        var safePart = username.Contains('\\') ? username[(username.LastIndexOf('\\') + 1)..] : username;
        safePart = SanitizeFileName(safePart);
        return Path.Combine(BaseDir, safePart);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
