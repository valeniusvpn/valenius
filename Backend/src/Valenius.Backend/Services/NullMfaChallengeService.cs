using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>OSS no-op: push-to-approve is a Pro feature.</summary>
public sealed class NullMfaChallengeService : IMfaChallengeService
{
    public Task EnsureChallengeAsync(Client requester, string? fromIp) => Task.CompletedTask;

    public Task<IReadOnlyList<MfaApproval>> GetPendingApprovalsAsync(int approverClientId) =>
        Task.FromResult<IReadOnlyList<MfaApproval>>([]);

    public Task<int?> GetRequesterMatchNumberAsync(int requesterClientId) =>
        Task.FromResult<int?>(null);

    public Task<bool> ApproveAsync(int approverClientId, int challengeId, int matchedNumber, string? fromIp) =>
        Task.FromResult(false);

    public Task DenyAsync(int approverClientId, int challengeId) => Task.CompletedTask;
}
