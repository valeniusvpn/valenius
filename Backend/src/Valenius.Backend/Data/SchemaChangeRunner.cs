using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Valenius.Backend.Data;

/// <summary>Immutable, atomic SQL revisions. Legacy patches remain startup-serialized.</summary>
public static class SchemaChangeRunner
{
    public static async Task ApplyAsync(ApplicationDbContext db, string revision, string sql)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Schema revisions require their own PostgreSQL transaction.");
        // The same immutable revision must hash identically after Windows/Linux checkout.
        sql = sql.ReplaceLineEndings("\n");
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql)));
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({StartupSchemaLease.LockKey})");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SchemaRevisions" (
                "Revision" VARCHAR(100) PRIMARY KEY, "Checksum" VARCHAR(64) NOT NULL, "AppliedAt" TIMESTAMPTZ NOT NULL);
            """);
        var previous = await db.Database.SqlQuery<string>($"SELECT \"Checksum\" AS \"Value\" FROM \"SchemaRevisions\" WHERE \"Revision\" = {revision}").SingleOrDefaultAsync();
        if (previous is not null)
        {
            if (previous != checksum) throw new InvalidOperationException($"Applied schema revision {revision} has changed; add a new revision instead.");
        }
        else
        {
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"SchemaRevisions\" (\"Revision\", \"Checksum\", \"AppliedAt\") VALUES ({revision}, {checksum}, {DateTime.UtcNow})");
        }
        await transaction.CommitAsync();
    }
}
