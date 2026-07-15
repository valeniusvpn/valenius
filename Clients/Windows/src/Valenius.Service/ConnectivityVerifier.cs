namespace Valenius.Service;

/// <summary>
/// Background connectivity verifier.
///
/// After WireGuard reports connected the caller fires <see cref="VerifyAsync"/> as a
/// fire-and-forget task.  The probe hits:
///
///   GET https://&lt;serverVpnIp&gt;:&lt;port&gt;/health
///
/// using the sidecar's VPN IP — forcing the request through the WireGuard tunnel.
/// A 200 response confirms end-to-end connectivity and triggers the UI checkmark.
///
/// The probe is retried every second for up to 30 seconds to accommodate the WireGuard
/// handshake and kernel route injection happening asynchronously after the tunnel service
/// starts.  Server certificate validation is intentionally relaxed: the request travels
/// through the WireGuard tunnel (already authenticated), so a self-signed internal cert
/// is acceptable here.
/// </summary>
public class ConnectivityVerifier
{
    private static readonly TimeSpan ProbeTimeout   = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RetryInterval  = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TotalBudget    = TimeSpan.FromSeconds(30);

    private static readonly HttpClient _http = new(
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        });

    private readonly ILogger<ConnectivityVerifier> _logger;

    public ConnectivityVerifier(ILogger<ConnectivityVerifier> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Probes the sidecar health endpoint through the VPN tunnel, retrying every second.
    /// Returns true on the first HTTP 200 response.  Returns false if no probe succeeds
    /// within 30 seconds.
    /// </summary>
    public Task<bool> VerifyAsync(
        string serverVpnIp,
        int    managementPort,
        CancellationToken ct)
        => VerifyAsync(serverVpnIp, managementPort, TotalBudget, ct);

    /// <summary>
    /// As <see cref="VerifyAsync(string,int,CancellationToken)"/> but with a caller-supplied
    /// budget. The network-change re-verifier uses a shorter budget than the connect path,
    /// since transitions are frequent and a definitive 200 (or its absence) comes quickly.
    /// </summary>
    public async Task<bool> VerifyAsync(
        string serverVpnIp,
        int    managementPort,
        TimeSpan budget,
        CancellationToken ct)
    {
        var url      = $"https://{serverVpnIp}:{managementPort}/health";
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ProbeTimeout);
                var response = await _http.GetAsync(url, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("ConnectivityVerifier: {Url} → {Status}", url, (int)response.StatusCode);
                    return true;
                }
                _logger.LogDebug("ConnectivityVerifier: {Url} → {Status} (retrying)", url, (int)response.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogDebug("ConnectivityVerifier: probe failed — {Msg} (retrying)", ex.Message);
            }

            await Task.Delay(RetryInterval, ct);
        }

        _logger.LogWarning("ConnectivityVerifier: {Url} not reachable within {Budget} s", url, (int)budget.TotalSeconds);
        return false;
    }
}
