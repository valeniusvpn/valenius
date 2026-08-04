using Microsoft.EntityFrameworkCore;
using System.Net;
using Valenius.Backend.Data;

namespace Valenius.Backend.Services;

/// <summary>Resolved identity of a validated Management API bearer token. Stashed in
/// <c>HttpContext.Items["ApiCaller"]</c> by <c>ApiTokenFilter</c> and read by controllers,
/// the audit hook, and — later — Backend.Pro's MCP tool handlers, which call
/// <see cref="ApiCallerResolver"/> directly outside the HTTP pipeline (see
/// docs/design/management-api.md §17.3).</summary>
public sealed record ApiCaller(
    int TokenRowId,
    string TokenId,
    string Name,
    int? CustomerId,
    IReadOnlySet<string> Scopes,
    DateTime? ExpiresAt)
{
    public bool HasScope(string scope) => Scopes.Contains(scope);
}

/// <summary>Why <see cref="ApiCallerResolver.ResolveAsync"/> did not produce an
/// <see cref="ApiCaller"/>. Deliberately distinct from a generic "unauthorized" so callers
/// can return the same diagnosable error codes the design doc specifies (§8.3):
/// <c>token_expired</c> / <c>token_revoked</c> / <c>ip_not_allowed</c> are not the same
/// failure as an unknown or malformed token, and an admin debugging "my automation broke
/// at midnight" needs that distinction without server log access.</summary>
public enum ApiCallerResolutionError
{
    MissingOrMalformedHeader,
    UnknownToken,
    SecretMismatch,
    Revoked,
    Expired,
    IpNotAllowed,
}

/// <summary>
/// The reusable core of Management API authentication: token lookup, constant-time secret
/// verification, revocation/expiry/IP-allowlist checks, and scope-set parsing. Deliberately
/// has no dependency on <c>HttpContext</c> or the ASP.NET Core pipeline — it is a plain
/// scoped service so it can be called both from <c>ApiTokenFilter</c> (over a real HTTP
/// request) and, later, from an in-process MCP tool handler (§17.3), without a second
/// implementation of the auth logic. <paramref name="remoteIp"/> is a plain value, not
/// <c>HttpContext.Connection.RemoteIpAddress</c> read internally — callers supply it.
///
/// Reads the token row from Postgres on every call — no cache. See
/// docs/design/management-api.md §4.3 for why: at this feature's design scale (≤500
/// clients per backend) a cache buys nothing measurable and makes revocation depend on
/// invalidation wiring being correct, instead of being correct by construction.
/// </summary>
public sealed class ApiCallerResolver(ApplicationDbContext db, ApiTokenUsageThrottle usageThrottle)
{
    public async Task<(ApiCaller? Caller, ApiCallerResolutionError? Error)> ResolveAsync(
        string? authorizationHeader, IPAddress? remoteIp, CancellationToken ct = default)
    {
        var parsed = ApiTokenGenerator.TryParseAuthorizationHeader(authorizationHeader);
        if (parsed is null)
            return (null, ApiCallerResolutionError.MissingOrMalformedHeader);

        // TokenId is unique + indexed — a single index seek, not a scan of every row's hash.
        var row = await db.ApiTokens.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenId == parsed.Value.TokenId, ct);
        if (row is null)
            return (null, ApiCallerResolutionError.UnknownToken);

        if (!ApiTokenGenerator.SecretMatches(parsed.Value.Secret, row.SecretHash))
            return (null, ApiCallerResolutionError.SecretMismatch);

        if (row.RevokedAt is not null)
            return (null, ApiCallerResolutionError.Revoked);

        if (row.ExpiresAt is { } expiresAt && expiresAt < DateTime.UtcNow)
            return (null, ApiCallerResolutionError.Expired);

        if (!IpAllowlist.IsAllowed(row.IpAllowlist, remoteIp))
            return (null, ApiCallerResolutionError.IpNotAllowed);

        // Throttled to at most once/minute/token — see ApiTokenUsageThrottle. A bare
        // ExecuteUpdateAsync (no tracked entity) so a polling integration doesn't turn
        // every GET into a full row load + save.
        if (usageThrottle.ShouldWrite(row.Id))
        {
            var ipText = remoteIp?.ToString();
            await db.ApiTokens
                .Where(t => t.Id == row.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.LastUsedAt, DateTime.UtcNow)
                    .SetProperty(t => t.LastUsedIp, ipText), ct);
        }

        var scopes = row.Scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);

        return (new ApiCaller(row.Id, row.TokenId, row.Name, row.CustomerId, scopes, row.ExpiresAt), null);
    }
}
