using System.Text.Json.Serialization;

namespace Valenius.Backend.Services;

public sealed record ServerInfo(
    [property: JsonPropertyName("interface")]        string  Interface,
    [property: JsonPropertyName("publicKey")]        string  PublicKey,
    [property: JsonPropertyName("listenPort")]       int     ListenPort,
    [property: JsonPropertyName("interfaceAddress")] string? InterfaceAddress,
    [property: JsonPropertyName("version")]          string? Version);

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

    // ── MFA grants (Pro) ─────────────────────────────────────────────────────────

    /// <summary>Pushes a CA-signed grant (POST /grants). <paramref name="grantJson"/> is the exact signed string.</summary>
    Task<bool> PushGrantAsync(string sidecarUrl, string grantJson, string signatureBase64);

    /// <summary>Revokes a grant by session id (DELETE /grants). Idempotent on the sidecar.</summary>
    Task<bool> RevokeGrantAsync(string sidecarUrl, Guid sessionId);

    /// <summary>Lists active grants for reconciliation (GET /grants).</summary>
    Task<List<SidecarGrantInfo>?> GetGrantsAsync(string sidecarUrl);
}
