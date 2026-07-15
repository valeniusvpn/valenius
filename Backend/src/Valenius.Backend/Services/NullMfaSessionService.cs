using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>
/// OSS no-op: MFA session gating is a Pro feature. Every peer resolves to
/// <see cref="MfaPolicy.Disabled"/> and all lifecycle operations are inert.
/// The Pro project replaces this with <c>MfaSessionService</c> via DI.
/// </summary>
public sealed class NullMfaSessionService : IMfaSessionService
{
    public MfaPolicyResolution ResolvePolicy(Client client, Customer? customer)
        => new(MfaPolicy.Disabled, 0, 0);

    public string? GetEnrollmentUri(Client client, Customer? customer) => null;

    public Task<string?> OpenEnrollmentAsync(Client client, Customer? customer)
        => Task.FromResult<string?>(null);

    public Task<bool> ConfirmEnrollmentAsync(Client client, string code)
        => Task.FromResult(false);

    public Task<string?> ResetEnrollmentAsync(Client client, Customer? customer)
        => Task.FromResult<string?>(null);

    public Task ApplyPolicyChangeAsync(Client client, Customer? customer, bool currentlyConnected)
        => Task.CompletedTask;

    public Task<bool> ValidateTotpAsync(Client client, string code)
        => Task.FromResult(false);

    public Task<MfaSession?> CreateSessionAsync(Client client, Customer? customer, string? fromIp)
        => Task.FromResult<MfaSession?>(null);

    public Task<MfaSession?> GetActiveSessionAsync(int clientId)
        => Task.FromResult<MfaSession?>(null);

    public Task<bool> RevokeSessionAsync(Guid sessionId, string? revokedBy)
        => Task.FromResult(false);

    public Task<int> SweepExpiredAsync()
        => Task.FromResult(0);

    public Task ReconcileSidecarAsync(Customer customer)
        => Task.CompletedTask;
}
