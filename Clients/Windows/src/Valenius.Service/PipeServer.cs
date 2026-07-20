using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Valenius.Shared;

namespace Valenius.Service;

public class PipeServer : BackgroundService
{
    public const string PipeName = "Valenius";

    private readonly WireGuardController _wg;
    private readonly ConfigManager _configs;
    private readonly TunnelStateManager _state;
    private readonly UpdateChecker _updater;
    private readonly RegistrationManager _registration;
    private readonly ClientRegistrationService _registrationSvc;
    private readonly AutoConnectManager _autoConnect;
    private readonly ILogger<PipeServer> _logger;

    private readonly ConnectivityVerifier _verifier;
    private readonly HandshakeVerifier _handshakeVerifier;
    private readonly BackendUrlProvider _backendUrl;

    public PipeServer(
        WireGuardController wg,
        ConfigManager configs,
        TunnelStateManager state,
        UpdateChecker updater,
        RegistrationManager registration,
        ClientRegistrationService registrationSvc,
        AutoConnectManager autoConnect,
        ConnectivityVerifier verifier,
        HandshakeVerifier handshakeVerifier,
        BackendUrlProvider backendUrl,
        ILogger<PipeServer> logger)
    {
        _wg                = wg;
        _configs           = configs;
        _state             = state;
        _updater           = updater;
        _registration      = registration;
        _registrationSvc   = registrationSvc;
        _autoConnect       = autoConnect;
        _verifier          = verifier;
        _handshakeVerifier = handshakeVerifier;
        _backendUrl        = backendUrl;
        _logger            = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                _ = Task.Run(() => HandleConnectionAsync(pipe, stoppingToken), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                _logger.LogError(ex, "Pipe server error; restarting listener");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                // RunAsClient requires data to have been read from the pipe first.
                var commandJson = await ReadMessageAsync(pipe, ct);

                string? callerUsername = null;
                string? callerUserSid  = null;
                pipe.RunAsClient(() =>
                {
                    var identity   = WindowsIdentity.GetCurrent();
                    callerUsername = identity.Name;
                    callerUserSid  = identity.User?.Value;
                });

                if (callerUsername is null)
                {
                    _logger.LogWarning("Could not identify pipe client - dropping connection");
                    return;
                }

                var command = JsonSerializer.Deserialize(commandJson, ValeniusJsonContext.Default.PipeCommand);
                if (command is null)
                    return;

                _logger.LogInformation("Command {Cmd} from {User}", command.Command, callerUsername);

                var response = await ProcessAsync(command, callerUsername, callerUserSid, ct);
                var responseJson = JsonSerializer.Serialize(response, ValeniusJsonContext.Default.PipeResponse);
                await WriteMessageAsync(pipe, responseJson, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling pipe connection");
            }
        }
    }

