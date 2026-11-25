using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Valenius.Backend.Data;

/// <summary>Serializes cooperative backend initialization on one physical PostgreSQL
/// session. No pool return, multiplexing or reconnect/retry may outlive its lock.</summary>
public sealed class StartupSchemaLease : IAsyncDisposable
{
    // Stable across backend releases/editions; PostgreSQL scopes this key to the database.
    public const long LockKey = 0x56414C454E495553;
    private readonly ApplicationDbContext db;
    private readonly NpgsqlConnection connection;
    private volatile bool acquired;
    private bool disposed;
    private volatile bool lost;

    private StartupSchemaLease(ApplicationDbContext db, NpgsqlConnection connection)
    {
        this.db = db;
        this.connection = connection;
        connection.StateChange += OnStateChange;
    }

    public static async Task<StartupSchemaLease> AcquireAsync(ApplicationDbContext db,
        TimeSpan waitTimeout, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
            throw new InvalidOperationException("Backend initialization requires PostgreSQL.");
        if (waitTimeout <= TimeSpan.Zero || waitTimeout > TimeSpan.FromMinutes(15))
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        if (db.Database.GetDbConnection().State != ConnectionState.Closed || db.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Database initialization requires an unopened context without an existing transaction.");
        if (db.Database.CreateExecutionStrategy().RetriesOnFailure)
            throw new InvalidOperationException("Database initialization cannot use automatic database retries: a reconnect would lose its startup lock.");

        // This scope is startup-only. A non-pooled dedicated connection guarantees
        // physical session closure even if unlocking fails. All schema/bootstrap
        // writes must use this same context, including Pro's CA bootstrap.
        var settings = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
            { Pooling = false, Multiplexing = false };
        var connection = new NpgsqlConnection(settings.ConnectionString);
        db.Database.SetDbConnection(connection, contextOwnsConnection: true);
        var lease = new StartupSchemaLease(db, connection);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(waitTimeout);
        try
        {
            await db.Database.OpenConnectionAsync(deadline.Token);
            while (true)
            {
                await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(@key)", connection);
                command.Parameters.AddWithValue("key", LockKey);
                if (await command.ExecuteScalarAsync(deadline.Token) is true)
                {
                    lease.acquired = true;
                    lease.EnsureHeld();
                    return lease;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200), deadline.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await lease.CloseAsync();
            throw new TimeoutException($"Database initialization is busy: startup lock was not acquired within {waitTimeout.TotalSeconds:g} seconds. This instance made no schema/bootstrap changes.", ex);
        }
        catch
        {
            await lease.CloseAsync();
            throw;
        }
    }

    private void OnStateChange(object? sender, StateChangeEventArgs e)
    {
        if (acquired && e.CurrentState != ConnectionState.Open) lost = true;
    }

    public void EnsureHeld()
    {
        if (!acquired || lost || connection.State != ConnectionState.Open)
            throw new InvalidOperationException("Database initialization lost its PostgreSQL startup session; refusing to continue or serve requests.");
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            EnsureHeld();
            await using var command = new NpgsqlCommand("SELECT pg_advisory_unlock(@key)", connection);
            command.Parameters.AddWithValue("key", LockKey);
            if (await command.ExecuteScalarAsync() is not true)
                throw new InvalidOperationException("Database initialization no longer owns its startup lock; refusing successful startup.");
        }
        finally { await CloseAsync(); }
    }

    private async Task CloseAsync()
    {
        acquired = false;
        connection.StateChange -= OnStateChange;
        try { await db.Database.CloseConnectionAsync(); }
        finally { await connection.DisposeAsync(); }
    }
}
