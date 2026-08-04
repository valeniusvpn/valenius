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
    /// switch itself).
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
            await _wireGuard.DisconnectAsync(tunnelName);
            var (success, output) = await _wireGuard.ConnectAsync(configPath, fallbackPort);
            if (!success)
            {
                _logger.LogWarning("Fallback-port reconnect failed for tunnel '{Tunnel}': {Output}", tunnelName, output);
                return;
            }
            _state.MarkUsedFallbackPort(tunnelName, fallbackPort);
            await ReportPortFallbackAsync(tunnelName, fallbackPort, triggerSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallback-port reconnect threw for tunnel '{Tunnel}'.", tunnelName);
        }
    }

    /// <summary>Best-effort report of a fallback-port switch to the backend so it shows up in the
    /// client's connection history (Admin/Details, ConnectionHistory) — same pattern and same
    /// swallow-on-failure as <see cref="ClientRegistrationService.LogEventAsync"/>, duplicated here
    /// rather than called there to avoid a circular dependency (ClientRegistrationService already
    /// depends on TunnelVerifier for RunPhase2Async).</summary>
    private async Task ReportPortFallbackAsync(string tunnelName, int fallbackPort, int triggerSeconds)
    {
        try
        {
            var backendUrl = _backendUrl.Current;
            if (string.IsNullOrEmpty(backendUrl)) return;

            var http     = _httpFactory.CreateClient("Registration");
            var clientId = await _registration.GetOrCreateClientIdAsync();
            var detail   = $"Switched to UDP {fallbackPort} after {triggerSeconds}s with no confirmation on the primary port.";
            var request  = new LogEventRequest(clientId.ToString(), "PortFallback", "", tunnelName, "", "", detail);
            await http.PostAsJsonAsync(
                $"{backendUrl}/api/clients/event",
                request,
                ServiceJsonContext.Default.LogEventRequest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report PortFallback event to backend for tunnel '{Tunnel}'.", tunnelName);
        }
    }
}
