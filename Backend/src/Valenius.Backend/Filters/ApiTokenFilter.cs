using Microsoft.AspNetCore.Mvc.Filters;
using Valenius.Backend.Services;

namespace Valenius.Backend.Filters;

/// <summary>
/// Authenticates and authorizes Management API (<c>/api/mgmt/v1/**</c>) requests via
/// <c>Authorization: Bearer vlnm_...</c> tokens. Mirrors <see cref="ApiKeyFilter"/>'s
/// fail-closed shape, but for a different credential plane (docs/design/management-api.md
/// §2) with per-action scope requirements instead of one shared key: every action must
/// declare <see cref="RequireScopeAttribute"/> or opt out of the scope check with
/// <see cref="MgmtAnonymousAttribute"/> (still authenticated — see that attribute's docs).
///
/// An action declaring neither fails closed here too (§10.1) — defense in depth alongside
/// the boot-time assertion in Backend.Pro's Program.cs, which is meant to catch this before
/// the process ever starts serving traffic.
/// </summary>
public sealed class ApiTokenFilter(ApiCallerResolver resolver) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        var remoteIp = context.HttpContext.Connection.RemoteIpAddress;

        var (caller, error) = await resolver.ResolveAsync(header, remoteIp, context.HttpContext.RequestAborted);

        if (caller is null)
        {
            context.Result = error switch
            {
                ApiCallerResolutionError.Revoked =>
                    ManagementProblem.Unauthorized("token_revoked", "This token has been revoked."),
                ApiCallerResolutionError.Expired =>
                    ManagementProblem.Unauthorized("token_expired", "This token has expired."),
                ApiCallerResolutionError.IpNotAllowed =>
                    ManagementProblem.Forbidden("ip_not_allowed", "This token is not permitted from the requesting IP address."),
                _ =>
                    ManagementProblem.Unauthorized("unauthorized", "Missing or invalid Authorization header."),
            };
            return;
        }

        var requiredScope = context.ActionDescriptor.EndpointMetadata.OfType<RequireScopeAttribute>().FirstOrDefault();
        var scopeExempt = context.ActionDescriptor.EndpointMetadata.OfType<MgmtAnonymousAttribute>().Any();

        if (requiredScope is not null)
        {
            if (!caller.HasScope(requiredScope.Scope))
            {
                context.Result = ManagementProblem.Forbidden(
                    "insufficient_scope", $"This token lacks the required scope '{requiredScope.Scope}'.");
                return;
            }
        }
        else if (!scopeExempt)
        {
            // Reached only if the boot-time assertion was bypassed or missed this route —
            // fail closed rather than silently granting access to an undeclared endpoint.
            context.Result = ManagementProblem.Forbidden(
                "insufficient_scope", "This endpoint has no declared Management API scope.");
            return;
        }

        context.HttpContext.Items["ApiCaller"] = caller;
        await next();
    }
}
