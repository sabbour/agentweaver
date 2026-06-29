using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Acquires a Postgres session-level advisory lock at startup so that exactly one replica runs
/// <c>WorkflowRestartService.RecoverAsync</c> + <c>CoordinatorRunService.RecoverInterruptedRunsAsync</c>
/// + <c>CoordinatorReconciler.SweepAsync</c>. Other replicas that lose the race skip those sweeps and
/// start serving traffic immediately — the winner's recovery is sufficient.
///
/// <para>On SQLite and other non-Postgres providers (local dev / test) the lock is always granted
/// so the single-process startup path is unchanged.</para>
///
/// <para>The advisory lock is session-scoped. The holder connection is opened with
/// <c>Pooling=false</c> so that disposing this object closes the real backend session and releases
/// the lock without leaving it stuck in the Npgsql connection pool.</para>
/// </summary>
public sealed class StartupRecoveryLeader : IAsyncDisposable
{
    // Stable key for pg_try_advisory_lock(bigint) — chosen once and never changed.
    // Encodes "AWRCVRY\0" (AgentWeaver ReCOVeRY) as a big-endian int64.
    private const long AdvisoryLockKey = 0x4157_5243_5652_5900L;

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
    public static async Task<StartupRecoveryLeader> AcquireAsync(
        IConfiguration configuration,
        ILogger logger,
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
            cmd.Parameters.AddWithValue("key", AdvisoryLockKey);
            var acquired = (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;

            if (!acquired)
            {
                logger.LogInformation(
                    "Startup recovery: another replica holds the leader lock (key={Key:#,0}) — " +
                    "skipping recovery sweep on this pod",
                    AdvisoryLockKey);
                await conn.CloseAsync().ConfigureAwait(false);
                conn.Dispose();
                return new StartupRecoveryLeader(isLeader: false, conn: null);
            }

            logger.LogInformation(
                "Startup recovery: acquired leader lock (key={Key:#,0}) — this pod will run the recovery sweep",
                AdvisoryLockKey);
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
