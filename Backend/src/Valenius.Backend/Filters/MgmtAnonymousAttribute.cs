namespace Valenius.Backend.Filters;

/// <summary>Opts a Management API action out of the <see cref="RequireScopeAttribute"/>
/// check — used only by <c>/ping</c>, <c>/me</c>, and <c>/openapi.json</c> (§7.1), which
/// require a valid, non-revoked, non-expired token like every other management route but
/// no specific scope. This does NOT skip authentication itself — <c>ApiTokenFilter</c>
/// still resolves and validates the token; it only skips the per-action scope-membership
/// check afterward. See docs/design/management-api.md §10.1.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class MgmtAnonymousAttribute : Attribute;
