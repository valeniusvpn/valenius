using System.Net.Http.Json;

namespace Valenius.Service;

/// <summary>
/// Post-connect tunnel verification, shared by every connect path (manual <see cref="PipeServer"/>
/// connect, <see cref="AutoConnectService"/>, and <see cref="ClientRegistrationService"/>'s
/// PendingConnect) so all of them get the same recoverable retry behavior instead of each
/// maintaining (and drifting from) its own copy.
///
/// Retries roughly every 5s. If nothing succeeds within ~60s, marks the tunnel's confirmation
/// as failed (tray shows an amber "Confirmation failed" tag) but keeps retrying at a slower
/// cadence for as long as the tunnel stays connected, rather than giving up outright — see
/// root CLAUDE.md -> "Tunnel verification" for why a one-shot budget was insufficient.
///
/// Also owns the port-443 fallback retry (docs/design/port-443-fallback.md §5): if the customer's
/// admin-configured trigger window (default 20s, <see cref="RegistrationManager.GetFallbackConfig"/>)
/// passes with no successful verification and this profile has a confirmed fallback port, it
/// rewrites the Endpoint port on a fresh temp copy of the config and reinstalls the tunnel once,
/// then lets the same loop keep polling. This does not reset the 60s failed-mark budget or
/// otherwise change the loop's timing — a still-unverified tunnel is marked failed at 60s exactly
/// as before, whichever port it ended up on. A successful switch is recorded on
/// <see cref="TunnelStateManager"/> (tray tag) and reported to the backend (client History) as a
/// best-effort "PortFallback" event.
/// </summary>
public class TunnelVerifier
{
    private static readonly TimeSpan AttemptBudget   = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FastInterval    = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastWindow      = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SlowInterval    = TimeSpan.FromSeconds(20);

    // How long to wait for a torn-down tunnel service to actually disappear before reinstalling
    // it on another port. Windows deletes a service only once its last handle closes.
    private static readonly TimeSpan ServiceRemovalTimeout = TimeSpan.FromSeconds(10);

    private readonly TunnelStateManager _state;
    private readonly ConnectivityVerifier _verifier;
    private readonly HandshakeVerifier _handshakeVerifier;
    private readonly ConfigManager _configs;
    private readonly WireGuardController _wireGuard;
    private readonly RegistrationManager _registration;
    private readonly IHttpClientFactory _httpFactory;
    private readonly BackendUrlProvider _backendUrl;
    private readonly ILogger<TunnelVerifier> _logger;

    public TunnelVerifier(
        TunnelStateManager state,
        ConnectivityVerifier verifier,
        HandshakeVerifier handshakeVerifier,
        ConfigManager configs,
        WireGuardController wireGuard,
        RegistrationManager registration,
        IHttpClientFactory httpFactory,
        BackendUrlProvider backendUrl,
        ILogger<TunnelVerifier> logger)
    {
        _state             = state;
        _verifier          = verifier;
        _handshakeVerifier = handshakeVerifier;
        _configs           = configs;
        _wireGuard         = wireGuard;
        _registration      = registration;
        _httpFactory       = httpFactory;
        _backendUrl        = backendUrl;
        _logger            = logger;
    }

    /// <summary>
    /// Verifies a sidecar tunnel via the HTTP /health gateway probe, falling back to the local
    /// WireGuard handshake check if the gateway probe doesn't succeed (e.g. the health port is
    /// firewalled from VPN clients). Either success marks the tunnel verified.
    /// <paramref name="onVerified"/> is an optional caller-supplied hook (invoked with whether the
    /// success was via gateway) for side effects like reporting a gateway-verified connect upstream.
    /// </summary>
    public Task RunGatewayVerifyAsync(
        string tunnelName, string gatewayIp, int healthPort, CancellationToken ct,
        Action<bool>? onVerified = null)
        => RunLoopAsync(tunnelName, async attemptCt =>
        {
            if (await _verifier.VerifyAsync(gatewayIp, healthPort, AttemptBudget, attemptCt))
                return (true, true);
            if (await _handshakeVerifier.VerifyAsync(tunnelName, AttemptBudget, attemptCt))
                return (true, false);
            return (false, false);
        }, ct, onVerified);

