using System.Globalization;
using System.Text;

namespace Valenius.Backend.Monitoring;

/// <summary>
/// Renders a <see cref="MetricsSnapshot"/> as Prometheus text exposition format
/// (<c>text/plain; version=0.0.4</c>). See docs/valenius-zabbix-monitoring-concept.md §4 for
/// the metric catalog this must stay in lockstep with — per Appendix B, a rename here is a
/// breaking change; only ever add or omit, never rename.
///
/// <c>scope</c> mirrors §3.1's <c>?scope=</c> query param — see <see cref="MetricsSnapshot"/>'s
/// doc comment for exactly what's implemented vs. deliberately omitted (including why there's
/// no <c>sites</c> breakout and no transport-classification metric).
/// </summary>
public static class PrometheusTextWriter
{
    public static string Render(MetricsSnapshot s, string scope)
    {
        var w = new Writer();

        // Always present — "presence is the real signal" (§4.1).
        w.Header("valenius_up", "gauge", "Valenius control plane is responding.");
        w.Sample("valenius_up", 1);

        if (scope is "control" or "all")
        {
            EmitControlPlane(w, s);
            EmitLicense(w, s);
        }

        if (scope is "all" or "gateways")
        {
            w.Header("valenius_gateways_total", "gauge",
                "Customers with an integrated (sidecar-managed) WireGuard server configured.");
            w.Sample("valenius_gateways_total", s.GatewaysTotal);

            w.Header("valenius_gateways_online", "gauge",
                "Gateways whose sidecar answered the most recent poll.");
            w.Sample("valenius_gateways_online", s.Gateways.Count(g => g.Up));

            EmitGateways(w, s);
        }

        if (scope is "all" or "peers")
        {
            w.Header("valenius_peers_total", "gauge", "Total non-deleted clients (WireGuard peers) across the fleet.");
            w.Sample("valenius_peers_total", s.PeersTotal);
            w.Header("valenius_peers_connected", "gauge", "Clients currently online (polled within the presence threshold).");
            w.Sample("valenius_peers_connected", s.PeersConnected);
        }

        if (scope == "all")
        {
            w.Header("valenius_tenants_total", "gauge", "Total customers (tenants).");
            w.Sample("valenius_tenants_total", s.TenantsTotal);
        }

        // Self-metric (§5.1) — includes itself in the count.
        w.Header("valenius_metrics_series_total", "gauge", "Number of series in this scrape, including this one.");
        w.Sample("valenius_metrics_series_total", w.SeriesCount + 1);

        return w.ToString();
    }

    private static void EmitControlPlane(Writer w, MetricsSnapshot s)
    {
        w.Header("valenius_build_info", "gauge", "Build information.");
        w.Sample("valenius_build_info", 1, ("version", s.BackendVersion), ("edition", s.Edition));

        w.Header("valenius_start_timestamp_seconds", "gauge", "Unix timestamp the process started.");
        w.Sample("valenius_start_timestamp_seconds", ToUnixSeconds(s.ProcessStartUtc));

        w.Header("valenius_stats_generated_timestamp_seconds", "gauge",
            "Unix timestamp this snapshot was generated — freshness check for the 15 s cache.");
        w.Sample("valenius_stats_generated_timestamp_seconds", ToUnixSeconds(s.GeneratedAtUtc));

        w.Header("valenius_db_up", "gauge", "Database reachability.");
        w.Sample("valenius_db_up", s.DbUp ? 1 : 0);

        w.Header("valenius_db_query_duration_seconds", "gauge", "Round-trip time of a trivial database probe query.");
        w.Sample("valenius_db_query_duration_seconds", s.DbQueryDurationSeconds);

        if (s.HttpRequestsTotal.Count > 0)
        {
            w.Header("valenius_http_requests_total", "counter",
                "HTTP requests served since process start, by method and status class.");
            foreach (var r in s.HttpRequestsTotal)
                w.Sample("valenius_http_requests_total", r.Count, ("method", r.Method), ("status_class", r.StatusClass));
        }

        if (s.HttpRequestDurationSeconds.Count > 0)
        {
            w.Header("valenius_http_request_duration_seconds", "gauge",
                "Request duration quantiles over a rolling 5-minute window.");
            foreach (var q in s.HttpRequestDurationSeconds)
                w.Sample("valenius_http_request_duration_seconds", q.Seconds, ("quantile", q.Quantile));
        }
    }

