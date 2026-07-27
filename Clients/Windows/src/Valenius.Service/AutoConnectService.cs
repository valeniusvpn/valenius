using System.Net.NetworkInformation;
using System.Threading.Channels;

namespace Valenius.Service;

/// <summary>
/// Enforces the auto-connect / auto-disconnect policy:
///
///   - Not in trusted network + VPN off  ->  connect automatically
///   - In trusted network     + VPN on   ->  disconnect automatically
///
/// Triggered immediately by <see cref="NetworkChange.NetworkAddressChanged"/> so the
/// reaction happens within seconds of a network transition.  A 5-minute periodic
/// fallback handles edge cases where the OS event is not raised (e.g. DHCP renewal
/// that keeps the same address, or a laptop waking from sleep).
///
/// Applies independently to up to two targets (see <see cref="AutoConnectTarget"/> /
/// <see cref="GetTargets"/>): the generic admin-configured profile (AdminEnabled via
/// heartbeat AND not disabled by the user's tray toggle), and — new — the Windows device
/// tunnel: the customer's own native sidecar profile, connected automatically before any
/// user logs on (per-Customer/Client backend flag, no user-facing toggle). Both share the
/// same trusted-network detection and startup-retry loop; a machine with nobody ever
/// logged in still gets the device tunnel via the machine-level config store (see
/// <see cref="ConfigManager.SaveDeviceTunnelConfig"/>).
/// </summary>
public class AutoConnectService : BackgroundService
{
    // How long to wait before re-checking if no network event arrives.
    private static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(5);

    // Debounce: Windows often fires several NetworkAddressChanged events in quick
    // succession during a single physical transition (adapter down, DHCP, DNS, …).
    // Wait this long after the first event before acting so the stack has settled.
    private static readonly TimeSpan EventDebounce = TimeSpan.FromSeconds(3);

    // Minimum gap between auto-actions to prevent flapping.
    private static readonly TimeSpan ActionCooldown = TimeSpan.FromSeconds(60);

    // After the debounce, wait a little longer before re-verifying so the new network
    // path (DHCP lease, default route, WireGuard endpoint roam) has time to settle.
    private static readonly TimeSpan ReverifySettle = TimeSpan.FromSeconds(5);

    // Re-verify uses a tighter budget than the connect-time verifiers (network changes
    // are frequent; a definitive answer comes quickly either way).
    private static readonly TimeSpan ReverifyBudget = TimeSpan.FromSeconds(15);

    private readonly AutoConnectManager _autoConnect;
    private readonly TunnelStateManager _state;
    private readonly WireGuardController _wg;
    private readonly ConfigManager _configs;
    private readonly RegistrationManager _registration;
    private readonly ClientRegistrationService _registrationSvc;
    private readonly ConnectivityVerifier _verifier;
    private readonly HandshakeVerifier _handshakeVerifier;
    private readonly ILogger<AutoConnectService> _logger;

    /// <summary>An independently-evaluated auto-connect policy: an admin-picked profile
    /// (<c>Label = "AutoConnect"</c>) or the Windows device tunnel (<c>Label = "DeviceTunnel"</c>)
    /// -- the customer's own native sidecar profile, connected automatically at boot before any
    /// user logs on. Both share the same trusted-network detection, Phase-1 health pre-check, and
    /// <see cref="ReverifyActiveTunnelsAsync"/> below; only the target profile, enablement, and
    /// connect-log attribution differ.</summary>
    private readonly record struct AutoConnectTarget(string Label, string? ProfileName, string ConnectedByFallback);

    private readonly Dictionary<string, DateTime> _lastActionByTarget = new(StringComparer.Ordinal);

    private IEnumerable<AutoConnectTarget> GetTargets()
    {
        if (_autoConnect.IsEnabled())
            yield return new AutoConnectTarget("AutoConnect", _autoConnect.GetProfileName(), "AutoConnect");
        if (_registration.GetDeviceTunnelEnabled())
            yield return new AutoConnectTarget("DeviceTunnel", _registration.GetServerProfileName(), "DeviceTunnel");
    }

