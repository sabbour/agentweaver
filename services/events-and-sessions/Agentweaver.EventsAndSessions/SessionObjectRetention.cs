using Agentweaver.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.EventsAndSessions;

/// <summary>Enumerates and removes expired or stale unreferenced objects through the Events owner.</summary>
internal sealed class SessionObjectRetentionService(
    NpgsqlDataSource dataSource,
    PostgresSessionsProviderOptions options,
    IObjectStore objects)
{
    private const int BatchSize = 100;
    private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromHours(24);
    private readonly string _schema = $"\"{options.Schema}\"";

    /// <summary>Returns inventory entries that have no committed journal reference.</summary>
    internal async Task<IReadOnlyList<ObjectKey>> ReadOrphanInventoryAsync(CancellationToken cancellationToken)
    {
        var inventory = $"{_schema}.session_object_inventory";
        var references = $"{_schema}.session_object_references";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Enumeration is immediate; the grace period applies only to deletion, not inventory visibility.
        await using var command = new NpgsqlCommand($"""
            SELECT item.object_key
            FROM {inventory} item
            WHERE item.retain_until IS NULL
                AND item.deleted_at IS NULL
                AND NOT EXISTS (
                    SELECT 1 FROM {references} stored_reference
                    WHERE stored_reference.object_key = item.object_key
                )
            ORDER BY item.tracked_at, item.object_key
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<ObjectKey>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(new ObjectKey(reader.GetString(0)));
        return keys;
    }

    /// <summary>Deletes eligible objects in bounded batches and records each completed deletion.</summary>
    internal async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        var total = 0;
        while (true)
        {
            // Drain full batches now; the hourly worker should not require one tick per hundred rows.
            var removed = await CleanupBatchAsync(cancellationToken).ConfigureAwait(false);
            total += removed;
            if (removed < BatchSize)
                return total;
        }
    }

    private async Task<int> CleanupBatchAsync(CancellationToken cancellationToken)
    {
        var inventory = $"{_schema}.session_object_inventory";
        var references = $"{_schema}.session_object_references";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<ObjectKey>(BatchSize);
        // Row locks serialize object deletion against producer retries and let other replicas skip this batch.
        await using (var select = new NpgsqlCommand($"""
            SELECT item.object_key
            FROM {inventory} item
            WHERE item.deleted_at IS NULL
                AND (
                    item.retain_until <= clock_timestamp()
                    OR (
                        item.retain_until IS NULL
                        AND item.tracked_at <= clock_timestamp() - @grace
                        AND NOT EXISTS (
                            SELECT 1 FROM {references} stored_reference
                            WHERE stored_reference.object_key = item.object_key
                        )
                    )
                )
            ORDER BY CASE
                WHEN item.retain_until IS NULL THEN item.tracked_at + @grace
                ELSE item.retain_until
            END, item.object_key
            LIMIT @batch_size
            FOR UPDATE SKIP LOCKED
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("grace", NpgsqlDbType.Interval, OrphanGracePeriod);
            select.Parameters.AddWithValue("batch_size", NpgsqlDbType.Integer, BatchSize);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                keys.Add(new ObjectKey(reader.GetString(0)));
        }

        foreach (var key in keys)
        {
            // Keep the inventory row locked until deletion is recorded so a writer cannot race cleanup.
            await objects.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
            await using var markDeleted = new NpgsqlCommand($"""
                UPDATE {inventory}
                SET deleted_at = clock_timestamp()
                WHERE object_key = @key AND deleted_at IS NULL
                """, connection, transaction);
            markDeleted.Parameters.AddWithValue("key", NpgsqlDbType.Varchar, key.Value);
            if (await markDeleted.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("A locked session object inventory row disappeared.");
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return keys.Count;
    }
}

/// <summary>Runs the bounded owner cleanup periodically and logs failures for retry on the next pass.</summary>
internal sealed class SessionObjectRetentionWorker(
    SessionObjectRetentionService retention,
    ILogger<SessionObjectRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Run once at startup so expired rows do not wait for the first timer tick.
                var deleted = await retention.CleanupAsync(stoppingToken).ConfigureAwait(false);
                if (deleted > 0)
                    logger.LogInformation("Removed {Count} expired or orphaned session objects.", deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger.LogError(exception, "Session object cleanup failed; it will retry on the next pass.");
            }

            await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
        }
    }
}
