using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Valenius.Backend.Services;

namespace Valenius.Backend.Filters;

public class ApiKeyFilter(ApiKeyStore apiKeyStore) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        // First-run bootstrap endpoints opt out explicitly and do their own auth (pairing token /
        // keyless pending-register). Everything else stays fail-closed. Opt-out is per-action, so a
        // new endpoint is protected by default unless someone deliberately marks it exempt.
        if (context.ActionDescriptor.EndpointMetadata.OfType<ApiKeyExemptAttribute>().Any())
            return;

        // Fail CLOSED: an unset store key (misconfiguration / a request served before
        // InitialiseOssAsync populated it) must reject, not wave every /api/** request
        // through unauthenticated. IsAccepted checks the current AND the still-valid
        // previous key (rotation grace window), both constant-time.
        var provided = context.HttpContext.Request.Headers.TryGetValue("X-Api-Key", out var v)
            ? v.ToString()
            : null;

        if (!apiKeyStore.IsAccepted(provided))
            context.Result = new UnauthorizedResult();
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
