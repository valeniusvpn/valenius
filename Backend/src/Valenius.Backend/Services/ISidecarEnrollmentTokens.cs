using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>Pro token issuance and verification for shared admin pages.</summary>
public interface ISidecarEnrollmentTokens
{
    string Issue(Customer customer);
    bool IsPending(Customer customer);
    bool Matches(Customer customer, string? token);
}

public sealed class NullSidecarEnrollmentTokens : ISidecarEnrollmentTokens
{
    public string Issue(Customer customer) => throw new NotSupportedException("Sidecar enrollment requires Pro.");
    public bool IsPending(Customer customer) => false;
    public bool Matches(Customer customer, string? token) => false;
}
