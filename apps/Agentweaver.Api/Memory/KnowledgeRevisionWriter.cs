using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Memory;

public enum KnowledgeWriteStatus
{
    Updated,
    NotFound,
    Stale,
    InvalidReplacement,
    ReplacementCycle,
}

public sealed record KnowledgeWriteResult<T>(
    KnowledgeWriteStatus Status,
    T? Record = default,
    int? CurrentRevision = null);

public static class KnowledgeRevisionWriter
{
    private const int MaxWriteAttempts = 3;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SqliteProjectLocks =
        new(StringComparer.Ordinal);

    public static async Task<KnowledgeWriteResult<AgentMemory>> UpdateMemoryAsync(
        MemoryDbContext db,
        int memoryId,
        int expectedRevision,
        string type,
        string importance,
        string content,
        string? tags,
        string status,
        int? replacedById,
        string actor,
        string reason,
        CancellationToken ct)
    {
        var projectId = await db.AgentMemory.AsNoTracking()
            .Where(memory => memory.Id == memoryId)
            .Select(memory => memory.ProjectId)
            .SingleOrDefaultAsync(ct);
        if (projectId is null)
            return new(KnowledgeWriteStatus.NotFound);

        return await ExecuteWithProjectLockAsync(
            db,
            projectId,
            token => UpdateMemoryLockedAsync(
                db, memoryId, expectedRevision, type, importance, content, tags, status,
                replacedById, actor, reason, token),
            ct);
    }

    private static async Task<KnowledgeWriteResult<AgentMemory>> UpdateMemoryLockedAsync(
        MemoryDbContext db,
        int memoryId,
        int expectedRevision,
        string type,
        string importance,
        string content,
        string? tags,
        string status,
        int? replacedById,
        string actor,
        string reason,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var memory = await db.AgentMemory.SingleOrDefaultAsync(m => m.Id == memoryId, ct);
        if (memory is null)
            return new(KnowledgeWriteStatus.NotFound);
        if (memory.Revision != expectedRevision)
            return new(KnowledgeWriteStatus.Stale, CurrentRevision: memory.Revision);

        var replacementStatus = await ValidateReplacementAsync(
            db, memory.ProjectId, memory.Id, status, replacedById, ct);
        if (replacementStatus is not null)
            return new(replacementStatus.Value, CurrentRevision: memory.Revision);

        memory.Type = type;
        memory.Importance = importance;
        memory.Content = content;
        memory.Tags = tags;
        memory.Status = status;
        memory.ReplacedById = status == KnowledgeLifecycleStates.Superseded ? replacedById : null;
        memory.TrustState = MemoryTrustStates.Pending;
        memory.ApprovedBy = null;
        memory.ApprovedAt = null;
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        memory.RevisionActor = actor;
        memory.RevisionReason = NormalizeReason(reason, "updated");
        MemoryWriteDeduplicator.RefreshMemoryIdentity(memory);

        try
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return new(KnowledgeWriteStatus.Updated,
                await db.AgentMemory.AsNoTracking().SingleAsync(m => m.Id == memoryId, ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.AgentMemory.AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == memoryId, ct);
            return current is null
                ? new(KnowledgeWriteStatus.NotFound)
                : new(KnowledgeWriteStatus.Stale, CurrentRevision: current.Revision);
        }
    }

    public static async Task<KnowledgeWriteResult<AgentMemory>> RestoreMemoryAsync(
        MemoryDbContext db,
        int memoryId,
        int expectedRevision,
        int revisionToRestore,
        string actor,
        string reason,
        CancellationToken ct)
    {
        var historical = await db.AgentMemoryRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.MemoryId == memoryId && r.Revision == revisionToRestore, ct);
        if (historical is null)
            return new(KnowledgeWriteStatus.NotFound);

