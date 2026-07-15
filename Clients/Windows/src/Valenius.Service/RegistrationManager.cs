using System.Text.Json;

namespace Valenius.Service;

public class RegistrationManager
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Valenius", "registration.json");

    private static readonly TimeSpan GracePeriod = TimeSpan.FromDays(1);

    private readonly SemaphoreSlim _lock = new(1, 1);

    // In-memory only — refreshed on every successful heartbeat.
    private volatile string? _serverProfileName;
    public string? GetServerProfileName() => _serverProfileName;
    public void SetServerProfileName(string? name) => _serverProfileName = name;

    // Health probe URLs for connectivity verification (spec §3).
    // ServerVpnIp is kept in memory only and never written to disk — it is
    // internal network topology that should not be persisted locally.
    private volatile string? _serverHealthUrl;
    private volatile string? _serverVpnIp;
    public string? GetServerHealthUrl() => _serverHealthUrl;
    public string? GetServerVpnIp()     => _serverVpnIp;
    public void SetServerHealthUrl(string? url) => _serverHealthUrl = url;
    public void SetServerVpnIp(string? ip)      => _serverVpnIp     = ip;

    private volatile bool _skipConnectivityCheck;
    public bool GetSkipConnectivityCheck()       => _skipConnectivityCheck;
    public void SetSkipConnectivityCheck(bool v) => _skipConnectivityCheck = v;

    // Per-profile gateway-probe targets (sidecar VPNs whose transit network is globally unique),
    // refreshed on every heartbeat. A profile present here uses the HTTP /health gateway check;
    // a profile absent here (plain uploads, OSS) uses the handshake check only.
    private volatile Dictionary<string, (string Ip, int Port)> _gatewayProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    public void SetGatewayProfiles(IEnumerable<(string Name, string Ip, int Port)> profiles)
    {
        var map = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, ip, port) in profiles)
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(ip))
                map[name] = (ip, port);
        _gatewayProfiles = map;
    }

    /// <summary>
    /// The gateway probe target (IP + health port) for <paramref name="profileName"/>, or null
    /// when the profile should be handshake-verified. Falls back to the native ServerVpnIp for
    /// the server profile, so a new client still verifies natively against an older backend that
    /// doesn't send GatewayProfiles.
    /// </summary>
    public (string Ip, int Port)? GetGatewayProbe(string profileName)
    {
        if (_gatewayProfiles.TryGetValue(profileName, out var entry))
            return entry;
        if (!string.IsNullOrEmpty(_serverVpnIp) &&
            string.Equals(profileName, _serverProfileName, StringComparison.OrdinalIgnoreCase))
        {
            var port = 8443;
            if (!string.IsNullOrEmpty(_serverHealthUrl) && Uri.TryCreate(_serverHealthUrl, UriKind.Absolute, out var u))
                port = u.Port;
            return (_serverVpnIp!, port);
        }
        return null;
    }

    // MFA session gating state, refreshed on every heartbeat/poll (in-memory only).
    private volatile bool _mfaRequired;
    private volatile string? _mfaAuthorizeUrl;
    private DateTime? _mfaSessionExpiresAt;
    private volatile bool _mfaEnrollmentOpen;
    private volatile string? _mfaEnrollmentUri;
    private int? _mfaApproveNumber;
    public bool      GetMfaRequired()         => _mfaRequired;
    public string?   GetMfaAuthorizeUrl()     => _mfaAuthorizeUrl;
    public DateTime? GetMfaSessionExpiresAt() => _mfaSessionExpiresAt;
    public bool      GetMfaEnrollmentOpen()   => _mfaEnrollmentOpen;
    public string?   GetMfaEnrollmentUri()    => _mfaEnrollmentUri;
    public int?      GetMfaApproveNumber()    => _mfaApproveNumber;
    public void SetMfaState(bool required, string? authorizeUrl, DateTime? expiresAt, bool enrollmentOpen, string? enrollmentUri, int? approveNumber)
    {
        _mfaRequired         = required;
        _mfaAuthorizeUrl     = authorizeUrl;
        _mfaSessionExpiresAt = expiresAt;
        _mfaEnrollmentOpen   = enrollmentOpen;
        _mfaEnrollmentUri    = enrollmentUri;
        _mfaApproveNumber    = approveNumber;
    }

    // Auto-update channel ("stable"|"beta"), refreshed from the heartbeat. The UpdateChecker
    // polls the matching release stream (…/api/version?channel=<this>).
    private volatile string _updateChannel = "stable";
    public string GetUpdateChannel() => _updateChannel;
    public void SetUpdateChannel(string? channel) =>
        _updateChannel = string.Equals(channel, "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

    public bool? GetCachedIsActive()
    {
        var state = Load();
        return state?.IsActive;
    }

    public Guid GetCachedClientId()
    {
        var state = Load();
        return state?.ClientId ?? Guid.Empty;
    }

    public async Task<bool> IsAllowedToConnectAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var state = Load();
            if (state is null) return false;
            if (!state.IsActive) return false;
            return DateTime.UtcNow - state.LastCheckedUtc <= GracePeriod;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<Guid> GetOrCreateClientIdAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var (state, denied) = LoadDetailed();
            if (denied)
                // registration.json EXISTS but is unreadable (e.g. a broken data-dir ACL).
                // Do NOT mint a new identity here — that would re-register this machine as a
                // brand-new "pending" client and orphan the real, already-activated one. Surface
                // the error so the heartbeat fails and retries after the ACL self-heal, preserving
                // the persistent ClientId.
                throw new UnauthorizedAccessException(
                    $"registration.json is not readable ({StatePath}); refusing to generate a new ClientId.");

            state ??= new RegistrationState();
            if (state.ClientId == Guid.Empty)
            {
                state.ClientId = Guid.NewGuid();
                Save(state);
            }
            return state.ClientId;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateAsync(bool isActive)
    {
        await _lock.WaitAsync();
        try
        {
            var state = Load() ?? new RegistrationState();
            state.IsActive = isActive;
            state.LastCheckedUtc = DateTime.UtcNow;
            Save(state);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static RegistrationState? Load() => LoadDetailed().State;

    /// <summary>
    /// Loads the persisted state. <c>Denied</c> is true when the file EXISTS but could not be
    /// read due to permissions — the caller must distinguish that from "absent" (fresh install)
    /// so it never mints a new identity over an existing-but-locked one. Note <c>File.Exists</c>
    /// returns false on access-denied, so we detect it from the open exception, not Exists.
    /// </summary>
    private static (RegistrationState? State, bool Denied) LoadDetailed()
    {
        try
        {
            using var fs = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var json = reader.ReadToEnd();
            return (JsonSerializer.Deserialize(json, ServiceJsonContext.Default.RegistrationState), false);
        }
        catch (FileNotFoundException)       { return (null, false); } // genuinely absent -> fresh
        catch (DirectoryNotFoundException)  { return (null, false); }
        catch (UnauthorizedAccessException) { return (null, true);  } // exists but denied -> do NOT mint
        catch                               { return (null, false); } // corrupt/other -> treat as fresh
    }

    private static void Save(RegistrationState state)
    {
        // No per-file ACLs: the file inherits SYSTEM FullControl from C:\ProgramData\Valenius
        // (set once by install-service.ps1). StateFileWriter retries transient AV-scanner
        // write collisions so they don't surface as spurious persist failures.
        var json = JsonSerializer.Serialize(state, ServiceJsonContext.Default.RegistrationState);
        StateFileWriter.WriteAllText(StatePath, json);
    }
}
