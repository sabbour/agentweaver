using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Npgsql;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Runs;

public sealed class DurableRunControlState(IServiceScopeFactory scopeFactory, IRunEventStream eventStream)
{
    private const int MaxExclusiveAttempts = 4;
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;
    private readonly IRunEventStream _eventStream = eventStream;

    public void Append(string runId, string eventType, object payload)
    {
        _eventStream.AppendAsync(
            runId,
            new RunEvent(0, eventType, payload),
            CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    public IReadOnlyList<RunEventRecord> Load(string runId, params string[] eventTypes)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId && eventTypes.Contains(e.EventType))
            .OrderBy(e => e.Sequence)
            .ToList();
    }

    /// <summary>
    /// Runs a read/claim/write operation as one durable transaction while holding the same
    /// per-stream advisory locks that serialize event sequence allocation across API replicas.
    /// Callers provide every stream they will inspect or append so a policy write cannot race a
    /// concurrent resolution or another append to a shared policy bucket.
    /// </summary>
    public async Task<T> ExecuteExclusivelyAsync<T>(
        IEnumerable<string> streamIds,
        Func<MemoryDbContext, CancellationToken, Task<T>> action,
        CancellationToken ct = default)
    {
        var lockIds = streamIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        for (var attempt = 1; attempt <= MaxExclusiveAttempts; attempt++)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);
            try
            {
                foreach (var streamId in lockIds)
                    await AcquireRunWriteLockAsync(db, streamId, ct).ConfigureAwait(false);

                var result = await action(db, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxExclusiveAttempts && IsRetryableConcurrencyFailure(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(20 * attempt + Random.Shared.Next(5, 30)), ct)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Failed to complete exclusive run-control operation after {MaxExclusiveAttempts} attempts.");
    }

    private static async Task AcquireRunWriteLockAsync(
        MemoryDbContext db,
        string runId,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;

        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2000ms';", ct)
            .ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0));",
            new object[] { runId },
            ct).ConfigureAwait(false);
    }

    private static bool IsRetryableConcurrencyFailure(Exception exception) =>
        exception switch
        {
            PostgresException
            {
                SqlState: PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.LockNotAvailable,
            } => true,
            DbUpdateException { InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.SerializationFailure
                    or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.LockNotAvailable,
            } } => true,
            SqliteException { SqliteErrorCode: 5 or 6 or 19 } => true,
            DbUpdateException { InnerException: SqliteException { SqliteErrorCode: 5 or 6 or 19 } } => true,
            _ => false,
        };
}