        return await UpdateMemoryAsync(
            db,
            memoryId,
            expectedRevision,
            historical.Type,
            historical.Importance,
            historical.Content,
            historical.Tags,
            KnowledgeLifecycleStates.Active,
            null,
            actor,
            NormalizeReason(reason, $"restored revision {revisionToRestore}"),
            ct);
    }

    public static async Task<KnowledgeWriteResult<Decision>> UpdateDecisionAsync(
        MemoryDbContext db,
        int decisionId,
        int expectedRevision,
        string content,
        string? rationale,
        string status,
        int? supersededById,
        string actor,
        string reason,
        string? approvedBy,
        CancellationToken ct,
        bool forcePending = false)
    {
        var projectId = await db.Decisions.AsNoTracking()
            .Where(decision => decision.Id == decisionId)
            .Select(decision => decision.ProjectId)
            .SingleOrDefaultAsync(ct);
        if (projectId is null)
            return new(KnowledgeWriteStatus.NotFound);

        return await ExecuteWithProjectLockAsync(
            db,
            projectId,
            token => UpdateDecisionLockedAsync(
                db, decisionId, expectedRevision, content, rationale, status, supersededById,
                actor, reason, approvedBy, token, forcePending),
            ct);
    }

    private static async Task<KnowledgeWriteResult<Decision>> UpdateDecisionLockedAsync(
        MemoryDbContext db,
        int decisionId,
        int expectedRevision,
        string content,
        string? rationale,
        string status,
        int? supersededById,
        string actor,
        string reason,
        string? approvedBy,
        CancellationToken ct,
        bool forcePending)
    {
        db.ChangeTracker.Clear();
        var decision = await db.Decisions.SingleOrDefaultAsync(d => d.Id == decisionId, ct);
        if (decision is null)
            return new(KnowledgeWriteStatus.NotFound);
        if (decision.Revision != expectedRevision)
            return new(KnowledgeWriteStatus.Stale, CurrentRevision: decision.Revision);

        var replacementStatus = await ValidateDecisionReplacementAsync(
            db, decision.ProjectId, decision.Id, status, supersededById, ct);
        if (replacementStatus is not null)
            return new(replacementStatus.Value, CurrentRevision: decision.Revision);

        var contentChanged = decision.Content != content || decision.Rationale != rationale;
        decision.Content = content;
        decision.Rationale = rationale;
        decision.Status = status;
        decision.SupersededById = status == KnowledgeLifecycleStates.Superseded ? supersededById : null;
        if (contentChanged || forcePending)
        {
            decision.TrustState = MemoryTrustStates.Pending;
            decision.ApprovedBy = null;
            decision.ApprovedAt = null;
        }
        else if (approvedBy is not null)
        {
            decision.TrustState = MemoryTrustStates.Approved;
            decision.ApprovedBy = approvedBy;
            decision.ApprovedAt = DateTimeOffset.UtcNow;
        }
        decision.UpdatedAt = DateTimeOffset.UtcNow;
        decision.RevisionActor = actor;
        decision.RevisionReason = NormalizeReason(reason, "updated");
        MemoryWriteDeduplicator.RefreshDecisionIdentity(decision);

        try
        {
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            return new(KnowledgeWriteStatus.Updated,
                await db.Decisions.AsNoTracking().SingleAsync(d => d.Id == decisionId, ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.Decisions.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == decisionId, ct);
            return current is null
                ? new(KnowledgeWriteStatus.NotFound)
                : new(KnowledgeWriteStatus.Stale, CurrentRevision: current.Revision);
        }
    }

    public static async Task<KnowledgeWriteResult<Decision>> RestoreDecisionAsync(
        MemoryDbContext db,
        int decisionId,
        int expectedRevision,
        int revisionToRestore,
        string actor,
        string reason,
        CancellationToken ct)
    {
        var historical = await db.DecisionRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.DecisionId == decisionId && r.Revision == revisionToRestore, ct);
        if (historical is null)
            return new(KnowledgeWriteStatus.NotFound);

        return await UpdateDecisionAsync(
            db,
            decisionId,
            expectedRevision,
            historical.Content,
            historical.Rationale,
            KnowledgeLifecycleStates.Active,
            null,
            actor,
            NormalizeReason(reason, $"restored revision {revisionToRestore}"),
            approvedBy: null,
            ct: ct,
            forcePending: true);
    }

    private static async Task<KnowledgeWriteStatus?> ValidateReplacementAsync(
        MemoryDbContext db,
        string projectId,
        int memoryId,
        string status,
        int? replacedById,
        CancellationToken ct)
    {
        if (status != KnowledgeLifecycleStates.Superseded)
            return replacedById is null ? null : KnowledgeWriteStatus.InvalidReplacement;
        if (replacedById is null || replacedById == memoryId)
            return KnowledgeWriteStatus.InvalidReplacement;

        var replacement = await db.AgentMemory.AsNoTracking()
            .SingleOrDefaultAsync(m => m.Id == replacedById.Value, ct);
        if (replacement is null || replacement.ProjectId != projectId)
            return KnowledgeWriteStatus.InvalidReplacement;

        var visited = new HashSet<int> { memoryId };
        var current = replacement;
        while (true)
        {
            if (!visited.Add(current.Id))
                return KnowledgeWriteStatus.ReplacementCycle;
            if (current.ReplacedById is null)
                return null;
            current = await db.AgentMemory.AsNoTracking()
                .SingleOrDefaultAsync(m => m.Id == current.ReplacedById.Value, ct);
            if (current is null || current.ProjectId != projectId)
                return KnowledgeWriteStatus.InvalidReplacement;
        }
    }

    private static async Task<KnowledgeWriteStatus?> ValidateDecisionReplacementAsync(
        MemoryDbContext db,
        string projectId,
        int decisionId,
        string status,
        int? supersededById,
        CancellationToken ct)
    {
        if (status != KnowledgeLifecycleStates.Superseded)
            return supersededById is null ? null : KnowledgeWriteStatus.InvalidReplacement;
        if (supersededById is null || supersededById == decisionId)
            return KnowledgeWriteStatus.InvalidReplacement;

        var replacement = await db.Decisions.AsNoTracking()
            .SingleOrDefaultAsync(d => d.Id == supersededById.Value, ct);
        if (replacement is null || replacement.ProjectId != projectId)
            return KnowledgeWriteStatus.InvalidReplacement;

        var visited = new HashSet<int> { decisionId };
        var current = replacement;
        while (true)
        {
            if (!visited.Add(current.Id))
                return KnowledgeWriteStatus.ReplacementCycle;
            if (current.SupersededById is null)
                return null;
            current = await db.Decisions.AsNoTracking()
                .SingleOrDefaultAsync(d => d.Id == current.SupersededById.Value, ct);
            if (current is null || current.ProjectId != projectId)
                return KnowledgeWriteStatus.InvalidReplacement;
        }
    }

    private static string NormalizeReason(string? reason, string fallback) =>
        string.IsNullOrWhiteSpace(reason) ? fallback : reason.Trim();

    private static async Task<T> ExecuteWithProjectLockAsync<T>(
        MemoryDbContext db,
        string projectId,
        Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        SemaphoreSlim? sqliteLock = null;
        if (db.Database.IsSqlite())
        {
            sqliteLock = SqliteProjectLocks.GetOrAdd(projectId, static _ => new SemaphoreSlim(1, 1));
            await sqliteLock.WaitAsync(ct);
        }

        try
        {
            if (db.Database.CurrentTransaction is not null)
            {
                await AcquirePostgresProjectLockAsync(db, projectId, ct);
                return await action(ct);
            }

            var attempt = 1;
            while (true)
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                try
                {
                    await AcquirePostgresProjectLockAsync(db, projectId, ct);
                    var result = await action(ct);
                    await transaction.CommitAsync(ct);
                    return result;
                }
                catch (Exception ex) when (attempt < MaxWriteAttempts && DecisionPromotion.IsRetryable(ex))
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    db.ChangeTracker.Clear();
                    await Task.Delay(DecisionPromotion.RetryDelay(attempt++), ct);
                }
            }
        }
        finally
        {
            sqliteLock?.Release();
        }
    }

    private static async Task AcquirePostgresProjectLockAsync(
        MemoryDbContext db,
        string projectId,
        CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;

        await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2000ms';", ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0));",
            [$"knowledge:{projectId}"],
            ct);
    }
}
