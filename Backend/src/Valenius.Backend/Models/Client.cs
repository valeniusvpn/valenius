using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Valenius.Backend.Models;

public class Client
{
    public int Id { get; set; }

    [MaxLength(36)]
    public string ClientKey { get; set; } = "";

    [MaxLength(255)]
    public string Hostname { get; set; } = "";

    /// <summary>
    /// Admin-set friendly name. When set, shown in place of <see cref="Hostname"/>
    /// throughout the admin UI (useful for mobile devices with unhelpful model
    /// names). Null/blank falls back to the hostname.
    /// </summary>
    [MaxLength(255)]
    public string? DisplayName { get; set; }

    /// <summary>The name to show in the admin UI: <see cref="DisplayName"/> if set, else the hostname.</summary>
    [NotMapped]
    public string EffectiveName =>
        string.IsNullOrWhiteSpace(DisplayName) ? Hostname : DisplayName;

    /// <summary>Bootstrap-icon class (without the base "bi") for the client's
    /// platform, used in the admin UI. Unknown/legacy clients default to Windows
    /// (the dominant desktop), or a laptop when the hostname hints at one.</summary>
    [NotMapped]
    public string PlatformIcon => Platform switch
    {
        "Android" => "bi-android2",
        "iOS" => "bi-phone",
        "macos" or "macOS" or "Mac" or "Darwin" => "bi-apple",
        "Linux" => "bi-ubuntu",
        "Windows" => "bi-windows",
        "homeassistant" => "bi-house-gear-fill",
        _ => Hostname.Contains("LAP", StringComparison.OrdinalIgnoreCase)
            ? "bi-laptop"
            : "bi-windows",
    };

    [MaxLength(200)]
    public string HostSid { get; set; } = "";

    [MaxLength(255)]
    public string Username { get; set; } = "";

    [MaxLength(200)]
    public string UserSid { get; set; } = "";

    public DateTime RegisteredAt { get; set; }

    public DateTime LastSeenAt { get; set; }

    public bool IsActive { get; set; }

    public bool IsDeleted { get; set; }

    /// <summary>
    /// True when this row was created by a registration whose hostname collides with an
    /// existing, non-deleted client (typically a reinstall that generated a fresh ClientKey).
    /// The client stays inactive and is shown an "already exists — ask your admin to reassign"
    /// message until an admin either reassigns the original record to this install's ClientKey
    /// or chooses to keep both. See <c>ClientsController.Register</c>.
    /// </summary>
    public bool IsDuplicatePending { get; set; }

    [MaxLength(20)]
    public string? ClientVersion { get; set; }

    /// <summary>
    /// Client platform/OS — "Windows", "Linux", "Android", "iOS". Null for legacy
    /// desktop clients that predate this field (treated as desktop). Lets the admin
    /// UI identify mobile devices and the backend skip desktop-only assumptions
    /// (e.g. installer auto-update).
    /// </summary>
    [MaxLength(20)]
    public string? Platform { get; set; }

    /// <summary>Firebase Cloud Messaging token for push-to-approve MFA (mobile). Null when unsupported.</summary>
    [MaxLength(255)]
    public string? FcmToken { get; set; }

