using System.ComponentModel.DataAnnotations;

namespace Valenius.Backend.Models;

/// <summary>
/// A per-token credential for the Management API (<c>/api/mgmt/v1/**</c>) — a separate
/// credential plane from the fleet-wide <see cref="ServerSettings.ApiKey"/> device key.
/// Presented as <c>Authorization: Bearer vlnm_&lt;TokenId&gt;_&lt;secret&gt;</c>; only
/// <see cref="SecretHash"/> (SHA-256 of the secret segment) is ever stored — the secret
/// itself is shown to the admin exactly once, at creation, and never again.
/// See docs/design/management-api.md §4.
/// </summary>
public class ApiToken
{
    public int Id { get; set; }

    /// <summary>Public token id (the middle segment of the presented token). Unique, indexed —
    /// this is the DB lookup key, and what the admin UI / audit log / error messages display.</summary>
    [MaxLength(20)]
    public string TokenId { get; set; } = "";

    /// <summary>Hex SHA-256 of the secret segment. Never the secret itself.</summary>
    [MaxLength(64)]
    public string SecretHash { get; set; } = "";

    [MaxLength(100)]
    public string Name { get; set; } = "";

    [MaxLength(500)]
    public string? Description { get; set; }

    /// <summary>Comma-separated scope list, e.g. "clients:read,clients:write".
    /// See docs/design/management-api.md §5.1 for the fixed scope catalog.</summary>
    [MaxLength(500)]
    public string Scopes { get; set; } = "";

    /// <summary>Null = fleet-wide. Set = every request through this token is filtered to
    /// this customer. Cascade-deletes with the customer — a customer-bound token must die
    /// with its customer, never silently widen to fleet-wide.</summary>
    public int? CustomerId { get; set; }
    public Customer? Customer { get; set; }

    /// <summary>Optional comma-separated CIDR allowlist. Null/empty = any source IP.</summary>
    [MaxLength(500)]
    public string? IpAllowlist { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(200)]
    public string CreatedBy { get; set; } = "";

    /// <summary>Null = no expiry.</summary>
    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastUsedAt { get; set; }

    [MaxLength(45)]
    public string? LastUsedIp { get; set; }

    /// <summary>Soft-revoke. Non-null immediately rejects every request through this token
    /// (looked up per request — see ApiCallerResolver — so revocation needs no cache
    /// invalidation). Revoked rows stay in the table so audit entries referencing them
    /// remain resolvable.</summary>
    public DateTime? RevokedAt { get; set; }
}