    /// <summary>
    /// Verifies a non-primary (foreign) tunnel, or any profile with no gateway target, via its
    /// local WireGuard handshake. Marks the tunnel verified once a recent handshake is observed;
    /// no routed gateway is required.
    /// </summary>
    public Task RunHandshakeVerifyAsync(string tunnelName, CancellationToken ct, Action<bool>? onVerified = null)
        => RunLoopAsync(tunnelName,
            async attemptCt => (await _handshakeVerifier.VerifyAsync(tunnelName, AttemptBudget, attemptCt), false),
            ct, onVerified);

    private async Task RunLoopAsync(
        string tunnelName, Func<CancellationToken, Task<(bool Ok, bool ViaGateway)>> attempt,
        CancellationToken ct, Action<bool>? onVerified)
    {
        var start = DateTime.UtcNow;
        var failedMarked = false;
        var fallbackTried = false;

        while (_state.IsConnected(tunnelName) && !ct.IsCancellationRequested)
        {
            try
            {
                var (ok, viaGateway) = await attempt(ct);
                if (ok)
                {
                    _state.SetVerified(tunnelName, viaGateway);
                    _logger.LogInformation("Verification succeeded for tunnel '{Tunnel}' ({Via}){Recovered}.",
                        tunnelName, viaGateway ? "gateway" : "handshake",
                        failedMarked ? " after a prior confirmation failure" : "");
                    onVerified?.Invoke(viaGateway);
                    return;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Verification attempt failed for tunnel '{Tunnel}' (retrying)", tunnelName);
            }

            if (!fallbackTried && _registration.GetFallbackConfig(tunnelName) is { } fb
                && DateTime.UtcNow - start >= TimeSpan.FromSeconds(fb.TriggerSeconds))
            {
                fallbackTried = true;
                await TrySwitchToFallbackPortAsync(tunnelName, fb.Port, fb.TriggerSeconds);
            }

            if (!failedMarked && DateTime.UtcNow - start >= FastWindow)
            {
                failedMarked = true;
                _state.MarkVerificationFailed(tunnelName);
                _logger.LogWarning(
                    "Confirmation failed for tunnel '{Tunnel}' after {Seconds}s — still retrying in the background.",
                    tunnelName, (int)FastWindow.TotalSeconds);
            }

            try
            {
                await Task.Delay(failedMarked ? SlowInterval : FastInterval, ct);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Reinstalls <paramref name="tunnelName"/> against its UDP fallback port (already confirmed
    /// active by the caller — <see cref="RegistrationManager.GetFallbackConfig"/>) after
    /// <paramref name="triggerSeconds"/> passed with no successful verification. Runs at most once
    /// per connect (guarded by the caller's <c>fallbackTried</c> flag) — this is a single
    /// alternate-port attempt, not a permanent pin to the fallback port; a later reconnect always
    /// starts on the primary port again. On success, records the switch on
    /// <see cref="TunnelStateManager"/> (tray tag) and reports it to the backend (best-effort,
    /// same as every other <c>LogEventAsync</c>-style call — a failed report never blocks the
    /// switch itself). On failure the tunnel is put back on its primary port — see
    /// <see cref="RestorePrimaryPortAsync"/> — so a failed switch can never cost the client the
    /// tunnel it already had.
    /// </summary>
    private async Task TrySwitchToFallbackPortAsync(string tunnelName, int fallbackPort, int triggerSeconds)
    {
        var configPath = _configs.ResolveConfigPathForTunnel(tunnelName);
        if (configPath is null) return;

        _logger.LogInformation(
            "No successful verification for tunnel '{Tunnel}' after {Seconds}s — retrying via UDP fallback port {Port}.",
            tunnelName, triggerSeconds, fallbackPort);

        try
        {
            // Tear the primary-port tunnel down and wait for the service to actually disappear.
            // Windows service deletion is asynchronous, so reinstalling immediately used to fail
            // with "Tunnel already installed and running" — which left the client with NO tunnel
            // at all while TunnelStateManager still said Connected, and no way out short of
            // restarting the service. Never reinstall without confirming the old service is gone.
            var (disconnectOk, disconnectOutput) = await _wireGuard.DisconnectAsync(tunnelName);
            if (!disconnectOk && !WireGuardController.IsTunnelAlreadyGone(disconnectOutput))
            {
                _logger.LogWarning(
                    "Fallback-port switch aborted for tunnel '{Tunnel}' — could not stop it: {Output}",
                    tunnelName, disconnectOutput);
                return;
            }

            await _wireGuard.WaitForTunnelServiceGoneAsync(tunnelName, ServiceRemovalTimeout);

            var (success, output) = await _wireGuard.ConnectAsync(configPath, fallbackPort);
            if (success)
            {
                _state.MarkUsedFallbackPort(tunnelName, fallbackPort);
                await ReportPortFallbackAsync(tunnelName, fallbackPort, triggerSeconds);
                return;
            }

            _logger.LogWarning("Fallback-port reconnect failed for tunnel '{Tunnel}': {Output}", tunnelName, output);
            await RestorePrimaryPortAsync(tunnelName, configPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback-port reconnect threw for tunnel '{Tunnel}'.", tunnelName);
            await RestorePrimaryPortAsync(tunnelName, configPath);
        }
    }

    /// <summary>
    /// Puts the tunnel back on its primary port after a failed fallback-port switch. If even that
    /// fails, the state is cleared so the client stops reporting a tunnel that no longer exists —
    /// a phantom "connected" is worse than an honest disconnect, because the tray then only offers
    /// Disconnect and the user can never get back to a working tunnel on their own.
    /// </summary>
    private async Task RestorePrimaryPortAsync(string tunnelName, string configPath)
    {
        // If a tunnel service is somehow still running, the fallback install was most likely
        // refused *because* the old tunnel never went away — meaning the tunnel we already have
        // is the one we want. Leave it alone (and leave the state connected) rather than tearing
        // down a working tunnel or reporting a disconnect that didn't happen.
        if (_wireGuard.IsTunnelServiceRunning(tunnelName))
        {
            _logger.LogInformation(
                "Tunnel '{Tunnel}' is still running after the failed fallback-port switch — keeping it as is.",
                tunnelName);
            return;
        }

        try
        {
            await _wireGuard.WaitForTunnelServiceGoneAsync(tunnelName, ServiceRemovalTimeout);
            var (restored, output) = await _wireGuard.ConnectAsync(configPath);
            if (restored)
            {
                _logger.LogInformation(
                    "Restored tunnel '{Tunnel}' on its primary port after the fallback-port attempt failed.",
                    tunnelName);
                return;
            }
            _logger.LogWarning(
                "Could not restore tunnel '{Tunnel}' on its primary port: {Output}", tunnelName, output);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Restoring tunnel '{Tunnel}' on its primary port threw.", tunnelName);
        }

        _state.SetDisconnected(tunnelName);
        await ReportEventAsync(tunnelName, "Disconnect",
            "Tunnel lost while switching to the UDP fallback port and could not be re-established.");
    }

    /// <summary>Best-effort report of a fallback-port switch to the backend so it shows up in the
    /// client's connection history (Admin/Details, ConnectionHistory).</summary>
    private Task ReportPortFallbackAsync(string tunnelName, int fallbackPort, int triggerSeconds)
        => ReportEventAsync(tunnelName, "PortFallback",
            $"Switched to UDP {fallbackPort} after {triggerSeconds}s with no confirmation on the primary port.");

    /// <summary>Best-effort connection event report — same pattern and same swallow-on-failure as
    /// <see cref="ClientRegistrationService.LogEventAsync"/>, duplicated here rather than called
    /// there to avoid a circular dependency (ClientRegistrationService already depends on
    /// TunnelVerifier for RunPhase2Async).</summary>
    private async Task ReportEventAsync(string tunnelName, string eventType, string detail)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();
            var request  = new LogEventRequest(clientId.ToString(), eventType, "", tunnelName, "", "", detail);
            await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/event",
                request,
                ServiceJsonContext.Default.LogEventRequest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report {EventType} event to backend for tunnel '{Tunnel}'.",
                eventType, tunnelName);
        }
    }
}
