using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>
/// Effective MFA policy for a peer after resolving the Machine override and the
/// client/customer inheritance chain (design doc §4.1).
/// </summary>
public readonly record struct MfaPolicyResolution(
    MfaPolicy Policy,
    int SessionLifetimeHours,
    int IdleGraceSeconds)
{
    /// <summary>True when the peer is subject to MFA gating.</summary>
    public bool Gated => Policy != MfaPolicy.Disabled;
}

/// <summary>
/// Pro feature: per-client TOTP enrollment + server-issued MFA session lifecycle.
/// OSS registers <see cref="NullMfaSessionService"/> (always ungated); the Pro project
/// replaces it via DI. Enforcement on the sidecar (grant push / expiry) is layered on in
/// Phase 2 — this service owns policy resolution, enrollment, and the session record.
/// </summary>
public interface IMfaSessionService
{
    /// <summary>Resolves the effective policy for a peer (Machine ⇒ Disabled; else override ?? default ?? Disabled).</summary>
    MfaPolicyResolution ResolvePolicy(Client client, Customer? customer);

    /// <summary>Returns the <c>otpauth://</c> enrollment URI if the client's window is open, else null.</summary>
    string? GetEnrollmentUri(Client client, Customer? customer);

    /// <summary>Opens the enrollment window: generates a secret, returns the <c>otpauth://</c> URI.</summary>
    Task<string?> OpenEnrollmentAsync(Client client, Customer? customer);

    /// <summary>Confirms enrollment with a candidate code; enables the factor on success.</summary>
    Task<bool> ConfirmEnrollmentAsync(Client client, string code);

    /// <summary>Admin reset: clears the factor and re-opens enrollment. Returns the new URI.</summary>
    Task<string?> ResetEnrollmentAsync(Client client, Customer? customer);

    /// <summary>
    /// Applies the activation/transition rules after an admin changes a peer's policy
    /// (design doc §4.4): opens enrollment, grandfathers a live tunnel with a capped
    /// synthetic grant, removes a pre-existing peer for a disconnected gated client, or
    /// restores normal peer + revokes sessions when un-gated. Call after saving the new
    /// policy fields on the client.
    /// </summary>
    Task ApplyPolicyChangeAsync(Client client, Customer? customer, bool currentlyConnected);

    /// <summary>Validates a candidate TOTP code against the client's enrolled secret (replay-protected).</summary>
    Task<bool> ValidateTotpAsync(Client client, string code);

    /// <summary>Creates and persists an MFA session (after successful TOTP) and returns it. Null if the peer is ungated.</summary>
    Task<MfaSession?> CreateSessionAsync(Client client, Customer? customer, string? fromIp);

    /// <summary>Returns the current active, non-expired session for a client, or null.</summary>
    Task<MfaSession?> GetActiveSessionAsync(int clientId);

    /// <summary>Revokes a session by its public id. Returns true if a session was revoked.</summary>
    Task<bool> RevokeSessionAsync(Guid sessionId, string? revokedBy);

    /// <summary>Marks every active session whose absolute expiry has passed as Expired. Returns the count.</summary>
    Task<int> SweepExpiredAsync();

    /// <summary>
    /// Reconciles one customer's sidecar against the DB: re-pushes active grants the sidecar
    /// is missing and revokes orphan grants the sidecar still holds. No-op if unreachable.
    /// </summary>
    Task ReconcileSidecarAsync(Customer customer);
}
