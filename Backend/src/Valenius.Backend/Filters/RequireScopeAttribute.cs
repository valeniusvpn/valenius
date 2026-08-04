namespace Valenius.Backend.Filters;

/// <summary>Declares the Management API scope a controller action requires (e.g.
/// "clients:read"). Every action under <c>/api/mgmt/v1/**</c> must carry this or
/// <see cref="MgmtAnonymousAttribute"/> — enforced at boot by Backend.Pro's startup scope
/// assertion (docs/design/management-api.md §10.1), so a new endpoint that forgets both
/// fails the deploy rather than serving unscoped.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireScopeAttribute(string scope) : Attribute
{
    public string Scope { get; } = scope;
}
