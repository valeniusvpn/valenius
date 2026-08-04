using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class Customer
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = "";

    [MaxLength(500)]
    public string? Notes { get; set; }

    [MaxLength(3)]
    public string? ClientPrefix { get; set; }

    /// <summary>
    /// How often clients heartbeat to the backend, in minutes.
    /// Null means the default (5 minutes) is used.
    /// </summary>
    public int? HeartbeatIntervalMinutes { get; set; }

    // ── WireGuard server configuration ───────────────────────────────────────

    /// <summary>
    /// "External"          — WireGuard is managed outside this app; configs are uploaded manually.
    /// "Valenius"  — the backend manages peers automatically via the wg-sidecar API.
    /// </summary>
    [MaxLength(30)]
    public string ServerMode { get; set; } = "External";

    /// <summary>
    /// Hostname or IP of the wg-sidecar, e.g. "host.docker.internal" or "10.0.1.2".
    /// No protocol prefix, no port.  The full mTLS URL is built by <see cref="SidecarBaseUrl"/>.
    /// </summary>
    [MaxLength(255)]
    public string? SidecarUrl { get; set; }

    /// <summary>HTTPS management port on the sidecar (MANAGEMENT_PORT env var). Default 9003.</summary>
    public int SidecarManagementPort { get; set; } = 9003;

    /// <summary>
    /// Dedicated HTTPS health-check port on the sidecar (HEALTH_PORT env var). Default 9004.
    /// Used for the Windows client Phase-2 probe so the management API can be firewalled
    /// from VPN clients while still allowing the lightweight health check through.
    /// NOTE: this MUST point at the sidecar's dedicated health server (server-TLS only, no
    /// client cert). It must NOT equal <see cref="SidecarManagementPort"/> — that port
    /// requires mTLS, and the certless Windows probe would be rejected at the TLS handshake.
    /// </summary>
    public int SidecarHealthPort { get; set; } = 9004;

    /// <summary>
    /// Whether this customer's server should additionally answer WireGuard handshakes on
    /// <see cref="FallbackPort"/>, for clients whose network blocks the primary UDP port.
    /// This is the admin's *intent* — the heartbeat only advertises the fallback to clients
    /// once the sidecar confirms the redirect is really installed (see
    /// <c>docs/design/port-443-fallback.md</c> §4.4), otherwise a customer whose preflight
    /// failed would have every client waste its fallback window on a dead port at every
    /// connect. Customer-level only: the underlying redirect is server-wide, so a per-client
    /// override could not mean anything.
    /// </summary>
    public bool FallbackPortEnabled { get; set; } = false;

    /// <summary>
    /// The alternate UDP port, default 443 (open on many networks because QUIC / HTTP-3 uses
    /// it). Configurable rather than hardcoded so an admin whose host already serves UDP 443
    /// can pick another commonly-open port without a code change.
    /// </summary>
    public int FallbackPort { get; set; } = 443;

    /// <summary>
    /// Whether the sidecar last confirmed the redirect is really installed — the observed
    /// counterpart to <see cref="FallbackPortEnabled"/>, cached from /info by
    /// <c>SidecarService.CacheSidecarInfoAsync</c> so the heartbeat can gate on it without an
    /// /info round-trip per request. Always false for External customers, which have no
    /// sidecar to confirm anything (their admin asserts reachability instead).
    /// </summary>
    public bool FallbackPortActive { get; set; } = false;

    /// <summary>
    /// How long (seconds) a client should see no successful verification on the primary port
    /// before it tries the fallback once. Admin-configurable per customer alongside the port
    /// itself — a slow/high-latency link may want longer than the 20s default before giving up
    /// on the primary port. Sent to clients via <c>FallbackEndpointEntry.TriggerSeconds</c>;
    /// clamped server-side to 1-300s (<c>ClientsController.FallbackPortFor</c>).
    /// </summary>
    public int FallbackTriggerSeconds { get; set; } = 20;

    /// <summary>Builds the full mTLS base URL from host + port, e.g. https://10.0.1.2:8443</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string SidecarBaseUrl =>
        string.IsNullOrEmpty(SidecarUrl)
            ? ""
            : $"https://{SidecarUrl}:{SidecarManagementPort}";

    /// <summary>Sidecar WireGuard interface address cached from /info (e.g. 10.100.0.1).
    /// Used to build the post-connect health probe URL sent to clients.
    /// Only sent to authenticated clients over HTTPS — never over plain HTTP.</summary>
    [MaxLength(45)]
    public string? SidecarVpnIp { get; set; }

    /// <summary>
    /// Sidecar WireGuard transit-network CIDR cached from /info (e.g. 10.100.0.0/24) — the
    /// VPN's own subnet, not a LAN behind it. Transit subnets must be globally disjoint across
    /// customers so every sidecar gateway IP is unique; this is what makes the gateway `/health`
    /// verification safe for multiple simultaneous sidecar tunnels (see docs/multi-vpn-plan.md).
    /// </summary>
    [MaxLength(45)]
    public string? SidecarVpnSubnet { get; set; }

    /// <summary>
    /// Comma-separated physical LAN CIDR(s) behind the sidecar host, cached from /info (e.g.
    /// "192.168.1.0/24"). Unlike <see cref="SidecarVpnSubnet"/> (the VPN's own transit subnet),
    /// this is the real network the sidecar host sits on — reachable to clients only when their
    /// profile's AllowedIPs is a full-tunnel default route, where the actual routed network can't
    /// otherwise be inferred client-side. Null for External customers (no sidecar) and for
    /// sidecars that haven't been upgraded to report it yet.
    /// </summary>
    [MaxLength(500)]
    public string? SidecarLanCidrs { get; set; }

    /// <summary>Single-use enrollment token set by an admin to bootstrap mTLS on a new sidecar.
    /// Cleared immediately after the sidecar successfully enrolls.</summary>
    [MaxLength(128)]
    public string? EnrollmentToken { get; set; }

    /// <summary>Public endpoint written into generated client configs, e.g. vpn.example.com:51820</summary>
    [MaxLength(255)]
    public string? ServerEndpoint { get; set; }

    /// <summary>Optional DNS pushed to clients in their generated config.</summary>
    [MaxLength(45)]
    public string? DnsServer { get; set; }

    /// <summary>
    /// Optional comma-separated DNS search domains appended to the <c>DNS =</c> line, e.g.
    /// "corp.internal, lan". WireGuard for Windows turns these into NRPT split-DNS rules so
    /// only those domains resolve through this tunnel — letting multiple simultaneous tunnels
    /// each own their own DNS without cross-leaking. Requires <see cref="DnsServer"/> to be set.
    /// Can be overridden per client via <see cref="Client.DnsSearchDomains"/>.
    /// </summary>
    [MaxLength(255)]
    public string? DnsSearchDomains { get; set; }

    /// <summary>
    /// Letters-only prefix used to name the VPN profile on the client, e.g. "MyCompany" → "MyCompany-VPN".
    /// Null falls back to a sanitised version of the customer name.
    /// </summary>
    [MaxLength(50)]
    public string? VpnPrefix { get; set; }

    /// <summary>
    /// When true, admins may also push externally-created .conf files to clients
    /// even when ServerMode = "Valenius".
    /// When false (the default), the Upload Config card is hidden for this customer's clients.
    /// Has no effect when ServerMode = "External" (upload is always available there).
    /// </summary>
    public bool AllowExternalConfig { get; set; }

    /// <summary>
    /// Default AllowedIPs written into generated client configs, e.g. "0.0.0.0/0, ::/0"
    /// for full tunnel or "10.100.0.0/24" for split tunnel.
    /// Null means the built-in default "0.0.0.0/0, ::/0" is used.
    /// Can be overridden per client via <see cref="Client.AllowedIPs"/>.
    /// </summary>
    [MaxLength(200)]
    public string? AllowedIPs { get; set; }

    // ── Licensing ─────────────────────────────────────────────────────────────

    /// <summary>Max endpoints allocated to this customer from the license pool. 0 = not yet assigned.</summary>
    public int MaxEndpoints { get; set; } = 0;

    // ── MFA session gating (Pro) — see docs/design/mfa-session-gating.md ──────────

    /// <summary>
    /// Tenant default MFA policy applied to <c>User</c> peers without a per-client override.
    /// Defaults to <see cref="MfaPolicy.Disabled"/> — the feature is opt-in.
    /// </summary>
    public MfaPolicy DefaultMfaPolicy { get; set; } = MfaPolicy.Disabled;

    /// <summary>Default session lifetime / absolute cap (hours) for gated peers.</summary>
    public int MfaSessionLifetimeHours { get; set; } = 12;

    // ── Device tunnel (Windows, Pro) ────────────────────────────────────────────

    /// <summary>
    /// Tenant default for whether the Windows client connects this customer's own native
    /// sidecar profile automatically at boot, before any user logs on (only while off a
    /// trusted network — see <see cref="TrustedNetwork"/>). Overridable per client via
    /// <see cref="Client.DeviceTunnelEnabled"/>. Has no effect when ServerMode != "Valenius"
    /// (there is no native profile to connect). Defaults to false — opt-in.
    /// </summary>
    public bool DeviceTunnelDefaultEnabled { get; set; } = false;

    // ── Cross-customer sidecar sharing (Pro) ─────────────────────────────────

    /// <summary>When true, admins may provision other customers' clients onto this customer's sidecar.</summary>
    public bool AllowShareSidecar { get; set; }

    /// <summary>When true, this customer's clients may receive profiles from other customers' sidecars.</summary>
    public bool AllowReceiveForeignProfiles { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────────

    public List<Client> Clients { get; set; } = [];
    public List<AppUser> AppUsers { get; set; } = [];
    public List<TrustedNetwork> TrustedNetworks { get; set; } = [];
}
