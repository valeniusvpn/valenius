namespace Valenius.Backend.Services;

public interface IAuditService
{
    /// <summary>
    /// Records an audit event.  Actor and IP are read from the current HttpContext.
    /// Call without awaiting (fire-and-forget) to avoid adding latency to the response.
    /// </summary>
    Task LogAsync(string category, string action, string target, string? details = null);
}
