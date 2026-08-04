using System.IO.Compression;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Valenius.Backend.Filters;
using Valenius.Backend.Monitoring;

namespace Valenius.Backend.Controllers;

/// <summary>
/// External NMS/monitoring surface (docs/valenius-zabbix-monitoring-concept.md). A separate
/// route prefix from the Management API (<c>/api/mgmt/v1/**</c>) but the exact same auth core
/// — <see cref="ApiTokenFilter"/>/<see cref="RequireScopeAttribute"/> have no coupling to that
/// prefix (see <c>ManagementApiScopeCatalog</c>'s "Monitoring" group for the
/// <c>monitoring:read</c> scope). Unlike Management API controllers this is **not** covered by
/// Backend.Pro's boot-time <c>AssertManagementApiScopesDeclared</c> (it only scans
/// <c>api/mgmt/</c> routes) — both actions below declare <see cref="RequireScopeAttribute"/>
/// explicitly for that reason, and <see cref="ApiTokenFilter"/> fails closed if either ever
/// doesn't.
/// </summary>
[ApiController]
[ServiceFilter(typeof(ApiTokenFilter))]
[EnableRateLimiting("monitoring")]
public sealed class MonitoringController(MetricsSnapshotCache cache, MetricsSnapshotBuilder builder) : ControllerBase
{
    private const string PrometheusContentType = "text/plain; version=0.0.4; charset=utf-8";

    /// <summary>§3.1. <c>tenant</c> is accepted for forward-compatibility with the documented
    /// contract but is currently a no-op — nothing tenant-scoped exists until Phase 2's
    /// site/gateway metrics land.</summary>
    [HttpGet("/api/v1/metrics")]
    [RequireScope("monitoring:read")]
    public async Task<IActionResult> GetMetrics(
        [FromQuery] string? scope, [FromQuery] string? tenant, CancellationToken ct)
    {
        var normalisedScope = scope?.ToLowerInvariant() switch
        {
            "control" => "control",
            "gateways" => "gateways",
            "sites" => "sites",
            "peers" => "peers",
            _ => "all",
        };

        var snapshot = await cache.GetOrBuildAsync(builder.BuildAsync, ct);
        var body = PrometheusTextWriter.Render(snapshot, normalisedScope);

        Response.Headers["X-Valenius-Metrics-Generated"] = snapshot.GeneratedAtUtc.ToString("O");
        Response.Headers["X-Valenius-Version"] = snapshot.BackendVersion;

        var bytes = Encoding.UTF8.GetBytes(body);

        // §3.1 "Compression: gzip when Accept-Encoding: gzip" — done here rather than the
        // ASP.NET Core response-compression middleware so it applies to exactly this endpoint,
        // not every response in the app.
        if (AcceptsGzip())
        {
            using var ms = new MemoryStream();
            await using (var gzip = new GZipStream(ms, CompressionMode.Compress, leaveOpen: true))
                await gzip.WriteAsync(bytes, ct);

            Response.Headers.ContentEncoding = "gzip";
            return File(ms.ToArray(), PrometheusContentType);
        }

        return Content(body, PrometheusContentType, Encoding.UTF8);
    }

    [HttpGet("/api/v1/monitoring/status")]
    [RequireScope("monitoring:read")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var snapshot = await cache.GetOrBuildAsync(builder.BuildAsync, ct);

        Response.Headers["X-Valenius-Metrics-Generated"] = snapshot.GeneratedAtUtc.ToString("O");
        Response.Headers["X-Valenius-Version"] = snapshot.BackendVersion;

        return Ok(new MonitoringStatusResponse(
            Status: snapshot.DbUp ? "ok" : "degraded",
            Version: snapshot.BackendVersion,
            GeneratedAt: snapshot.GeneratedAtUtc,
            License: new MonitoringLicenseStatus(
                snapshot.LicenseValid, snapshot.LicenseExpiresAtUtc,
                snapshot.LicenseSeatsTotal, snapshot.LicenseSeatsUsed, snapshot.LicenseType),
            Database: new MonitoringDatabaseStatus(
                snapshot.DbUp, Math.Round(snapshot.DbQueryDurationSeconds * 1000, 2)),
            Gateways: new MonitoringGatewaysStatus(snapshot.GatewaysTotal, snapshot.Gateways.Count(g => g.Up)),
            Peers: new MonitoringPeersStatus(snapshot.PeersTotal, snapshot.PeersConnected)));
    }

    private bool AcceptsGzip() =>
        Request.Headers.AcceptEncoding
            .Any(h => h is not null && h.Contains("gzip", StringComparison.OrdinalIgnoreCase));
}

/// <summary>§3.3 JSON contract. Deliberately omits <c>sites</c>, <c>gateways.degraded</c>, and
/// <c>peers.transport</c> — see <see cref="Monitoring.MetricsSnapshot"/>'s doc comment for why
/// those are dropped rather than deferred (Appendix B).</summary>
public sealed record MonitoringStatusResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("generated_at")] DateTime GeneratedAt,
    [property: JsonPropertyName("license")] MonitoringLicenseStatus License,
    [property: JsonPropertyName("database")] MonitoringDatabaseStatus Database,
    [property: JsonPropertyName("gateways")] MonitoringGatewaysStatus Gateways,
    [property: JsonPropertyName("peers")] MonitoringPeersStatus Peers);

public sealed record MonitoringLicenseStatus(
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("expires_at")] DateTime? ExpiresAt,
    [property: JsonPropertyName("seats_total")] int SeatsTotal,
    [property: JsonPropertyName("seats_used")] int SeatsUsed,
    [property: JsonPropertyName("edition")] string Edition);

public sealed record MonitoringDatabaseStatus(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("latency_ms")] double LatencyMs);

public sealed record MonitoringGatewaysStatus(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("online")] int Online);

public sealed record MonitoringPeersStatus(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("connected")] int Connected);
