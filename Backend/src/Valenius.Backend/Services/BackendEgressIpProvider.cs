namespace Valenius.Backend.Services;

/// <summary>
/// Resolves the public (WAN) IPv4 address this backend appears to come from, so the admin UI can
/// tell an operator exactly which source address to allow through a sidecar host's firewall —
/// e.g. "allow TCP 9003 from 203.0.113.10/32" instead of the far worse "expose 9003 to the world".
///
/// <para>
/// Only meaningful for sidecars reached over the public internet. When the backend reaches a
/// sidecar across a site-to-site VPN the sidecar sees the tunnel-internal source address instead,
/// and no firewall rule is needed at all — which is why callers only surface this when a sidecar
/// is actually unreachable (see Admin/Customers/Details).
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// The backend cannot determine its own egress address without asking something outside itself, so
/// this makes one outbound HTTPS call to an IP echo service. That call is:
/// <list type="bullet">
///   <item>lazy — only made when a caller actually needs the value (i.e. a sidecar is unreachable
///   and the admin is looking at that customer's page), never on startup or on a hot path;</item>
///   <item>cached for <see cref="CacheDuration"/>, since an egress IP rarely changes;</item>
///   <item>best-effort — a failure yields null and the UI falls back to generic wording rather
///   than blocking or erroring.</item>
/// </list>
/// </para>
/// <para>
/// A self-hosted operator who does not want the backend contacting a third party (or whose egress
/// IP the echo service would get wrong, e.g. behind an outbound proxy) can set
/// <c>Valenius:BackendPublicIp</c> — configuration wins and suppresses the call entirely.
/// Checked with IsNullOrWhiteSpace rather than <c>??</c> on purpose: an empty placeholder in
/// appsettings.json satisfies <c>??</c> and would silently shadow a real value, which has already
/// caused one production bug in this codebase (see Backend/CLAUDE.md → licensing server URL).
/// </para>
/// </remarks>
public sealed class BackendEgressIpProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<BackendEgressIpProvider> logger)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan Timeout       = TimeSpan.FromSeconds(4);

    /// <summary>IPv4-only echo endpoint — a v6 answer would be useless for the firewall rule the
    /// caller is about to suggest, since the sidecar sees the v4 source address.</summary>
    private const string EchoUrl = "https://api4.ipify.org";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string?  _cached;
    private DateTime _cachedAt = DateTime.MinValue;

    /// <summary>
    /// The configured or last-resolved public IPv4 address, or null if it could not be determined.
    /// Safe to call from a page handler: never throws, and at most one lookup runs at a time.
    /// </summary>
    public async Task<string?> GetAsync(CancellationToken ct = default)
    {
        var configured = config["Valenius:BackendPublicIp"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

        if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheDuration) return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: a caller that queued behind an in-flight lookup should use
            // its result rather than immediately firing a second identical request.
            if (_cached is not null && DateTime.UtcNow - _cachedAt < CacheDuration) return _cached;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);

            var http = httpClientFactory.CreateClient();
            var body = (await http.GetStringAsync(EchoUrl, cts.Token)).Trim();

            if (!System.Net.IPAddress.TryParse(body, out var parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                logger.LogDebug("Egress IP lookup returned an unusable value: {Body}", body);
                return _cached; // keep any previous good value rather than discarding it
            }

            _cached   = parsed.ToString();
            _cachedAt = DateTime.UtcNow;
            return _cached;
        }
        catch (Exception ex)
        {
            // Offline, blocked outbound, DNS failure — all expected in some deployments.
            logger.LogDebug(ex, "Egress IP lookup failed; firewall guidance will omit the address.");
            return _cached;
        }
        finally { _gate.Release(); }
    }
}
