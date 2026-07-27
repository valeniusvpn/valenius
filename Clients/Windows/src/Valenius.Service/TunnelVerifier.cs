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
/// </summary>
public class TunnelVerifier
{
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan FastInterval   = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastWindow     = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SlowInterval   = TimeSpan.FromSeconds(20);

    private readonly TunnelStateManager _state;
    private readonly ConnectivityVerifier _verifier;
    private readonly HandshakeVerifier _handshakeVerifier;
    private readonly ILogger<TunnelVerifier> _logger;

    public TunnelVerifier(
        TunnelStateManager state,
        ConnectivityVerifier verifier,
        HandshakeVerifier handshakeVerifier,
        ILogger<TunnelVerifier> logger)
    {
        _state             = state;
        _verifier          = verifier;
        _handshakeVerifier = handshakeVerifier;
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
}