    private static void EmitLicense(Writer w, MetricsSnapshot s)
    {
        w.Header("valenius_license_valid", "gauge", "Installation license validity (signature + expiry, incl. grace).");
        w.Sample("valenius_license_valid", s.LicenseValid ? 1 : 0);

        w.Header("valenius_license_grace_active", "gauge",
            "1 while the installation license is in its post-expiry grace period.");
        w.Sample("valenius_license_grace_active", s.LicenseGraceActive ? 1 : 0);

        if (s.LicenseExpiresAtUtc is { } expiresAt)
        {
            w.Header("valenius_license_expiry_timestamp_seconds", "gauge",
                "Unix timestamp the installation license expires.");
            w.Sample("valenius_license_expiry_timestamp_seconds", ToUnixSeconds(expiresAt));
        }

        w.Header("valenius_license_seats_total", "gauge", "Maximum licensed endpoints (-1 = unlimited).");
        w.Sample("valenius_license_seats_total", s.LicenseSeatsTotal);

        w.Header("valenius_license_seats_used", "gauge", "Active, non-deleted clients counted against the license.");
        w.Sample("valenius_license_seats_used", s.LicenseSeatsUsed);

        // Deliberately no customer_id/customer-name label — see §5.2 and root CLAUDE.md
        // privacy guidance; ILicenseContext doesn't model a numeric customer id to begin with.
        w.Header("valenius_license_info", "gauge", "Installation license identity. Value always 1.");
        if (!string.IsNullOrEmpty(s.LicenseId))
            w.Sample("valenius_license_info", 1, ("edition", s.LicenseType), ("license_id", s.LicenseId));
        else
            w.Sample("valenius_license_info", 1, ("edition", s.LicenseType));
    }

