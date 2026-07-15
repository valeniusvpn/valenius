using System.Text;

namespace Valenius.Service;

/// <summary>
/// Holds the current client API key and lets the service adopt a new one at runtime when the
/// backend rotates the fleet key (delivered in the heartbeat as <c>ClientApiKey</c>).
///
/// The adopted key is persisted to <c>C:\ProgramData\Valenius\apikey.dat</c> (SYSTEM+Administrators
/// only, inherited from the data-dir ACL) so a rotation survives a restart/upgrade. On first run,
/// and whenever no persisted key exists, it falls back to the install-time <c>Valenius:ApiKey</c>
/// from appsettings.json. A genuinely fresh install (no ProgramData) therefore bootstraps from the
/// installer-provided key; an upgrade preserves any rotated key already on disk.
/// </summary>
public sealed class ApiKeyProvider
{
    private static readonly string KeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Valenius", "apikey.dat");

    private readonly ILogger<ApiKeyProvider> _logger;
    private readonly object _lock = new();
    private string _current;

    public ApiKeyProvider(IConfiguration config, ILogger<ApiKeyProvider> logger)
    {
        _logger  = logger;
        _current = Load(config["Valenius:ApiKey"] ?? "");
    }

    public string Current
    {
        get { lock (_lock) { return _current; } }
    }

    /// <summary>Adopt a rotated key delivered by the backend: persist + switch. No-op if empty or unchanged.</summary>
    public void Update(string? newKey)
    {
        if (string.IsNullOrEmpty(newKey)) return;
        lock (_lock)
        {
            if (string.Equals(newKey, _current, StringComparison.Ordinal)) return;
            _current = newKey;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
            File.WriteAllText(KeyFile, newKey, Encoding.UTF8);
            _logger.LogInformation("Adopted rotated client API key from backend.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist rotated API key; using it for this session only.");
        }
    }

    private string Load(string fallback)
    {
        try
        {
            if (File.Exists(KeyFile))
            {
                var persisted = File.ReadAllText(KeyFile, Encoding.UTF8).Trim();
                if (!string.IsNullOrEmpty(persisted))
                    return persisted;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read persisted API key; falling back to appsettings.");
        }
        return fallback;
    }
}
