using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure.Ef;

/// <summary>
/// EF Core-backed run store. Used when Database:Provider = postgres (or any non-SQLite provider).
/// Replaces SqliteRunStore — all methods are equivalent but dialect-neutral (no julianday, no pragmas).
/// </summary>
public sealed class EfRunStore : IRunStore
{
    private readonly IDbContextFactory<MemoryDbContext> _factory;
    private readonly ILogger<EfRunStore>? _logger;

    public EfRunStore(IDbContextFactory<MemoryDbContext> factory, ILogger<EfRunStore>? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    public async Task InsertAsync(Run run, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Runs.Add(ToRecord(run));
        await db.SaveChangesAsync(ct);
    }

    public async Task<Run?> GetAsync(RunId runId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId.ToString(), ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<IReadOnlyList<Run>> GetByStatusAsync(RunStatus status, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statusStr = status.ToApiString();
        var recs = await db.Runs.AsNoTracking().Where(r => r.Status == statusStr).ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task UpdateStatusAsync(RunId runId, RunStatus status, DateTimeOffset? endedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statusStr = status.ToApiString();
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, statusStr)
                .SetProperty(r => r.EndedAt, endedAt), ct);
        WarnIfNoRows(rows, runId, $"update status to {statusStr}");
    }

    public async Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statusStr = status.ToApiString();
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, statusStr)
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)endedAt)
                .SetProperty(r => r.Result, result), ct);
        WarnIfNoRows(rows, runId, $"update result to {statusStr}");
    }

    public async Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount,
        CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var id = runId.ToString();
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && !terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.TreeHash, treeHash)
                .SetProperty(r => r.Diff, diff)
                .SetProperty(r => r.Status, RunStatus.AwaitingReview.ToApiString())
                .SetProperty(r => r.ReviewReadyAt, ts), ct);
        WarnIfNoRows(rows, runId, "mark review ready");
    }

    public async Task<bool> TryTransitionReviewToInProgressAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id && r.Status == "awaiting_review", ct);
        if (rec is null) return false;
        rec.Status = "in_progress";
        rec.EndedAt = null;
        rec.ReviewReadyAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryTransitionReviewAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result,
        string? reviewer = null, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id && r.Status == "awaiting_review", ct);
        if (rec is null) return false;
        rec.Status = toStatus.ToApiString();
        rec.EndedAt = endedAt;
        rec.Result = result;
        rec.ReviewedBy = reviewer;
        rec.ReviewReadyAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryTransitionToCommittingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id && r.Status == "awaiting_review", ct);
        if (rec is null) return false;
        rec.Status = "committing";
        rec.ReviewReadyAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryRevertCommittingAsync(
        RunId runId, string? treeHash = null, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id && r.Status == "committing", ct);
        if (rec is null) return false;
        rec.Status = "awaiting_review";
        rec.ReviewReadyAt = ts;
        if (treeHash is not null) rec.TreeHash = treeHash;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TryStartMergingAsync(
        RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var id = runId.ToString();
        var mergingFromStates = new[] { "awaiting_review", "committing" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id && mergingFromStates.Contains(r.Status), ct);
        if (rec is null) return false;
        rec.Status = "merging";
        rec.ReviewedBy = reviewer ?? rec.ReviewedBy;
        rec.ReviewReadyAt = null;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RevertMergingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var ts = now ?? DateTimeOffset.UtcNow;
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.Status == "merging")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "awaiting_review")
                .SetProperty(r => r.ReviewReadyAt, ts), ct);
        return rows > 0;
    }

    public async Task<bool> CompleteMergingAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result,
        string? mergeConflicts = null, CancellationToken ct = default, string? mergedCommitHash = null)
    {
        var id = runId.ToString();
        var toStr = toStatus.ToApiString();
        await using var db = await _factory.CreateDbContextAsync();
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id && r.Status == "merging");
        if (rec is null) return false;
        rec.Status = toStr;
        rec.EndedAt = endedAt;
        rec.Result = result;
        rec.MergeConflicts = mergeConflicts;
        if (mergedCommitHash is not null) rec.MergedCommitHash = mergedCommitHash;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task UpdateTreeHashAfterCommitAsync(RunId runId, string newTreeHash, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.Status == "committing")
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TreeHash, newTreeHash), ct);
        WarnIfNoRows(rows, runId, "update tree hash after commit");
    }

    public async Task<bool> SetAssembleReadyAsync(
        RunId runId, string treeHash, string worktreeBranch, string diff, int stepCount,
        DateTimeOffset endedAt, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && !terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "assemble_ready")
                .SetProperty(r => r.TreeHash, treeHash)
                .SetProperty(r => r.WorktreeBranch, worktreeBranch)
                .SetProperty(r => r.Diff, diff)
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)endedAt), ct);
        return rows > 0;
    }

    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var toStr = toStatus.ToApiString();
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && !terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, toStr)
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)endedAt)
                .SetProperty(r => r.Result, result), ct);
        WarnIfNoRows(rows, runId, $"set terminal status to {toStr}");
        return rows > 0;
    }

    public async Task UpdateToInProgressAsync(
        RunId runId, string worktreePath, string worktreeBranch, DateTimeOffset startedAt, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.Status == "pending")
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, "in_progress")
                .SetProperty(r => r.WorktreePath, worktreePath)
                .SetProperty(r => r.WorktreeBranch, worktreeBranch)
                .SetProperty(r => r.StartedAt, startedAt), ct);
        WarnIfNoRows(rows, runId, "transition to in_progress");
    }

    public async Task DeleteAsync(RunId runId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Runs.Where(r => r.RunId == id).ExecuteDeleteAsync(ct);
    }

    public async Task UpdateWorktreeAsync(RunId runId, string worktreePath, string worktreeBranch, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Runs
            .Where(r => r.RunId == id && r.WorktreePath == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.WorktreePath, worktreePath)
                .SetProperty(r => r.WorktreeBranch, worktreeBranch), ct);
    }

    public async Task<bool> ArchiveAsync(RunId runId, DateTimeOffset archivedAt, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.ArchivedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ArchivedAt, archivedAt), ct);
        return rows > 0;
    }

    public async Task<Run?> FindActiveChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default)
    {
        var activeStatuses = new[] { "in_progress", "awaiting_review", "assembling", "in_review", "assemble_ready" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.AsNoTracking()
            .Where(r => r.ParentRunId == parentRunId && r.SubtaskId == subtaskId && activeStatuses.Contains(r.Status))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<IReadOnlyList<Run>> GetRunsByProjectAsync(
        ProjectId projectId, bool includeChildren = false, CancellationToken ct = default)
    {
        var pid = projectId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Runs.AsNoTracking()
            .Where(r => r.ProjectId == pid && r.ArchivedAt == null);
        if (!includeChildren) q = q.Where(r => r.ParentRunId == null);
        var recs = await q.OrderByDescending(r => r.StartedAt).ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<IReadOnlyList<Run>> GetRunsByParentAsync(string parentRunId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var recs = await db.Runs.AsNoTracking()
            .Where(r => r.ParentRunId == parentRunId)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    public async Task<IReadOnlyList<Run>> GetRunsByProjectAndStatusesAsync(
        ProjectId projectId, IEnumerable<RunStatus> statuses, CancellationToken ct = default)
    {
        var pid = projectId.ToString();
        var statusStrings = statuses.Select(s => s.ToApiString()).ToList();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var recs = await db.Runs.AsNoTracking()
            .Where(r => r.ProjectId == pid && statusStrings.Contains(r.Status))
            .ToListAsync(ct);
        return recs.Select(FromRecord).ToList();
    }

    /// <summary>
    /// Inserts a Pending run only when the referenced project is still Active.
    /// EF equivalent of the SQLite INSERT ... WHERE EXISTS pattern.
    /// </summary>
    public async Task<bool> TryCreateProjectRunAsync(Run run, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var pid = run.ProjectId!.Value.ToString();
        var projectActive = await db.Projects.AsNoTracking()
            .AnyAsync(p => p.ProjectId == pid && p.State == "active", ct);
        if (!projectActive)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        db.Runs.Add(ToRecord(run));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<Run?> GetByWorkflowRunIdAsync(string workflowRunId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.AsNoTracking()
            .Where(r => r.WorkflowRunId == workflowRunId || r.RunId == workflowRunId)
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return rec is null ? null : FromRecord(rec);
    }

    private void WarnIfNoRows(int rows, RunId runId, string operation)
    {
        if (rows == 0)
            _logger?.LogWarning("Run transition no-op while attempting to {Operation} for run {RunId}", operation, runId);
    }

    private static RunRecord ToRecord(Run r) => new()
    {
        RunId = r.Id.ToString(),
        RepositoryPath = r.RepositoryPath,
        OriginatingBranch = r.OriginatingBranch,
        ModelSource = r.ModelSource.ToApiString(),
        Task = r.Task,
        SubmittingUser = r.SubmittingUser,
        Status = r.Status.ToApiString(),
        StartedAt = r.StartedAt,
        EndedAt = r.EndedAt,
        Result = r.Result,
        WorktreePath = r.WorktreePath,
        WorktreeBranch = r.WorktreeBranch,
        TreeHash = r.TreeHash,
        Diff = r.Diff,
        MergeConflicts = r.MergeConflicts,
        ProjectId = r.ProjectId?.ToString(),
        ModelId = r.ModelId,
        AgentName = r.AgentName,
        AgentCharter = r.AgentCharter,
        ReviewedBy = r.ReviewedBy,
        WorkflowRunId = r.WorkflowRunId,
        MergedCommitHash = r.MergedCommitHash,
        ParentRunId = r.ParentRunId,
        SubtaskId = r.SubtaskId,
        Origin = r.Origin.ToApiString(),
        RetriedFrom = r.RetriedFrom,
        ArchivedAt = r.ArchivedAt,
        ReviewReadyAt = null,
    };

    private static Run FromRecord(RunRecord r) => new()
    {
        Id = RunId.Parse(r.RunId),
        RepositoryPath = r.RepositoryPath,
        OriginatingBranch = r.OriginatingBranch,
        ModelSource = ModelSourceExtensions.FromApiString(r.ModelSource),
        Task = r.Task,
        SubmittingUser = r.SubmittingUser,
        Status = RunStatusExtensions.ParseStatus(r.Status),
        StartedAt = r.StartedAt,
        EndedAt = r.EndedAt,
        Result = r.Result,
        WorktreePath = r.WorktreePath,
        WorktreeBranch = r.WorktreeBranch,
        TreeHash = r.TreeHash,
        StepCount = 0,
        Diff = r.Diff,
        MergeConflicts = r.MergeConflicts,
        ProjectId = r.ProjectId is null ? null : ProjectId.Parse(r.ProjectId),
        ModelId = r.ModelId,
        AgentName = r.AgentName,
        AgentCharter = r.AgentCharter,
        ReviewedBy = r.ReviewedBy,
        WorkflowRunId = r.WorkflowRunId,
        MergedCommitHash = r.MergedCommitHash,
        ParentRunId = r.ParentRunId,
        SubtaskId = r.SubtaskId,
        Origin = RunOriginExtensions.ParseOrigin(r.Origin),
        RetriedFrom = r.RetriedFrom,
        ArchivedAt = r.ArchivedAt,
    };
}
