using System.Security.Cryptography;
using System.Text.Json;

namespace Valenius.Service;

public class RegistrationManager
{
    private static readonly string DefaultStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Valenius", "registration.json");
    private static readonly byte[] PolicyEntropy = "Valenius.ConnectionPolicy.v1"u8.ToArray();

    private readonly object _lock = new();
    private readonly string _statePath;
    private readonly TimeSpan _offlineAccessPeriod;
    private readonly TimeProvider _time;
    private readonly ILogger<RegistrationManager> _logger;
    private readonly Action<string, string> _writeState;
    private RegistrationState? _state;
    private CachedConnectionPolicy _policy = new();
    private bool _readFailed;
    private bool _policyInvalid;
    private bool _writeFailed;
    private bool? _backendReachable;
    private string? _lastKnownUsername;
    // Enrollment secrets and push challenges are intentionally never persisted.
    private string? _mfaEnrollmentUri;
    private int? _mfaApproveNumber;

    public RegistrationManager(IConfiguration config, ILogger<RegistrationManager> logger, TimeProvider time)
        : this(config, logger, time, DefaultStatePath) { }

    internal RegistrationManager(IConfiguration config, ILogger<RegistrationManager> logger,
        TimeProvider time, string statePath, Action<string, string>? writeState = null)
    {
        var days = config.GetValue<int?>("Valenius:OfflineAccessDays") ?? 7;
        if (days is < 1 or > 365)
            throw new InvalidOperationException("Valenius:OfflineAccessDays must be between 1 and 365.");
        _offlineAccessPeriod = TimeSpan.FromDays(days);
        _statePath = statePath;
        _time = time;
        _logger = logger;
        _writeState = writeState ?? StateFileWriter.WriteAllTextAtomic;
    }

    private CachedConnectionPolicy Policy
    {
        get { lock (_lock) { Load(); return _policy; } }
    }

    public string? GetServerProfileName() => Policy.ServerProfileName;
    public string? GetServerHealthUrl() => Policy.ServerHealthUrl;
    public string? GetServerVpnIp() => Policy.ServerVpnIp;
    public bool GetSkipConnectivityCheck() => Policy.SkipConnectivityCheck;
    public bool GetDeviceTunnelEnabled() => Policy.DeviceTunnelEnabled;
    public string GetUpdateChannel() => Policy.UpdateChannel;
    public string? GetMfaAuthorizeUrl() => Policy.MfaAuthorizeUrl;
    public DateTime? GetMfaSessionExpiresAt() => Policy.MfaSessionExpiresAt;
    public bool GetMfaEnrollmentOpen() => Policy.MfaEnrollmentOpen;
    public string? GetMfaEnrollmentUri() { lock (_lock) return _mfaEnrollmentUri; }
    public int? GetMfaApproveNumber() { lock (_lock) return _mfaApproveNumber; }

    public bool GetMfaRequired()
    {
        var policy = Policy;
        // An offline restart must not revive an expired MFA session. The sidecar still
        // independently enforces its signed grant; offline activation never renews one.
        return policy.MfaRequired || policy.MfaSessionExpiresAt is { } expiresAt && expiresAt <= UtcNow;
    }

    public string? GetLastKnownUsername() { lock (_lock) return _lastKnownUsername; }
    public void SetLastKnownUsername(string? username)
    {
        if (!string.IsNullOrEmpty(username))
            lock (_lock) _lastKnownUsername = username;
    }

    public string[] GetRemoteLanCidrs(string profileName) =>
        Policy.RemoteLanCidrsByProfile.FirstOrDefault(p =>
            string.Equals(p.ProfileName, profileName, StringComparison.OrdinalIgnoreCase))?.Cidrs ?? [];

    public (string Ip, int Port)? GetGatewayProbe(string profileName)
    {
        var policy = Policy;
        var target = policy.GatewayProfiles.FirstOrDefault(p =>
            string.Equals(p.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));
        if (target is not null) return (target.GatewayIp, target.HealthPort);
        if (string.IsNullOrEmpty(policy.ServerVpnIp) ||
            !string.Equals(profileName, policy.ServerProfileName, StringComparison.OrdinalIgnoreCase))
            return null;
        var port = Uri.TryCreate(policy.ServerHealthUrl, UriKind.Absolute, out var url) ? url.Port : 8443;
        return (policy.ServerVpnIp, port);
    }

    public (int Port, int TriggerSeconds)? GetFallbackConfig(string profileName)
    {
        var target = Policy.FallbackEndpoints.FirstOrDefault(p =>
            string.Equals(p.ProfileName, profileName, StringComparison.OrdinalIgnoreCase));
        return target is { Port: > 0 } ? (target.Port, target.TriggerSeconds > 0 ? target.TriggerSeconds : 20) : null;
    }

    public bool? GetCachedIsActive() { lock (_lock) return Load()?.IsActive; }
    public Guid GetCachedClientId() { lock (_lock) return Load()?.ClientId ?? Guid.Empty; }
    public bool GetStandaloneModeChosen() { lock (_lock) return Load()?.StandaloneModeChosen ?? false; }
    public bool GetProfilesTransferred() { lock (_lock) return Load()?.ProfilesTransferredToBackend ?? false; }
    public bool? GetBackendReachable() { lock (_lock) return _backendReachable; }
    public void SetBackendReachable(bool reachable) { lock (_lock) _backendReachable = reachable; }

    public DateTime? GetOfflineAccessExpiresAt()
    {
        lock (_lock)
        {
            var state = Load();
            return _policyInvalid ? null : OfflineAccessExpiresAt(state);
        }
    }

