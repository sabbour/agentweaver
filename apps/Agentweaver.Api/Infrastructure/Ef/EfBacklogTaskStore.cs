using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// EF Core-backed <see cref="IBacklogTaskStore"/>. Used when Database:Provider = postgres.
/// Replaces SqliteBacklogTaskStore — semantics are identical, dialect-neutral.
/// </summary>
public sealed class EfBacklogTaskStore : IBacklogTaskStore
{
    private const int MaxOrderKeyRetries = 5;
    private readonly IDbContextFactory<MemoryDbContext> _factory;

    public EfBacklogTaskStore(IDbContextFactory<MemoryDbContext> factory) => _factory = factory;

    public async Task InsertAsync(BacklogTask task, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.BacklogTasks.Add(ToRecord(task));
        await db.SaveChangesAsync(ct);
    }

    public async Task<BacklogTask?> GetAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var tid = id.ToString();
        var rec = await db.BacklogTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.TaskId == tid && t.ProjectId == pid, ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<BacklogTask?> GetByRunIdAsync(RunId runId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rid = runId.ToString();
        var rec = await db.BacklogTasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.RunId == rid, ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<IReadOnlyList<BacklogTask>> ListByProjectAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == pid && t.ArchivedAt == null)
            .OrderBy(t => t.State).ThenBy(t => t.OrderKey).ThenBy(t => t.CommittedAt).ThenBy(t => t.TaskId)
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<IReadOnlyList<BacklogTask>> ListReadyForClaimAsync(
        ProjectId projectId, int limit, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == pid && t.State == "ready" && t.RunId == null && t.ArchivedAt == null)
            .OrderBy(t => t.OrderKey).ThenBy(t => t.CommittedAt).ThenBy(t => t.TaskId)
            .Take(limit)
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<bool> UpdateContentAsync(
        ProjectId projectId, BacklogTaskId id, string title, string? description, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var tid = id.ToString();
        var rows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid && t.ArchivedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Title, title)
                .SetProperty(t => t.Description, description), ct);
        return rows > 0;
    }

    public async Task<bool> UpdateWorkflowOverrideAsync(
        ProjectId projectId, BacklogTaskId id, string? workflowId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var tid = id.ToString();
        var rows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid
                && (t.State == "backlog" || t.State == "ready")
                && t.RunId == null && t.ArchivedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.WorkflowOverrideId, workflowId), ct);
        return rows > 0;
    }

    public async Task<bool> TryDeleteAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var tid = id.ToString();
        var rows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid
                && (t.State == "backlog" || t.State == "ready")
                && t.RunId == null && t.ArchivedAt == null)
            .ExecuteDeleteAsync(ct);
        return rows > 0;
    }

    public async Task<bool> TryArchiveAsync(
        ProjectId projectId, BacklogTaskId id, DateTimeOffset archivedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pid = projectId.ToString();
        var tid = id.ToString();

        var task = await db.BacklogTasks
            .FirstOrDefaultAsync(t => t.TaskId == tid && t.ProjectId == pid && t.ArchivedAt == null, ct);
        if (task is null)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        var linkedRunId = task.RunId;
        task.ArchivedAt = archivedAt;

        if (!string.IsNullOrEmpty(linkedRunId))
        {
            await db.Runs
                .Where(r => r.RunId == linkedRunId && r.ProjectId == pid && r.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ArchivedAt, archivedAt), ct);
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public Task<bool> TryMoveToReadyAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "ready", newOrderKey, async (db, key, c) =>
        {
            var pid = projectId.ToString();
            var tid = id.ToString();
            return await db.BacklogTasks
                .Where(t => t.TaskId == tid && t.ProjectId == pid && t.State == "backlog" && t.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "ready")
                    .SetProperty(t => t.OrderKey, key)
                    .SetProperty(t => t.CommittedAt, committedAt), c);
        }, ct);

    public Task<bool> TryMoveToBacklogAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "backlog", newOrderKey, async (db, key, c) =>
        {
            var pid = projectId.ToString();
            var tid = id.ToString();
            return await db.BacklogTasks
                .Where(t => t.TaskId == tid && t.ProjectId == pid
                    && t.State == "ready" && t.RunId == null && t.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "backlog")
                    .SetProperty(t => t.OrderKey, key)
                    .SetProperty(t => t.CommittedAt, (DateTimeOffset?)null), c);
        }, ct);

    public Task<bool> TryReorderAsync(
        ProjectId projectId, BacklogTaskId id, BacklogTaskState expectedState, string newOrderKey, CancellationToken ct = default)
    {
        var destState = expectedState.ToApiString();
        return RunWithOrderKeyRetryAsync(projectId, id, destState, newOrderKey, async (db, key, c) =>
        {
            var pid = projectId.ToString();
            var tid = id.ToString();
            return await db.BacklogTasks
                .Where(t => t.TaskId == tid && t.ProjectId == pid
                    && t.State == destState && t.RunId == null && t.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.OrderKey, key), c);
        }, ct);
    }

    public async Task<int> MoveAllBacklogToReadyAsync(
        ProjectId projectId, DateTimeOffset committedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pid = projectId.ToString();

        // (a) Read Backlog tasks in order
        var backlogTasks = await db.BacklogTasks
            .Where(t => t.ProjectId == pid && t.State == "backlog" && t.ArchivedAt == null)
            .OrderBy(t => t.OrderKey).ThenBy(t => t.TaskId)
            .Select(t => new { t.TaskId, t.OrderKey })
            .ToListAsync(ct);

        if (backlogTasks.Count == 0)
        {
            await tx.CommitAsync(ct);
            return 0;
        }

        // (b) Seed append cursor at max existing Ready order_key
        var lastKey = await db.BacklogTasks
            .Where(t => t.ProjectId == pid && t.State == "ready" && t.ArchivedAt == null)
            .OrderByDescending(t => t.OrderKey)
            .Select(t => (string?)t.OrderKey)
            .FirstOrDefaultAsync(ct);

        // (c) Promote each Backlog task in order
        var moved = 0;
        foreach (var item in backlogTasks)
        {
            var newKey = OrderKey.Between(lastKey, null);
            var rows = await db.BacklogTasks
                .Where(t => t.TaskId == item.TaskId && t.ProjectId == pid
                    && t.State == "backlog" && t.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "ready")
                    .SetProperty(t => t.OrderKey, newKey)
                    .SetProperty(t => t.CommittedAt, committedAt), ct);
            moved += rows;
            lastKey = newKey;
        }

        await tx.CommitAsync(ct);
        return moved;
    }

    public async Task<ClaimReserveResult> TryClaimAndReserveCoordinatorRunAsync(
        ProjectId projectId,
        BacklogTaskId id,
        Run coordinatorRun,
        DateTimeOffset claimedAt,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pid = projectId.ToString();
        var tid = id.ToString();

        // (a) exactly-once, project-scoped claim gate.
        var claimedRows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid
                && t.State == "ready" && t.RunId == null && t.ArchivedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.State, "claimed")
                .SetProperty(t => t.RunId, coordinatorRun.Id.ToString())
                .SetProperty(t => t.ClaimedAt, claimedAt), ct);

        if (claimedRows != 1)
        {
            await tx.RollbackAsync(ct);
            return ClaimReserveResult.Lost;
        }

        // (b) persist the coordinator run row gated on the project still being active.
        var projectActive = await db.Projects.AsNoTracking()
            .AnyAsync(p => p.ProjectId == pid && p.State == "active", ct);
        if (!projectActive)
        {
            await tx.RollbackAsync(ct);
            return ClaimReserveResult.ProjectUnavailable;
        }

        db.Runs.Add(new Memory.RunRecord
        {
            RunId = coordinatorRun.Id.ToString(),
            RepositoryPath = coordinatorRun.RepositoryPath,
            OriginatingBranch = coordinatorRun.OriginatingBranch,
            ModelSource = coordinatorRun.ModelSource.ToApiString(),
            Task = coordinatorRun.Task,
            SubmittingUser = coordinatorRun.SubmittingUser,
            Status = coordinatorRun.Status.ToApiString(),
            StartedAt = coordinatorRun.StartedAt,
            ProjectId = pid,
            ModelId = coordinatorRun.ModelId,
            AgentName = coordinatorRun.AgentName,
            AgentCharter = coordinatorRun.AgentCharter,
            WorkflowRunId = coordinatorRun.WorkflowRunId,
            ParentRunId = coordinatorRun.ParentRunId,
            SubtaskId = coordinatorRun.SubtaskId,
            Origin = "backlog_pickup",
        });

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ClaimReserveResult.Won;
    }

    /// <summary>
    /// Returns titles of existing (non-archived) tasks for the given project and source file path.
    /// Used by the decompose endpoint for idempotency checks.
    /// </summary>
    public async Task<HashSet<string>> GetExistingTitlesFromSourceAsync(
        ProjectId projectId, string sourceFilePath, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var titles = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == pid && t.SourceFilePath == sourceFilePath && t.ArchivedAt == null)
            .Select(t => t.Title)
            .ToListAsync(ct);
        return new HashSet<string>(titles, StringComparer.Ordinal);
    }

    private async Task<bool> RunWithOrderKeyRetryAsync(
        ProjectId projectId,
        BacklogTaskId id,
        string destState,
        string initialKey,
        Func<MemoryDbContext, string, CancellationToken, Task<int>> update,
        CancellationToken ct)
    {
        var key = initialKey;
        for (var attempt = 0; attempt < MaxOrderKeyRetries; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(ct);
            try
            {
                var rows = await update(db, key, ct);
                return rows > 0;
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                key = await RegenerateKeyAsync(projectId, destState, key, ct);
            }
        }
        throw new OrderKeyConflictException(
            $"Could not place backlog task {id} in the '{destState}' bucket after {MaxOrderKeyRetries} attempts.");
    }

    private async Task<string> RegenerateKeyAsync(
        ProjectId projectId, string destState, string collidingKey, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var next = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == pid && t.State == destState
                && t.OrderKey.CompareTo(collidingKey) > 0 && t.ArchivedAt == null)
            .OrderBy(t => t.OrderKey)
            .Select(t => (string?)t.OrderKey)
            .FirstOrDefaultAsync(ct);
        return OrderKey.Between(collidingKey, next);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // Postgres: 23505, SQLite: constraint, SQL Server: 2627/2601
        var inner = ex.InnerException?.Message ?? ex.Message;
        return inner.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || inner.Contains("23505")
            || inner.Contains("duplicate key");
    }

    private static BacklogTaskRecord ToRecord(BacklogTask t) => new()
    {
        TaskId = t.Id.ToString(),
        ProjectId = t.ProjectId.ToString(),
        Title = t.Title,
        Description = t.Description,
        State = t.State.ToApiString(),
        OrderKey = t.OrderKey,
        CapturedBy = t.CapturedBy,
        CreatedAt = t.CreatedAt,
        CommittedAt = t.CommittedAt,
        ClaimedAt = t.ClaimedAt,
        RunId = t.RunId?.ToString(),
        WorkflowOverrideId = t.WorkflowOverrideId,
        ArchivedAt = t.ArchivedAt,
        SourceFilePath = t.SourceFilePath,
    };

    private static BacklogTask FromRecord(BacklogTaskRecord r) => new()
    {
        Id = BacklogTaskId.Parse(r.TaskId),
        ProjectId = ProjectId.Parse(r.ProjectId),
        Title = r.Title,
        Description = r.Description,
        State = BacklogTaskStateExtensions.ParseState(r.State),
        OrderKey = r.OrderKey,
        CapturedBy = r.CapturedBy,
        CreatedAt = r.CreatedAt,
        CommittedAt = r.CommittedAt,
        ClaimedAt = r.ClaimedAt,
        RunId = r.RunId is null ? null : RunId.Parse(r.RunId),
        WorkflowOverrideId = r.WorkflowOverrideId,
        ArchivedAt = r.ArchivedAt,
        SourceFilePath = r.SourceFilePath,
    };
}
