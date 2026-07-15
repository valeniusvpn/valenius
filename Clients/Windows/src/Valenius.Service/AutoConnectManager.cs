using System.Text.Json;

namespace Valenius.Service;

/// <summary>
/// Loads, persists, and exposes the auto-connect configuration.
/// Thread-safe via SemaphoreSlim.
/// </summary>
public class AutoConnectManager
{
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Valenius", "autoconnect.json");

    private readonly SemaphoreSlim _lock = new(1, 1);

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsEnabled()
    {
        var cfg = Load();
        return cfg?.Enabled ?? false;
    }

    public TrustedNetworkEntry[] GetTrustedNetworks()
    {
        var cfg = Load();
        return cfg?.TrustedNetworks ?? [];
    }

    public string? GetProfileName()
    {
        var cfg = Load();
        return cfg?.ProfileName;
    }

    /// <summary>
    /// Called when the user toggles the tray menu item.
    /// </summary>
    public async Task SetUserEnabledAsync(bool enabled)
    {
        await _lock.WaitAsync();
        try
        {
            var cfg = Load() ?? new AutoConnectConfig();
            cfg.Enabled = enabled;
            Save(cfg);
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Called on each backend heartbeat response.
    /// The backend value is always authoritative: it overwrites the local Enabled flag
    /// so the admin can enable or disable auto-connect from the backend at any time.
    /// The user's tray toggle (<see cref="SetUserEnabledAsync"/>) works immediately but
    /// is overwritten on the next heartbeat (~5 min) by whatever the admin has set.
    /// </summary>
    public async Task UpdateFromBackendAsync(
        bool adminEnabled,
        string? profileName,
        TrustedNetworkEntry[] trustedNetworks)
    {
        await _lock.WaitAsync();
        try
        {
            var cfg = Load() ?? new AutoConnectConfig();
            cfg.AdminEnabled    = adminEnabled;
            cfg.Enabled         = adminEnabled;   // admin is always authoritative
            cfg.ProfileName     = profileName;
            cfg.TrustedNetworks = trustedNetworks;
            Save(cfg);
        }
        finally { _lock.Release(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static AutoConnectConfig? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize(json, ServiceJsonContext.Default.AutoConnectConfig);
        }
        catch { return null; }
    }

    private static void Save(AutoConnectConfig cfg)
    {
        // No per-file ACLs: the file inherits SYSTEM FullControl from C:\ProgramData\Valenius
        // (set once by install-service.ps1). StateFileWriter retries transient AV-scanner
        // write collisions so they don't surface as spurious persist failures.
        var json = JsonSerializer.Serialize(cfg, ServiceJsonContext.Default.AutoConnectConfig);
        StateFileWriter.WriteAllText(ConfigPath, json);
    }
}
