namespace Valenius.Backend.Models;

/// <summary>
/// MFA gating policy for a WireGuard peer. Resolved per client:
/// <c>Machine</c> peers are never gated; otherwise the effective policy is
/// <c>Client.MfaPolicy ?? Customer.DefaultMfaPolicy ?? Disabled</c>.
/// </summary>
public enum MfaPolicy
{
    /// <summary>No MFA gating — the peer is provisioned and active as today.</summary>
    Disabled = 0,

    /// <summary>
    /// Session bound to tunnel liveness. Re-auth required for every new connection;
    /// explicit disconnect revokes immediately, idle teardown after a grace window,
    /// absolute cap = session lifetime. See design doc §4.1.
    /// </summary>
    PerConnection = 1,

    /// <summary>Session lasts a fixed number of hours regardless of connect/disconnect.</summary>
    Interval = 2,
}

/// <summary>
/// Peer classification. <c>Machine</c> peers (site-to-site gateways, servers) are never
/// subject to MFA gating; <c>User</c> peers can be gated by policy.
/// </summary>
public enum PeerType
{
    User,
    Machine,
}