    public Task<bool> IsAllowedToConnectAsync()
    {
        lock (_lock)
        {
            var state = Load();
            var expiry = OfflineAccessExpiresAt(state);
            return Task.FromResult(!_policyInvalid && expiry is not null &&
                // Reject large backwards clock changes instead of extending a cached lease.
                state!.LastCheckedUtc <= UtcNow.AddMinutes(5) && UtcNow < expiry);
        }
    }

    public string GetConnectionDeniedReason()
    {
        lock (_lock)
        {
            var state = Load();
            if (_readFailed || _policyInvalid || state is null || state.ClientId == Guid.Empty)
                return "Cached VPN authorization is unavailable. Contact your administrator to restore access.";
            if (!state.IsActive)
                return "This device is not activated. Contact your administrator.";
            if (OfflineAccessExpiresAt(state) is { } expiry && UtcNow >= expiry)
                return $"Cached VPN access expired on {expiry.ToLocalTime():g}. Reconnect to the management server to refresh access.";
            return "Cached VPN authorization could not be validated. Reconnect to the management server to refresh access.";
        }
    }

    public Task<Guid> GetOrCreateClientIdAsync()
    {
        lock (_lock)
        {
            var state = Load();
            if (_readFailed || state is { ClientId: var id } && id == Guid.Empty)
                throw new InvalidOperationException("Registration state is unreadable or invalid; refusing to replace the existing client identity.");
            if (state is null)
            {
                state = new RegistrationState { ClientId = Guid.NewGuid() };
                Save(state);
            }
            return Task.FromResult(state.ClientId);
        }
    }

    internal Task UpdateAsync(ClientStatusResponse status)
    {
        lock (_lock)
        {
            var state = Load() ?? throw new InvalidOperationException("A client identity is required before applying backend authorization.");
            _policy = CachedConnectionPolicy.FromStatus(status);
            _policyInvalid = false;
            _mfaEnrollmentUri = status.MfaEnrollmentUri;
            _mfaApproveNumber = status.MfaApproveNumber;
            _backendReachable = true;
            // Update memory BEFORE any disk operation. In particular, a received revocation
            // must take effect even if an AV lock or broken ACL prevents persistence.
            _state = state with { IsActive = status.IsActive, LastCheckedUtc = UtcNow };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_policy, ServiceJsonContext.Default.CachedConnectionPolicy);
            var protectedPolicy = Convert.ToBase64String(ProtectedData.Protect(bytes, PolicyEntropy, DataProtectionScope.LocalMachine));
            Save(_state with { ProtectedConnectionPolicy = protectedPolicy });
            return Task.CompletedTask;
        }
    }

    public Task SetStandaloneModeAsync(bool value)
    {
        lock (_lock)
        {
            Save(RequireState() with { StandaloneModeChosen = value });
            return Task.CompletedTask;
        }
    }

    public Task SetProfilesTransferredAsync(bool value)
    {
        lock (_lock)
        {
            Save(RequireState() with { ProfilesTransferredToBackend = value });
            return Task.CompletedTask;
        }
    }

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;
    private RegistrationState RequireState()
    {
        var state = Load();
        if (_readFailed)
            throw new InvalidOperationException("Registration state is unreadable; refusing to replace the existing client identity.");
        return state ?? new RegistrationState { ClientId = Guid.NewGuid() };
    }

    private DateTime? OfflineAccessExpiresAt(RegistrationState? state) =>
        state is { IsActive: true } && state.ClientId != Guid.Empty && state.LastCheckedUtc != default &&
        state.LastCheckedUtc <= DateTime.MaxValue - _offlineAccessPeriod
            ? state.LastCheckedUtc + _offlineAccessPeriod : null;

    // Caller holds _lock. Only read disk until a complete state has loaded; subsequent
    // decisions use the latest backend response in memory even when persistence fails.
    private RegistrationState? Load()
    {
        if (_state is not null) return _state;
        try
        {
            using var stream = new FileStream(_statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _state = JsonSerializer.Deserialize(stream, ServiceJsonContext.Default.RegistrationState)
                ?? throw new JsonException("Empty registration state.");
            _readFailed = false;
            if (_state.ProtectedConnectionPolicy is { } protectedPolicy)
            {
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedPolicy), PolicyEntropy, DataProtectionScope.LocalMachine);
                    _policy = JsonSerializer.Deserialize(bytes, ServiceJsonContext.Default.CachedConnectionPolicy)
                        ?? throw new JsonException("Empty connection policy.");
                    if (_policy.Version != 1) throw new JsonException("Unsupported connection policy version.");
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
                {
                    _policyInvalid = true;
                    _policy = new();
                    _logger.LogWarning(ex, "Cached connection policy is unreadable; a successful backend check is required.");
                }
            }
            return _state;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or JsonException)
        {
            if (!_readFailed)
                _logger.LogWarning(ex, "Registration cache is unreadable; preserving the existing client identity.");
            _readFailed = true;
            return null;
        }
    }

    private void Save(RegistrationState state)
    {
        _state = state;
        try
        {
            _writeState(_statePath, JsonSerializer.Serialize(state, ServiceJsonContext.Default.RegistrationState));
            if (_writeFailed) _logger.LogInformation("Registration cache persistence recovered.");
            _writeFailed = false;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            if (!_writeFailed)
                _logger.LogWarning(ex, "Could not persist registration cache. Current authorization remains in memory; repair storage before restarting the service.");
            _writeFailed = true;
            throw;
        }
    }
}
