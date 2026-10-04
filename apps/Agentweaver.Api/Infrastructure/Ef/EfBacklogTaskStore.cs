using Agentweaver.Api.Contracts;
using Agentweaver.Api.Backlog;
using System.Text.Json;
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

    public async Task<IReadOnlyList<BacklogTaskDependency>> ListDependenciesAsync(
        ProjectId projectId,
        IReadOnlyCollection<BacklogTaskId> taskIds,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return Array.Empty<BacklogTaskDependency>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.BacklogTaskDependencies.AsNoTracking()
            .Where(d => d.ProjectId == projectId.ToString() && taskIds.Select(id => id.ToString()).Contains(d.TaskId))
            .OrderBy(d => d.TaskId)
            .ThenBy(d => d.DependsOnTaskId)
            .Select(d => new BacklogTaskDependency
            {
                ProjectId = ProjectId.Parse(d.ProjectId),
                TaskId = BacklogTaskId.Parse(d.TaskId),
                DependsOnTaskId = BacklogTaskId.Parse(d.DependsOnTaskId),
                CreatedAt = d.CreatedAt,
            })
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BacklogDependencyStatus>> ListDependencyStatusesAsync(
        ProjectId projectId,
        IReadOnlyCollection<BacklogTaskId> taskIds,
        CancellationToken ct = default)
    {
        if (taskIds.Count == 0)
            return Array.Empty<BacklogDependencyStatus>();

        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await (
            from dependency in db.BacklogTaskDependencies.AsNoTracking()
            join prerequisite in db.BacklogTasks.AsNoTracking()
                on dependency.DependsOnTaskId equals prerequisite.TaskId
            join run in db.Runs.AsNoTracking()
                on prerequisite.RunId equals run.RunId into runJoin
            from run in runJoin.DefaultIfEmpty()
            where dependency.ProjectId == projectId.ToString() && taskIds.Select(id => id.ToString()).Contains(dependency.TaskId)
            orderby dependency.TaskId, dependency.DependsOnTaskId
            select new
            {
                dependency.TaskId,
                dependency.DependsOnTaskId,
                prerequisite.Title,
                prerequisite.RunId,
                RunStatus = run == null ? null : run.Status,
                RunResult = run == null ? null : run.Result,
                Commit = run == null ? null : run.MergedCommitHash,
                Tree = run == null ? null : run.TreeHash,
                RevisionId = run == null ? null : run.CurrentOutputRevisionId,
                ApprovedRevisionId = run == null ? null : run.ApprovedOutputRevisionId,
                Generation = run == null ? 0 : run.LifecycleGeneration,
                prerequisite.ArchivedAt,
            }).ToListAsync(ct);
        var revisionIds = rows.Where(row => row.RevisionId is not null)
            .Select(row => row.RevisionId!).ToArray();
        var revisions = await db.RunOutputRevisions.AsNoTracking()
            .Where(r => revisionIds.Contains(r.RevisionId))
            .ToDictionaryAsync(r => r.RevisionId, ct);

        return rows.Select(row =>
        {
            var revision = row.RevisionId is not null && revisions.TryGetValue(row.RevisionId, out var found)
                ? found : null;
            var available = revision is { ManifestIncomplete: false, DiffBytes: not null,
                TreeContent: not null, TreeContentSha256: not null }
                && (revision.OutputKind == "collective" && (revision.SchemaVersion == RunOutputRevision.CollectiveSchemaVersion
                   && revision.MergedCommitHash == row.Commit && revision.MergeEffectId is not null
                   || revision.SchemaVersion == RunOutputRevision.CollectiveCandidateSchemaVersion
                   && revision.MergedCommitHash is null && row.ApprovedRevisionId == revision.RevisionId)
                    || revision.OutputKind == "no_change"
                    && revision.SchemaVersion == RunOutputRevision.NoChangeSchemaVersion
                    && revision.AcceptedNoChange && row.RunResult == "confirmed"
                    && revision.MergedCommitHash == row.Commit)
                && revision.RunId == row.RunId && revision.LifecycleGeneration == row.Generation
                && revision.TreeHash == row.Tree
                && (revision.OutputKind != "collective" || revision.WorkPlanId is not null)
                && RunOutputRevision.Sha256(revision.DiffBytes!) == revision.DiffSha256
                && RunOutputRevision.Sha256(revision.TreeContent!) == revision.TreeContentSha256;
            var acceptedNoChange = available
                && revision!.SchemaVersion == RunOutputRevision.NoChangeSchemaVersion
                && revision.OutputKind == "no_change"
                && revision.AcceptedNoChange;
            return new BacklogDependencyStatus(
                BacklogTaskId.Parse(row.TaskId),
                BacklogTaskId.Parse(row.DependsOnTaskId),
                row.Title,
                row.RunId is null ? null : RunId.Parse(row.RunId),
                row.RunStatus is null ? null : RunStatusExtensions.ParseStatus(row.RunStatus),
                row.ArchivedAt is null && BacklogPrerequisiteOutcome.IsSatisfied(
                    row.RunStatus, row.RunResult, row.Commit, row.Tree, available,
                    acceptedNoChange),
                BacklogPrerequisiteOutcome.Reason(
                    row.ArchivedAt is not null, row.RunStatus, row.RunResult, row.Commit, row.Tree,
                    available, acceptedNoChange));
        })
            .ToList();
    }

    public async Task<long> GetDependencyRevisionAsync(ProjectId projectId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Projects.AsNoTracking()
            .Where(p => p.ProjectId == projectId.ToString())
            .Select(p => (long?)p.BacklogGraphRevision)
            .SingleOrDefaultAsync(ct)
            ?? throw new BacklogDependencyEditException("project_not_found");
    }

    public async Task<BacklogDependencyEditResult> EditDependenciesAsync(
        ProjectId projectId, long expectedRevision, BacklogDependencyEdit edit,
        bool preview = false, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pid = projectId.ToString();
        var project = await db.Projects
            .FromSqlInterpolated($"SELECT * FROM projects WHERE project_id = {pid} FOR UPDATE")
            .SingleOrDefaultAsync(ct)
            ?? throw new BacklogDependencyEditException("project_not_found");
        if (project.BacklogGraphRevision != expectedRevision)
            throw new BacklogDependencyEditException("stale_graph_revision");
        var tasks = await db.BacklogTasks.AsNoTracking().Where(t => t.ProjectId == pid)
            .ToDictionaryAsync(t => BacklogTaskId.Parse(t.TaskId),
                t => t.State is "backlog" or "ready" && t.RunId == null
                    && t.ArchivedAt == null && !t.IsAutomationInvocationPending, ct);
        var edges = (await db.BacklogTaskDependencies.AsNoTracking()
            .Where(d => d.ProjectId == pid).ToListAsync(ct))
            .Select(d => new BacklogTaskDependency
            {
                ProjectId = projectId,
                TaskId = BacklogTaskId.Parse(d.TaskId),
                DependsOnTaskId = BacklogTaskId.Parse(d.DependsOnTaskId),
                CreatedAt = d.CreatedAt,
            }).ToArray();
        var (result, before, after) = BacklogDependencyGraph.Project(
            project.BacklogGraphRevision, edit, tasks, edges);
        if (preview || !result.Changed)
            return result;
        var tid = edit.TaskId.ToString();
        var removed = before.Except(after).Select(id => id.ToString()).ToArray();
        if (removed.Length > 0)
            await db.BacklogTaskDependencies.Where(d => d.ProjectId == pid && d.TaskId == tid
                    && removed.Contains(d.DependsOnTaskId))
                .ExecuteDeleteAsync(ct);
        foreach (var added in after.Except(before))
            db.BacklogTaskDependencies.Add(new Memory.BacklogTaskDependencyRecord
            {
                ProjectId = pid,
                TaskId = tid,
                DependsOnTaskId = added.ToString(),
                CreatedAt = DateTimeOffset.UtcNow,
            });
        project.BacklogGraphRevision++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<BacklogTask>> ListReadyForClaimAsync(
        ProjectId projectId, int limit, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var recs = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.ProjectId == pid && t.State == "ready" && t.RunId == null && t.ArchivedAt == null)
            .Where(t => !db.BacklogTaskDependencies.Any(d => d.ProjectId == pid && d.TaskId == t.TaskId
                && db.BacklogTasks.Any(p => p.TaskId == d.DependsOnTaskId
                    && (p.ArchivedAt != null || p.RunId == null
                        || !db.Runs.Any(r => r.RunId == p.RunId
                            && ((r.Status == "merged" || (r.Status == "completed"
                                    && (r.Result == "assembly_complete" || r.Result == "complete" || r.Result == "confirmed")))
                                    && r.MergedCommitHash != null && r.MergedCommitHash.Trim() != ""
                                    && r.TreeHash != null && r.TreeHash.Trim() != ""
                                    && (r.Status != "completed" || db.RunOutputRevisions.Any(v =>
                                        v.RevisionId == r.CurrentOutputRevisionId
                                        && v.RunId == r.RunId && v.LifecycleGeneration == r.LifecycleGeneration
                                        && (r.Result != "confirmed" || (v.SchemaVersion == RunOutputRevision.NoChangeSchemaVersion
                                            && v.OutputKind == "no_change"))
                                        && ((v.SchemaVersion == RunOutputRevision.CollectiveSchemaVersion
                                                && v.MergedCommitHash == r.MergedCommitHash)
                                            || (v.SchemaVersion == RunOutputRevision.CollectiveCandidateSchemaVersion
                                                && v.MergedCommitHash == null
                                                && r.ApprovedOutputRevisionId == v.RevisionId)
                                            || (v.SchemaVersion == RunOutputRevision.NoChangeSchemaVersion
                                                && r.Result == "confirmed" && v.AcceptedNoChange
                                                && v.MergedCommitHash == r.MergedCommitHash))
                                        && (v.OutputKind == "collective" || v.OutputKind == "no_change")
                                        && !v.ManifestIncomplete
                                        && v.DiffBytes != null && v.TreeContent != null
                                        && v.TreeContentSha256 != null
                                        && v.TreeHash == r.TreeHash))))))))
            .OrderBy(t => t.OrderKey).ThenBy(t => t.CommittedAt).ThenBy(t => t.TaskId)
            .Take(limit)
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<int> CountReadyForPickupAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var tasks = await db.BacklogTasks.AsNoTracking()
            .Where(t => t.State == "ready" && t.RunId == null && t.ArchivedAt == null)
            .Join(
                db.Projects.AsNoTracking().Where(p => p.State == "active"),
                task => task.ProjectId,
                project => project.ProjectId,
                (task, _) => task)
            .ToListAsync(ct);

        var count = 0;
        foreach (var group in tasks.GroupBy(t => t.ProjectId))
        {
            var projectId = ProjectId.Parse(group.Key);
            var taskIds = group.Select(t => BacklogTaskId.Parse(t.TaskId)).ToList();
            var statuses = await ListDependencyStatusesAsync(projectId, taskIds, ct);
            var blocked = statuses.Where(s => !s.IsSatisfied).Select(s => s.TaskId).ToHashSet();
            count += taskIds.Count(id => !blocked.Contains(id));
        }

        return count;
    }

    public async Task<bool> UpdateContentAsync(
        ProjectId projectId, BacklogTaskId id, string title, string? description, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var pid = projectId.ToString();
        var tid = id.ToString();
        var rows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid && t.ArchivedAt == null
                && !t.IsAutomationInvocationPending)
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
                && t.RunId == null && t.ArchivedAt == null && !t.IsAutomationInvocationPending)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.WorkflowOverrideId, workflowId), ct);
        return rows > 0;
    }

    public async Task<bool> TryDeleteAsync(ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pid = projectId.ToString();
        var project = await db.Projects
            .FromSqlInterpolated($"SELECT * FROM projects WHERE project_id = {pid} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (project is null)
            return false;
        if (await db.BacklogTaskDependencies.AsNoTracking()
            .AnyAsync(d => d.ProjectId == pid && d.DependsOnTaskId == id.ToString(), ct))
            throw new BacklogTaskDependencyException("task_is_dependency");
        var tid = id.ToString();
        var hadOutgoingLinks = await db.BacklogTaskDependencies.AsNoTracking()
            .AnyAsync(d => d.ProjectId == pid && d.TaskId == tid, ct);
        var rows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid
                && (t.State == "backlog" || t.State == "ready")
                && t.RunId == null && t.ArchivedAt == null && !t.IsAutomationInvocationPending)
            .ExecuteDeleteAsync(ct);
        if (rows > 0 && hadOutgoingLinks)
        {
            project.BacklogGraphRevision++;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return rows > 0;
    }

    public async Task<bool> TryDeleteProvisionalAutomationTaskAsync(
        ProjectId projectId, BacklogTaskId id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.BacklogTasks
            .Where(t => t.TaskId == id.ToString() && t.ProjectId == projectId.ToString()
                && t.State == "backlog" && t.RunId == null && t.ArchivedAt == null
                && t.IsAutomationInvocationPending)
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

        await db.Projects.FromSqlInterpolated(
            $"SELECT * FROM projects WHERE project_id = {pid} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        var task = await db.BacklogTasks
            .FirstOrDefaultAsync(t => t.TaskId == tid && t.ProjectId == pid && t.ArchivedAt == null
                && !t.IsAutomationInvocationPending, ct);
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
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt,
        CancellationToken ct = default, string? providerKey = null, string? readyByUserId = null) =>
        RunWithOrderKeyRetryAsync(projectId, id, "ready", newOrderKey, async (db, key, c) =>
        {
            var pid = projectId.ToString();
            var tid = id.ToString();
            return await db.BacklogTasks
                .Where(t => t.TaskId == tid && t.ProjectId == pid && t.State == "backlog"
                    && t.ArchivedAt == null && !t.IsAutomationInvocationPending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "ready")
                    .SetProperty(t => t.OrderKey, key)
                    .SetProperty(t => t.CommittedAt, committedAt)
                    .SetProperty(t => t.AiExecutionProviderKey, t => providerKey ?? t.AiExecutionProviderKey)
                    .SetProperty(t => t.ReadyByUserId, readyByUserId), c);
        }, ct);

    public Task<bool> TryPublishAutomationInvocationTaskAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, DateTimeOffset committedAt, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "ready", newOrderKey, async (db, key, c) =>
        {
            var pid = projectId.ToString();
            var tid = id.ToString();
            return await db.BacklogTasks
                .Where(t => t.TaskId == tid && t.ProjectId == pid && t.State == "backlog"
                    && t.ArchivedAt == null && t.IsAutomationInvocationPending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "ready")
                    .SetProperty(t => t.OrderKey, key)
                    .SetProperty(t => t.CommittedAt, committedAt)
                    .SetProperty(t => t.IsAutomationInvocationPending, false), c);
        }, ct);

    public Task<bool> TryMoveToBacklogAsync(
        ProjectId projectId, BacklogTaskId id, string newOrderKey, CancellationToken ct = default) =>
        RunWithOrderKeyRetryAsync(projectId, id, "backlog", newOrderKey, async (db, key, c) =>
        {
            var pid = projectId.ToString();
            var tid = id.ToString();
            return await db.BacklogTasks
                .Where(t => t.TaskId == tid && t.ProjectId == pid
                    && t.State == "ready" && t.RunId == null && t.ArchivedAt == null
                    && !t.IsAutomationInvocationPending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "backlog")
                    .SetProperty(t => t.OrderKey, key)
                    .SetProperty(t => t.CommittedAt, (DateTimeOffset?)null)
                    .SetProperty(t => t.AiExecutionProviderKey, (string?)null)
                    .SetProperty(t => t.ReadyByUserId, (string?)null), c);
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
                        && t.State == destState && t.RunId == null && t.ArchivedAt == null
                        && !t.IsAutomationInvocationPending)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.OrderKey, key), c);
        }, ct);
    }

    public async Task<int> MoveAllBacklogToReadyAsync(
        ProjectId projectId, DateTimeOffset committedAt, CancellationToken ct = default,
        string? providerKey = null, string? readyByUserId = null)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pid = projectId.ToString();

        // (a) Read Backlog tasks in order
        var backlogTasks = await db.BacklogTasks
            .Where(t => t.ProjectId == pid && t.State == "backlog" && t.ArchivedAt == null
                && !t.IsAutomationInvocationPending)
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
                    && t.State == "backlog" && t.ArchivedAt == null
                    && !t.IsAutomationInvocationPending)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.State, "ready")
                    .SetProperty(t => t.OrderKey, newKey)
                    .SetProperty(t => t.CommittedAt, committedAt)
                    .SetProperty(t => t.AiExecutionProviderKey, providerKey)
                    .SetProperty(t => t.ReadyByUserId, readyByUserId), ct);
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
        CancellationToken ct = default) =>
        (await TryClaimAndReserveCoordinatorRunWithPolicyAsync(
            projectId, id, coordinatorRun, claimedAt, ct).ConfigureAwait(false)).Result;

    public async Task<ClaimReserveOutcome> TryClaimAndReserveCoordinatorRunWithPolicyAsync(
        ProjectId projectId,
        BacklogTaskId id,
        Run coordinatorRun,
        DateTimeOffset claimedAt,
        CancellationToken ct = default,
        string? expectedProviderKey = null,
        string? expectedReadyByUserId = null)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var pid = projectId.ToString();
        var tid = id.ToString();
        var projectLock = await db.Projects
            .FromSqlInterpolated($"SELECT * FROM projects WHERE project_id = {pid} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (projectLock is null || projectLock.State != "active")
            return new ClaimReserveOutcome(ClaimReserveResult.ProjectUnavailable);

        // Hold the prerequisite identities and their run outcomes through the claim write.
        // The project lock already fences dependency-graph edits; these row locks fence
        // concurrent archive/run-link and terminal-outcome changes before snapshotting.
        await db.Database.SqlQuery<string>(
            $"""
             SELECT p.task_id AS "Value"
             FROM backlog_task_dependencies d
             JOIN backlog_tasks p ON p.task_id = d.depends_on_task_id AND p.project_id = d.project_id
             WHERE d.project_id = {pid} AND d.task_id = {tid}
             ORDER BY p.task_id
             FOR SHARE OF p
             """).ToListAsync(ct);
        await db.Database.SqlQuery<string>(
            $"""
             SELECT r.run_id AS "Value"
             FROM backlog_task_dependencies d
             JOIN backlog_tasks p ON p.task_id = d.depends_on_task_id AND p.project_id = d.project_id
             JOIN runs r ON r.run_id = p.run_id AND r.project_id = d.project_id
             WHERE d.project_id = {pid} AND d.task_id = {tid}
             ORDER BY r.run_id
             FOR SHARE OF r
             """).ToListAsync(ct);

        var inputs = await (
            from dependency in db.BacklogTaskDependencies.AsNoTracking()
            join prerequisite in db.BacklogTasks.AsNoTracking().Where(p => p.ProjectId == pid)
                on dependency.DependsOnTaskId equals prerequisite.TaskId
            join run in db.Runs.AsNoTracking().Where(r => r.ProjectId == pid)
                on prerequisite.RunId equals run.RunId into runs
            from run in runs.DefaultIfEmpty()
            where dependency.ProjectId == pid && dependency.TaskId == tid
            orderby dependency.DependsOnTaskId
            select new
            {
                dependency.DependsOnTaskId,
                prerequisite.ArchivedAt,
                prerequisite.RunId,
                Status = run == null ? null : run.Status,
                Result = run == null ? null : run.Result,
                Commit = run == null ? null : run.MergedCommitHash,
                Tree = run == null ? null : run.TreeHash,
                Generation = run == null ? 0 : run.LifecycleGeneration,
                WorkflowDigest = run == null ? null : run.ExecutableWorkflowContentDigest,
                RevisionId = run == null ? null : run.CurrentOutputRevisionId,
                ApprovedRevisionId = run == null ? null : run.ApprovedOutputRevisionId,
            }).ToListAsync(ct);
        var revisionIds = inputs.Where(input => input.RevisionId is not null)
            .Select(input => input.RevisionId!).ToArray();
        var revisions = await db.RunOutputRevisions.AsNoTracking()
            .Where(r => revisionIds.Contains(r.RevisionId)).ToDictionaryAsync(r => r.RevisionId, ct);
        if (inputs.Any(input => input.ArchivedAt is not null
            || !BacklogPrerequisiteOutcome.IsSatisfied(
                input.Status, input.Result, input.Commit, input.Tree,
                input.RevisionId is not null
                    && revisions.TryGetValue(input.RevisionId, out var revision)
                    && (input.Result != "confirmed" || (revision.SchemaVersion == RunOutputRevision.NoChangeSchemaVersion
                        && revision.OutputKind == "no_change"))
                    && ((revision.SchemaVersion == RunOutputRevision.CollectiveSchemaVersion
                            && revision.MergedCommitHash == input.Commit)
                        || (revision.SchemaVersion == RunOutputRevision.CollectiveCandidateSchemaVersion
                            && revision.MergedCommitHash is null
                            && input.ApprovedRevisionId == revision.RevisionId)
                        || (revision.SchemaVersion == RunOutputRevision.NoChangeSchemaVersion
                            && input.Result == "confirmed"
                            && revision.AcceptedNoChange
                            && revision.MergedCommitHash == input.Commit))
                    && (revision.OutputKind == "collective" || revision.OutputKind == "no_change")
                    && !revision.ManifestIncomplete
                    && revision.RunId == input.RunId && revision.LifecycleGeneration == input.Generation
                    && revision.TreeHash == input.Tree
                    && revision.DiffBytes is not null && revision.TreeContent is not null
                    && revision.TreeContentSha256 is not null,
                input.RevisionId is not null
                    && revisions.TryGetValue(input.RevisionId, out var acceptedRevision)
                    && acceptedRevision.AcceptedNoChange)))
        {
            await tx.RollbackAsync(ct);
            return new ClaimReserveOutcome(ClaimReserveResult.Lost);
        }
        foreach (var input in inputs.Where(input => input.Status == "completed"
            && input.Result is "assembly_complete" or "complete" or "confirmed"))
        {
            var revision = revisions[input.RevisionId!];
            _ = new RunOutputRevision(revision.RevisionId, revision.SchemaVersion,
                RunId.Parse(revision.RunId), revision.LifecycleGeneration, revision.WorkflowDigest,
                revision.ManifestIncomplete, revision.TreeHash, revision.DiffSha256,
                revision.PredecessorRevisionId, revision.DiffBytes, revision.CreatedAt,
                revision.OutputKind, revision.MergedCommitHash, revision.WorkPlanId,
                revision.MergeEffectId, revision.AcceptedNoChange,
                revision.TreeContent, revision.TreeContentSha256).ResolveFiles();
        }
        var claimedInputs = JsonSerializer.Serialize(inputs.Select(input => new BacklogClaimedPrerequisite(
            input.DependsOnTaskId, input.RunId!,
            BacklogPrerequisiteOutcome.Reason(false, input.Status, input.Result, input.Commit, input.Tree,
                input.Status == "completed" && input.Result is "assembly_complete" or "complete" or "confirmed",
                input.RevisionId is not null && revisions.TryGetValue(input.RevisionId, out var revision)
                    && revision.AcceptedNoChange),
            input.Generation, input.Commit, input.Tree, input.WorkflowDigest, input.RevisionId)).ToArray());
        var graphRevision = projectLock.BacklogGraphRevision;

        // (a) exactly-once, project-scoped claim gate.
        var claimedRows = await db.BacklogTasks
            .Where(t => t.TaskId == tid && t.ProjectId == pid
                && t.State == "ready" && t.RunId == null && t.ArchivedAt == null
                && t.AiExecutionProviderKey == expectedProviderKey
                && t.ReadyByUserId == expectedReadyByUserId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.State, "claimed")
                .SetProperty(t => t.RunId, coordinatorRun.Id.ToString())
                .SetProperty(t => t.ClaimedAt, claimedAt)
                .SetProperty(t => t.ClaimedGraphRevision, graphRevision)
                .SetProperty(t => t.ClaimedPrerequisitesJson, claimedInputs), ct);

        if (claimedRows != 1)
        {
            await tx.RollbackAsync(ct);
            return new ClaimReserveOutcome(ClaimReserveResult.Lost);
        }

        // (b) snapshot the persisted pickup settings from the project row inside this transaction.
        var projectSettings = await db.Projects.AsNoTracking()
            .Where(p => p.ProjectId == pid && p.State == "active")
            .Select(p => new
            {
                p.PickupAutoApproveTools,
                p.PickupAutopilot,
                p.UpdatedAt,
            })
            .SingleOrDefaultAsync(ct);
        if (projectSettings is null)
        {
            await tx.RollbackAsync(ct);
            return new ClaimReserveOutcome(ClaimReserveResult.ProjectUnavailable);
        }

        var approvalSnapshot = new RunApprovalPolicySnapshot(
            RunApprovalPolicy.ForBacklogPickup(
                projectSettings.PickupAutoApproveTools,
                projectSettings.PickupAutopilot),
            Source: "backlog_pickup",
            CapturedAt: claimedAt,
            SettingsUpdatedAt: projectSettings.UpdatedAt);

        // (c) persist the coordinator run and immutable policy snapshot atomically with the claim.
        var persistedRun = coordinatorRun with
        {
            ExecutionInputRequired = inputs.Count > 0,
            Origin = RunOrigin.BacklogPickup,
            LaunchAutoApproveTools = approvalSnapshot.Policy.AutoApproveTools,
            LaunchAutopilot = approvalSnapshot.Policy.Autopilot,
            ApprovalPolicySnapshotId = approvalSnapshot.SnapshotId,
            ApprovalPolicySource = approvalSnapshot.Source,
            ApprovalPolicyCapturedAt = approvalSnapshot.CapturedAt,
            ApprovalPolicySettingsUpdatedAt = approvalSnapshot.SettingsUpdatedAt,
        };
        db.Runs.Add(new Memory.RunRecord
        {
            RunId = coordinatorRun.Id.ToString(),
            RepositoryPath = coordinatorRun.RepositoryPath,
            OriginatingBranch = coordinatorRun.OriginatingBranch,
            ExecutionInputRequired = inputs.Count > 0,
            ExecutionInputSourceCommitHash = coordinatorRun.ExecutionInputSourceCommitHash,
            ExecutionInputCommitHash = coordinatorRun.ExecutionInputCommitHash,
            ExecutionInputCompositeId = coordinatorRun.ExecutionInputCompositeId,
            ModelSource = coordinatorRun.ModelSource.ToApiString(),
            Task = coordinatorRun.Task,
            SubmittingUser = coordinatorRun.SubmittingUser,
            Status = coordinatorRun.Status.ToApiString(),
            StartedAt = coordinatorRun.StartedAt,
            EndedAt = coordinatorRun.EndedAt,
            Result = coordinatorRun.Result,
            ProjectId = pid,
            ModelId = coordinatorRun.ModelId,
            AgentName = coordinatorRun.AgentName,
            AgentCharter = coordinatorRun.AgentCharter,
            WorkflowRunId = coordinatorRun.WorkflowRunId,
            ParentRunId = coordinatorRun.ParentRunId,
            SubtaskId = coordinatorRun.SubtaskId,
            Origin = "backlog_pickup",
            LaunchAutoApproveTools = approvalSnapshot.Policy.AutoApproveTools,
            LaunchAutopilot = approvalSnapshot.Policy.Autopilot,
            ApprovalPolicySnapshotId = approvalSnapshot.SnapshotId,
            ApprovalPolicySource = approvalSnapshot.Source,
            ApprovalPolicyCapturedAt = approvalSnapshot.CapturedAt,
            ApprovalPolicySettingsUpdatedAt = approvalSnapshot.SettingsUpdatedAt,
            ExecutableWorkflowPinRequired = coordinatorRun.ExecutableWorkflowPinRequired,
            ExecutableWorkflowManifestSchemaVersion = coordinatorRun.ExecutableWorkflowManifestSchemaVersion,
            ExecutableWorkflowDefinitionId = coordinatorRun.ExecutableWorkflowDefinitionId,
            ExecutableWorkflowDefinitionVersion = coordinatorRun.ExecutableWorkflowDefinitionVersion,
            ExecutableWorkflowSource = coordinatorRun.ExecutableWorkflowSource,
            ExecutableWorkflowContentDigest = coordinatorRun.ExecutableWorkflowContentDigest,
            ExecutableWorkflowDefinitionYaml = coordinatorRun.ExecutableWorkflowDefinitionYaml,
            ExecutableWorkflowPinnedAt = coordinatorRun.ExecutableWorkflowPinnedAt,
        });
        db.ExecutionIdentities.Add(await EfRunStore.CreateExecutionIdentityAsync(db, persistedRun, ct));

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ClaimReserveOutcome(ClaimReserveResult.Won, approvalSnapshot);
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
        CapturedByUserId = t.CapturedByUserId,
        ReadyByUserId = t.ReadyByUserId,
        CreatedAt = t.CreatedAt,
        CommittedAt = t.CommittedAt,
        ClaimedAt = t.ClaimedAt,
        RunId = t.RunId?.ToString(),
        ClaimedGraphRevision = t.ClaimedGraphRevision,
        ClaimedPrerequisitesJson = t.ClaimedPrerequisitesJson,
        WorkflowOverrideId = t.WorkflowOverrideId,
        WorkflowDefinitionSnapshotYaml = t.WorkflowDefinitionSnapshotYaml,
        ArchivedAt = t.ArchivedAt,
        SourceFilePath = t.SourceFilePath,
        ParentPrdRunId = t.ParentPrdRunId?.ToString(),
        PromotionKey = t.PromotionKey,
        PromotionReason = t.PromotionReason,
        IsAutomationInvocationPending = t.IsAutomationInvocationPending,
        AiExecutionProviderKey = t.AiExecutionProviderKey,
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
        CapturedByUserId = r.CapturedByUserId,
        ReadyByUserId = r.ReadyByUserId,
        CreatedAt = r.CreatedAt,
        CommittedAt = r.CommittedAt,
        ClaimedAt = r.ClaimedAt,
        RunId = r.RunId is null ? null : RunId.Parse(r.RunId),
        ClaimedGraphRevision = r.ClaimedGraphRevision,
        ClaimedPrerequisitesJson = r.ClaimedPrerequisitesJson,
        WorkflowOverrideId = r.WorkflowOverrideId,
        WorkflowDefinitionSnapshotYaml = r.WorkflowDefinitionSnapshotYaml,
        ArchivedAt = r.ArchivedAt,
        SourceFilePath = r.SourceFilePath,
        ParentPrdRunId = r.ParentPrdRunId is null ? null : RunId.Parse(r.ParentPrdRunId),
        PromotionKey = r.PromotionKey,
        PromotionReason = r.PromotionReason,
        IsAutomationInvocationPending = r.IsAutomationInvocationPending,
        AiExecutionProviderKey = r.AiExecutionProviderKey,
    };
}
