using System.Text;

namespace Valenius.Service;

/// <summary>
/// Holds the current backend server URL and permits administrator provisioning at first run when the installer
/// did not provide one (interactive install without <c>/BACKENDURL=</c> or a valenius-setup.ini).
///
/// The user-entered URL is persisted to <c>C:\ProgramData\Valenius\backend.dat</c> (SYSTEM+Administrators
/// only, inherited from the data-dir ACL) so it survives a restart/upgrade. On first run, and whenever no
/// persisted URL exists, it falls back to the install-time <c>Valenius:BackendUrl</c> from appsettings.json
/// (which MDM/silent installs write). A genuinely fresh interactive install ships an EMPTY appsettings URL,
/// so <see cref="IsConfigured"/> is false until the user provides the DNS via the tray prompt.
///
/// Mirrors <see cref="ApiKeyProvider"/>. Kept separate from appsettings so the persisted user choice always
/// wins over (and is never clobbered by) the bundled default.
/// </summary>
public sealed class BackendUrlProvider
{
    private static readonly string DefaultUrlFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Valenius", "backend.dat");

    private readonly ILogger<BackendUrlProvider> _logger;
    private readonly object _lock = new();
    private string _current;
    private readonly string _urlFile;
    private readonly Action<string, string> _write;

    public BackendUrlProvider(IConfiguration config, ILogger<BackendUrlProvider> logger)
        : this(config, logger, DefaultUrlFile) { }

    internal BackendUrlProvider(IConfiguration config, ILogger<BackendUrlProvider> logger,
        string urlFile, Action<string, string>? write = null)
    {
        _logger  = logger;
        _urlFile = urlFile;
        _write = write ?? StateFileWriter.WriteAllTextAtomic;
        _current = Normalize(Load(config["Valenius:BackendUrl"] ?? ""));
    }

    /// <summary>The normalized backend URL (https://host, no trailing slash), or "" when unconfigured.</summary>
    public string Current
    {
        get { lock (_lock) { return _current; } }
    }

    /// <summary>True once a non-empty backend URL is available (from disk or appsettings).</summary>
    public bool IsConfigured => !string.IsNullOrEmpty(Current);

    /// <summary>
    /// Provision an initial backend with explicit service-authorized administrator authority.
    /// Persist before switching; reject a different existing origin. Return null for unusable input.
    /// </summary>
    public string? Set(string? dnsOrUrl, bool privileged = false)
    {
        if (!privileged)
            throw new UnauthorizedAccessException("An administrator must provision the backend address.");
        var normalized = Normalize(dnsOrUrl ?? "");
        if (string.IsNullOrEmpty(normalized)) return null;

        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_current))
            {
                if (string.Equals(_current, normalized, StringComparison.OrdinalIgnoreCase)) return _current;
                throw new InvalidOperationException("Backend reassignment is not supported through IPC. Administrator migration is required.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_urlFile)!);
            _write(_urlFile, normalized);
            _current = normalized;
            _logger.LogInformation("Backend URL set to {Url} and persisted.", normalized);
        }
        return normalized;
    }

    /// <summary>
    /// Coerce a user-entered DNS/URL to the canonical https form the rest of the client assumes:
    /// strip any scheme the user typed/pasted, drop path/query and surrounding whitespace/slashes,
    /// then prepend https:// . Returns "" when no host remains.
    /// </summary>
    public static string Normalize(string input)
    {
        var s = (input ?? "").Trim();
        if (s.Length == 0) return "";

        // Drop any scheme the user pasted (http://, https://, or a bare //).
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0) s = s[(schemeIdx + 3)..];
        s = s.TrimStart('/');

        // Keep host[:port] only — cut everything from the first path/query/fragment separator.
        var cut = s.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) s = s[..cut];
        s = s.Trim().TrimEnd('.');

        return s.Length == 0 ? "" : "https://" + s;
    }

    private string Load(string fallback)
    {
        try
        {
            var persisted = File.ReadAllText(_urlFile, Encoding.UTF8).Trim();
            if (string.IsNullOrEmpty(Normalize(persisted)))
                throw new InvalidDataException("Persisted backend URL is empty or invalid.");
            return persisted;
        }
        catch (FileNotFoundException) when (new FileInfo(_urlFile).LinkTarget is null) { return fallback; }
        catch (DirectoryNotFoundException) { return fallback; }
        catch (Exception ex)
        {
            throw new IOException("Could not read persisted backend URL; refusing to change backend trust.", ex);
        }
    }
}
