using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// Single-row table that stores global backend settings (auth configuration).
/// Always seeded with Id = 1 on first run.  Loaded by the Admin -> Settings page.
/// Per-customer WireGuard server settings live on the Customer model.
/// </summary>
public class ServerSettings
{
    public int Id { get; set; }

    // ── Authentication method toggles ─────────────────────────────────────

    /// <summary>
    /// When true, the Authentik / OIDC login option is shown on the login page.
    /// At least one of OidcEnabled / LocalAuthEnabled must be true.
    /// </summary>
    public bool OidcEnabled { get; set; }   // default off for new installs; enable after configuring OIDC settings

    /// <summary>
    /// When true, integrated username + password login is available.
    /// Users must have a PasswordHash set (managed in Admin → Users).
    /// At least one of OidcEnabled / LocalAuthEnabled must be true.
    /// </summary>
    public bool LocalAuthEnabled { get; set; } = true;   // default on for new installs

    // ── OIDC provider configuration ───────────────────────────────────────
    // Stored in DB so they can be managed via Admin → Settings without editing
    // env vars or rebuilding the image.  Changes take effect on next restart.

    [MaxLength(500)]
    public string? OidcAuthority    { get; set; }

    [MaxLength(200)]
    public string? OidcClientId     { get; set; }

    [MaxLength(500)]
    public string? OidcClientSecret { get; set; }

    [MaxLength(500)]
    public string? OidcRedirectUri  { get; set; }

    // ── SMTP / outbound e-mail ────────────────────────────────────────────

    public bool SmtpEnabled { get; set; }

    /// <summary>Hostname or IP of the SMTP relay, e.g. smtp.sendgrid.net</summary>
    [MaxLength(255)]
    public string? SmtpHost { get; set; }

    /// <summary>SMTP port. 587 = STARTTLS (default), 465 = implicit SSL.</summary>
    public int SmtpPort { get; set; } = 587;

    [MaxLength(255)]
    public string? SmtpUsername { get; set; }

    /// <summary>Password or API key. Leave blank to keep existing value when saving.</summary>
    [MaxLength(500)]
    public string? SmtpPassword { get; set; }

    /// <summary>When true uses implicit SSL (port 465). When false uses STARTTLS (port 587).</summary>
    public bool SmtpUseSsl { get; set; }

    /// <summary>
    /// RFC 5322 sender address, e.g. "Valenius &lt;noreply@example.com&gt;".
    /// A plain address without display name is also accepted.
    /// </summary>
    [MaxLength(255)]
    public string? EmailFrom { get; set; }

    // ── Client API key (auto-generated on first run) ──────────────────────
    // Used by Windows/Linux clients to authenticate against /api/**
    // Displayed read-only in Admin → Settings → Authentication.

    [MaxLength(128)]
    public string? ApiKey { get; set; }

    // The previous client API key, kept valid during a rotation grace window so
    // deployed clients can roll forward to the new key on their next heartbeat before
    // it is retired. Null when no rotation is in flight.
    [MaxLength(128)]
    public string? PreviousApiKey { get; set; }

    /// <summary>UTC time the API key was last rotated (for the admin grace-window display). Null if never.</summary>
    public DateTime? ApiKeyRotatedAt { get; set; }

    /// <summary>How many days of per-client WireGuard traffic samples to keep (charts window).
    /// Older <see cref="ClientTrafficSample"/> rows are pruned daily by TrafficSamplingService. Default 90.</summary>
    public int TrafficRetentionDays { get; set; } = 90;

    /// <summary>System-wide default heartbeat interval (minutes) used when a customer doesn't set
    /// its own <see cref="Customer.HeartbeatIntervalMinutes"/> override. Governs the periodic
    /// register cadence / presence refresh; admin-triggered actions (Connect, config push, etc.)
    /// are delivered independently via the long-poll (~1 s-55 s), not gated by this value.</summary>
    public int DefaultHeartbeatIntervalMinutes { get; set; } = 1;

    // ── Operational metadata ──────────────────────────────────────────────
    public DateTime? LastBackupAt { get; set; }

    // ── Installation license state (Pro only; ignored in OSS) ─────────────
    /// <summary>UTC timestamp of the last successful revocation check.</summary>
    public DateTime? LicenseLastValidatedAt { get; set; }

    /// <summary>"Valid", "Warning", or "OssMode".</summary>
    [MaxLength(20)]
    public string LicenseStatus { get; set; } = "Valid";

    // ── Management API (Pro) ───────────────────────────────────────────────
    /// <summary>When true, Management API responses that carry <c>clients:read</c> scope
    /// include the client's raw <see cref="Client.ClientKey"/>. Off by default: the key can
    /// drive a device's registration/heartbeat (POST /api/clients/register is
    /// [ApiKeyExempt]), so it's treated as a secret that happens to be useful as an RMM
    /// correlation id, not a plain identifier. See docs/design/management-api.md §7.2.1.
    /// Toggling this writes an Audit.Settings event — it changes what every existing
    /// management token can read, retroactively.</summary>
    public bool ApiExposeClientKey { get; set; }

    // ── Internal CA (generated once on first run) ─────────────────────────
    // PEM-encoded.  Never exposed via any API endpoint in raw form.
    // Included in the encrypted backup export only.

    /// <summary>CA private key (PEM). Loss requires re-enrollment of all sidecars.</summary>
    public string? CaPrivateKeyPem  { get; set; }

    /// <summary>CA certificate (PEM). Safe to share; downloadable from admin UI.</summary>
    public string? CaCertPem        { get; set; }

    /// <summary>Backend's own client certificate (PEM), presented to sidecars during mTLS.</summary>
    public string? BackendCertPem   { get; set; }

    /// <summary>Backend's own client private key (PEM).</summary>
    public string? BackendKeyPem    { get; set; }
}
