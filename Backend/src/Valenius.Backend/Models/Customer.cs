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
