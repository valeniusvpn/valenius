using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Principal;
using Microsoft.Win32;

namespace Valenius.Service;

public class ClientRegistrationService : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly RegistrationManager _registration;
    private readonly ConfigManager _configs;
    private readonly AutoConnectManager _autoConnect;
    private readonly WireGuardController _wg;
    private readonly TunnelStateManager _state;
    private readonly TunnelVerifier _tunnelVerifier;
    private readonly UpdateChecker _updater;
    private readonly ApiKeyProvider _apiKeys;
    private readonly BackendUrlProvider _backendUrl;
    private readonly ILogger<ClientRegistrationService> _logger;

    /// <summary>Last heartbeat interval received from the backend (minutes). Default 5.</summary>
    private volatile int _heartbeatIntervalMinutes = 5;

    public ClientRegistrationService(
        IHttpClientFactory httpFactory,
        RegistrationManager registration,
        ConfigManager configs,
        AutoConnectManager autoConnect,
        WireGuardController wg,
        TunnelStateManager state,
        TunnelVerifier tunnelVerifier,
        UpdateChecker updater,
        ApiKeyProvider apiKeys,
        BackendUrlProvider backendUrl,
        ILogger<ClientRegistrationService> logger)
    {
        _httpFactory     = httpFactory;
        _registration    = registration;
        _configs         = configs;
        _autoConnect     = autoConnect;
        _wg              = wg;
        _state           = state;
        _tunnelVerifier  = tunnelVerifier;
        _updater         = updater;
        _apiKeys         = apiKeys;
        _backendUrl      = backendUrl;
        _logger          = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial startup delay lets the service settle before hitting the backend.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        // Run the heartbeat and long-poll loops in parallel for the lifetime of the service.
        await Task.WhenAll(
            RunHeartbeatLoopAsync(stoppingToken),
            RunLongPollLoopAsync(stoppingToken));
    }

    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await TryRegisterAsync(ct: ct);
            await Task.Delay(TimeSpan.FromMinutes(_heartbeatIntervalMinutes), ct);
        }
    }

    /// <summary>
    /// Continuously issues long-poll requests to GET /api/clients/poll.
    /// The backend holds each request for up to 55 seconds and returns immediately when an
    /// admin action changes the client's state.  The response is processed identically to a
    /// heartbeat, so pending flags (Connect, config, profile delete, auto-connect) are acted
    /// on within ~1 second of the admin clicking a button rather than waiting up to
    /// <see cref="_heartbeatIntervalMinutes"/> minutes.
    /// </summary>
    private async Task RunLongPollLoopAsync(CancellationToken ct)
    {
        // Give the heartbeat a head start so the client is registered before we poll.
        await Task.Delay(TimeSpan.FromSeconds(5), ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var backendUrl = _backendUrl.Current;
                if (string.IsNullOrEmpty(backendUrl))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    continue;
                }

                var clientId   = await _registration.GetOrCreateClientIdAsync();
                var http       = _httpFactory.CreateClient("LongPoll");
                var trayActive = IsTrayRunning();

                var response = await http.GetAsync(
                    $"{backendUrl}/api/clients/poll?clientKey={clientId}&trayRunning={trayActive}", ct);

                if (response.IsSuccessStatusCode)
                {
                    var result = await response.Content.ReadFromJsonAsync(
                        ServiceJsonContext.Default.ClientStatusResponse, ct);
                    if (result is not null)
                        await ProcessStatusResponseAsync(result, backendUrl, clientId, ct);
                }
                else if ((int)response.StatusCode >= 500)
                {
                    // Server-side error — back off before retrying.
                    _logger.LogDebug("Long poll returned {Status}; backing off.", (int)response.StatusCode);
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                }
                // 404 (not yet registered) or other 4xx: reconnect immediately;
                // the next heartbeat will handle re-registration.
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Long poll failed; will retry in 15 s.");
                await Task.Delay(TimeSpan.FromSeconds(15), ct);
            }
        }
    }

    // Returns (IsActive, DuplicatePending, ErrorDetail). IsActive is null when the server could
    // not be reached. DuplicatePending is true when the server accepted the registration but it
    // collides with an existing same-hostname client awaiting an admin reassign decision.
    public async Task<(bool? IsActive, bool DuplicatePending, string? Error)> TryRegisterAsync(
        string? username = null,
        string? userSid  = null,
        CancellationToken ct = default)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl))
                return (null, false, "Valenius:BackendUrl is not configured.");

            var http = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();

            var request = new RegisterClientRequest(
                ClientKey:   clientId.ToString(),
                Hostname:    Environment.MachineName,
                HostSid:     GetMachineSid(),
                Username:    username ?? "",
                UserSid:     userSid  ?? "",
                Version:     UpdateChecker.GetCurrentVersion(),
                TrayRunning: IsTrayRunning(),
                Profiles:    _configs.GetAllProfiles(),
                Platform:    "Windows");

            var response = await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/register",
                request,
                ServiceJsonContext.Default.RegisterClientRequest,
                ct);

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync(
                    ServiceJsonContext.Default.ClientStatusResponse, ct);
                if (result is not null)
                {
                    await ProcessStatusResponseAsync(result, backendUrl, clientId, ct);
                    return (result.IsActive, result.DuplicatePending, null);
                }
                return (null, false, "Server returned an empty response body.");
            }

            _logger.LogWarning("Registration server returned {Status}", (int)response.StatusCode);
            return (null, false, $"Server returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registration heartbeat failed; cached state will be used.");
            return (null, false, ex.Message);
        }
    }

    /// <summary>
    /// Pulls the backend's authoritative profile set for this Client (machine) and reconciles it
    /// into <paramref name="username"/>'s local folder via <see cref="ConfigManager.SyncManagedProfiles"/>.
    /// Called from <see cref="PipeServer"/>'s <c>SyncStatus</c> handling on every tray start and
    /// popup-open, so a new/empty OS user account on an already-provisioned machine gets its
    /// profiles without any local file copying and without waiting for the next heartbeat cycle.
    /// Deliberately does nothing on any failure (offline, timeout, non-2xx) rather than falling
    /// back to a cached/local list — an unreachable backend must never hand a user someone else's
    /// stale profile, and must never destructively prune what's already on disk for a user who
    /// already has profiles.
    /// </summary>
    public async Task<bool> ResyncProfilesAsync(string username, CancellationToken ct)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return false;

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();

            var response = await http.GetAsync(
                $"{backendUrl}/api/clients/profiles?clientKey={clientId}", ct);
            if (!response.IsSuccessStatusCode) return false;

            var entries = await response.Content.ReadFromJsonAsync(
                ServiceJsonContext.Default.PendingForeignConfigEntryArray, ct);
            if (entries is null) return false;

            _configs.SyncManagedProfiles(username,
                entries.Select(e => (e.FileName, e.Content)).ToArray());
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Profile resync failed; local profiles left unchanged.");
            return false;
        }
    }

    /// <summary>
    /// Ensures the machine-level device-tunnel copy (<see cref="ConfigManager.SaveDeviceTunnelConfig"/>)
    /// of <paramref name="profileName"/> (the customer's own native sidecar profile) is current, by
    /// pulling the same authoritative list <see cref="ResyncProfilesAsync"/> uses and picking out the
    /// matching entry. Called unconditionally from every heartbeat/poll response when the device
    /// tunnel is enabled, regardless of whether any OS user is logged in — this is what lets
    /// <see cref="AutoConnectService"/> connect it at boot on a machine that has never been locally
    /// logged into. Does nothing on any failure; the machine copy simply stays whatever it last was.
    /// </summary>
    private async Task EnsureDeviceTunnelConfigAsync(string profileName, string backendUrl, Guid clientId, CancellationToken ct)
    {
        try
        {
            var http     = _httpFactory.CreateClient("Registration");
            var response = await http.GetAsync($"{backendUrl}/api/clients/profiles?clientKey={clientId}", ct);
            if (!response.IsSuccessStatusCode) return;

            var entries = await response.Content.ReadFromJsonAsync(
                ServiceJsonContext.Default.PendingForeignConfigEntryArray, ct);
            var match = entries?.FirstOrDefault(e =>
                string.Equals(e.FileName, profileName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                _configs.SaveDeviceTunnelConfig(profileName, match.Content);
        }
        catch (OperationCanceledException)
        {
            // best-effort
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device tunnel config sync failed; machine copy left unchanged.");
        }
    }

    /// <summary>
    /// Archives a manually-uploaded profile's content to the backend so it becomes part of this
    /// Client's authoritative set and can be pulled by <see cref="ResyncProfilesAsync"/> onto every
    /// other OS user account on the machine. Called only when the uploading user opts in to
    /// "available to all users on this PC" — <see cref="PipeServer"/> marks the profile managed for
    /// the uploader only if this returns true, so a failed/offline archive attempt leaves it as a
    /// normal private profile rather than one a later resync could prune.
    /// </summary>
    public async Task<bool> ArchiveProfileAsync(string profileName, string content, CancellationToken ct)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return false;

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();

            var request  = new ArchiveProfileRequest(clientId.ToString(), profileName, content);
            var response = await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/profiles/archive",
                request,
                ServiceJsonContext.Default.ArchiveProfileRequest,
                ct);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to share profile '{Profile}' with other users.", profileName);
            return false;
        }
    }

    /// <summary>
    /// Shared response handler called by both the heartbeat loop and the long-poll loop.
    /// Persists registration state, syncs auto-connect config, and acts on any pending flags.
    /// </summary>
    private async Task ProcessStatusResponseAsync(
        ClientStatusResponse result,
        string backendUrl,
        Guid clientId,
        CancellationToken ct)
    {
        // Backend key rotation: adopt the new key (persisted for next boot) so subsequent
        // requests authenticate with it. Only sent while this client is still on the old key.
        _apiKeys.Update(result.ClientApiKey);

        try
        {
            await _registration.UpdateAsync(result.IsActive);
        }
        catch (Exception ex)
        {
            // Non-fatal: this is a cache of IsActive/LastChecked; the service runs fine from the
            // heartbeat + in-memory state without it. Debug, not Warning — a persistent ACL problem
            // is surfaced once (actionably) by DataDirSelfHeal, not re-logged every heartbeat.
            _logger.LogDebug(ex, "Could not persist registration state to local cache.");
        }

        _registration.SetServerProfileName(result.ServerProfileName);
        _registration.SetServerHealthUrl(result.ServerHealthUrl);
        _registration.SetServerVpnIp(result.ServerVpnIp);
        _registration.SetRemoteLanCidrs(
            (result.RemoteLanCidrsByProfile ?? [])
            .Select(e => (e.ProfileName, e.Cidrs)));
        _registration.SetSkipConnectivityCheck(result.SkipConnectivityCheck);
        _registration.SetGatewayProfiles(
            (result.GatewayProfiles ?? [])
            .Select(g => (g.ProfileName, g.GatewayIp, g.HealthPort)));
        _registration.SetDeviceTunnelEnabled(result.DeviceTunnelEnabled);
        if (result.DeviceTunnelEnabled && !string.IsNullOrEmpty(result.ServerProfileName))
        {
            // Runs unconditionally regardless of any OS user login, so the machine-level device-tunnel
            // config is fresh before AutoConnectService's boot-time check needs it (see ConfigManager.SaveDeviceTunnelConfig).
            await EnsureDeviceTunnelConfigAsync(result.ServerProfileName, backendUrl, clientId, ct);
        }
        _registration.SetMfaState(
            result.MfaRequired,
            result.MfaAuthorizeUrl,
            result.MfaSessionExpiresAt,
            result.MfaEnrollmentOpen,
            result.MfaEnrollmentUri,
            result.MfaApproveNumber);
        _registration.SetUpdateChannel(result.UpdateChannel);

        if (result.HeartbeatIntervalMinutes is >= 1 and <= 60)
            _heartbeatIntervalMinutes = result.HeartbeatIntervalMinutes;

        try
        {
            var networks = (result.TrustedNetworks ?? [])
                .Select(n => new TrustedNetworkEntry { Subnet = n.Subnet, PrimaryDns = n.PrimaryDns })
                .ToArray();
            await _autoConnect.UpdateFromBackendAsync(
                result.AutoConnectEnabled,
                result.AutoConnectProfileName,
                networks);
        }
        catch (Exception ex)
        {
            // Non-fatal cache write (auto-connect config); the admin value is re-applied on the
            // next heartbeat regardless. Debug, not Warning — see the registration-persist note above.
            _logger.LogDebug(ex, "Could not persist auto-connect config.");
        }

        if (result.HasPendingConfig)
        {
            var http = _httpFactory.CreateClient("Registration");
            await FetchAndStagePendingConfigAsync(http, backendUrl, clientId, ct);
        }

        if (result.PendingForeignConfigs is { Length: > 0 })
        {
            foreach (var fp in result.PendingForeignConfigs)
            {
                try
                {
                    var profileName = Valenius.Shared.ProfileNameHelper.IsValid(fp.FileName)
                        ? fp.FileName
                        : Valenius.Shared.ProfileNameHelper.Sanitize(fp.FileName);
                    _configs.SaveStagedConfig(profileName, fp.Content);
                    _logger.LogInformation("Foreign profile staged from backend: {ProfileName}", profileName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to save foreign profile '{FileName}'.", fp.FileName);
                }
            }
        }

        if (!string.IsNullOrEmpty(result.PendingDeleteProfileName))
        {
            _configs.DeleteProfile(result.PendingDeleteProfileName);
            _logger.LogInformation("Deleted profile '{Profile}' as requested by backend.",
                result.PendingDeleteProfileName);

            // If the tunnel for this profile is currently active, disconnect just it.
            if (_state.IsConnected(result.PendingDeleteProfileName))
            {
                _logger.LogInformation(
                    "Disconnecting active tunnel '{Tunnel}' — profile removed by backend.",
                    result.PendingDeleteProfileName);
                await _wg.DisconnectAsync(result.PendingDeleteProfileName);
                _state.SetDisconnected(result.PendingDeleteProfileName);
                _ = LogEventAsync("Disconnect", "", result.PendingDeleteProfileName, null, ct);
            }
        }

        if (result.PendingConnect)
            _ = Task.Run(() => ExecutePendingConnectAsync(ct), ct);

        if (result.PendingDisconnect)
            _ = Task.Run(() => ExecutePendingDisconnectAsync(ct), ct);

        // MFA session expired (the gate returned) while a gated tunnel is still up: the sidecar
        // has already dropped the peer server-side, but the local tunnel remains installed and
        // would otherwise linger showing a stale "connected" until an admin kills it. Tear down
        // the primary/server tunnel so the local state matches the server and the user must
        // re-authorize to reconnect. Foreign tunnels are not gated by this client's MFA.
        if (result.MfaRequired)
            _ = Task.Run(() => ExecuteMfaGateTeardownAsync(ct), ct);

        if (result.ForceUpdate)
        {
            _logger.LogInformation("Force-update requested by admin — triggering update check.");
            _ = Task.Run(() => _updater.CheckAndApplyUpdateAsync(ct), ct);
        }

        if (result.LogUploadRequested)
        {
            _logger.LogInformation("Log upload requested by admin — collecting Valenius logs.");
            _ = Task.Run(() => TriggerLogUploadAsync("Admin", ct), ct);
        }

        if (result.PendingConfigRepair)
        {
            _logger.LogInformation("Client config repair requested by admin — running data-dir ACL self-heal.");
            _ = Task.Run(async () =>
            {
                // allowOwnershipTakeover:true only here — an admin explicitly asked for this repair,
                // so if a plain DACL reset isn't enough (e.g. a file's OWNER, not just its DACL,
                // drifted away from SYSTEM) escalate to forcibly reclaiming ownership via takeown.exe.
                var outcome = DataDirSelfHeal.EnsureAccessible(DataDirHealthService.DataDir, _logger, allowOwnershipTakeover: true);
                switch (outcome)
                {
                    case DataDirSelfHeal.Result.Failed:
                        await ReportConfigRepairFailedAsync(DataDirSelfHeal.FailureDetail ?? "ACL self-heal failed.", ct);
                        break;
                    case DataDirSelfHeal.Result.RepairedViaOwnershipTakeover:
                        await ReportOwnershipReclaimedAsync(ct);
                        break;
                }

                // A permission/attribute fix alone doesn't retry any delete that already failed —
                // UpdateChecker's own cleanup only runs as a side effect of an actual update check
                // finding a newer version. Sweep leftover installer .exes now so a fix here is
                // immediately visible instead of waiting for the next real update to happen to
                // trigger cleanup.
                _updater.CleanupStaleInstallers();
            }, ct);
        }
    }

    /// <summary>
    /// Collects the (Valenius-only, redacted) diagnostic bundle and uploads it to the backend.
    /// Called on an admin heartbeat request ("Admin") and by the tray "Send logs" action ("User").
    /// Best-effort — a collection or upload failure is logged, never thrown.
    /// </summary>
    public async Task TriggerLogUploadAsync(string trigger, CancellationToken ct)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;
            var clientId = await _registration.GetOrCreateClientIdAsync();

            var apiKey = _apiKeys.Current;
            var bundle = await Task.Run(() => DiagnosticsCollector.BuildGzippedBundle(apiKey), ct);

            var http = _httpFactory.CreateClient("Registration");   // carries X-Api-Key
            using var form = new MultipartFormDataContent
            {
                { new StringContent(clientId.ToString()), "clientKey" },
                { new StringContent(trigger), "trigger" },
            };
            var fileContent = new ByteArrayContent(bundle);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/gzip");
            form.Add(fileContent, "file", "valenius-logs.log.gz");

            var resp = await http.PostAsync($"{backendUrl}/api/clients/logs", form, ct);
            if (resp.IsSuccessStatusCode)
                _logger.LogInformation("Diagnostic logs uploaded ({Trigger}, {Bytes} bytes).", trigger, bundle.Length);
            else
                _logger.LogWarning("Log upload returned HTTP {Status}.", (int)resp.StatusCode);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Diagnostic log upload ({Trigger}) failed.", trigger);
        }
    }

    private async Task ExecutePendingConnectAsync(CancellationToken ct)
    {
        try
        {
            if (!await _registration.IsAllowedToConnectAsync())
            {
                _logger.LogWarning("PendingConnect: client not authorized — skipping.");
                return;
            }

            var profileName = _autoConnect.GetProfileName();
            var configPath  = _configs.GetAnyConfigPath(profileName);
            if (configPath is null)
            {
                _logger.LogWarning("PendingConnect: no config found (profile: {Profile}).",
                    profileName ?? "<first available>");
                return;
            }

            var tunnelName = ConfigManager.GetTunnelNameFromPath(configPath)
                          ?? Path.GetFileNameWithoutExtension(configPath);

            if (_state.IsConnected(tunnelName))
            {
                _logger.LogInformation("PendingConnect: tunnel '{Tunnel}' already connected — skipping.", tunnelName);
                return;
            }

            _logger.LogInformation("PendingConnect: connecting tunnel '{Tunnel}'.", tunnelName);

            var wanIp = await GetWanIpAsync(ct);
            var (success, output) = await _wg.ConnectAsync(configPath);
            if (!success)
            {
                _logger.LogWarning("PendingConnect: connect failed: {Output}", output);
                return;
            }

            // Prefer the actual logged-in Windows user (cached from the last pipe call — the tray's
            // 5s Status poll keeps this fresh) over the "PendingConnect" trigger label: the label
            // is meaningless on the admin client list's "Last user" column, whereas the real
            // username is exactly what an admin wants to see there. Falls back to the label only
            // when no interactive user has ever been observed (e.g. before the tray first connects).
            var connectedBy = _registration.GetLastKnownUsername() ?? "PendingConnect";
            _state.SetConnected(tunnelName, connectedBy);
            _ = LogEventAsync("Connect", connectedBy, tunnelName, wanIp, ct);

            _ = Task.Run(() => RunPhase2Async(tunnelName, ct), ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PendingConnect: unexpected error.");
        }
    }

    private async Task ExecutePendingDisconnectAsync(CancellationToken ct)
    {
        try
        {
            // PendingDisconnect targets the primary/server peer. Pick that tunnel specifically
            // so foreign profiles the user connected by hand are left running. When no server
            // profile is configured (OSS/manual), fall back to the single active tunnel.
            var serverProfile = _registration.GetServerProfileName();
            var (isConnected, activeTunnel, _, _) = string.IsNullOrEmpty(serverProfile)
                ? _state.GetState()
                : (_state.IsConnected(serverProfile), serverProfile, (string?)null, (DateTime?)null);
            if (!isConnected || string.IsNullOrEmpty(activeTunnel))
            {
                _logger.LogInformation("PendingDisconnect: no active tunnel — skipping.");
                return;
            }
            _logger.LogInformation("PendingDisconnect: disconnecting tunnel '{Tunnel}'.", activeTunnel);
            await _wg.DisconnectAsync(activeTunnel);
            _state.SetDisconnected(activeTunnel);
            _ = LogEventAsync("Disconnect", "PendingDisconnect", activeTunnel, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PendingDisconnect: unexpected error.");
        }
    }

    /// <summary>
    /// Tears down the gated primary/server tunnel when the MFA session has expired (heartbeat
    /// reports <c>MfaRequired</c> again). The sidecar drops the peer on its own clock at expiry,
    /// so the local tunnel is already dead; without this it stays installed and shows a stale
    /// "connected" until an admin kills it. Idempotent — a no-op once the tunnel is down.
    /// Only the server profile is touched; hand-connected foreign tunnels are left running.
    /// </summary>
    private async Task ExecuteMfaGateTeardownAsync(CancellationToken ct)
    {
        try
        {
            var serverProfile = _registration.GetServerProfileName();
            if (string.IsNullOrEmpty(serverProfile) || !_state.IsConnected(serverProfile))
                return;

            _logger.LogInformation(
                "MFA gate: session expired — disconnecting gated tunnel '{Tunnel}'.", serverProfile);
            await _wg.DisconnectAsync(serverProfile);
            _state.SetDisconnected(serverProfile);
            _ = LogEventAsync("Disconnect", "MfaSessionExpired", serverProfile, null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MFA gate teardown: unexpected error.");
        }
    }

    private async Task FetchAndStagePendingConfigAsync(
        HttpClient http, string backendUrl, Guid clientId, CancellationToken ct)
    {
        try
        {
            var response = await http.GetAsync(
                $"{backendUrl}/api/clients/pending-config?clientKey={clientId}", ct);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return;
            if (!response.IsSuccessStatusCode) return;

            var result = await response.Content.ReadFromJsonAsync(
                ServiceJsonContext.Default.PendingConfigResponse, ct);
            if (result is null) return;

            var profileName = Valenius.Shared.ProfileNameHelper.IsValid(result.FileName)
                ? result.FileName
                : Valenius.Shared.ProfileNameHelper.Sanitize(result.FileName);
            _configs.SaveStagedConfig(profileName, result.Content);
            _logger.LogInformation("Pending config staged from backend: {ProfileName}", profileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch pending config from backend.");
        }
    }

    /// <summary>
    /// Post-connect verification for a tunnel. A sidecar profile with a unique transit network
    /// (present in the heartbeat gateway map) probes the gateway /health endpoint, falling back
    /// to the local WireGuard handshake on failure or when the probe is skipped; any other
    /// profile uses the handshake check directly. Used by AutoConnect and PendingConnect.
    /// </summary>
    internal Task RunPhase2Async(string tunnelName, CancellationToken ct)
    {
        // Only the gateway-verified path reports upstream — this is what lets the admin client
        // list's "connected" star require actual verification for gateway-eligible (Pro sidecar)
        // clients. The handshake-only path is deliberately NOT reported: WireGuard's own rekey
        // timers mean handshake freshness isn't a reliable enough signal, so those clients keep
        // the backend's simpler "peer registered as connected" behavior instead.
        void ReportIfViaGateway(bool viaGateway)
        {
            if (viaGateway) _ = LogEventAsync("Verified", "", tunnelName, null, ct);
        }

        var probe = _registration.GetGatewayProbe(tunnelName);
        return probe is { } gw && !_registration.GetSkipConnectivityCheck()
            ? _tunnelVerifier.RunGatewayVerifyAsync(tunnelName, gw.Ip, gw.Port, ct, ReportIfViaGateway)
            : _tunnelVerifier.RunHandshakeVerifyAsync(tunnelName, ct);
    }

    public async Task LogEventAsync(string eventType, string username, string tunnelName,
        string? wanIp = null, CancellationToken ct = default)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;

            wanIp ??= await GetWanIpAsync(ct);

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();
            var request  = new LogEventRequest(clientId.ToString(), eventType, username, tunnelName, GetLanIp(), wanIp);
            await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/event",
                request,
                ServiceJsonContext.Default.LogEventRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log {EventType} event to backend.", eventType);
        }
    }

    /// <summary>Reports a client-side repair failure to the backend so it's surfaced on
    /// Admin/Alerts — used when a local self-heal (currently: the data-directory ACL repair) runs
    /// and still can't fully fix the problem, which can indicate the machine's local state was
    /// tampered with rather than an ordinary permission glitch. Best-effort: a network failure here
    /// is logged and swallowed, same as <see cref="LogEventAsync"/> — the underlying problem is
    /// already logged locally by the caller regardless of whether this report lands.</summary>
    public async Task ReportConfigRepairFailedAsync(string detail, CancellationToken ct = default)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();
            var request  = new LogEventRequest(clientId.ToString(), "ConfigRepairFailed", "", "", "", "", detail);
            await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/event",
                request,
                ServiceJsonContext.Default.LogEventRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report config-repair failure to backend.");
        }
    }

    /// <summary>Reports to the backend that a config repair only succeeded after forcibly
    /// reclaiming ownership of the data directory (see DataDirSelfHeal.Result.RepairedViaOwnershipTakeover)
    /// — the repair itself worked and the user is unaffected, but an object under a folder only
    /// SYSTEM should ever write to had lost SYSTEM ownership, which is unusual enough to be worth
    /// an admin's attention (AV/backup-tool interference, a stale legacy issue, or tampering).
    /// Best-effort, same as <see cref="ReportConfigRepairFailedAsync"/>.</summary>
    public async Task ReportOwnershipReclaimedAsync(CancellationToken ct = default)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();
            var request  = new LogEventRequest(clientId.ToString(), "OwnershipReclaimed", "", "", "", "",
                $"Data-directory ownership was forcibly reclaimed by SYSTEM to complete a config repair ({DataDirHealthService.DataDir}).");
            await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/event",
                request,
                ServiceJsonContext.Default.LogEventRequest,
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report ownership-reclaimed event to backend.");
        }
    }

    public async Task<string> GetWanIpAsync(CancellationToken ct = default)
    {
        try
        {
            var http = _httpFactory.CreateClient();
            var ip   = await http.GetStringAsync("https://api4.ipify.org", ct);
            return ip.Trim();
        }
        catch { return ""; }
    }

    private static string GetLanIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static bool IsTrayRunning() =>
        System.Diagnostics.Process.GetProcessesByName("Valenius.TrayApp").Any();

    private static string GetMachineSid()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (key is not null)
            {
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        var sid = new SecurityIdentifier(name);
                        if (sid.IsAccountSid() && sid.AccountDomainSid is not null)
                            return sid.AccountDomainSid.Value;
                    }
                    catch { /* skip malformed entries */ }
                }
            }
        }
        catch { /* fall through */ }

        return $"MACHINE-{Environment.MachineName}";
    }

    /// <summary>
    /// Sets the backend server URL from a user-entered DNS (the first-run tray prompt shown when the
    /// installer provided no URL). Persists it (always — "save anyway"), probes reachability with a short
    /// timeout, and kicks off a registration attempt so activation starts immediately.
    /// Returns (Saved, Warning): Saved is false only when the DNS is unusable; Warning is a non-fatal note
    /// (e.g. the server could not be reached) to surface to the user.
    /// </summary>
    public async Task<(bool Saved, string? Warning)> SetBackendUrlAsync(string? dns, CancellationToken ct = default)
    {
        var normalized = _backendUrl.Set(dns);
        if (normalized is null)
            return (false, null);

        string? warning = null;
        try
        {
            // Anonymous probe against the public version endpoint — no API key needed.
            var http = _httpFactory.CreateClient("Update");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var resp = await http.GetAsync($"{normalized}/api/version", cts.Token);
            if (!resp.IsSuccessStatusCode)
                warning = $"Saved, but the server responded with HTTP {(int)resp.StatusCode}. " +
                          "Double-check the address; Valenius will keep trying to connect.";
        }
        catch
        {
            warning = "Saved, but the server could not be reached yet. " +
                      "Valenius will keep trying to connect in the background.";
        }

        // Start registering right away rather than waiting for the next heartbeat cycle.
        _ = TryRegisterAsync(ct: ct);
        return (true, warning);
    }

    /// <summary>
    /// Best-effort: calls POST /api/clients/offline so the backend removes this client from
    /// the presence tracker immediately.  Errors are swallowed — this is fire-and-forget.
    /// </summary>
    public async Task NotifyOfflineAsync()
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;

            var clientId = await _registration.GetOrCreateClientIdAsync();
            var http     = _httpFactory.CreateClient("Registration");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await http.PostAsync(
                $"{backendUrl}/api/clients/offline?clientKey={clientId}",
                null,
                cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Offline notification failed (best effort).");
        }
    }

    /// <summary>Confirms MFA enrollment by posting the user's TOTP code to the backend.</summary>
    public async Task<bool> ConfirmMfaEnrollmentAsync(string code)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return false;

            var clientId = await _registration.GetOrCreateClientIdAsync();
            var http     = _httpFactory.CreateClient("Registration");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var request  = new MfaEnrollConfirmRequest(clientId.ToString(), code);
            var response = await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/mfa/enroll-confirm",
                request,
                ServiceJsonContext.Default.MfaEnrollConfirmRequest,
                cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MFA enrollment confirmation failed.");
            return false;
        }
    }

    /// <summary>Notifies the backend before the service process exits.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await NotifyOfflineAsync();
        await base.StopAsync(cancellationToken);
    }
}
