using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

public sealed class AuditService(
    IServiceScopeFactory  scopeFactory,
    IHttpContextAccessor  http,
    ILogger<AuditService> logger) : IAuditService
{
    public Task LogAsync(string category, string action, string target, string? details = null)
    {
        // Capture request-context values synchronously (HttpContext may be gone after first await).
        var actor = GetActor();
        var ip    = GetIp();
        var ts    = DateTime.UtcNow;

        // Fire-and-forget async write so the calling handler is not delayed.
        return WriteAsync(ts, category, action, actor, target, details, ip);
    }

    private async Task WriteAsync(DateTime ts, string category, string action,
        string actor, string target, string? details, string? ip)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AuditLogs.Add(new AuditLog
            {
                Timestamp = ts,
                Category  = category,
                Action    = action,
                Actor     = actor,
                Target    = target,
                Details   = details,
                IpAddress = ip
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Audit log write failed.");
        }
    }

    private string GetActor()
    {
        var ctx = http.HttpContext;
        if (ctx is null) return "System";

        // Management API caller (docs/design/management-api.md §11) — stashed by
        // ApiTokenFilter. Checked first: a management-API request has no OIDC/local
        // AppUser identity on ctx.User at all, so falling through to the claim lookups
        // below would just return "Unknown" for every token-authenticated write.
        if (ctx.Items["ApiCaller"] is ApiCaller caller)
            return $"token:{caller.Name}#{caller.TokenId}";

        return ctx.User.FindFirstValue(ClaimTypes.Email)
            ?? ctx.User.FindFirstValue(ClaimTypes.Name)
            ?? ctx.User.FindFirstValue("sub")
            ?? "Unknown";
    }

    private string? GetIp()
    {
        var ctx = http.HttpContext;
        return ctx?.Connection.RemoteIpAddress?.ToString();
    }
}