    /// <summary>§4.4. One series per metric per gateway, labelled <c>gateway</c> (the opaque
    /// customer id). A field that's null for every gateway (host stats on a pre-upgrade
    /// sidecar, handshake age with zero peers) skips its HELP/TYPE header entirely rather than
    /// emitting an empty metric family.</summary>
    private static void EmitGateways(Writer w, MetricsSnapshot s)
    {
        if (s.Gateways.Count == 0) return;

        w.Header("valenius_gateway_info", "gauge", "Discovery source — one series per configured gateway. Value always 1.");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_info", 1, ("gateway", g.Gateway));

        w.Header("valenius_gateway_up", "gauge", "1 if the gateway's sidecar answered the most recent poll.");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_up", g.Up ? 1 : 0, ("gateway", g.Gateway));

        var seen = s.Gateways.Where(g => g.LastSeenUtc is not null).ToList();
        if (seen.Count > 0)
        {
            w.Header("valenius_gateway_last_seen_timestamp_seconds", "gauge", "Unix timestamp of the last successful poll.");
            foreach (var g in seen)
                w.Sample("valenius_gateway_last_seen_timestamp_seconds", ToUnixSeconds(g.LastSeenUtc!.Value), ("gateway", g.Gateway));

            w.Header("valenius_gateway_stats_age_seconds", "gauge", "Age of the underlying wg show dump snapshot.");
            foreach (var g in seen)
                w.Sample("valenius_gateway_stats_age_seconds", (s.GeneratedAtUtc - g.LastSeenUtc!.Value).TotalSeconds, ("gateway", g.Gateway));
        }

        w.Header("valenius_gateway_peers_total", "gauge", "Configured peers on this gateway.");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_peers_total", g.PeersTotal, ("gateway", g.Gateway));

        w.Header("valenius_gateway_peers_connected", "gauge", "Peers with ping-confirmed liveness on this gateway.");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_peers_connected", g.PeersConnected, ("gateway", g.Gateway));

        var withHandshake = s.Gateways.Where(g => g.HandshakeAgeMaxSeconds is not null).ToList();
        if (withHandshake.Count > 0)
        {
            w.Header("valenius_gateway_handshake_age_max_seconds", "gauge", "Oldest WireGuard handshake among this gateway's peers.");
            foreach (var g in withHandshake)
                w.Sample("valenius_gateway_handshake_age_max_seconds", g.HandshakeAgeMaxSeconds!.Value, ("gateway", g.Gateway));
        }

        w.Header("valenius_gateway_rx_bytes_total", "counter", "Lifetime received bytes across this gateway's peers (reset-aware).");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_rx_bytes_total", g.RxBytesTotal, ("gateway", g.Gateway));

        w.Header("valenius_gateway_tx_bytes_total", "counter", "Lifetime sent bytes across this gateway's peers (reset-aware).");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_tx_bytes_total", g.TxBytesTotal, ("gateway", g.Gateway));

        w.Header("valenius_gateway_endpoint_changes_total", "counter", "WireGuard endpoint (IP:port) changes observed — roaming/NAT churn indicator.");
        foreach (var g in s.Gateways)
            w.Sample("valenius_gateway_endpoint_changes_total", g.EndpointChangesTotal, ("gateway", g.Gateway));

        var withCpu = s.Gateways.Where(g => g.CpuUsageRatio is not null).ToList();
        if (withCpu.Count > 0)
        {
            w.Header("valenius_gateway_cpu_usage_ratio", "gauge", "Host CPU usage, 0..1.");
            foreach (var g in withCpu)
                w.Sample("valenius_gateway_cpu_usage_ratio", g.CpuUsageRatio!.Value, ("gateway", g.Gateway));
        }

        var withMem = s.Gateways.Where(g => g.MemoryUsageRatio is not null).ToList();
        if (withMem.Count > 0)
        {
            w.Header("valenius_gateway_memory_usage_ratio", "gauge", "Host memory usage, 0..1.");
            foreach (var g in withMem)
                w.Sample("valenius_gateway_memory_usage_ratio", g.MemoryUsageRatio!.Value, ("gateway", g.Gateway));
        }

        var withLoad = s.Gateways.Where(g => g.Load1 is not null).ToList();
        if (withLoad.Count > 0)
        {
            w.Header("valenius_gateway_load1", "gauge", "Host 1-minute load average.");
            foreach (var g in withLoad)
                w.Sample("valenius_gateway_load1", g.Load1!.Value, ("gateway", g.Gateway));
        }

        var withConntrackUsed = s.Gateways.Where(g => g.ConntrackUsed is not null).ToList();
        if (withConntrackUsed.Count > 0)
        {
            w.Header("valenius_gateway_conntrack_used", "gauge", "Host connection-tracking table entries in use.");
            foreach (var g in withConntrackUsed)
                w.Sample("valenius_gateway_conntrack_used", g.ConntrackUsed!.Value, ("gateway", g.Gateway));
        }

        var withConntrackMax = s.Gateways.Where(g => g.ConntrackMax is not null).ToList();
        if (withConntrackMax.Count > 0)
        {
            w.Header("valenius_gateway_conntrack_max", "gauge", "Host connection-tracking table size limit.");
            foreach (var g in withConntrackMax)
                w.Sample("valenius_gateway_conntrack_max", g.ConntrackMax!.Value, ("gateway", g.Gateway));
        }
    }

    /// <summary>Accumulates HELP/TYPE/sample lines and the emitted series count.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private readonly HashSet<string> _headersWritten = new(StringComparer.Ordinal);
        public int SeriesCount { get; private set; }

        public void Header(string name, string type, string help)
        {
            if (!_headersWritten.Add(name)) return;
            _sb.Append("# HELP ").Append(name).Append(' ').Append(help).Append('\n');
            _sb.Append("# TYPE ").Append(name).Append(' ').Append(type).Append('\n');
        }

        public void Sample(string name, double value, params (string Key, string Value)[] labels)
        {
            // §8 unit test contract: never emit NaN/+Inf.
            if (double.IsNaN(value) || double.IsInfinity(value)) return;

            _sb.Append(name);
            if (labels.Length > 0)
            {
                _sb.Append('{');
                for (var i = 0; i < labels.Length; i++)
                {
                    if (i > 0) _sb.Append(',');
                    _sb.Append(labels[i].Key).Append("=\"").Append(EscapeLabelValue(labels[i].Value)).Append('"');
                }
                _sb.Append('}');
            }
            _sb.Append(' ').Append(value.ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            SeriesCount++;
        }

        public override string ToString() => _sb.ToString();
    }

    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");

    private static double ToUnixSeconds(DateTime utc) =>
        (utc.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;
}