    public AutoConnectService(
        AutoConnectManager autoConnect,
        TunnelStateManager state,
        WireGuardController wg,
        ConfigManager configs,
        RegistrationManager registration,
        ClientRegistrationService registrationSvc,
        ConnectivityVerifier verifier,
        HandshakeVerifier handshakeVerifier,
        ILogger<AutoConnectService> logger)
    {
        _autoConnect       = autoConnect;
        _state             = state;
        _wg                = wg;
        _configs           = configs;
        _registration      = registration;
        _registrationSvc   = registrationSvc;
        _verifier          = verifier;
        _handshakeVerifier = handshakeVerifier;
        _logger            = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Bounded channel with capacity 1: multiple rapid-fire events collapse into
        // a single pending signal so we never queue up a backlog of checks.
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        NetworkAddressChangedEventHandler onNetworkChanged = (_, _) =>
            channel.Writer.TryWrite(true);

        NetworkChange.NetworkAddressChanged += onNetworkChanged;
        try
        {
            // Let the service fully start up before the first check.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);

            // Run an initial check in case the machine is already outside its trusted network.
            // Retries briefly if the first attempt doesn't result in a connection — this is the
            // path that reconnects the tunnel after a service restart (a normal boot, or the
            // restart the auto-updater's installer triggers), since WireGuard tunnel services are
            // deliberately left on Manual start (see WireGuardController.ConnectAsync) and never
            // come back on their own. See RunStartupCheckAsync for why a single attempt isn't
            // enough.
            await RunStartupCheckAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Wait for either a network-change event or the periodic fallback timer.
                await WaitForTriggerAsync(channel.Reader, stoppingToken);

                if (stoppingToken.IsCancellationRequested) break;

                await RunCheckSafeAsync(stoppingToken);
            }
        }
        finally
        {
            NetworkChange.NetworkAddressChanged -= onNetworkChanged;
        }
    }

    /// <summary>
    /// Waits until a network-change signal arrives (with debounce) or the fallback
    /// interval elapses, whichever comes first.
    /// </summary>
    private async Task WaitForTriggerAsync(ChannelReader<bool> reader, CancellationToken ct)
    {
        // Build a cancellation token that fires after FallbackInterval.
        using var fallbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fallbackCts.CancelAfter(FallbackInterval);

        try
        {
            // Block until a network-change event is written OR the fallback fires.
            await reader.ReadAsync(fallbackCts.Token);

            // Debounce: Windows raises several events per physical transition.
            // Wait for the storm to pass, then drain anything that accumulated.
            await Task.Delay(EventDebounce, ct);
            while (reader.TryRead(out _)) { }

            _logger.LogDebug("AutoConnect: network change detected — running check.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Fallback timer fired — proceed with a routine check.
            _logger.LogDebug("AutoConnect: fallback timer — running check.");
        }
    }

    // Retry window for the startup check only. A network stack that hasn't fully settled yet
    // (DHCP lease, default route, DNS) right after a boot or an update-triggered service restart
    // can make a single attempt fail transiently (e.g. the Phase 1 health probe times out) —
    // without a retry here, the only remaining trigger is a genuine NetworkAddressChanged event
    // (which won't fire if the network hasn't actually changed) or the 5-minute fallback timer,
    // so "auto-connect enabled" could silently sit disconnected for minutes, or indefinitely,
    // after every update. A failed attempt never sets _lastActionByTarget, so ActionCooldown does
    // not block these retries.
    private static readonly TimeSpan StartupRetryInterval = TimeSpan.FromSeconds(10);
    private const int StartupRetryAttempts = 6; // ~1 minute total, then fall into the normal loop.

    private async Task RunStartupCheckAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= StartupRetryAttempts; attempt++)
        {
            await RunCheckSafeAsync(ct);

            if (!IsAutoConnectPending())
                return;

            if (attempt < StartupRetryAttempts)
            {
                _logger.LogDebug("AutoConnect: startup check attempt {Attempt} did not connect — retrying.", attempt);
                await Task.Delay(StartupRetryInterval, ct);
            }
        }
    }

    /// <summary>
    /// True if AutoConnectAsync still has something to do: auto-connect enabled, trusted networks
    /// configured, current network not trusted, and the target tunnel not yet connected. Mirrors
    /// CheckAsync's own connect condition (minus the action cooldown, which a failed attempt never
    /// sets) — used only to decide whether the startup retry loop should keep going.
    /// </summary>
    private bool IsAutoConnectPending()
    {
        var networks = _autoConnect.GetTrustedNetworks();
        if (networks.Length == 0) return false;
        if (NetworkDetector.IsInTrustedNetwork(networks)) return false;

        foreach (var target in GetTargets())
        {
            var targetPath = FindConfigPath(target);
            var targetName = targetPath is null
                ? null
                : (ConfigManager.GetTunnelNameFromPath(targetPath) ?? Path.GetFileNameWithoutExtension(targetPath));
            if (targetName is null || !_state.IsConnected(targetName))
                return true;
        }
        return false;
    }

    private async Task RunCheckSafeAsync(CancellationToken ct)
    {
        try
        {
            // Let the new network path settle before probing (DHCP, default route,
            // WireGuard endpoint roam). Then re-verify every active tunnel — this runs
            // regardless of whether auto-connect is enabled — and finally apply the
            // auto-connect policy (which may reconnect a torn-down primary tunnel).
            await Task.Delay(ReverifySettle, ct);
            await ReverifyActiveTunnelsAsync(ct);
            await CheckAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AutoConnect: check failed.");
        }
    }

    // ── Network-change re-verification ────────────────────────────────────────

    /// <summary>
    /// After a network change, re-confirms each active tunnel and tears down only those we can
    /// be confident are dead. The only reliable "dead" signal is a tunnel that was verified
    /// end-to-end via the gateway /health probe and whose probe now fails for the whole budget
    /// (the new network can't reach the sidecar). Tunnels that were never gateway-verified
    /// (health-port firewalled, plain uploaded .conf, foreign profiles with no gateway) are
    /// left connected — a stale handshake can't distinguish "dead" from "idle/roaming", so
    /// tearing them down would risk dropping a working VPN. The verified flag is refreshed
    /// either way. Runs regardless of the auto-connect setting / cooldown.
    /// </summary>
    private async Task ReverifyActiveTunnelsAsync(CancellationToken ct)
    {
        var active = _state.GetAllConnected();
        if (active.Length == 0) return;

        foreach (var tunnel in active)
        {
            ct.ThrowIfCancellationRequested();
            var name           = tunnel.Name;
            var wasViaGateway  = _state.IsVerifiedViaGateway(name);
            var probe          = _registration.GetGatewayProbe(name);
            var probeEnabled   = probe is not null && !_registration.GetSkipConnectivityCheck();

            // Drop the green check while we re-confirm so the tray doesn't show stale "Verified".
            _state.SetUnverified(name);

            // Gateway tunnels: the /health probe is the trusted end-to-end signal.
            if (probeEnabled && probe is { } gw)
            {
                if (await _verifier.VerifyAsync(gw.Ip, gw.Port, ReverifyBudget, ct))
                {
                    _state.SetVerified(name, viaGateway: true);
                    _logger.LogDebug("Reverify: tunnel '{Tunnel}' gateway-confirmed after network change.", name);
                    continue;
                }

                if (wasViaGateway)
                {
                    _logger.LogWarning(
                        "Reverify: tunnel '{Tunnel}' gateway unreachable after network change — disconnecting.", name);
                    var (ok, output) = await _wg.DisconnectAsync(name);
                    if (!ok)
                    {
                        _logger.LogWarning("Reverify: disconnect of '{Tunnel}' failed: {Output}", name, output);
                        continue;
                    }
                    _state.SetDisconnected(name);
                    _ = _registrationSvc.LogEventAsync("Disconnect", tunnel.ConnectedUser ?? "ConnectionLost", name, ct: ct);
                    continue;
                }

                _logger.LogDebug(
                    "Reverify: gateway probe for '{Tunnel}' failed but it was never gateway-verified " +
                    "(health likely firewalled) — keeping it and falling back to handshake.", name);
            }

            // Non-gateway (or firewalled-health) tunnels: best-effort handshake refresh, no teardown.
            if (await _handshakeVerifier.VerifyAsync(name, ReverifyBudget, ct))
                _state.SetVerified(name);
        }
    }

    // ── Core check logic ──────────────────────────────────────────────────────

    private async Task CheckAsync(CancellationToken ct)
    {
        var networks = _autoConnect.GetTrustedNetworks();
        if (networks.Length == 0) return;

        var inTrusted = NetworkDetector.IsInTrustedNetwork(networks);

        foreach (var target in GetTargets())
        {
            // Cooldown: don't act again too quickly after this target's last auto-action. Tracked
            // per target so one policy's action never blocks the other's in the same check.
            var lastAction = _lastActionByTarget.GetValueOrDefault(target.Label, DateTime.MinValue);
            if (DateTime.UtcNow - lastAction < ActionCooldown) continue;

            // Auto-connect acts only on this target's own profile — never on foreign tunnels the
            // user connected by hand, and never on the other target's profile.
            var targetPath = FindConfigPath(target);
            var targetName = targetPath is null
                ? null
                : (ConfigManager.GetTunnelNameFromPath(targetPath) ?? Path.GetFileNameWithoutExtension(targetPath));
            var targetConnected = targetName is not null && _state.IsConnected(targetName);

            if (!targetConnected && !inTrusted)
                await AutoConnectAsync(target, ct);
            else if (targetConnected && inTrusted)
                await AutoDisconnectAsync(target, targetName!, ct);
        }
    }

    // ── Auto-connect ──────────────────────────────────────────────────────────

    private static readonly HttpClient _healthCheckHttp = new(
        new HttpClientHandler
        {
            // Same rationale as ConnectivityVerifier: the health endpoint uses the
            // internal CA.  The Windows client does not have the CA installed.
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        })
    { Timeout = TimeSpan.FromSeconds(5) };

    private async Task AutoConnectAsync(AutoConnectTarget target, CancellationToken ct)
    {
        if (!await _registration.IsAllowedToConnectAsync())
        {
            _logger.LogInformation("AutoConnect[{Target}]: skipped — client not authorized.", target.Label);
            return;
        }

        // Phase 1: pre-connect public reachability check (spec §3.1).
        // Skips auto-connect silently if the sidecar's public endpoint is unreachable.
        // This prevents entering a broken tunnel state when the server is down.
        var serverHealthUrl = _registration.GetServerHealthUrl();
        if (!string.IsNullOrEmpty(serverHealthUrl))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var resp = await _healthCheckHttp.GetAsync(serverHealthUrl, cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("AutoConnect[{Target}]: Phase 1 check {Url} returned {Status} — skipping.", target.Label, serverHealthUrl, (int)resp.StatusCode);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug("AutoConnect[{Target}]: Phase 1 check failed ({Msg}) — skipping.", target.Label, ex.Message);
                return;
            }
        }

        var configPath = FindConfigPath(target);
        if (configPath is null)
        {
            _logger.LogWarning("AutoConnect[{Target}]: no config found (profile: {Profile}).",
                target.Label, target.ProfileName ?? "<first available>");
            return;
        }

        var tunnelName = ConfigManager.GetTunnelNameFromPath(configPath)
                      ?? Path.GetFileNameWithoutExtension(configPath);
        _logger.LogInformation("AutoConnect[{Target}]: connecting tunnel '{Tunnel}'.", target.Label, tunnelName);

        var wanIp = await _registrationSvc.GetWanIpAsync(ct);
        var (success, output) = await _wg.ConnectAsync(configPath);

        if (!success)
        {
            _logger.LogWarning("AutoConnect[{Target}]: wireguard connect failed: {Output}", target.Label, output);
            return;
        }

        _lastActionByTarget[target.Label] = DateTime.UtcNow;

        // Prefer the actual logged-in Windows user (see RegistrationManager.GetLastKnownUsername)
        // over the target's trigger-label fallback — the label isn't useful on the admin client
        // list's "Last user" column, and the real username is fresh since the tray polls every 5s.
        var connectedBy = _registration.GetLastKnownUsername() ?? target.ConnectedByFallback;
        _state.SetConnected(tunnelName, connectedBy);
        _ = _registrationSvc.LogEventAsync("Connect", connectedBy, tunnelName, wanIp, ct);

        _ = Task.Run(() => _registrationSvc.RunPhase2Async(tunnelName, ct), ct);
    }

    // ── Auto-disconnect ───────────────────────────────────────────────────────

    private async Task AutoDisconnectAsync(AutoConnectTarget target, string tunnelName, CancellationToken ct)
    {
        _logger.LogInformation(
            "AutoConnect[{Target}]: back in trusted network — disconnecting tunnel '{Tunnel}'.", target.Label, tunnelName);

        var connectedUser = _state.GetAllConnected()
            .FirstOrDefault(t => string.Equals(t.Name, tunnelName, StringComparison.OrdinalIgnoreCase))
            ?.ConnectedUser;

        var (success, output) = await _wg.DisconnectAsync(tunnelName);

        if (!success)
        {
            _logger.LogWarning("AutoConnect[{Target}]: wireguard disconnect failed: {Output}", target.Label, output);
            return;
        }

        _state.SetDisconnected(tunnelName);
        _lastActionByTarget[target.Label] = DateTime.UtcNow;
        _ = _registrationSvc.LogEventAsync("Disconnect", connectedUser ?? target.ConnectedByFallback, tunnelName, ct: ct);
    }

    // ── Config path resolution ────────────────────────────────────────────────

    private string? FindConfigPath(AutoConnectTarget target)
    {
        // A per-OS-user copy may already exist from a prior login / cross-user profile sync —
        // prefer it over the machine-level store when present. The DeviceTunnel target additionally
        // falls back to the machine-level store (ConfigManager.SaveDeviceTunnelConfig) so it still
        // works on a machine nobody has ever locally logged into; the generic AutoConnect target
        // has no such fallback (it was never meant to work before any login).
        var path = _configs.GetAnyConfigPath(target.ProfileName);
        if (path is not null) return path;
        if (target.Label == "DeviceTunnel" && target.ProfileName is not null)
            return _configs.GetDeviceTunnelConfigPath(target.ProfileName);
        return null;
    }
}