    /// <summary>Mobile client (by Id) that approves this client's MFA via push-to-approve.
    /// Null = authenticator/TOTP only. Admin-set on the client's MFA settings.</summary>
    public int? MfaApproverClientId { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    [MaxLength(260)]
    public string? PendingConfigFileName { get; set; }

    public string? PendingConfigContent { get; set; }

    public bool AutoConnectEnabled { get; set; }

    [MaxLength(50)]
    public string? AutoConnectProfileName { get; set; }

    /// <summary>JSON array of profile names last reported by the client, e.g. ["Office_VPN","Home"]</summary>
    public string? KnownProfiles { get; set; }

    /// <summary>Unused — superseded by the <c>ClientPendingDeletes</c> queue table (a single scalar
    /// field can't hold more than one in-flight deletion; see <see cref="ClientPendingDelete"/>).
    /// Left in place only so existing backups still round-trip; always null going forward.</summary>
    [MaxLength(50)]
    public string? PendingDeleteProfileName { get; set; }

    /// <summary>When true, the client will connect on its next heartbeat. Cleared atomically after being sent.</summary>
    public bool PendingConnect { get; set; }

    /// <summary>When true, the client will disconnect its active VPN tunnel on its next heartbeat. Cleared atomically after being sent.</summary>
    public bool PendingDisconnect { get; set; }

    /// <summary>When true, the client will immediately check for and apply a pending update. Cleared atomically after being sent.</summary>
    public bool ForceUpdate { get; set; }

    /// <summary>When true, the client collects its (Valenius-only, redacted) logs and uploads them
    /// via POST /api/clients/logs on its next heartbeat. Cleared atomically after being sent.</summary>
    public bool PendingLogRequest { get; set; }

    /// <summary>When true, the client runs its local repair checks on its next heartbeat instead of
    /// waiting for its next service restart — currently the data-directory ACL self-heal, which
    /// fixes "access to path ... is denied" errors caused by a corrupted/empty ACL on a file under
    /// its ProgramData tree (e.g. a poisoned temp\*.conf left over from a crashed connect). A
    /// generic hook other client-side repairs can join later. Windows-only; cleared atomically
    /// after being sent.</summary>
    public bool PendingConfigRepair { get; set; }

    /// <summary>Auto-update channel this client tracks: "stable" (default) or "beta". Admin-set
    /// per client; surfaced in the heartbeat so the updater polls the matching release stream.</summary>
    [MaxLength(10)]
    public string UpdateChannel { get; set; } = ReleaseChannel.Stable;

    /// <summary>
    /// When true the Windows service skips the Phase 2 connectivity probe
    /// (https://SidecarVpnIp/health through the tunnel) and treats the tunnel
    /// as connected as soon as WireGuard installs it.
    /// </summary>
    public bool SkipConnectivityCheck { get; set; }

    // ── WireGuard peer fields (populated when Customer.ServerMode = "Valenius") ──

    /// <summary>Client's X25519 public key — used as the WireGuard peer identity on the server.</summary>
    [MaxLength(50)]
    public string? WgPublicKey { get; set; }

    /// <summary>Client's X25519 private key — staged in the generated .conf file sent to the client.</summary>
    [MaxLength(50)]
    public string? WgPrivateKey { get; set; }

    /// <summary>IP address allocated to this client within the customer's VPN subnet, e.g. 10.100.0.5</summary>
    [MaxLength(45)]
    public string? VpnIpAddress { get; set; }

    /// <summary>
    /// Set when an admin force-removes this client's peer from the sidecar via "Kill Tunnel"
    /// (immediate, works even if the client is unreachable — unlike the cooperative
    /// <see cref="PendingDisconnect"/>). Cleared automatically the next time the client
    /// reports a Connect event, at which point the peer is transparently re-added so the
    /// client can reconnect on its own without needing re-provisioning.
    /// </summary>
    public bool WgPeerKilled { get; set; }

    // ── WireGuard traffic accounting (sidecar byte counters) ──────────────────────
    // WireGuard's counters are cumulative since the peer was added and RESET to 0 on every
    // re-add (kill/re-provision, MFA grant re-push, interface/container restart), so we can't
    // just read the live value. TrafficSamplingService (Pro) accumulates a durable, reset-aware
    // lifetime total: on each poll delta = raw >= lastRaw ? raw - lastRaw : raw (a lower raw
    // means the counter rolled), added to the lifetime. LastRaw is null until first observed
    // (so the pre-sampling backlog isn't counted as one huge first interval).
    public long? WgRxLastRaw { get; set; }
    public long? WgTxLastRaw { get; set; }
    public long WgRxLifetime { get; set; }
    public long WgTxLifetime { get; set; }

    /// <summary>
    /// Per-client AllowedIPs override for the generated config, e.g. "10.100.0.0/24" for split tunnel.
    /// Null means the customer-level default (<see cref="Customer.AllowedIPs"/>) is used,
    /// which itself falls back to "0.0.0.0/0, ::/0".
    /// </summary>
    [MaxLength(200)]
    public string? AllowedIPs { get; set; }

    /// <summary>
    /// Per-client DNS server override for the generated config.
    /// Null means the customer-level <see cref="Customer.DnsServer"/> is used.
    /// </summary>
    [MaxLength(45)]
    public string? DnsServer { get; set; }

    /// <summary>
    /// Per-client DNS search-domains override (comma-separated). Null inherits the
    /// customer-level <see cref="Customer.DnsSearchDomains"/>. Appended to the <c>DNS =</c>
    /// line as NRPT split-DNS domains (see <see cref="Customer.DnsSearchDomains"/>).
    /// </summary>
    [MaxLength(255)]
    public string? DnsSearchDomains { get; set; }

    /// <summary>
    /// Maximum number of days a provisioned config is considered valid.
    /// When set, the Pro config-renewal service re-provisions the client after this many days.
    /// Null means the config never expires automatically.
    /// </summary>
    public int? MaxConfigValidDays { get; set; }

    /// <summary>UTC timestamp of the last time this client's config was staged by PeerProvisioningService.</summary>
    public DateTime? ConfigProvisionedAt { get; set; }

    // ── MFA session gating (Pro) — see docs/design/mfa-session-gating.md ──────────

    /// <summary>
    /// Peer classification. <c>Machine</c> peers are never MFA-gated. Existing peers
    /// default to <c>User</c> for admin review (never auto-enabled).
    /// </summary>
    public PeerType PeerType { get; set; } = PeerType.User;

    /// <summary>
    /// Per-client MFA policy override. Null inherits <see cref="Customer.DefaultMfaPolicy"/>,
    /// which itself defaults to <see cref="MfaPolicy.Disabled"/> (feature is opt-in).
    /// </summary>
    public MfaPolicy? MfaPolicy { get; set; }

    /// <summary>
    /// Per-client session-lifetime override (hours). Null inherits
    /// <see cref="Customer.MfaSessionLifetimeHours"/>.
    /// </summary>
    public int? MfaSessionLifetimeHours { get; set; }

    // ── Device tunnel (Windows, Pro) ────────────────────────────────────────────

    /// <summary>
    /// Per-client override for <see cref="Customer.DeviceTunnelDefaultEnabled"/>. Null inherits
    /// the customer default.
    /// </summary>
    public bool? DeviceTunnelEnabled { get; set; }

    /// <summary>Base32-encoded per-client TOTP secret. Null = not enrolled. Server-generated.</summary>
    [MaxLength(64)]
    public string? MfaTotpSecret { get; set; }

    /// <summary>True once the user has confirmed enrollment with a valid code.</summary>
    public bool MfaTotpEnabled { get; set; }

    /// <summary>
    /// True while the enrollment window is open (QR shown in the tray). Set when an admin
    /// first assigns a gating policy; cleared on confirm. Re-opened only by admin reset.
    /// </summary>
    public bool MfaEnrollmentOpen { get; set; }

    /// <summary>RFC 6238 time-step of the last accepted MFA TOTP code (replay protection).</summary>
    public long? MfaTotpLastUsedInterval { get; set; }

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    public List<ClientForeignProfile> ForeignProfiles { get; set; } = [];
}