    private async Task<PipeResponse> ProcessAsync(PipeCommand cmd, string username, string? userSid, CancellationToken ct)
    {
        try
        {
        switch (cmd.Command)
        {
            case CommandType.Status:
            {
                var status = BuildTunnelStatus(username);
                return Ok(status, ValeniusJsonContext.Default.TunnelStatus);
            }

            case CommandType.GetConfigInfo:
            {
                // Peek before claiming so we know which profiles are about to be replaced.
                var stagedNames = _configs.GetStagedProfileNames();
                _configs.ClaimStagedConfig(username);

                // If any currently-connected tunnel was just replaced on disk, reconnect it
                // so the new keys take effect (WireGuard reads the conf only at install-time).
                foreach (var conn in _state.GetAllConnected())
                {
                    if (!stagedNames.Any(n => string.Equals(n, conn.Name, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var (disconnOk, _) = await _wg.DisconnectAsync(conn.Name);
                    if (!disconnOk) continue;
                    _state.SetDisconnected(conn.Name);
                    var configPath = _configs.GetConfigPath(username, conn.Name);
                    if (configPath is null) continue;
                    var wanIp = await _registrationSvc.GetWanIpAsync(ct);
                    var (connOk, _) = await _wg.ConnectAsync(configPath);
                    if (connOk)
                    {
                        _state.SetConnected(conn.Name, username);
                        _ = _registrationSvc.LogEventAsync("Connect", username, conn.Name, wanIp, ct);
                    }
                }

                var profiles = _configs.GetProfiles(username);
                var info = new ConfigInfo
                {
                    HasConfig  = profiles.Length > 0,
                    TunnelName = profiles.FirstOrDefault(),
                    FileName   = profiles.FirstOrDefault() is { } p ? $"{p}.conf" : null,
                    Profiles   = profiles
                };
                return Ok(info, ValeniusJsonContext.Default.ConfigInfo);
            }

            case CommandType.UploadConfig:
            {
                if (string.IsNullOrWhiteSpace(cmd.ConfigContent))
                    return Fail("Config content is required.");

                if (!Valenius.Shared.ProfileNameHelper.IsValid(cmd.ProfileName))
                    return Fail("Profile name may only contain letters, digits, underscores, and hyphens (max 50 chars).");

                _configs.SaveConfig(username, cmd.ProfileName!, cmd.ConfigContent);
                return Ok();
            }

            case CommandType.Connect:
            {
                if (!await _registration.IsAllowedToConnectAsync())
                {
                    // Cache is stale or missing — do a live backend check before rejecting.
                    // This lets a just-activated machine connect without waiting for the
                    // next 5-minute heartbeat, and also recovers from failed cache writes.
                    var (isActive, dupPending, liveError) = await _registrationSvc.TryRegisterAsync(ct: ct);
                    if (isActive != true)
                    {
                        var clientId = _registration.GetCachedClientId();
                        return Fail(dupPending
                            ? $"A client named '{Environment.MachineName}' already exists. Ask your administrator to reassign it to this machine."
                            : liveError is not null
                                ? $"Not authorized (live check failed: {liveError})"
                                : $"Not authorized — activate key {clientId:D} in the admin panel.");
                    }
                }

                _configs.ClaimStagedConfig(username);
                string? configPath;
                if (!string.IsNullOrEmpty(cmd.ProfileName))
                {
                    configPath = _configs.GetConfigPath(username, cmd.ProfileName);
                    if (configPath is null)
                        return Fail($"Profile '{cmd.ProfileName}' not found.");
                }
                else
                {
                    configPath = _configs.GetFirstConfigPath(username);
                    if (configPath is null)
                        return Fail("No config uploaded. Please upload a WireGuard .conf file first.");
                }

                // Use ConfigManager to strip the profile name correctly — a plain .conf
                // loses one extension, but an encrypted .conf.enc must lose two.
                var tunnelName = ConfigManager.GetTunnelNameFromPath(configPath)
                              ?? Path.GetFileNameWithoutExtension(configPath);

                // Idempotent: connecting an already-connected profile is a no-op success.
                if (_state.IsConnected(tunnelName))
                    return Ok();

                // Reject if this profile's routes overlap any already-connected tunnel.
                // Multiple tunnels may run at once, but their AllowedIPs must be disjoint
                // and none may be a full tunnel (0.0.0.0/0).
                var conflict = _wg.FindAllowedIpsConflict(
                    configPath, _state.GetAllConnected().Select(t => t.Name));
                if (conflict is not null)
                    return Fail(conflict);

                // Reject if this machine's own local LAN overlaps a network reachable through
                // this profile (explicit AllowedIPs, or the backend-reported remote LAN for a
                // full-tunnel profile against a Valenius-managed sidecar) or the VPN IP it would
                // be assigned. No override — the user must resolve the conflict before connecting.
                var lanConflict = _wg.FindLocalLanConflict(configPath, _registration.GetRemoteLanCidrs());
                if (lanConflict is not null)
                    return Fail(lanConflict, isLanConflict: true);

                // Capture WAN IP before the tunnel comes up so we record the real
                // client public IP, not the VPN endpoint address.
                var wanIp = await _registrationSvc.GetWanIpAsync(ct);
                var (success, output) = await _wg.ConnectAsync(configPath);
                if (!success)
                    return Fail($"Failed to start tunnel: {output}");

                _state.SetConnected(tunnelName, username);
                _ = _registrationSvc.LogEventAsync("Connect", username, tunnelName, wanIp, ct);

                // Verification: a sidecar profile with a globally-unique transit network probes
                // the gateway /health endpoint (falling back to the handshake on failure). Plain
                // uploaded profiles (and OSS) have no gateway target, so they use the handshake
                // check only. SkipConnectivityCheck bypasses the gateway probe → handshake.
                var probe     = _registration.GetGatewayProbe(tunnelName);
                var skipCheck = _registration.GetSkipConnectivityCheck();
                if (probe is { } gw && !skipCheck)
                    _ = Task.Run(() => RunGatewayVerifyAsync(tunnelName, gw.Ip, gw.Port, ct), ct);
                else
                    _ = Task.Run(() => RunHandshakeVerifyAsync(tunnelName, ct), ct);

                return Ok();
            }

            case CommandType.Disconnect:
            {
                // A profile name targets one specific tunnel (multi-tunnel per-row toggle).
                // No profile name keeps the legacy behaviour: disconnect whichever single
                // tunnel is active — so old TrayApp builds still work against this service.
                string? tunnelName;
                if (!string.IsNullOrEmpty(cmd.ProfileName))
                {
                    if (!_state.IsConnected(cmd.ProfileName))
                        return Fail($"Profile '{cmd.ProfileName}' is not connected.");
                    tunnelName = cmd.ProfileName;
                }
                else
                {
                    var (isConnected, primaryName, _, _) = _state.GetState(_registration.GetServerProfileName());
                    if (!isConnected || primaryName is null)
                        return Fail("No tunnel is currently active.");
                    tunnelName = primaryName;
                }

                // Only the user who established the tunnel may disconnect it.
                // AutoConnect / PendingConnect tunnels may be disconnected by anyone.
                var connectedUser = _state.GetAllConnected()
                    .FirstOrDefault(t => string.Equals(t.Name, tunnelName, StringComparison.OrdinalIgnoreCase))
                    ?.ConnectedUser;
                var isSystemTunnel = connectedUser is "AutoConnect" or "PendingConnect";
                if (!isSystemTunnel && connectedUser is not null &&
                    !string.Equals(connectedUser, username, StringComparison.OrdinalIgnoreCase))
                    return Fail("Not authorized to disconnect another user's tunnel.");

                var (success, output) = await _wg.DisconnectAsync(tunnelName);
                if (!success)
                    return Fail($"Failed to stop tunnel: {output}");

                _state.SetDisconnected(tunnelName);
                _ = _registrationSvc.LogEventAsync("Disconnect", connectedUser ?? username, tunnelName, ct: ct);
                return Ok();
            }

            case CommandType.CheckUpdate:
            {
                var result = await _updater.CheckAsync(ct);
                if (result.UpdateAvailable)
                    _ = _updater.CheckAndApplyUpdateAsync(ct);
                return Ok(result, ValeniusJsonContext.Default.VersionCheckResult);
            }

            case CommandType.Register:
            {
                var (isActive, dupPending, error) = await _registrationSvc.TryRegisterAsync(
                    username: username,
                    userSid:  userSid,
                    ct:       ct);
                var message = dupPending
                    ? $"A client named '{Environment.MachineName}' already exists. Ask your administrator to reassign it to this machine."
                    : isActive switch
                    {
                        true  => "VPN access is active.",
                        false => "Registration submitted. Contact your administrator to activate this machine.",
                        // The server was reached but returned an error — surface it instead of the
                        // misleading "could not reach server".
                        null  => error is { Length: > 0 }
                            ? $"Registration could not be completed: {error}"
                            : "Could not reach the registration server. Please try again later."
                    };
                var result = new Valenius.Shared.RegistrationResult
                {
                    IsActive = isActive ?? false,
                    Message  = message
                };
                return Ok(result, ValeniusJsonContext.Default.RegistrationResult);
            }

            case CommandType.SyncStatus:
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                var regTask    = _registrationSvc.TryRegisterAsync(ct: cts.Token);
                var updateTask = _updater.CheckAsync(cts.Token);
                try { await regTask; }    catch { }
                try { await updateTask; } catch { }

                var syncStatus = BuildTunnelStatus(username);
                return Ok(syncStatus, ValeniusJsonContext.Default.TunnelStatus);
            }

            case CommandType.DeleteProfile:
            {
                if (!ProfileNameHelper.IsValid(cmd.ProfileName))
                    return Fail("Invalid profile name.");

                if (_configs.IsManaged(username, cmd.ProfileName!))
                    return Fail($"'{cmd.ProfileName}' is managed by the administrator and cannot be deleted.");

                // If this profile is connected, disconnect it first.
                if (_state.IsConnected(cmd.ProfileName!))
                {
                    var (ok, _) = await _wg.DisconnectAsync(cmd.ProfileName!);
                    if (ok)
                    {
                        _state.SetDisconnected(cmd.ProfileName!);
                        _ = _registrationSvc.LogEventAsync("Disconnect", username, cmd.ProfileName!, ct: ct);
                    }
                }

                if (!_configs.DeleteUserProfile(username, cmd.ProfileName!))
                    return Fail($"Profile '{cmd.ProfileName}' not found or could not be deleted.");

                _logger.LogInformation("Profile '{Profile}' deleted by {User}", cmd.ProfileName, username);
                return Ok();
            }

            case CommandType.SetAutoConnect:
            {
                if (cmd.AutoConnectEnabled is null)
                    return Fail("AutoConnectEnabled must be specified.");
                await _autoConnect.SetUserEnabledAsync(cmd.AutoConnectEnabled.Value);
                _logger.LogInformation("AutoConnect set to {Value} by {User}",
                    cmd.AutoConnectEnabled.Value, username);
                return Ok();
            }

            case CommandType.NotifyOffline:
            {
                _ = _registrationSvc.NotifyOfflineAsync();
                return Ok();
            }

            case CommandType.MfaEnrollConfirm:
            {
                if (string.IsNullOrWhiteSpace(cmd.MfaCode))
                    return Fail("A TOTP code is required.");
                var ok = await _registrationSvc.ConfirmMfaEnrollmentAsync(cmd.MfaCode!.Trim());
                // Refresh registration state so MfaEnrollmentOpen/MfaRequired update promptly.
                if (ok)
                    try { await _registrationSvc.TryRegisterAsync(ct: ct); } catch { }
                return ok ? Ok() : Fail("That code was not accepted. Please try again.");
            }

            case CommandType.SendLogs:
            {
                // User-initiated diagnostic log upload (collected + redacted in the service).
                _ = _registrationSvc.TriggerLogUploadAsync("User", CancellationToken.None);
                return Ok();
            }

            case CommandType.SetBackendUrl:
            {
                // First-run: the user provides the backend DNS (the service prepends https://).
                var (saved, warning) = await _registrationSvc.SetBackendUrlAsync(cmd.BackendDns, ct);
                if (!saved)
                    return Fail("Enter a valid server address, for example vpn.example.com");
                // Success with an optional non-fatal advisory (e.g. server unreachable) in Error.
                return new PipeResponse { Success = true, Error = warning };
            }

            default:
                return Fail($"Unknown command: {cmd.Command}");
        }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Command {Cmd} failed", cmd.Command);
            return Fail(ex.Message);
        }
    }

    /// <summary>
    /// Verifies a sidecar tunnel via the HTTP /health gateway probe, falling back to the local
    /// WireGuard handshake check if the gateway probe doesn't succeed (e.g. the health port is
    /// firewalled from VPN clients). Either success marks the tunnel verified.
    /// </summary>
    private async Task RunGatewayVerifyAsync(string tunnelName, string gatewayIp, int healthPort, CancellationToken ct)
    {
        try
        {
            if (await _verifier.VerifyAsync(gatewayIp, healthPort, ct))
            {
                _state.SetVerified(tunnelName, viaGateway: true);
                _logger.LogInformation("Gateway connectivity verified for tunnel '{Tunnel}'.", tunnelName);
                return;
            }

            _logger.LogInformation(
                "Gateway probe did not succeed for '{Tunnel}' — falling back to handshake check.", tunnelName);
            if (await _handshakeVerifier.VerifyAsync(tunnelName, ct))
            {
                _state.SetVerified(tunnelName);
                _logger.LogInformation("Handshake verified for tunnel '{Tunnel}' (gateway fallback).", tunnelName);
            }
        }
        catch (OperationCanceledException) { /* service stopping */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gateway verification threw for tunnel '{Tunnel}'.", tunnelName);
        }
    }

    /// <summary>
    /// Verifies a non-primary (foreign) tunnel via its local WireGuard handshake. Marks the
    /// tunnel verified once a recent handshake is observed; no routed gateway is required.
    /// </summary>
    private async Task RunHandshakeVerifyAsync(string tunnelName, CancellationToken ct)
    {
        try
        {
            if (await _handshakeVerifier.VerifyAsync(tunnelName, ct))
            {
                _state.SetVerified(tunnelName);
                _logger.LogInformation("Handshake verified for tunnel '{Tunnel}'.", tunnelName);
            }
        }
        catch (OperationCanceledException) { /* service stopping */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handshake verification threw for tunnel '{Tunnel}'.", tunnelName);
        }
    }

    /// <summary>
    /// Builds the full tunnel status for the calling user, including the multi-tunnel
    /// <see cref="TunnelStatus.ConnectedTunnels"/> set and the mirrored single-tunnel
    /// compatibility fields (taken from the server/primary profile, else the first entry).
    /// </summary>
    private TunnelStatus BuildTunnelStatus(string username)
    {
        var connected = _state.GetAllConnected();
        var serverProfile = _registration.GetServerProfileName();
        var primary = connected.FirstOrDefault(t =>
                          string.Equals(t.Name, serverProfile, StringComparison.OrdinalIgnoreCase))
                      ?? connected.FirstOrDefault();

        var status = new TunnelStatus
        {
            ConnectedTunnels     = connected,
            IsConnected          = primary is not null,
            IsVerified           = primary?.IsVerified ?? false,
            TunnelName           = primary?.Name,
            ConnectedUser        = primary?.ConnectedUser,
            ConnectedSince       = primary?.ConnectedSince,
            RegistrationIsActive = _registration.GetCachedIsActive(),
            HasStagedConfig      = _configs.HasStagedConfig(),
            UpdateAvailable      = _updater.GetCachedResult()?.UpdateAvailable ?? false,
            BackendUnconfigured  = !_backendUrl.IsConfigured,
            AvailableProfiles    = OrderProfiles(_configs.GetProfiles(username), serverProfile),
            DeletableProfiles    = _configs.GetDeletableProfiles(username),
            AutoConnectEnabled   = _autoConnect.IsEnabled()
        };
        FillMfaState(status);
        return status;
    }

    /// <summary>Copies the latest MFA gating state from the registration manager into a status.</summary>
    private void FillMfaState(TunnelStatus status)
    {
        status.MfaRequired         = _registration.GetMfaRequired();
        status.MfaAuthorizeUrl     = _registration.GetMfaAuthorizeUrl();
        status.MfaSessionExpiresAt = _registration.GetMfaSessionExpiresAt();
        status.MfaEnrollmentOpen   = _registration.GetMfaEnrollmentOpen();
        status.MfaEnrollmentUri    = _registration.GetMfaEnrollmentUri();
        status.MfaApproveNumber    = _registration.GetMfaApproveNumber();
    }

    private static string[] OrderProfiles(string[] profiles, string? serverProfileName)
    {
        if (string.IsNullOrEmpty(serverProfileName) || profiles.Length < 2) return profiles;
        var idx = Array.FindIndex(profiles, p => string.Equals(p, serverProfileName, StringComparison.OrdinalIgnoreCase));
        if (idx <= 0) return profiles;
        var ordered = new string[profiles.Length];
        ordered[0] = profiles[idx];
        Array.Copy(profiles, 0, ordered, 1, idx);
        Array.Copy(profiles, idx + 1, ordered, idx + 1, profiles.Length - idx - 1);
        return ordered;
    }

    private static PipeResponse Ok() => new() { Success = true };

    private static PipeResponse Ok<T>(T data, JsonTypeInfo<T> typeInfo) where T : notnull => new()
    {
        Success = true,
        DataJson = JsonSerializer.Serialize(data, typeInfo)
    };

    private static PipeResponse Fail(string error) => new()
    {
        Success = false,
        Error = error
    };

    private static PipeResponse Fail(string error, bool isLanConflict) => new()
    {
        Success = false,
        Error = error,
        IsLanConflict = isLanConflict
    };

    internal static async Task<string> ReadMessageAsync(PipeStream pipe, CancellationToken ct)
    {
        var lenBuf = new byte[4];
        await pipe.ReadExactlyAsync(lenBuf, ct);
        var length = BitConverter.ToInt32(lenBuf, 0);

        if (length is <= 0 or > 1_048_576)
            throw new InvalidDataException($"Invalid message length: {length}");

        var data = new byte[length];
        await pipe.ReadExactlyAsync(data, ct);
        return Encoding.UTF8.GetString(data);
    }

    internal static async Task WriteMessageAsync(PipeStream pipe, string message, CancellationToken ct)
    {
        var data = Encoding.UTF8.GetBytes(message);
        var lenBuf = BitConverter.GetBytes(data.Length);
        await pipe.WriteAsync(lenBuf, ct);
        await pipe.WriteAsync(data, ct);
        await pipe.FlushAsync(ct);
    }
}
