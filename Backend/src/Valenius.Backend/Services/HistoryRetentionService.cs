using Microsoft.EntityFrameworkCore;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

public sealed class HistoryRetentionPolicy
{
    public int AuditDays { get; set; } = 365;
    public int ConnectionDays { get; set; } = 90;
    public int ErrorDays { get; set; } = 90;
    public int DiagnosticDays { get; set; } = 30;
    public void Validate()
    {
        foreach (var days in new[] { AuditDays, ConnectionDays, ErrorDays, DiagnosticDays })
            if (days is < 0 or > 3650) throw new InvalidOperationException("HistoryRetention days must be 0 (disabled) or 1–3650.");
    }
}

public sealed class HistoryRetentionService(IServiceScopeFactory scopes, HistoryRetentionPolicy policy,
    ILogger<HistoryRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (policy.AuditDays + policy.ConnectionDays + policy.ErrorDays + policy.DiagnosticDays == 0) return;
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await PruneAsync(scope.ServiceProvider.GetRequiredService<ApplicationDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IAuditService>(), policy, DateTime.UtcNow, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError("History retention failed ({FailureType}); inspect database and audit alerts.", ex.GetType().Name); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public static async Task<int> PruneAsync(ApplicationDbContext db, IAuditService audit, HistoryRetentionPolicy policy,
        DateTime now, CancellationToken ct = default)
    {
        policy.Validate();
        var removed = 0;
        removed += await Prune<AuditLog>(policy.AuditDays, nameof(AuditLog.Timestamp));
        removed += await Prune<ConnectionLog>(policy.ConnectionDays, nameof(ConnectionLog.OccurredAt));
        removed += await Prune<ErrorLog>(policy.ErrorDays, nameof(ErrorLog.OccurredAt));
        removed += await Prune<ClientLogBundle>(policy.DiagnosticDays, nameof(ClientLogBundle.UploadedAt));
        return removed;

        async Task<int> Prune<T>(int days, string timestamp) where T : class
        {
            if (days == 0) return 0;
            var total = 0;
            var cutoff = now.AddDays(-days);
            for (var batch = 0; batch < 20; batch++)
            {
                ct.ThrowIfCancellationRequested();
                var old = db.Set<T>().Where(e => EF.Property<DateTime>(e, timestamp) < cutoff);
                var ids = await old.OrderBy(e => EF.Property<int>(e, "Id")).Select(e => EF.Property<int>(e, "Id")).Take(500).ToArrayAsync(ct);
                if (ids.Length == 0) break;
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var count = await old.Where(e => ids.Contains(EF.Property<int>(e, "Id"))).ExecuteDeleteAsync(ct);
                await audit.SaveChangesWithAuditAsync(db, Audit.System, "History pruned", typeof(T).Name, $"Entries removed: {count}; retention days: {days}");
                await transaction.CommitAsync(ct);
                total += count;
            }
            return total;
        }
    }
}
