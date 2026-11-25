using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Services;

namespace Valenius.Backend.Filters;

/// <summary>Authorize the model-bound target, including JSON/query/multipart calls.
/// Shared-key authority is confined to the explicit legacy migration policy.</summary>
public sealed class DeviceCredentialFilter(ApplicationDbContext db, DeviceRequestBudget budget, DeviceAuthentication authentication) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor.EndpointMetadata.OfType<ApiKeyExemptAttribute>().Any())
        {
            await next(); // Register and pairing implement their own bootstrap proof.
            return;
        }
        var key = context.ActionArguments.TryGetValue("clientKey", out var argument)
            ? argument as string
            : context.ActionArguments.Values.OfType<IDeviceRequest>().SingleOrDefault()?.ClientKey;
        var device = await db.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.ClientKey == key,
            context.HttpContext.RequestAborted);
        var provided = context.HttpContext.Request.Headers["X-Api-Key"].ToString();
        var statusOnly = context.ActionDescriptor.EndpointMetadata.OfType<DeviceStatusAttribute>().Any();
        if (string.IsNullOrEmpty(key) || device is null || device.IsDeleted
            || (!device.IsActive && !statusOnly)
            || !await authentication.AuthenticateAsync(device, provided, context.HttpContext.RequestAborted))
        {
            context.Result = new UnauthorizedResult();
            return;
        }
        using var lease = context.ActionDescriptor.EndpointMetadata.OfType<DevicePollAttribute>().Any()
            ? budget.TryPoll(key) : budget.TryControl(key);
        if (lease is null)
        {
            context.HttpContext.Response.Headers.RetryAfter = "2";
            context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
            return;
        }
        await next();
    }
}

/// <summary>Inactive devices may receive a minimal denial status, never profiles or MFA secrets.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DeviceStatusAttribute : Attribute;

[AttributeUsage(AttributeTargets.Method)]
public sealed class DevicePollAttribute : Attribute;
