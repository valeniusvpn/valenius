using System.Text;

namespace Valenius.Service;

/// <summary>
/// Holds the current backend server URL and lets the user set it at first run when the installer
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
    private static readonly string UrlFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Valenius", "backend.dat");

    private readonly ILogger<BackendUrlProvider> _logger;
    private readonly object _lock = new();
    private string _current;

    public BackendUrlProvider(IConfiguration config, ILogger<BackendUrlProvider> logger)
    {
        _logger  = logger;
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
    /// Set the backend URL from a user-entered DNS host (scheme optional). Normalizes to
    /// <c>https://host</c>, persists it, and switches. Returns the normalized URL, or null if the
    /// input does not contain a usable host.
    /// </summary>
    public string? Set(string? dnsOrUrl)
    {
        var normalized = Normalize(dnsOrUrl ?? "");
        if (string.IsNullOrEmpty(normalized)) return null;

        lock (_lock) { _current = normalized; }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UrlFile)!);
            File.WriteAllText(UrlFile, normalized, Encoding.UTF8);
            _logger.LogInformation("Backend URL set to {Url} and persisted.", normalized);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist backend URL; using it for this session only.");
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
            if (File.Exists(UrlFile))
            {
                var persisted = File.ReadAllText(UrlFile, Encoding.UTF8).Trim();
                if (!string.IsNullOrEmpty(persisted))
                    return persisted;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read persisted backend URL; falling back to appsettings.");
        }
        return fallback;
    }
}
