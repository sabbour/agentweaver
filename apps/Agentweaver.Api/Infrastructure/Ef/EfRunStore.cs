using Agentweaver.Api.Contracts;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Execution;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

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
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Runs.Add(ToRecord(run));
        db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, run, ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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
        RejectTerminalStatus(status);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statusStr = status.ToApiString();
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, statusStr)
                .SetProperty(r => r.EndedAt, endedAt)
                .SetProperty(r => r.ApprovalGeneration,
                    r => r.Status == RunStatus.InProgress.ToApiString() && statusStr != RunStatus.InProgress.ToApiString()
                        ? r.ApprovalGeneration + 1 : r.ApprovalGeneration)
                .SetProperty(r => r.LifecycleGeneration,
                    r => statusStr == RunStatus.InProgress.ToApiString()
                         && new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready" }.Contains(r.Status)
                        ? r.LifecycleGeneration + 1 : r.LifecycleGeneration), ct);
        WarnIfNoRows(rows, runId, $"update status to {statusStr}");
    }

    public async Task UpdateResultAsync(RunId runId, RunStatus status, string result, DateTimeOffset endedAt, CancellationToken ct = default)
    {
        RejectTerminalStatus(status);
        await using var db = await _factory.CreateDbContextAsync(ct);
        var statusStr = status.ToApiString();
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, statusStr)
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)endedAt)
                .SetProperty(r => r.Result, result)
                .SetProperty(r => r.ApprovalGeneration,
                    r => r.Status == RunStatus.InProgress.ToApiString() && statusStr != RunStatus.InProgress.ToApiString()
                        ? r.ApprovalGeneration + 1 : r.ApprovalGeneration)
                .SetProperty(r => r.LifecycleGeneration,
                    r => statusStr == RunStatus.InProgress.ToApiString()
                         && new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready" }.Contains(r.Status)
                        ? r.LifecycleGeneration + 1 : r.LifecycleGeneration), ct);
        WarnIfNoRows(rows, runId, $"update result to {statusStr}");
    }

    public async Task UpdateAssemblyArtifactsAsync(
        RunId runId, string treeHash, string diff, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == runId.ToString())
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.TreeHash, treeHash)
                .SetProperty(r => r.Diff, diff), ct);
        WarnIfNoRows(rows, runId, "persist assembly artifacts");
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
                .SetProperty(r => r.ReviewReadyAt, ts)
                .SetProperty(r => r.ApprovalGeneration,
                    r => r.Status == RunStatus.InProgress.ToApiString() ? r.ApprovalGeneration + 1 : r.ApprovalGeneration), ct);
        WarnIfNoRows(rows, runId, "mark review ready");
    }

    public async Task<bool> TryTransitionReviewToInProgressAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.Status == "awaiting_review")
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(r => r.Status, "in_progress")
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ReviewReadyAt, (DateTimeOffset?)null)
                .SetProperty(r => r.LifecycleGeneration, r => r.LifecycleGeneration + 1), ct);
        if (rows != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        var rec = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == id, ct);
        db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, FromRecord(rec), ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryParkForChildWorkAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id
                && r.Status == RunStatus.InProgress.ToApiString()
                && r.LifecycleGeneration == lifecycleGeneration)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(r => r.Status, RunStatus.AwaitingReview.ToApiString())
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ReviewReadyAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ApprovalGeneration, r => r.ApprovalGeneration + 1), ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<bool> TryResumeFromChildWorkAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id
                && r.Status == RunStatus.AwaitingReview.ToApiString()
                && r.LifecycleGeneration == lifecycleGeneration)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(r => r.Status, RunStatus.InProgress.ToApiString())
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ReviewReadyAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

    public async Task<bool> TryReopenTerminalToInProgressAsync(RunId runId, CancellationToken ct = default)
    {
        var terminalStatuses = new[]
        {
            RunStatus.Failed.ToApiString(),
            RunStatus.MergeFailed.ToApiString(),
            RunStatus.AssembleReady.ToApiString(),
        };
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id && terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(r => r.Status, RunStatus.InProgress.ToApiString())
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.LifecycleGeneration, r => r.LifecycleGeneration + 1), ct);
        if (rows != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        var record = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == id, ct);
        db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, FromRecord(record), ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryTransitionReviewAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result,
        string? reviewer = null, CancellationToken ct = default)
    {
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.AwaitingReview) return false;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(toStatus, EventTypes.ReviewDeclined, new { result, reviewer }, endedAt, run.LifecycleGeneration),
            result, new HashSet<RunStatus> { RunStatus.AwaitingReview }, Reviewer: reviewer), ct).ConfigureAwait(false);
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
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run?.Status != RunStatus.Merging) return false;
        var type = toStatus == RunStatus.Merged ? EventTypes.MergeCompleted : EventTypes.MergeFailed;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(toStatus, type, new { result, mergeConflicts, mergedCommitHash }, endedAt, run.LifecycleGeneration),
            result, new HashSet<RunStatus> { RunStatus.Merging }, MergeConflicts: mergeConflicts,
            MergedCommitHash: mergedCommitHash), ct).ConfigureAwait(false);
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
        var run = await GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return false;
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(
            TerminalRunOutcome.Create(RunStatus.AssembleReady, EventTypes.RunAssembleReady,
                new { treeHash, worktreeBranch, diff, stepCount }, endedAt, run.LifecycleGeneration),
            null, TreeHash: treeHash, WorktreeBranch: worktreeBranch, Diff: diff), ct).ConfigureAwait(false);
    }

    public async Task<bool> TrySetTerminalStatusAsync(
        RunId runId, RunStatus toStatus, DateTimeOffset endedAt, string? result, CancellationToken ct = default)
    {
        throw new NotSupportedException(
            "Status-only terminal mutations are not supported; persist a typed TerminalRunOutcome instead.");
    }

    public async Task<bool> TrySetTerminalOutcomeAsync(
        RunId runId,
        TerminalRunOutcome outcome,
        string? result,
        CancellationToken ct = default)
    {
        return await TryMutateTerminalOutcomeAsync(runId, new TerminalRunMutation(outcome, result), ct).ConfigureAwait(false);
    }

    public async Task<bool> TryMutateTerminalOutcomeAsync(
        RunId runId, TerminalRunMutation mutation, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Match EfRunEventStream's per-run advisory-lock protocol. The lock and row update are
        // in this transaction, so two API instances cannot each write a terminal winner.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({runId.ToString()}, 0));", ct);
        var record = await db.Runs.SingleOrDefaultAsync(r => r.RunId == runId.ToString(), ct);
        if (record is null)
            return false;
        if (record.LifecycleGeneration != mutation.Outcome.ExpectedLifecycleGeneration
            || TerminalRunOutcome.IsTerminal(RunStatusExtensions.ParseStatus(record.Status)))
            return false;
        if (record.PreviewPublicationLeaseUntil is { } leaseUntil
            && leaseUntil > DateTimeOffset.UtcNow)
            return false;
        if (mutation.ExpectedStatuses is { Count: > 0 }
            && !mutation.ExpectedStatuses.Contains(RunStatusExtensions.ParseStatus(record.Status)))
            return false;

        var wasInProgress = record.Status == RunStatus.InProgress.ToApiString();
        record.Status = mutation.Outcome.Status.ToApiString();
        record.EndedAt = mutation.Outcome.OccurredAt;
        record.Result = mutation.Result;
        if (mutation.Reviewer is not null) record.ReviewedBy = mutation.Reviewer;
        if (mutation.MergeConflicts is not null) record.MergeConflicts = mutation.MergeConflicts;
        if (mutation.MergedCommitHash is not null) record.MergedCommitHash = mutation.MergedCommitHash;
        if (mutation.TreeHash is not null) record.TreeHash = mutation.TreeHash;
        if (mutation.WorktreeBranch is not null) record.WorktreeBranch = mutation.WorktreeBranch;
        if (mutation.Diff is not null) record.Diff = mutation.Diff;
        if (wasInProgress)
            record.ApprovalGeneration++;
        db.TerminalRunOutcomes.Add(new TerminalRunOutcomeRecord
        {
            RunId = record.RunId,
            LifecycleGeneration = record.LifecycleGeneration,
            Status = record.Status,
            EventType = mutation.Outcome.EventType,
            PayloadJson = mutation.Outcome.Payload.GetRawText(),
            OccurredAt = mutation.Outcome.OccurredAt,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PendingTerminalRunOutcome>> GetUnprojectedTerminalOutcomesAsync(
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var records = await db.TerminalRunOutcomes.AsNoTracking()
            .Where(x => x.ProjectedAt == null)
            .OrderBy(x => x.OccurredAt)
            .ToListAsync(ct);
        return records.Select(record =>
        {
            using var payload = JsonDocument.Parse(record.PayloadJson);
            return new PendingTerminalRunOutcome(
                RunId.Parse(record.RunId),
                record.LifecycleGeneration,
                new TerminalRunOutcome(
                    RunStatusExtensions.ParseStatus(record.Status),
                    record.EventType,
                    payload.RootElement.Clone(),
                    record.OccurredAt,
                    record.LifecycleGeneration));
        }).ToList();
    }

    public async Task MarkTerminalOutcomeProjectedAsync(
        RunId runId,
        int lifecycleGeneration,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.TerminalRunOutcomes
            .Where(x => x.RunId == runId.ToString()
                && x.LifecycleGeneration == lifecycleGeneration
                && x.ProjectedAt == null)
            .ExecuteUpdateAsync(
                updates => updates.SetProperty(x => x.ProjectedAt, DateTimeOffset.UtcNow), ct);
    }

    public async Task<bool> TryAdoptLegacyTerminalOutcomeAsync(
        RunId runId,
        TerminalRunOutcome outcome,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({runId.ToString()}, 0));", ct);
        var run = await db.Runs.SingleOrDefaultAsync(r => r.RunId == runId.ToString(), ct);
        if (run is null
            || run.LifecycleGeneration != outcome.ExpectedLifecycleGeneration
            || run.Status != outcome.Status.ToApiString())
            return false;
        if (await db.TerminalRunOutcomes.AnyAsync(
                x => x.RunId == run.RunId && x.LifecycleGeneration == run.LifecycleGeneration, ct))
            return false;
        db.TerminalRunOutcomes.Add(new TerminalRunOutcomeRecord
        {
            RunId = run.RunId,
            LifecycleGeneration = run.LifecycleGeneration,
            Status = run.Status,
            EventType = outcome.EventType,
            PayloadJson = outcome.Payload.GetRawText(),
            OccurredAt = outcome.OccurredAt,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TryBeginPreviewPublicationAsync(
        RunId runId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && !terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)leaseUntil), ct);
        return rows > 0;
    }

    public async Task<bool> TryAcquirePreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var now = DateTimeOffset.UtcNow;
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id
                && !terminalStatuses.Contains(r.Status)
                && (r.PreviewPublicationLeaseUntil == null
                    || r.PreviewPublicationLeaseUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseOwner, ownerId)
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)leaseUntil), ct);
        return rows > 0;
    }

    public async Task<bool> TryRenewPreviewPublicationAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id
                && r.PreviewPublicationLeaseOwner == ownerId
                && !terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)leaseUntil), ct);
        return rows > 0;
    }

    public async Task EndPreviewPublicationAsync(RunId runId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseOwner, (string?)null)
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)null), ct);
    }

    public async Task EndPreviewPublicationAsync(
        RunId runId, string ownerId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await db.Runs
            .Where(r => r.RunId == id && r.PreviewPublicationLeaseOwner == ownerId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseOwner, (string?)null)
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)null), ct);
    }

    public async Task<bool> IsPreviewPublicationOwnerAsync(
        RunId runId, string ownerId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().AnyAsync(
            r => r.RunId == id
                && r.PreviewPublicationLeaseOwner == ownerId,
            ct);
    }

    public async Task<DateTimeOffset?> GetPreviewPublicationLeaseAsync(RunId runId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking()
            .Where(r => r.RunId == id)
            .Select(r => r.PreviewPublicationLeaseUntil)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> TryTransitionToIdleAsync(RunId runId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var inProgressStr = RunStatus.InProgress.ToApiString();
        var idleStr = RunStatus.Idle.ToApiString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        // CAS: only the replica that still sees this run as InProgress flips it to Idle. A loser
        // (already parked by another replica, or since resumed/terminal) simply gets 0 rows. Note we
        // intentionally do NOT touch EndedAt — an Idle run is dormant, not ended.
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.Status == inProgressStr)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, idleStr)
                .SetProperty(r => r.ApprovalGeneration, r => r.ApprovalGeneration + 1), ct);
        return rows > 0;
    }

    public async Task<bool> TryWakeFromIdleAsync(RunId runId, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var idleStr = RunStatus.Idle.ToApiString();
        var inProgressStr = RunStatus.InProgress.ToApiString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.Status == idleStr)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(r => r.Status, inProgressStr)
                .SetProperty(r => r.LifecycleGeneration, r => r.LifecycleGeneration + 1), ct);
        if (rows != 1)
        {
            await tx.RollbackAsync(ct);
            return false;
        }
        var record = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == id, ct);
        db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, FromRecord(record), ct));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
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

    public async Task SetSandboxInfoAsync(
        RunId runId,
        string? backend,
        string? claimName,
        string? podName,
        string? @namespace,
        CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.FirstOrDefaultAsync(r => r.RunId == id, ct);
        if (rec is null)
        {
            WarnIfNoRows(0, runId, "set sandbox info");
            return;
        }

        if (string.IsNullOrWhiteSpace(rec.SandboxBackend) && !string.IsNullOrWhiteSpace(backend))
            rec.SandboxBackend = backend;
        if (string.IsNullOrWhiteSpace(rec.SandboxClaimName) && !string.IsNullOrWhiteSpace(claimName))
            rec.SandboxClaimName = claimName;
        if (string.IsNullOrWhiteSpace(rec.SandboxPodName) && !string.IsNullOrWhiteSpace(podName))
            rec.SandboxPodName = podName;
        if (string.IsNullOrWhiteSpace(rec.SandboxNamespace) && !string.IsNullOrWhiteSpace(@namespace))
            rec.SandboxNamespace = @namespace;

        await db.SaveChangesAsync(ct);
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
        var activeStatuses = new[] { "in_progress", "awaiting_review", "assembling", "in_review" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.AsNoTracking()
            .Where(r => r.ParentRunId == parentRunId && r.SubtaskId == subtaskId && activeStatuses.Contains(r.Status))
            .OrderByDescending(r => r.StartedAt)
            .FirstOrDefaultAsync(ct);
        return rec is null ? null : FromRecord(rec);
    }

    public async Task<Run?> FindChildAsync(string parentRunId, string subtaskId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rec = await db.Runs.AsNoTracking()
            .Where(r => r.ParentRunId == parentRunId && r.SubtaskId == subtaskId)
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

    public async Task<IReadOnlyList<Run>> GetRunsBySubmittingUserAsync(
        string submittingUser, string? agentName, int limit, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var q = db.Runs.AsNoTracking()
            .Where(r => r.SubmittingUser == submittingUser && r.ArchivedAt == null);
        if (agentName is not null)
            q = q.Where(r => r.AgentName == agentName);
        var recs = await q.OrderByDescending(r => r.StartedAt).Take(limit).ToListAsync(ct);
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
        db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, run, ct));
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

    public async Task UpdateWorkflowSelectionReasonAsync(RunId runId, string? reason, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.WorkflowSelectionReason, reason), ct);
        WarnIfNoRows(rows, runId, "update workflow selection reason");
    }

    public async Task UpdateExecutableWorkflowPinAsync(
        RunId runId,
        ExecutableWorkflowPin pin,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var id = runId.ToString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ExecutableWorkflowPinRequired, true)
                .SetProperty(r => r.ExecutableWorkflowManifestSchemaVersion, pin.ManifestSchemaVersion)
                .SetProperty(r => r.ExecutableWorkflowDefinitionId, pin.DefinitionId)
                .SetProperty(r => r.ExecutableWorkflowDefinitionVersion, pin.DefinitionVersion)
                .SetProperty(r => r.ExecutableWorkflowSource, pin.Source)
                .SetProperty(r => r.ExecutableWorkflowContentDigest, pin.ContentDigest)
                .SetProperty(r => r.ExecutableWorkflowDefinitionYaml, pin.DefinitionYaml)
                .SetProperty(r => r.ExecutableWorkflowPinnedAt, pin.PinnedAt), ct);
        if (rows == 0)
            throw new InvalidOperationException($"Cannot pin executable workflow because run {runId} does not exist.");
    }

    public async Task UpdateModelSourceAsync(RunId runId, ModelSource modelSource, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var id = runId.ToString();
        var value = modelSource.ToApiString();
        var rows = await db.Runs
            .Where(r => r.RunId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ModelSource, value), ct);
        WarnIfNoRows(rows, runId, "update model source");
    }

    private void WarnIfNoRows(int rows, RunId runId, string operation)
    {
        if (rows == 0)
            _logger?.LogWarning("Run transition no-op while attempting to {Operation} for run {RunId}", operation, runId);
    }

    internal static async Task<ExecutionIdentityRecord> CreateExecutionIdentityAsync(
        MemoryDbContext db,
        Run run,
        CancellationToken ct)
    {
        async Task<string?> LatestDescriptorIdAsync(string? linkedRunId)
        {
            if (string.IsNullOrWhiteSpace(linkedRunId))
                return null;

            return await db.ExecutionIdentities.AsNoTracking()
                .Where(identity => identity.RunId == linkedRunId)
                .OrderByDescending(identity => identity.Attempt)
                .Select(identity => identity.DescriptorId)
                .FirstOrDefaultAsync(ct);
        }

        var parentDescriptorId = await LatestDescriptorIdAsync(run.ParentRunId);
        var retryDescriptorId = await LatestDescriptorIdAsync(run.RetriedFrom);
        return ExecutionIdentityDescriptor.CreateWithResolvedLineage(
            run,
            parentDescriptorId,
            retryDescriptorId).ToRecord();
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
        ApprovalGeneration = r.ApprovalGeneration,
        LifecycleGeneration = r.LifecycleGeneration,
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
        WorkflowSelectionReason = r.WorkflowSelectionReason,
        MergedCommitHash = r.MergedCommitHash,
        ParentRunId = r.ParentRunId,
        SubtaskId = r.SubtaskId,
        Origin = r.Origin.ToApiString(),
        LaunchAutoApproveTools = r.LaunchAutoApproveTools,
        LaunchAutopilot = r.LaunchAutopilot,
        ApprovalPolicySnapshotId = r.ApprovalPolicySnapshotId,
        ApprovalPolicySource = r.ApprovalPolicySource,
        ApprovalPolicyCapturedAt = r.ApprovalPolicyCapturedAt,
        ApprovalPolicySettingsUpdatedAt = r.ApprovalPolicySettingsUpdatedAt,
        ApprovalPolicyInheritedFromRunId = r.ApprovalPolicyInheritedFromRunId,
        RetriedFrom = r.RetriedFrom,
        ArchivedAt = r.ArchivedAt,
        SandboxBackend = r.SandboxBackend,
        SandboxClaimName = r.SandboxClaimName,
        SandboxPodName = r.SandboxPodName,
        SandboxNamespace = r.SandboxNamespace,
        ExecutableWorkflowPinRequired = r.ParentRunId is null && r.ProjectId is not null,
        ExecutableWorkflowManifestSchemaVersion = r.ExecutableWorkflowManifestSchemaVersion,
        ExecutableWorkflowDefinitionId = r.ExecutableWorkflowDefinitionId,
        ExecutableWorkflowDefinitionVersion = r.ExecutableWorkflowDefinitionVersion,
        ExecutableWorkflowSource = r.ExecutableWorkflowSource,
        ExecutableWorkflowContentDigest = r.ExecutableWorkflowContentDigest,
        ExecutableWorkflowDefinitionYaml = r.ExecutableWorkflowDefinitionYaml,
        ExecutableWorkflowPinnedAt = r.ExecutableWorkflowPinnedAt,
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
        ApprovalGeneration = r.ApprovalGeneration,
        LifecycleGeneration = r.LifecycleGeneration,
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
        WorkflowSelectionReason = r.WorkflowSelectionReason,
        MergedCommitHash = r.MergedCommitHash,
        ParentRunId = r.ParentRunId,
        SubtaskId = r.SubtaskId,
        Origin = RunOriginExtensions.ParseOrigin(r.Origin),
        LaunchAutoApproveTools = r.LaunchAutoApproveTools,
        LaunchAutopilot = r.LaunchAutopilot,
        ApprovalPolicySnapshotId = r.ApprovalPolicySnapshotId,
        ApprovalPolicySource = r.ApprovalPolicySource,
        ApprovalPolicyCapturedAt = r.ApprovalPolicyCapturedAt,
        ApprovalPolicySettingsUpdatedAt = r.ApprovalPolicySettingsUpdatedAt,
        ApprovalPolicyInheritedFromRunId = r.ApprovalPolicyInheritedFromRunId,
        RetriedFrom = r.RetriedFrom,
        ArchivedAt = r.ArchivedAt,
        SandboxBackend = r.SandboxBackend,
        SandboxClaimName = r.SandboxClaimName,
        SandboxPodName = r.SandboxPodName,
        SandboxNamespace = r.SandboxNamespace,
        ExecutableWorkflowPinRequired = r.ExecutableWorkflowPinRequired,
        ExecutableWorkflowManifestSchemaVersion = r.ExecutableWorkflowManifestSchemaVersion,
        ExecutableWorkflowDefinitionId = r.ExecutableWorkflowDefinitionId,
        ExecutableWorkflowDefinitionVersion = r.ExecutableWorkflowDefinitionVersion,
        ExecutableWorkflowSource = r.ExecutableWorkflowSource,
        ExecutableWorkflowContentDigest = r.ExecutableWorkflowContentDigest,
        ExecutableWorkflowDefinitionYaml = r.ExecutableWorkflowDefinitionYaml,
        ExecutableWorkflowPinnedAt = r.ExecutableWorkflowPinnedAt,
    };

    private static void RejectTerminalStatus(RunStatus status)
    {
        if (TerminalRunOutcome.IsTerminal(status))
            throw new InvalidOperationException(
                $"Terminal status '{status}' requires TrySetTerminalOutcomeAsync or a typed terminal mutation.");
    }
}
