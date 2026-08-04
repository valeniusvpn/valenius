using System.Diagnostics;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Licensing;
using Valenius.Backend.Services;

namespace Valenius.Backend.Monitoring;

/// <summary>Builds one <see cref="MetricsSnapshot"/> from live state — called by
/// <see cref="MetricsSnapshotCache"/> at most once per 15 s TTL window. Scoped (needs
/// <see cref="ApplicationDbContext"/>), unlike the cache itself which is a singleton.</summary>
public sealed class MetricsSnapshotBuilder(
    ApplicationDbContext db,
    ILicenseContext licenseContext,
    ClientPresenceTracker presence,
    RequestMetricsCollector requestMetrics,
    GatewayMetricsTracker gatewayMetrics)
{
    private static readonly DateTime ProcessStartUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime();

    // InformationalVersion mirrors the csproj <Version> (e.g. "1.11.153"), but the SDK
    // appends "+<git-sha>" source-revision metadata — stripped here so this matches what
    // Admin/Releases and the pre-commit hook actually stamp. Falls back to the 4-part,
    // SDK-padded assembly Version (e.g. "1.11.153.0") if InformationalVersion is unavailable.
    private static readonly string BackendVersion =
        (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown").Split('+')[0];

    public async Task<MetricsSnapshot> BuildAsync(CancellationToken ct)
    {
        var dbSw = Stopwatch.StartNew();
        bool dbUp;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
            dbUp = true;
        }
        catch
        {
            dbUp = false;
        }
        dbSw.Stop();

        // Degrade, not fail (§5.3): if the DB is unreachable, every count below defaults to 0
        // rather than throwing — the caller still gets valenius_up 1 + valenius_db_up 0.
        var tenantsTotal = 0;
        var gatewaysTotal = 0;
        var peersTotal = 0;
        var peersConnected = 0;
        var seatsUsed = 0;
        var gateways = new List<GatewayMetric>();

        if (dbUp)
        {
            try
            {
                tenantsTotal = await db.Customers.CountAsync(ct);
                gatewaysTotal = await db.Customers.CountAsync(c => c.ServerMode == "Valenius", ct);

                var clientKeys = await db.Clients
                    .Where(c => !c.IsDeleted)
                    .Select(c => new { c.ClientKey, c.IsActive })
                    .ToListAsync(ct);

                peersTotal = clientKeys.Count;
                peersConnected = clientKeys.Count(c => presence.IsOnline(c.ClientKey));
                seatsUsed = clientKeys.Count(c => c.IsActive);

                gateways = await BuildGatewayMetricsAsync(ct);
            }
            catch
            {
                // A transient query failure after the SELECT 1 probe succeeded still counts as
                // db_up=1 (the connection works) but the aggregates stay at their zero defaults.
            }
        }

        return new MetricsSnapshot(
            GeneratedAtUtc: DateTime.UtcNow,
            BackendVersion: BackendVersion,
            Edition: licenseContext.LicenseType,
            ProcessStartUtc: ProcessStartUtc,
            DbUp: dbUp,
            DbQueryDurationSeconds: dbSw.Elapsed.TotalSeconds,
            HttpRequestsTotal: requestMetrics.SnapshotCounts(),
            HttpRequestDurationSeconds: requestMetrics.SnapshotDurationQuantiles(),
            LicenseValid: licenseContext.IsValid,
            LicenseGraceActive: licenseContext.IsInGracePeriod,
            LicenseExpiresAtUtc: licenseContext.ExpiresAt,
            LicenseSeatsTotal: licenseContext.MaxEndpoints,
            LicenseSeatsUsed: seatsUsed,
            LicenseType: licenseContext.LicenseType,
            LicenseId: licenseContext.LicenseId,
            TenantsTotal: tenantsTotal,
            GatewaysTotal: gatewaysTotal,
            PeersTotal: peersTotal,
            PeersConnected: peersConnected,
            Gateways: gateways);
    }

    /// <summary>Merges <see cref="GatewayMetricsTracker"/>'s in-memory poll state (peer counts,
    /// handshake age, host stats — refreshed every 30 s by Backend.Pro's
    /// WgTunnelReconcilerService) with reset-aware lifetime traffic totals already accumulated
    /// per client by TrafficSamplingService, summed per customer. Always empty in OSS — the
    /// tracker is never populated there (see GatewayMetricsTracker's doc comment).</summary>
    private async Task<List<GatewayMetric>> BuildGatewayMetricsAsync(CancellationToken ct)
    {
        var snapshots = gatewayMetrics.Snapshots;
        if (snapshots.Count == 0) return [];

        var customerIds = snapshots.Select(s => s.CustomerId).ToList();
        var traffic = await db.Clients
            .Where(c => !c.IsDeleted && c.CustomerId != null && customerIds.Contains(c.CustomerId!.Value))
            .GroupBy(c => c.CustomerId!.Value)
            .Select(g => new { CustomerId = g.Key, Rx = g.Sum(c => c.WgRxLifetime), Tx = g.Sum(c => c.WgTxLifetime) })
            .ToListAsync(ct);
        var trafficByCustomer = traffic.ToDictionary(t => t.CustomerId, t => (t.Rx, t.Tx));

        return snapshots.Select(s =>
        {
            var (rx, tx) = trafficByCustomer.GetValueOrDefault(s.CustomerId, (0L, 0L));
            return new GatewayMetric(
                Gateway: s.CustomerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Up: s.Up,
                LastSeenUtc: s.LastSeenUtc == DateTime.MinValue ? null : s.LastSeenUtc,
                PeersTotal: s.PeersTotal,
                PeersConnected: s.PeersConnected,
                HandshakeAgeMaxSeconds: s.HandshakeAgeMaxSeconds,
                RxBytesTotal: rx,
                TxBytesTotal: tx,
                EndpointChangesTotal: s.EndpointChangesTotal,
                CpuUsageRatio: s.CpuUsageRatio,
                MemoryUsageRatio: s.MemoryUsageRatio,
                Load1: s.Load1,
                ConntrackUsed: s.ConntrackUsed,
                ConntrackMax: s.ConntrackMax);
        }).ToList();
    }
}
