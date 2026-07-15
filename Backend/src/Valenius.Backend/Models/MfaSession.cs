using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A server-issued MFA session (grant lease) for a gated WireGuard peer.
/// The backend mints the row after a successful TOTP authorization; the sidecar
/// enforces the absolute <see cref="ExpiresAt"/> on its own clock (design doc §5–6).
/// </summary>
public class MfaSession
{
    public int Id { get; set; }

    public int ClientId { get; set; }
    public Client? Client { get; set; }

    /// <summary>Owning customer at issue time (denormalised for fast listing / audit).</summary>
    public int? CustomerId { get; set; }

    /// <summary>Stable public session identifier carried in the signed grant.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Per-grant nonce (replay protection on the sidecar).</summary>
    [MaxLength(64)]
    public string Nonce { get; set; } = "";

    public DateTime IssuedAt { get; set; }

    /// <summary>Absolute expiry the sidecar enforces locally (never "until told otherwise").</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>"Active" | "Expired" | "Revoked" — see <see cref="MfaSessionStatus"/>.</summary>
    [MaxLength(10)]
    public string Status { get; set; } = MfaSessionStatus.Active;

    [MaxLength(45)]
    public string? CreatedFromIp { get; set; }

    public DateTime? RevokedAt { get; set; }

    [MaxLength(200)]
    public string? RevokedBy { get; set; }
}

/// <summary>String constants for <see cref="MfaSession.Status"/>.</summary>
public static class MfaSessionStatus
{
    public const string Active  = "Active";
    public const string Expired = "Expired";
    public const string Revoked = "Revoked";
}
