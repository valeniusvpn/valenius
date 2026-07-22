using System.Text.Json.Serialization;

namespace Valenius.Service;

internal record RegisterClientRequest(string ClientKey, string Hostname, string HostSid, string Username, string UserSid, string Version, bool TrayRunning = false, string[]? Profiles = null, string? Platform = null);
internal record ClientStatusResponse(
    bool IsActive,
    bool HasPendingConfig = false,
    bool AutoConnectEnabled = false,
    string? AutoConnectProfileName = null,
    TrustedNetworkEntry[]? TrustedNetworks = null,
    string? PendingDeleteProfileName = null,
    bool PendingConnect = false,
    int HeartbeatIntervalMinutes = 5,
    string? ServerProfileName = null,
    /// <summary>Health probe URL for pre/post-connect checks, e.g. https://vpn.company.com:8443/health</summary>
    string? ServerHealthUrl = null,
    /// <summary>Sidecar WireGuard interface IP for the post-connect tunnel probe. Sent over HTTPS only.</summary>
    string? ServerVpnIp = null,
    /// <summary>When true, immediately run the update check instead of waiting for the hourly cycle.</summary>
    bool ForceUpdate = false,
    /// <summary>When true, skip the Phase 2 tunnel probe and treat the tunnel as connected immediately.</summary>
    bool SkipConnectivityCheck = false,
    /// <summary>When true, disconnect the active VPN tunnel.</summary>
    bool PendingDisconnect = false,
    // ── MFA session gating (Pro) ──────────────────────────────────────────────
    /// <summary>True when this peer is MFA-gated with no active session — the tray must prompt for re-auth.</summary>
    bool MfaRequired = false,
    /// <summary>Deep link the tray opens in the browser to authorize.</summary>
    string? MfaAuthorizeUrl = null,
    /// <summary>Absolute expiry of the active MFA session, for the tray countdown.</summary>
    DateTime? MfaSessionExpiresAt = null,
    /// <summary>True when the TOTP enrollment window is open — the tray shows the QR.</summary>
    bool MfaEnrollmentOpen = false,
    /// <summary>otpauth:// URI for the enrollment QR.</summary>
    string? MfaEnrollmentUri = null,
    /// <summary>For a gated requester with a push-approver: the number to display.</summary>
    int? MfaApproveNumber = null,
    /// <summary>True when this registration collides with an existing same-hostname client and is
    /// awaiting an admin reassign decision. The tray shows an "already exists" message, not an error.</summary>
    bool DuplicatePending = false,
    /// <summary>Configs from cross-customer sidecar provisioning, delivered inline alongside the heartbeat.
    /// Each entry is installed as an additional WireGuard profile.</summary>
    PendingForeignConfigEntry[]? PendingForeignConfigs = null,
    /// <summary>Per-profile gateway-probe targets for sidecar VPNs with a unique transit network.
    /// Profiles listed here use the HTTP /health gateway check (handshake fallback on failure);
    /// profiles not listed (plain uploads, OSS) use the handshake check only.</summary>
    GatewayProfileEntry[]? GatewayProfiles = null,
    /// <summary>When true the client collects its (Valenius-only, redacted) logs and uploads them
    /// via POST /api/clients/logs. One-shot — cleared server-side after being sent.</summary>
    bool LogUploadRequested = false,
    /// <summary>Auto-update channel this client tracks: "stable" (default) or "beta".</summary>
    string UpdateChannel = "stable",
    /// <summary>New client API key sent during a backend key rotation (only when this client
    /// authenticated with the old key). The service persists it and uses it for subsequent
    /// requests. Null in the normal case.</summary>
    string? ClientApiKey = null,
    /// <summary>Per-profile physical LAN CIDR(s) behind that profile's sidecar, self-reported by the
    /// sidecar. Scoped to the profile whose sidecar reports them — the native server profile (this
    /// client's own customer) and each foreign profile (its own source customer) each get their
    /// own entry. Used by the pre-connect LAN-conflict check to detect the remote network even for
    /// full-tunnel (0.0.0.0/0) profiles. Mirrors the per-profile shape of GatewayProfiles — never
    /// apply one profile's entry to a different profile's connect.</summary>
    LanCidrProfileEntry[]? RemoteLanCidrsByProfile = null,
    /// <summary>When true, run the local repair checks immediately instead of waiting for the next
    /// service restart — currently the data-directory ACL self-heal. A generic hook other
    /// client-side repairs can join later. One-shot — cleared server-side after being sent.</summary>
    bool PendingConfigRepair = false);

internal record PendingForeignConfigEntry(string FileName, string Content);
internal record GatewayProfileEntry(string ProfileName, string GatewayIp, int HealthPort);
internal record LanCidrProfileEntry(string ProfileName, string[] Cidrs);
internal record LogEventRequest(string ClientKey, string EventType, string Username, string TunnelName, string LanIp = "", string WanIp = "", string? Detail = null);
internal record PendingConfigResponse(string FileName, string Content);
internal record MfaEnrollConfirmRequest(string ClientKey, string Code);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(RegistrationState))]
[JsonSerializable(typeof(RegisterClientRequest))]
[JsonSerializable(typeof(ClientStatusResponse))]
[JsonSerializable(typeof(LogEventRequest))]
[JsonSerializable(typeof(PendingConfigResponse))]
[JsonSerializable(typeof(MfaEnrollConfirmRequest))]
[JsonSerializable(typeof(AutoConnectConfig))]
[JsonSerializable(typeof(TrustedNetworkEntry[]))]
[JsonSerializable(typeof(PendingForeignConfigEntry[]))]
[JsonSerializable(typeof(GatewayProfileEntry[]))]
[JsonSerializable(typeof(LanCidrProfileEntry[]))]
internal partial class ServiceJsonContext : JsonSerializerContext { }
