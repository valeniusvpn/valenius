using System.Text.Json.Serialization;

namespace Valenius.Backend.Services;

/// <param name="FallbackActive">
/// Whether the sidecar's UDP port-fallback redirect is really installed right now — the
/// OBSERVED state, not the admin's stored intent. The heartbeat gates what it advertises to
/// clients on this (see <c>docs/design/port-443-fallback.md</c> §4.4): advertising a fallback
/// the sidecar never installed would make every client burn its fallback window on a dead
/// port at every connect. Null/false when talking to a pre-upgrade sidecar.
/// </param>
/// <param name="FallbackError">
/// Why intent and reality disagree (failed preflight, iptables unavailable). Shown verbatim
/// in the admin UI.
/// </param>
public sealed record ServerInfo(
    [property: JsonPropertyName("interface")]        string  Interface,
    [property: JsonPropertyName("publicKey")]        string  PublicKey,
    [property: JsonPropertyName("listenPort")]       int     ListenPort,
    [property: JsonPropertyName("interfaceAddress")] string? InterfaceAddress,
    [property: JsonPropertyName("version")]          string? Version,
    [property: JsonPropertyName("lanCidrs")]         List<string>? LanCidrs = null,
    [property: JsonPropertyName("fallbackPort")]     int     FallbackPort   = 0,
    [property: JsonPropertyName("fallbackActive")]   bool    FallbackActive = false,
    [property: JsonPropertyName("fallbackError")]    string? FallbackError  = null,
    // Host stats (Server/sidecar/hoststats.go) — Zabbix/Prometheus monitoring Phase 2. Null
    // means "not sampled yet" or "unavailable on this host" (conntrack needs nf_conntrack
    // loaded) — never faked. Talking to a pre-upgrade sidecar that doesn't send these yet
    // also deserializes to null, same as every other optional field on this record.
    [property: JsonPropertyName("cpuUsageRatio")]    double? CpuUsageRatio    = null,
    [property: JsonPropertyName("memoryUsageRatio")] double? MemoryUsageRatio = null,
    [property: JsonPropertyName("load1")]            double? Load1           = null,
    [property: JsonPropertyName("conntrackUsed")]    long?   ConntrackUsed   = null,
    [property: JsonPropertyName("conntrackMax")]     long?   ConntrackMax    = null);

/// <param name="Active">
/// Ping-confirmed liveness verdict from the sidecar's presence monitor. Null when talking
/// to a pre-upgrade sidecar that doesn't send this field yet — callers should fall back to
/// a handshake-age heuristic in that case (see WgTunnelReconcilerService).
/// </param>
public sealed record PeerStats(
    [property: JsonPropertyName("publicKey")]       string    PublicKey,
    [property: JsonPropertyName("allowedIps")]      string    AllowedIps,
    [property: JsonPropertyName("endpoint")]        string?   Endpoint,
    [property: JsonPropertyName("latestHandshake")] DateTime? LatestHandshake,
    [property: JsonPropertyName("bytesReceived")]   long      BytesReceived,
    [property: JsonPropertyName("bytesSent")]       long      BytesSent,
    [property: JsonPropertyName("active")]          bool?     Active = null);

/// <summary>One finding from the sidecar's GET /selftest. Status is "pass", "warn" or "fail".</summary>
public sealed record SidecarSelfCheck(
    [property: JsonPropertyName("key")]    string Key,
    [property: JsonPropertyName("name")]   string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("detail")] string Detail);

/// <param name="Ok">False when any check failed outright. Warnings deliberately do not clear it.</param>
public sealed record SidecarSelfTest(
    [property: JsonPropertyName("ok")]      bool   Ok,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("checks")]  List<SidecarSelfCheck> Checks);

/// <summary>An active MFA grant as reported by the sidecar's GET /grants (unix-seconds expiry).</summary>
public sealed record SidecarGrantInfo(
    [property: JsonPropertyName("sessionId")]     Guid   SessionId,
    [property: JsonPropertyName("peerPublicKey")] string PeerPublicKey,
    [property: JsonPropertyName("expiresAt")]     long   ExpiresAt);

public interface ISidecarService
{
    Task<ServerInfo?> GetInfoAsync(string sidecarUrl, int customerId = 0);
    Task<List<PeerStats>?> GetPeersAsync(string sidecarUrl);
    Task<(bool Ok, string Message, ServerInfo? Info)> TestConnectionAsync(string sidecarUrl, int customerId = 0);
    Task<bool> AddPeerAsync(string sidecarUrl, string publicKey, string allowedIp);
    Task<bool> RemovePeerAsync(string sidecarUrl, string publicKey);

    /// <summary>
    /// Enables or disables the sidecar's UDP port-fallback redirect (PUT /fallback) — a
    /// nat/PREROUTING REDIRECT that makes the WireGuard interface additionally answer on
    /// <paramref name="port"/> (typically 443) for clients whose network blocks the primary
    /// UDP port. See <c>docs/design/port-443-fallback.md</c>.
    /// </summary>
    /// <returns>
    /// <c>Ok</c> is false when the sidecar refused (something else already owns the UDP port)
    /// or is too old to know the endpoint; <c>Message</c> then carries the reason for the admin.
    /// <c>Active</c> is the sidecar's own confirmation that the rule is installed — never
    /// assume the write took effect.
    /// </returns>
    Task<(bool Ok, string Message, bool Active)> SetFallbackAsync(string sidecarUrl, bool enabled, int port);

    /// <summary>
    /// Runs the sidecar's own host self-diagnosis (GET /selftest) — IP forwarding, NAT, FORWARD
    /// rules, upstream route, conf-vs-runtime peer drift. These are the failures that are
    /// invisible from outside: the tunnel connects and then carries no traffic. Null when the
    /// sidecar is unreachable or predates the endpoint.
    /// </summary>
    Task<SidecarSelfTest?> GetSelfTestAsync(string sidecarUrl);

    // ── MFA grants (Pro) ─────────────────────────────────────────────────────────

    /// <summary>Pushes a CA-signed grant (POST /grants). <paramref name="grantJson"/> is the exact signed string.</summary>
    Task<bool> PushGrantAsync(string sidecarUrl, string grantJson, string signatureBase64);

    /// <summary>Revokes a grant by session id (DELETE /grants). Idempotent on the sidecar.</summary>
    Task<bool> RevokeGrantAsync(string sidecarUrl, Guid sessionId);

    /// <summary>Lists active grants for reconciliation (GET /grants).</summary>
    Task<List<SidecarGrantInfo>?> GetGrantsAsync(string sidecarUrl);
}
