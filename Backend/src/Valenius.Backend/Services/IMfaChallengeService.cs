using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>
/// Cross-device push-to-approve MFA (Pro). When a gated client that has an approver
/// device needs a session, a challenge is created and delivered to the approver phone
/// (via its heartbeat plus an FCM wake); the phone approves by matching a number,
/// which creates the requester's session — the same grant pipeline as the TOTP path.
/// No-op in OSS (<see cref="NullMfaChallengeService"/>).
/// </summary>
public interface IMfaChallengeService
{
    /// <summary>Ensure a pending challenge exists for a gated requester that has an
    /// approver device. Idempotent — reuses an existing unexpired pending challenge,
    /// and (Pro) sends the FCM wake.</summary>
    Task EnsureChallengeAsync(Client requester, string? fromIp);

    /// <summary>Pending approvals addressed to this approver device (for its heartbeat).
    /// The correct answer is never included — the approver matches what the requester shows.</summary>
    Task<IReadOnlyList<MfaApproval>> GetPendingApprovalsAsync(int approverClientId);

    /// <summary>The match number a gated requester should display ("approve N on your
    /// phone"), or null when there's no active push-approve challenge.</summary>
    Task<int?> GetRequesterMatchNumberAsync(int requesterClientId);

    /// <summary>Approve a challenge: verifies the matched number, then creates the
    /// requester's MFA session. Returns false on invalid/expired/mismatch/wrong-approver.</summary>
    Task<bool> ApproveAsync(int approverClientId, int challengeId, int matchedNumber, string? fromIp);

    /// <summary>Deny/cancel a challenge.</summary>
    Task DenyAsync(int approverClientId, int challengeId);
}

/// <summary>A pending approval shown on the approver phone.</summary>
public record MfaApproval(
    int ChallengeId,
    string RequesterName,
    string? RequesterIp,
    int[] Choices,
    DateTime ExpiresAt);
