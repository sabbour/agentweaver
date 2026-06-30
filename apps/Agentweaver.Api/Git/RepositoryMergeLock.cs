using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentweaver.Api.Git;

/// <summary>
/// Per-repository lock that serializes concurrent approve/merge requests for
/// the same repository. In Postgres deployments the lock is backed by session
/// advisory locks so it spans API replicas; local SQLite/dev keeps the existing
/// process-wide semaphore behavior.
///
/// Keyed by canonical repository path.
/// </summary>
public sealed class RepositoryMergeLock
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _postgresConnectionString;
    private readonly ILogger<RepositoryMergeLock> _logger;

    public RepositoryMergeLock(IConfiguration configuration, ILogger<RepositoryMergeLock> logger)
    {
        var provider = configuration["Database:Provider"]?.ToLowerInvariant() ?? "sqlite";
        if (provider is "postgres" or "postgresql")
        {
            _postgresConnectionString =
                configuration.GetConnectionString("Postgres")
                ?? configuration.GetConnectionString("MemoryDb")
                ?? configuration["Database:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "Postgres connection string not found. " +
                    "Set ConnectionStrings:Postgres, ConnectionStrings:MemoryDb, or Database:ConnectionString.");
        }

        _logger = logger;
    }

    /// <summary>
    /// Tries to acquire the per-repository lock within <paramref name="timeout"/>.
    /// Returns a disposable handle that releases the semaphore on disposal, or
    /// null when the timeout expires (caller should return 409 retriable).
    /// </summary>
    public async Task<IDisposable?> TryAcquireAsync(
        string canonicalRepoPath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (_postgresConnectionString is not null)
            return await TryAcquirePostgresAsync(canonicalRepoPath, timeout, ct).ConfigureAwait(false);

        var semaphore = _locks.GetOrAdd(canonicalRepoPath, _ => new SemaphoreSlim(1, 1));

        if (!await semaphore.WaitAsync(timeout, ct).ConfigureAwait(false))
            return null;

        return new SemaphoreReleaser(semaphore);
    }

    private async Task<IDisposable?> TryAcquirePostgresAsync(
        string canonicalRepoPath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var key = AdvisoryLockKey(canonicalRepoPath);
        var deadline = DateTimeOffset.UtcNow + timeout;
        var builder = new NpgsqlConnectionStringBuilder(_postgresConnectionString) { Pooling = false };
        var conn = new NpgsqlConnection(builder.ConnectionString);

        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            while (true)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
                cmd.Parameters.AddWithValue("key", key);
                if ((bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
                    return new PostgresAdvisoryLockReleaser(conn, key, _logger);

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    await conn.CloseAsync().ConfigureAwait(false);
                    conn.Dispose();
                    return null;
                }

                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    continue;

                await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await conn.CloseAsync().ConfigureAwait(false);
            conn.Dispose();
            throw;
        }
    }

    private static long AdvisoryLockKey(string canonicalRepoPath)
    {
        var normalized = canonicalRepoPath.ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"agentweaver:repository-merge:{normalized}"));
        return BitConverter.ToInt64(bytes, 0);
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                semaphore.Release();
        }
    }

    private sealed class PostgresAdvisoryLockReleaser(
        NpgsqlConnection connection,
        long key,
        ILogger logger) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                cmd.Parameters.AddWithValue("key", key);
                cmd.ExecuteScalar();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release repository merge advisory lock {Key}", key);
            }
            finally
            {
                connection.Close();
                connection.Dispose();
            }
        }
    }
}
