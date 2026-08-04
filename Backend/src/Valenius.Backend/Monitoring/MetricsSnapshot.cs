namespace Valenius.Backend.Monitoring;

/// <summary>One count of HTTP requests seen for a given (method, status_class) pair since
/// process start. Cardinality-safe by design — no path label, see root CLAUDE.md
/// "Do not add per-path HTTP request labels".</summary>
public sealed record HttpRequestCount(string Method, string StatusClass, long Count);

/// <summary>One quantile of the rolling 5-minute request-duration window.</summary>
public sealed record HttpRequestDurationQuantile(string Quantile, double Seconds);

/// <summary>One customer's ("gateway" — see the class doc comment below on why there's no
/// separate "site") polled WireGuard state, for §4.4. <c>Gateway</c> is the opaque label
/// value (the customer id as a string, per §5.2's opaque-by-default privacy default).</summary>
public sealed record GatewayMetric(
    string Gateway,
    bool Up,
    DateTime? LastSeenUtc,
    int PeersTotal,
    int PeersConnected,
    double? HandshakeAgeMaxSeconds,
    long RxBytesTotal,
    long TxBytesTotal,
    long EndpointChangesTotal,
    double? CpuUsageRatio,
    double? MemoryUsageRatio,
    double? Load1,
    long? ConntrackUsed,
    long? ConntrackMax);

/// <summary>
/// Everything the Zabbix monitoring concept needs to render <c>/api/v1/metrics</c> and
/// <c>/api/v1/monitoring/status</c>: Phase 1's control-plane + license + OSS-native
/// aggregates (§4.1-4.3), plus Phase 2's per-gateway telemetry (§4.4, <see cref="Gateways"/>).
///
/// Deliberately omits several cataloged series that don't exist in this codebase rather than
/// faking them (Appendix B): <c>db_connections_used/max</c> (no pool-stats hook),
/// <c>worker_queue_depth</c> / <c>worker_last_success</c> / <c>worker_failures_total</c> (OSS
/// has no generic worker-queue concept — mail/audit are synchronous), <c>tls_cert_expiry</c>
/// (TLS terminates at the reverse proxy, not in-process), <c>update_available</c> /
/// <c>latest_version_info</c> (the backend has no self-update-check mechanism),
/// <c>gateway_config_generation/applied_generation</c> and <c>config_push_errors_total</c>
/// (no provisioning-generation counter exists), and <c>gateway_wg_interface_up</c> (would need
/// a second, redundant per-cycle <c>/info</c> just for the interface name).
///
/// <c>valenius_site_*</c> and <c>valenius_gateway_peers_transport</c> are dropped entirely,
/// not just deferred — §11 Q3 asked whether "site" is a resolved concept; it isn't: one
/// customer has at most one sidecar today (a scalar field, not a collection), so "gateway" and
/// "site" are the same thing in this codebase, and a separate site metric family would just be
/// gateway data wearing a second name. Similarly, <c>udp</c>/<c>wstunnel</c>/<c>dnat</c>
/// transports don't exist in Valenius — the only real transport dimension is the UDP
/// port-fallback feature, and even that isn't observable per-peer from <c>wg show dump</c>
/// (the fallback redirect rewrites the port before WireGuard ever sees it).
/// </summary>
public sealed record MetricsSnapshot(
    DateTime GeneratedAtUtc,
    string BackendVersion,
    string Edition,
    DateTime ProcessStartUtc,
    bool DbUp,
    double DbQueryDurationSeconds,
    IReadOnlyList<HttpRequestCount> HttpRequestsTotal,
    IReadOnlyList<HttpRequestDurationQuantile> HttpRequestDurationSeconds,
    bool LicenseValid,
    bool LicenseGraceActive,
    DateTime? LicenseExpiresAtUtc,
    int LicenseSeatsTotal,
    int LicenseSeatsUsed,
    string LicenseType,
    string? LicenseId,
    int TenantsTotal,
    int GatewaysTotal,
    int PeersTotal,
    int PeersConnected,
    IReadOnlyList<GatewayMetric> Gateways);
