namespace Valenius.Shared;

public enum CommandType
{
    Connect,
    Disconnect,
    Status,
    UploadConfig,
    GetConfigInfo,
    CheckUpdate,
    Register,
    SyncStatus,
    SetAutoConnect,
    DeleteProfile,
    /// <summary>Sent by the TrayApp just before it exits so the service can notify the backend.</summary>
    NotifyOffline,
    /// <summary>Sent by the TrayApp to confirm MFA enrollment with a TOTP code (carried in <see cref="PipeCommand.MfaCode"/>).</summary>
    MfaEnrollConfirm,
    /// <summary>Sent by the TrayApp "Send logs" action — the service collects its (Valenius-only,
    /// redacted) diagnostic logs and uploads them to the backend.</summary>
    SendLogs,
    /// <summary>Sent by the TrayApp first-run prompt to set the backend server address. The DNS name
    /// is carried in <see cref="PipeCommand.BackendDns"/>; the service prepends https:// and persists it.</summary>
    SetBackendUrl,
    /// <summary>Sent by the TrayApp's first-run "Use standalone" choice. No payload — the service just
    /// persists that the user opted out of backend setup, so the tray stops prompting and shows the
    /// normal popup (local profiles, no backend contacted) instead of the setup-required block.</summary>
    SetStandaloneMode
}

public class PipeCommand
{
    public CommandType Command { get; set; }
    public string? ConfigContent { get; set; }
    public string? ProfileName { get; set; }
    public bool? AutoConnectEnabled { get; set; }
    /// <summary>TOTP code for <see cref="CommandType.MfaEnrollConfirm"/>.</summary>
    public string? MfaCode { get; set; }
    /// <summary>DNS host for <see cref="CommandType.SetBackendUrl"/> (without scheme; the service prepends https://).</summary>
    public string? BackendDns { get; set; }
    /// <summary>For <see cref="CommandType.UploadConfig"/>: when true, the uploaded profile is also
    /// archived to the backend so every OS user on this machine can receive it, not just the
    /// uploading account. Defaults to (null/false) local-only, matching prior behavior.</summary>
    public bool? ShareWithAllUsers { get; set; }
}

public class PipeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? DataJson { get; set; }
    /// <summary>True when <see cref="Error"/> is a pre-connect local-LAN-conflict refusal
    /// (the client's own LAN overlaps a network reachable through the VPN, or the assigned VPN
    /// IP itself). The TrayApp shows a blocking modal for this instead of the usual balloon.</summary>
    public bool IsLanConflict { get; set; }
    /// <summary>For <see cref="CommandType.UploadConfig"/> with <see cref="PipeCommand.ShareWithAllUsers"/>:
    /// true when the local save succeeded (<see cref="Success"/> is still true) but archiving the
    /// profile to the backend for other OS users failed — <see cref="Error"/> carries a
    /// user-facing explanation. The profile stays usable, just not yet shared.</summary>
    public bool ShareFailed { get; set; }
}

/// <summary>One connected WireGuard tunnel. Multiple may be active simultaneously
/// (the primary/server VPN plus any number of foreign-customer profiles).</summary>
public class ConnectedTunnelInfo
{
    public string Name { get; set; } = "";
    public bool IsVerified { get; set; }
    /// <summary>
    /// True once the initial ~60s verification window elapsed with no success. Not terminal —
    /// verification keeps retrying at a slower cadence in the background, and a late success
    /// still clears this and sets <see cref="IsVerified"/>. The tray shows an amber
    /// "Confirmation failed" tag instead of the plain "Connected" one.
    /// </summary>
    public bool VerificationFailed { get; set; }
    /// <summary>
    /// The UDP port this tunnel switched to (typically 443) after the primary WireGuard® port
    /// produced no successful verification within the trigger window
    /// (docs/design/port-443-fallback.md), or 0 if it's still on the primary port. Sticky for the
    /// life of the connection — cleared only by a fresh connect, which always starts back on the
    /// primary port. The tray appends " · &lt;port&gt;" to whichever tag it would otherwise show,
    /// since it's useful for the user to know their traffic left on an alternate port.
    /// </summary>
    public int FallbackPortUsed { get; set; }
    public string? ConnectedUser { get; set; }
    public DateTime ConnectedSince { get; set; }
}

