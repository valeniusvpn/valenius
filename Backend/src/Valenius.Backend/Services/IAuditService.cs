namespace Valenius.Backend.Services;

public interface IAuditService
{
    /// <summary>
    /// Records an audit event.  Actor and IP are read from the current HttpContext.
    /// Await the persistence attempt. Failures are reported to operators; use
    /// LogRequiredAsync to prevent a sensitive response/effect after audit failure.
    /// </summary>
    Task LogAsync(string category, string action, string target, string? details = null);

    /// <summary>Persist before returning; throws AuditPersistenceException on failure.</summary>
    Task LogRequiredAsync(string category, string action, string target, string? details = null);

    /// <summary>Save the caller's pending changes and audit row together, using its
    /// context/transaction. An outer transaction must be committed before revealing secrets.</summary>
    Task SaveChangesWithAuditAsync(Data.ApplicationDbContext db, string category, string action,
        string target, string? details = null);
}
