using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

public class AppUser
{
    public int Id { get; set; }

    [MaxLength(200)]
    public string Subject { get; set; } = "";

    [MaxLength(200)]
    public string DisplayName { get; set; } = "";

    [MaxLength(200)]
    public string? Email { get; set; }

    [MaxLength(50)]
    public string Role { get; set; } = "User";

    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>
    /// PBKDF2 password hash for integrated (local) login users.
    /// Format: "{iterations}:{base64 salt}:{base64 hash}".
    /// Null means the user can only log in via OIDC.
    /// </summary>
    [MaxLength(255)]
    public string? PasswordHash { get; set; }

    // ── TOTP / two-factor authentication ─────────────────────────────────────

    /// <summary>
    /// Base32-encoded TOTP secret (RFC 6238).  Null = TOTP not configured.
    /// Compatible with any TOTP app (Google Authenticator, Microsoft Authenticator, Authy, …).
    /// </summary>
    [MaxLength(64)]
    public string? TotpSecret { get; set; }

    /// <summary>Whether TOTP is required at login for this user.</summary>
    public bool TotpEnabled { get; set; }

    /// <summary>
    /// The RFC 6238 time-step counter of the last successfully verified TOTP code.
    /// Null until TOTP has been used at least once.  Used to prevent code replay within
    /// the VerificationWindow tolerance window.
    /// </summary>
    public long? TotpLastUsedInterval { get; set; }

    /// <summary>True if this is a local (non-OIDC) user (Subject starts with "local:").</summary>
    public bool IsLocalUser => Subject.StartsWith("local:", StringComparison.Ordinal);
}