public class TunnelStatus
{
    public bool IsConnected { get; set; }
    /// <summary>
    /// True once the background probe confirmed end-to-end traffic through the tunnel.
    /// The UI replaces the green dot with a green checkmark when this is true.
    /// </summary>
    public bool IsVerified { get; set; }
    /// <summary>Mirrors <see cref="ConnectedTunnelInfo.VerificationFailed"/> for the primary/server tunnel.</summary>
    public bool VerificationFailed { get; set; }
    /// <summary>Mirrors <see cref="ConnectedTunnelInfo.FallbackPortUsed"/> for the primary/server tunnel.</summary>
    public int FallbackPortUsed { get; set; }
    public string? ConnectedUser { get; set; }
    public string? TunnelName { get; set; }
    public DateTime? ConnectedSince { get; set; }

    /// <summary>
    /// All currently-connected tunnels. The source of truth for per-profile row
    /// indicators in the tray. The single-tunnel fields above mirror the primary/server
    /// profile entry here (or the first entry) for backward compatibility with the status
    /// bar, MFA flow, and the old no-profile disconnect path.
    /// </summary>
    public ConnectedTunnelInfo[] ConnectedTunnels { get; set; } = [];
    public bool? RegistrationIsActive { get; set; }
    public bool HasStagedConfig { get; set; }
    public bool UpdateAvailable { get; set; }

    /// <summary>
    /// True when no backend server address is configured yet (fresh install without an installer-provided
    /// URL) — the tray prompts the user for the server DNS. Intentionally phrased so that a missing field
    /// from an older service deserializes to <c>false</c> (configured) and never triggers a spurious prompt.
    /// </summary>
    public bool BackendUnconfigured { get; set; }
    /// <summary>
    /// True once the user explicitly chose "Use standalone" in the first-run setup dialog while
    /// <see cref="BackendUnconfigured"/> is true. The tray shows the normal popup (local profiles,
    /// connect/upload/delete all work with no backend contacted) instead of the setup-required
    /// block, plus a "Connect to a server" action to opt in later. Default <c>false</c> preserves
    /// today's behavior — same reasoning as <see cref="BackendUnconfigured"/>'s doc comment.
    /// </summary>
    public bool StandaloneMode { get; set; }
    public string[] AvailableProfiles  { get; set; } = [];
    public string[] DeletableProfiles  { get; set; } = [];
    public bool     AutoConnectEnabled { get; set; }

    // ── MFA session gating (Pro) ──────────────────────────────────────────────
    /// <summary>True when this peer is MFA-gated and has no active session — the tray prompts for re-auth.</summary>
    public bool      MfaRequired         { get; set; }
    /// <summary>Deep link the tray opens in the browser to authorize.</summary>
    public string?   MfaAuthorizeUrl     { get; set; }
    /// <summary>Absolute expiry of the active MFA session, for the countdown. Null when none.</summary>
    public DateTime? MfaSessionExpiresAt { get; set; }
    /// <summary>True when the TOTP enrollment window is open — the tray shows the QR.</summary>
    public bool      MfaEnrollmentOpen   { get; set; }
    /// <summary>otpauth:// URI for the enrollment QR.</summary>
    public string?   MfaEnrollmentUri    { get; set; }
    /// <summary>For a gated requester with a push-approver device: the number to display
    /// ("approve N on your phone"). Null when there's no active push-approve challenge.</summary>
    public int?      MfaApproveNumber    { get; set; }
}

public class RegistrationResult
{
    public bool IsActive { get; set; }
    public string? Message { get; set; }
}

public class ConfigInfo
{
    public bool HasConfig { get; set; }
    public string? TunnelName { get; set; }
    public string? FileName { get; set; }
    public string[] Profiles { get; set; } = [];
}

public static class ProfileNameHelper
{
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name.Length <= 50 &&
        name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-');

    public static string Sanitize(string filename)
    {
        var stem = Path.GetFileNameWithoutExtension(filename);
        var safe = new string(stem.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
        return safe.Length > 50 ? safe[..50] : safe;
    }
}

public class VersionCheckResult
{
    public bool UpdateAvailable { get; set; }
    public string? CurrentVersion { get; set; }
    public string? LatestVersion { get; set; }
    public string? ReleaseNotes { get; set; }
}
