using System.Data;
using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Valenius.Backend.Data;
using Valenius.Backend.Models;

namespace Valenius.Backend.Services;

/// <summary>
/// Versioned database snapshots of mapped scalar properties. Navigation graphs are never
/// serialized or attached. New tables/columns are included automatically; incompatible
/// snapshots are rejected rather than silently applying security-policy defaults.
/// </summary>
public sealed class DatabaseBackupService(ApplicationDbContext db)
{
    public const int FormatVersion = 2;
    private static readonly MethodInfo ReadMethod = typeof(DatabaseBackupService)
        .GetMethod(nameof(ReadAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public async Task<DatabaseBackup> CaptureAsync(bool includeConnectionLogs)
    {
        var entities = OrderedEntities();
        await using var tx = await db.Database.BeginTransactionAsync(
            db.Database.IsNpgsql() ? IsolationLevel.RepeatableRead : IsolationLevel.Serializable);
        var backup = new DatabaseBackup
        {
            BackupVersion = FormatVersion,
            CreatedAt = DateTime.UtcNow,
            IncludesConnectionLogs = includeConnectionLogs,
            Schema = Schema(entities)
        };
        foreach (var entity in entities)
        {
            var rows = entity.ClrType == typeof(ConnectionLog) && !includeConnectionLogs
                ? []
                : await (Task<List<Dictionary<string, JsonElement>>>)ReadMethod
                    .MakeGenericMethod(entity.ClrType).Invoke(this, [entity])!;
            backup.Tables.Add(entity.GetTableName()!, rows);
            backup.IdentityHighWatermarks.Add(entity.GetTableName()!, await HighWatermarkAsync(entity));
        }
        await tx.CommitAsync();
        return backup;
    }

    public async Task RestoreAsync(DatabaseBackup backup)
    {
        var entities = OrderedEntities();
        var schema = Schema(entities);
        if (backup.BackupVersion != FormatVersion)
            throw new InvalidOperationException("This legacy or unsupported backup cannot be restored automatically. Use a version 2 encrypted backup created by a compatible backend.");
        if (backup.Schema is null || backup.Tables is null || backup.IdentityHighWatermarks is null ||
            !SameKeys(schema, backup.Schema) || !SameKeys(schema, backup.Tables) ||
            !SameKeys(schema, backup.IdentityHighWatermarks) ||
            schema.Any(t => backup.Schema[t.Key] is null || !SameKeys(t.Value, backup.Schema[t.Key]) ||
                t.Value.Any(p => backup.Schema[t.Key][p.Key] != p.Value)))
            throw new InvalidOperationException("Backup schema does not match this backend. No tables or security-policy fields may be missing. Restore using a compatible backend version.");

        // Materialize and validate every mapped value before the first DELETE.
        var restored = new Dictionary<IEntityType, List<object>>();
        foreach (var entity in entities)
        {
            var table = entity.GetTableName()!;
            if (backup.Tables[table] is null || backup.IdentityHighWatermarks[table] < 0)
                throw new InvalidOperationException($"Invalid backup table: {table}.");
            var rows = new List<object>();
            foreach (var values in backup.Tables[table])
            {
                if (values is null || !SameKeys(schema[table], values))
                    throw new InvalidOperationException($"Missing or unexpected fields in {table}.");
                var row = Activator.CreateInstance(entity.ClrType)!;
                foreach (var property in entity.GetProperties())
                {
                    var value = values[property.Name].Deserialize(property.ClrType);
                    var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                    if ((value is null && !property.IsNullable) ||
                        (value is string str && property.GetMaxLength() is int max && str.Length > max) ||
                        (value is not null && type.IsEnum && !Enum.IsDefined(type, value)))
                        throw new InvalidOperationException($"Invalid value in {table}.{property.Name}.");
                    if (value is DateTime date)
                        value = date.Kind == DateTimeKind.Unspecified
                            ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : date.ToUniversalTime();
                    property.PropertyInfo!.SetValue(row, value);
                }
                if (Convert.ToInt64(entity.FindPrimaryKey()!.Properties.Single().PropertyInfo!.GetValue(row)) <= 0)
                    throw new InvalidOperationException($"Invalid identity in {table}.");
                InvalidateTemporaryAuthorization(row);
                rows.Add(row);
            }
            restored.Add(entity, rows);
        }
        if (restored[entities.Single(e => e.ClrType == typeof(ServerSettings))] is not [ServerSettings { Id: 1 }])
            throw new InvalidOperationException("Backup must contain exactly one ServerSettings row with Id 1.");
        if (!backup.IncludesConnectionLogs && backup.Tables["ConnectionLogs"].Count != 0)
            throw new InvalidOperationException("Connection log contents disagree with the backup manifest.");

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        try
        {
            var sql = db.GetService<ISqlGenerationHelper>();
            if (db.Database.IsNpgsql())
            {
                // Exclude concurrent writes for the replacement, including background services.
                var tables = string.Join(", ", entities.Select(e => sql.DelimitIdentifier(e.GetTableName()!, e.GetSchema())));
#pragma warning disable EF1002 // Identifiers originate only from the EF model and are quoted.
                await db.Database.ExecuteSqlRawAsync($"LOCK TABLE {tables} IN ACCESS EXCLUSIVE MODE");
#pragma warning restore EF1002
            }
            var watermarks = new Dictionary<IEntityType, long>();
            foreach (var entity in entities)
                watermarks[entity] = Math.Max(backup.IdentityHighWatermarks[entity.GetTableName()!], await HighWatermarkAsync(entity));
            foreach (var entity in entities.AsEnumerable().Reverse())
            {
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"DELETE FROM {sql.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema())}");
#pragma warning restore EF1002
            }
            foreach (var entity in entities)
            {
                db.AddRange(restored[entity]);
                await db.SaveChangesAsync();
                // Avoid navigation fixup and retaining large blob graphs between tables.
                db.ChangeTracker.Clear();
            }
            foreach (var entity in entities)
                await RestartIdentityAsync(entity, watermarks[entity]);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    private static void InvalidateTemporaryAuthorization(object row)
    {
        switch (row)
        {
            case MfaSession session when session.Status == MfaSessionStatus.Active:
                session.Status = MfaSessionStatus.Revoked;
                session.RevokedAt = DateTime.UtcNow;
                session.RevokedBy = "Backup restore";
                break;
            case MfaChallenge challenge:
                challenge.Status = "Expired";
                break;
            case PairingToken token:
                token.UsedAt ??= DateTime.UtcNow;
                break;
            case Customer customer:
                customer.EnrollmentToken = null;
                customer.EnrollmentTokenHash = null;
                customer.EnrollmentTokenCreatedAt = null;
                customer.EnrollmentTokenExpiresAt = null;
                break;
            case AppUser user when !user.TotpEnabled:
                // A disabled factor can now be a pending server-generated enrollment.
                // Do not revive its old QR/TempData confirmation after restore.
                user.TotpSecret = null;
                break;
            case Client client:
                client.MfaEnrollmentOpen = false;
                client.PendingConnect = false;
                client.PendingDisconnect = false;
                client.ForceUpdate = false;
                client.PendingLogRequest = false;
                client.PendingConfigRepair = false;
                client.PendingServiceRestart = false;
                break;
        }
    }

    private async Task<List<Dictionary<string, JsonElement>>> ReadAsync<T>(IEntityType entity) where T : class
    {
        var rows = await db.Set<T>().AsNoTracking().ToListAsync();
        return rows.Select(row => entity.GetProperties().ToDictionary(p => p.Name,
            p => JsonSerializer.SerializeToElement(p.PropertyInfo!.GetValue(row), p.ClrType))).ToList();
    }

    private List<IEntityType> OrderedEntities()
    {
        var remaining = db.Model.GetEntityTypes().OrderBy(e => e.GetTableName(), StringComparer.Ordinal).ToList();
        var ordered = new List<IEntityType>();
        while (remaining.Count > 0)
        {
            var next = remaining.FirstOrDefault(e => e.GetForeignKeys().All(fk => ordered.Contains(fk.PrincipalEntityType)))
                ?? throw new InvalidOperationException("Backup does not support a cyclic database model.");
            // Fail explicitly if a future model needs a richer snapshot representation.
            if (next.FindPrimaryKey()?.Properties.Count != 1 ||
                next.GetProperties().Any(p => p.PropertyInfo is null) ||
                next.FindPrimaryKey()!.Properties[0].Name != "Id")
                throw new InvalidOperationException("Unsupported database model for backup.");
            ordered.Add(next);
            remaining.Remove(next);
        }
        return ordered;
    }

    private static Dictionary<string, Dictionary<string, string>> Schema(IEnumerable<IEntityType> entities)
        => entities.ToDictionary(e => e.GetTableName()!, e => e.GetProperties().ToDictionary(p => p.Name,
            p => $"{p.ClrType.FullName}|nullable={p.IsNullable}|max={p.GetMaxLength()}"));

    private static bool SameKeys<T, U>(Dictionary<string, T> a, Dictionary<string, U> b)
        => a.Count == b.Count && a.Keys.All(b.ContainsKey);

    private async Task<long> HighWatermarkAsync(IEntityType entity)
    {
        var sql = db.GetService<ISqlGenerationHelper>();
        var table = sql.DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
        long sequence;
        if (db.Database.IsNpgsql())
            sequence = await ScalarAsync("SELECT COALESCE(pg_sequence_last_value(pg_get_serial_sequence(@table, 'Id')::regclass), 0)", table);
        else if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
            sequence = await ScalarAsync("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = @table), 0)", entity.GetTableName()!);
        else
            throw new InvalidOperationException("Backup supports PostgreSQL and SQLite only.");
        // Explicitly imported IDs may exceed the sequence's last allocated value.
        var maximum = await ScalarAsync($"SELECT COALESCE(MAX(\"Id\"), 0) FROM {table}", "");
        return Math.Max(sequence, maximum);
    }

    private async Task<long> ScalarAsync(string query, string table)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = query;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private async Task RestartIdentityAsync(IEntityType entity, long watermark)
    {
        watermark = Math.Max(watermark, await HighWatermarkAsync(entity));
        var next = checked(watermark + 1);
        if (db.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var updated = await db.Database.ExecuteSqlAsync($"UPDATE sqlite_sequence SET seq = {next - 1} WHERE name = {entity.GetTableName()!}");
            if (updated == 0)
                await db.Database.ExecuteSqlAsync($"INSERT INTO sqlite_sequence (name, seq) VALUES ({entity.GetTableName()!}, {next - 1})");
            return;
        }
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT n.nspname, c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.oid = pg_get_serial_sequence(@table, 'Id')::regclass";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "table";
        parameter.Value = db.GetService<ISqlGenerationHelper>().DelimitIdentifier(entity.GetTableName()!, entity.GetSchema());
        command.Parameters.Add(parameter);
        string sequenceName, schemaName;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (!await reader.ReadAsync()) throw new InvalidOperationException("Missing identity sequence.");
            schemaName = reader.GetString(0);
            sequenceName = reader.GetString(1);
        }
        // ALTER SEQUENCE RESTART is transactional; setval is not and could damage IDs on rollback.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"ALTER SEQUENCE {db.GetService<ISqlGenerationHelper>().DelimitIdentifier(sequenceName, schemaName)} RESTART WITH {next.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
#pragma warning restore EF1002
    }
}

public sealed class DatabaseBackup
{
    public int BackupVersion { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IncludesConnectionLogs { get; init; }
    public Dictionary<string, Dictionary<string, string>> Schema { get; init; } = [];
    public Dictionary<string, List<Dictionary<string, JsonElement>>> Tables { get; init; } = [];
    public Dictionary<string, long> IdentityHighWatermarks { get; init; } = [];
}
