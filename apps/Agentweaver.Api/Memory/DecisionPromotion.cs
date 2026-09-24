using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Api.Memory;

/// <summary>
/// Shared inbox to active <see cref="Decision"/> promotion logic. Used by both the
/// <c>POST /api/projects/{id}/decisions/inbox/{entryId}/merge</c> endpoint and the
/// coordinator-side finalization backstop so the mapping lives in exactly one place.
/// </summary>
public static class DecisionPromotion
{
    private const int MaxWriteAttempts = 5;

    /// <summary>
    /// Decision types the Coordinator (not the per-run Scribe) is responsible for reviewing and
    /// promoting. The Scribe auto-merges learning/pattern/update; these are left for the Coordinator.
    /// </summary>
    public static readonly string[] CoordinatorReviewTypes = ["architectural", "scope"];

    /// <summary>
    /// Atomically promotes one inbox entry, or returns its existing decision when the same
    /// promotion is replayed. PostgreSQL replicas serialize on the inbox id; transient
    /// serialization, deadlock, and lock-timeout failures are retried with a bounded delay.
    /// </summary>
    public static async Task<DecisionPromotionResult?> PromoteEntryAsync(
        MemoryDbContext db,
        string projectId,
        int entryId,
        DateTimeOffset now,
        string approvedBy,
        CancellationToken ct = default)
    {
        return await ExecuteWithEntryLockAsync(
            db,
            projectId,
            entryId,
            token => PromoteLockedAsync(db, projectId, entryId, now, approvedBy, token),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically rejects a pending inbox entry. Uses the same per-entry serialization as
    /// promotion so a concurrent rejection cannot overwrite a completed promotion.
    /// </summary>
    public static async Task<DecisionInboxEntry?> RejectEntryAsync(
        MemoryDbContext db,
        string projectId,
        int entryId,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        return await ExecuteWithEntryLockAsync(
            db,
            projectId,
            entryId,
            async token =>
            {
                var entry = await db.DecisionInbox
                    .SingleOrDefaultAsync(candidate =>
                        candidate.Id == entryId
                        && candidate.ProjectId == projectId
                        && candidate.Status == "pending", token)
                    .ConfigureAwait(false);
                if (entry is null)
                    return null;

                entry.Status = "rejected";
                entry.UpdatedAt = now;
                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return entry;
            },
            ct).ConfigureAwait(false);
    }

    private static async Task<DecisionPromotionResult?> PromoteLockedAsync(
        MemoryDbContext db,
        string projectId,
        int entryId,
        DateTimeOffset now,
        string approvedBy,
        CancellationToken ct)
    {
        var entry = await db.DecisionInbox
            .SingleOrDefaultAsync(candidate =>
                candidate.Id == entryId && candidate.ProjectId == projectId, ct)
            .ConfigureAwait(false);
        if (entry is null || entry.Status == "rejected")
            return null;

        if (entry.Status == "merged")
        {
            if (entry.DecisionId is null)
                throw new InvalidOperationException(
                    $"Merged decision inbox entry '{entryId}' has no decision link.");

            var existing = await db.Decisions
                .SingleOrDefaultAsync(decision => decision.Id == entry.DecisionId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"Decision inbox entry '{entryId}' links to missing decision '{entry.DecisionId}'.");
            return new(existing, entry, false);
        }

        if (entry.Status != "pending")
            return null;

        var candidate = new Decision
        {
            ProjectId = entry.ProjectId,
            AgentName = entry.AgentName,
            Type = entry.Type,
            Status = "active",
            Title = entry.Title,
            Content = entry.Content,
            Rationale = entry.Rationale,
            SourceKind = entry.SourceKind,
            SourceIdentity = entry.SourceIdentity,
            SourceRunId = entry.SourceRunId,
            TrustState = MemoryTrustStates.Approved,
            ApprovedBy = approvedBy,
            ApprovedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var (decision, _) = await MemoryWriteDeduplicator
            .GetOrCreateDecisionAsync(db, candidate, ct)
            .ConfigureAwait(false);
        entry.Status = "merged";
        entry.UpdatedAt = now;
        entry.MergedAt = now;
        entry.DecisionId = decision.Id;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new(decision, entry, true);
    }

    private static async Task<T> ExecuteWithEntryLockAsync<T>(
        MemoryDbContext db,
        string projectId,
        int entryId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await AcquireEntryLockAsync(db, projectId, entryId, ct).ConfigureAwait(false);
            return await action(ct).ConfigureAwait(false);
        }

        var attempt = 1;
        while (true)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                await AcquireEntryLockAsync(db, projectId, entryId, ct).ConfigureAwait(false);
                var result = await action(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (Exception ex) when (attempt < MaxWriteAttempts && IsRetryable(ex))
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                db.ChangeTracker.Clear();
                await Task.Delay(RetryDelay(attempt++), ct).ConfigureAwait(false);
            }
        }
    }

    private static Task<int> AcquireEntryLockAsync(
        MemoryDbContext db,
        string projectId,
        int entryId,
        CancellationToken ct) =>
        db.Database.IsNpgsql()
            ? AcquirePostgresEntryLockAsync(db, projectId, entryId, ct)
            : Task.FromResult(0);

    private static async Task<int> AcquirePostgresEntryLockAsync(
        MemoryDbContext db,
        string projectId,
        int entryId,
        CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2000ms';", ct)
            .ConfigureAwait(false);
        return await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0));",
            [$"decision-inbox:{projectId}:{entryId}"],
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Backstop used by the Coordinator finalization pass: promotes every still-pending
    /// architectural/scope inbox entry for <paramref name="projectId"/> authored by the verified
    /// <paramref name="coordinatorRunId"/> into an active decision.
    /// Each entry is promoted atomically and idempotently. Returns the number newly promoted.
    /// </summary>
    public static async Task<int> PromotePendingCoordinatorDecisionsAsync(
        MemoryDbContext db,
        string projectId,
        string coordinatorRunId,
        CancellationToken ct = default)
    {
        // EF Core/SQLite cannot translate array.Contains in WHERE — filter the type set in memory.
        var pending = (await db.DecisionInbox
            .AsNoTracking()
            .Where(e => e.ProjectId == projectId
                     && e.Status == "pending"
                     && e.SourceKind == MemorySourceKinds.Run
                     && e.SourceRunId == coordinatorRunId)
            .ToListAsync(ct).ConfigureAwait(false))
            .Where(e => CoordinatorReviewTypes.Contains(e.Type)
                     && string.Equals(e.AgentName, "coordinator", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var now = DateTimeOffset.UtcNow;
        var promoted = 0;
        foreach (var entry in pending)
        {
            var result = await PromoteEntryAsync(
                db,
                projectId,
                entry.Id,
                now,
                entry.SourceIdentity ?? $"run:{entry.SourceRunId}",
                ct).ConfigureAwait(false);
            if (result?.Promoted == true)
                promoted++;
        }

        return promoted;
    }

    internal static bool IsRetryable(Exception exception)
    {
        return ExceptionChain.Contains(
            exception,
            current => current is PostgresException
                {
                    SqlState: PostgresErrorCodes.SerializationFailure
                        or PostgresErrorCodes.DeadlockDetected
                        or PostgresErrorCodes.LockNotAvailable,
                }
                or SqliteException { SqliteErrorCode: 5 or 6 });
    }

    internal static TimeSpan RetryDelay(int attempt) =>
        TimeSpan.FromMilliseconds((20 * attempt) + Random.Shared.Next(5, 30));
}

public sealed record DecisionPromotionResult(
    Decision Decision,
    DecisionInboxEntry Entry,
    bool Promoted);

internal static class ExceptionChain
{
    public static bool Contains(Exception exception, Func<Exception, bool> predicate)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (predicate(current))
                return true;
        }

        return false;
    }
}
