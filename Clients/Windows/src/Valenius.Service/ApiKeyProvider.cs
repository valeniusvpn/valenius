using System.Text;

namespace Valenius.Service;

/// <summary>
/// Holds the current client API key and lets the service adopt a new one at runtime when the
/// backend offers its individual credential (heartbeat <c>ClientApiKey</c>).
///
/// The adopted key is persisted to <c>C:\ProgramData\Valenius\apikey.dat</c> (SYSTEM+Administrators
/// only, with an explicit protected file ACL) so a rotation survives a restart/upgrade. On first run,
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
    private readonly string _keyFile;
    private readonly Action<string, string> _write;
    private string _current;

    public ApiKeyProvider(IConfiguration config, ILogger<ApiKeyProvider> logger)
        : this(config, logger, KeyFile, ServiceFileAccess.WriteCredential) { }

    internal ApiKeyProvider(IConfiguration config, ILogger<ApiKeyProvider> logger, string keyFile,
        Action<string, string>? write = null)
    {
        _logger  = logger;
        _keyFile = keyFile;
        _write = write ?? ServiceFileAccess.WriteCredential;
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
            try
            {
                // The next authenticated request confirms migration server-side. Never
                // send the new credential until it survives a service restart.
                _write(_keyFile, newKey);
                _current = newKey;
                _logger.LogInformation("Persisted and adopted individual client API credential.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not persist API credential ({ExceptionType}); retaining the current credential for retry.", ex.GetType().Name);
            }
        }
    }

    private string Load(string fallback)
    {
        string? persisted = null;
        ServiceFileAccess.Run(() =>
        {
            try
            {
                File.GetAttributes(_keyFile); // Unlike File.Exists, access denial is not absence.
            }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            // Repair old readable-by-Users credentials before using them. Failure must
            // stop startup, never silently fall back to the retired installation key.
            ServiceFileAccess.ProtectCredential(_keyFile);
            using var file = new FileStream(_keyFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(file, Encoding.UTF8);
            persisted = reader.ReadToEnd().Trim();
            if (string.IsNullOrEmpty(persisted)) throw new IOException("Persisted device credential is empty; administrator recovery is required.");
        });
        return persisted ?? fallback;
    }
}
