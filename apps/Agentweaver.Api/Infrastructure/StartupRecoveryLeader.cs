using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Serializes recovery sweeps across API and worker replicas. Per-run leases and
/// coordinator plan claims, not this lock, protect run mutations.
/// A successful leader holds the session lock until host shutdown; an interrupted or
/// failed sweep releases it so a replica can retry.
///
/// <para>On SQLite and other non-Postgres providers (local dev / test) the lock is always granted
/// so single-process recovery still runs.</para>
///
/// <para>The advisory lock is session-scoped. The holder connection is opened with
/// <c>Pooling=false</c> so that disposing this object closes the real backend session and releases
/// the lock without leaving it stuck in the Npgsql connection pool.</para>
/// </summary>
public sealed class StartupRecoveryLeader : IAsyncDisposable
{
    // 00 was the old fleet-wide key; 01/02 were role-specific. Use a new shared
    // key so older pods cannot block this leader during a rolling upgrade.
    internal const long FleetAdvisoryLockKey = 0x4157_5243_5652_5905L;

    internal static long LockKeyForRole(IConfiguration _) =>
        FleetAdvisoryLockKey;

    private DbConnection? _conn;
    /// <summary>True when this process won the advisory-lock race and must run recovery.</summary>
    public bool IsLeader { get; }

    private StartupRecoveryLeader(bool isLeader, DbConnection? conn)
    {
        IsLeader = isLeader;
        _conn = conn;
    }

    /// <summary>
    /// Tries to become the recovery leader. Returns immediately — no blocking wait.
    /// Callers should check <see cref="IsLeader"/> before running the recovery sweep.
    /// </summary>
    public static Task<StartupRecoveryLeader> AcquireAsync(
        IConfiguration configuration,
        ILogger logger,
        CancellationToken ct = default) =>
        AcquireAsync(configuration, logger, LockKeyForRole(configuration), ct);

    internal static async Task<StartupRecoveryLeader> AcquireAsync(
        IConfiguration configuration,
        ILogger logger,
        long lockKey,
        CancellationToken ct = default)
    {
        var provider = configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        if (provider is not ("postgres" or "postgresql"))
        {
            // SQLite / other single-process providers — always proceed.
            return new StartupRecoveryLeader(isLeader: true, conn: null);
        }

        var connectionString =
            configuration.GetConnectionString("Postgres")
            ?? configuration.GetConnectionString("MemoryDb")
            ?? configuration["Database:ConnectionString"]
            ?? throw new InvalidOperationException(
                "Postgres connection string not found. " +
                "Set ConnectionStrings:Postgres, ConnectionStrings:MemoryDb, or Database:ConnectionString.");

        // Use a non-pooled connection so closing it truly ends the backend session,
        // which releases the session-level advisory lock without lingering in the pool.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
            cmd.Parameters.AddWithValue("key", lockKey);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            if (!acquired)
            {
                logger.LogInformation(
                    "Startup recovery: another replica holds the leader lock (key={Key:#,0}) — " +
                    "waiting to retry recovery leadership",
                    lockKey);
                await conn.CloseAsync().ConfigureAwait(false);
                conn.Dispose();
                return new StartupRecoveryLeader(isLeader: false, conn: null);
            }

            logger.LogInformation(
                "Startup recovery: acquired leader lock (key={Key:#,0}) — this pod will run the recovery sweep",
                lockKey);
            return new StartupRecoveryLeader(isLeader: true, conn: conn);
        }
        catch
        {
            await conn.CloseAsync().ConfigureAwait(false);
            conn.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes the leader connection, releasing the Postgres advisory lock.
    /// Safe to call on a non-leader instance (no-op).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_conn is not null)
        {
            await _conn.CloseAsync().ConfigureAwait(false);
            _conn.Dispose();
            _conn = null;
        }
    }
}
