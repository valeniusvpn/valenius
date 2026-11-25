using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

public sealed class AuditService(
    IServiceScopeFactory  scopeFactory,
    IHttpContextAccessor  http,
    ILogger<AuditService> logger,
    AuditWriteStatus status) : IAuditService
{
    public Task LogAsync(string category, string action, string target, string? details = null) =>
        WriteAsync(Entry(category, action, target, details), required: false);

    public Task LogRequiredAsync(string category, string action, string target, string? details = null) =>
        WriteAsync(Entry(category, action, target, details), required: true);

    public async Task SaveChangesWithAuditAsync(ApplicationDbContext db, string category, string action,
        string target, string? details = null)
    {
        try
        {
            db.AuditLogs.Add(Entry(category, action, target, details));
            await db.SaveChangesAsync();
            status.Succeeded();
        }
        catch (Exception ex) { Failed(ex, required: true); }
    }

    private AuditLog Entry(string category, string action, string target, string? details) => new()
    {
        Timestamp = DateTime.UtcNow, Category = category, Action = action,
        Actor = GetActor(), Target = target, Details = details, IpAddress = GetIp()
    };

    private async Task WriteAsync(AuditLog entry, bool required)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AuditLogs.Add(entry);
            await db.SaveChangesAsync();
            status.Succeeded();
        }
        catch (Exception ex)
        {
            Failed(ex, required);
        }
    }

    private void Failed(Exception ex, bool required)
    {
        status.Failed();
        // Exception messages/DB diagnostics can include credentials or SQL values.
        // Log a stable failure signal without event content or exception payloads.
        logger.LogError("Audit persistence failed ({FailureType}); required={Required}. Check database availability and permissions.",
            ex.GetType().Name, required);
        if (required) throw new AuditPersistenceException();
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
