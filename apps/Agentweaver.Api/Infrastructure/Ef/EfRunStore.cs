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
    private readonly TimeProvider _clock;

    public EfRunStore(
        IDbContextFactory<MemoryDbContext> factory,
        ILogger<EfRunStore>? logger = null,
        TimeProvider? clock = null)
    {
        _factory = factory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>
    /// Reserves the canonical child and its execution identity while holding the plan and subtask
    /// row locks. A launch is authorized only after the child lease is committed.
    /// </summary>
    public async Task<ChildDispatchReservation> TryReserveCoordinatorChildAsync(
        int workPlanId, int subtaskId, string coordinatorPodId, int parentLifecycleGeneration,
        Run proposedChild, string leaseOwnerId, TimeSpan leaseTtl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coordinatorPodId);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwnerId);
        if (leaseTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(leaseTtl));
        if (string.IsNullOrWhiteSpace(proposedChild.ParentRunId)
            || proposedChild.SubtaskId != subtaskId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new ArgumentException("Child must identify its coordinator and subtask.", nameof(proposedChild));

        await using var db = await _factory.CreateDbContextAsync(ct);
        if (!db.Database.IsNpgsql())
            throw new NotSupportedException("Coordinator child reservations require PostgreSQL row locks.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var plan = await db.WorkPlans
            .FromSqlInterpolated($"SELECT * FROM \"WorkPlans\" WHERE \"Id\" = {workPlanId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (plan is null || plan.CoordinatorRunId != proposedChild.ParentRunId
            || plan.CoordinatorPodId != coordinatorPodId || plan.Status != "dispatching"
            || plan.CoordinatorCancellationRequestedAt is not null)
            return new ChildDispatchReservation(ChildDispatchReservationState.NotOwner);

        var subtask = await db.Subtasks
            .FromSqlInterpolated($"SELECT * FROM \"Subtasks\" WHERE \"Id\" = {subtaskId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (subtask is null || subtask.WorkPlanId != workPlanId
            || subtask.CancellationRequestedAt is not null)
            return new ChildDispatchReservation(ChildDispatchReservationState.NotOwner);
        if (subtask.Status != "pending")
            return new ChildDispatchReservation(ChildDispatchReservationState.ExistingActive,
                subtask.ChildRunId, ExistingStatus: subtask.Status);

        var parent = await db.Runs
            .FromSqlInterpolated($"SELECT * FROM runs WHERE run_id = {plan.CoordinatorRunId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (parent is null || parent.LifecycleGeneration != parentLifecycleGeneration
            || parent.Status != RunStatus.InProgress.ToApiString())
            return new ChildDispatchReservation(ChildDispatchReservationState.NotOwner);

        var childId = subtask.ChildRunId;
        if (childId is null)
        {
            var previous = await db.Runs.AsNoTracking()
                .Where(r => r.ParentRunId == plan.CoordinatorRunId
                    && r.SubtaskId == subtaskId.ToString()
                    && (r.Status == "pending" || r.Status == "in_progress"))
                .OrderBy(r => r.Status == "pending")
                .ThenByDescending(r => r.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (previous is not null && previous.Status != "pending")
                return new ChildDispatchReservation(ChildDispatchReservationState.ExistingActive,
                    previous.RunId, ExistingStatus: previous.Status);
            childId = previous?.RunId ?? proposedChild.Id.ToString();
            subtask.ChildRunId = childId;
            subtask.UpdatedAt = _clock.GetUtcNow();
        }
        else if (!RunId.TryParse(childId, out _))
            throw new InvalidOperationException($"Subtask {subtaskId} has an invalid canonical child run id.");

        var child = await db.Runs
            .FromSqlInterpolated($"SELECT * FROM runs WHERE run_id = {childId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);
        if (child is null)
        {
            var pending = proposedChild with
            {
                Id = RunId.Parse(childId),
                Status = RunStatus.Pending,
                StartedAt = proposedChild.StartedAt == default ? DateTimeOffset.UtcNow : proposedChild.StartedAt,
                EndedAt = null,
                Result = null,
            };
            child = ToRecord(pending);
            db.Runs.Add(child);
            db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, pending, ct));
        }
        else if (child.ParentRunId != plan.CoordinatorRunId || child.SubtaskId != subtaskId.ToString())
            throw new InvalidOperationException($"Child {childId} does not belong to subtask {subtaskId}.");
        else if (child.Status != RunStatus.Pending.ToApiString())
            return new ChildDispatchReservation(ChildDispatchReservationState.ExistingActive,
                childId, ExistingStatus: child.Status);

        if (!await db.ExecutionIdentities.AnyAsync(i => i.RunId == childId, ct)
            && db.Entry(child).State != EntityState.Added)
            db.ExecutionIdentities.Add(await CreateExecutionIdentityAsync(db, FromRecord(child), ct));

        var now = _clock.GetUtcNow();
        // A pending child has not started a turn. Once another coordinator pod owns the
        // plan, the former pod's launch authorization is fenced by the plan-owner check;
        // retaining its five-minute reservation would strand this sibling during recovery.
        if (child.OwnerId is not null && child.LeaseExpiresAt >= now
            && (!child.OwnerId.Contains("/child-dispatch/", StringComparison.Ordinal)
                || child.OwnerId.StartsWith(
                    coordinatorPodId + "/child-dispatch/", StringComparison.Ordinal)))
            return new ChildDispatchReservation(ChildDispatchReservationState.LeaseHeld, childId);

        child.OwnerId = leaseOwnerId;
        child.LeaseExpiresAt = now.Add(leaseTtl);
        child.HeartbeatAt = now;
        child.FencingToken++;
        child.Attempt++;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ChildDispatchReservation(ChildDispatchReservationState.Claimed, childId,
            child.FencingToken, child.LifecycleGeneration);
    }

    public async Task<bool> IsCoordinatorChildLaunchAuthorizedAsync(
        int workPlanId, int subtaskId, string coordinatorPodId, int parentLifecycleGeneration,
        string childRunId, string leaseOwnerId, long fencingToken, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var now = _clock.GetUtcNow();
        return await db.WorkPlans.AsNoTracking().AnyAsync(w =>
            w.Id == workPlanId
            && w.Status == "dispatching"
            && w.CoordinatorPodId == coordinatorPodId
            && w.CoordinatorCancellationRequestedAt == null
            && db.Subtasks.Any(s => s.Id == subtaskId && s.WorkPlanId == w.Id
                && s.ChildRunId == childRunId && s.CancellationRequestedAt == null)
            && db.Runs.Any(p => p.RunId == w.CoordinatorRunId
                && p.Status == "in_progress" && p.LifecycleGeneration == parentLifecycleGeneration)
            && db.Runs.Any(c => c.RunId == childRunId && c.OwnerId == leaseOwnerId
                && c.FencingToken == fencingToken && c.LeaseExpiresAt > now), ct);
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

    public async Task<bool> TryBindExecutionInputAsync(
        RunId runId,
        int expectedLifecycleGeneration,
        string sourceCommitHash,
        string executionCommitHash,
        string compositeId,
        CancellationToken ct = default)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var record = await db.Runs.SingleOrDefaultAsync(
            run => run.RunId == id && run.LifecycleGeneration == expectedLifecycleGeneration,
            ct).ConfigureAwait(false);
        if (record is null || !record.ExecutionInputRequired)
            return false;

        if (record.ExecutionInputSourceCommitHash is not null
            || record.ExecutionInputCommitHash is not null
            || record.ExecutionInputCompositeId is not null)
        {
            return record.ExecutionInputSourceCommitHash == sourceCommitHash
                && record.ExecutionInputCommitHash == executionCommitHash
                && record.ExecutionInputCompositeId == compositeId;
        }

        record.ExecutionInputSourceCommitHash = sourceCommitHash;
        record.ExecutionInputCommitHash = executionCommitHash;
        record.ExecutionInputCompositeId = compositeId;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
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
                .SetProperty(r => r.CurrentOutputRevisionId,
                    r => statusStr == "in_progress"
                         && new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready" }.Contains(r.Status)
                        ? null : r.CurrentOutputRevisionId)
                .SetProperty(r => r.ApprovedOutputRevisionId,
                    r => statusStr == "in_progress"
                         && new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready" }.Contains(r.Status)
                        ? null : r.ApprovedOutputRevisionId)
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
                .SetProperty(r => r.CurrentOutputRevisionId,
                    r => statusStr == "in_progress"
                         && new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready" }.Contains(r.Status)
                        ? null : r.CurrentOutputRevisionId)
                .SetProperty(r => r.ApprovedOutputRevisionId,
                    r => statusStr == "in_progress"
                         && new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready" }.Contains(r.Status)
                        ? null : r.ApprovedOutputRevisionId)
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

    public Task UpdateReviewReadyAsync(RunId runId, string treeHash, string diff, int stepCount,
        CancellationToken ct = default, DateTimeOffset? now = null) =>
        UpdateReviewReadyAsync(runId, treeHash, diff, stepCount, ct, now, null);

    public async Task UpdateReviewReadyAsync(
        RunId runId, string treeHash, string diff, int stepCount,
        CancellationToken ct, DateTimeOffset? now, byte[]? treeContent) =>
        await PublishReviewReadyCoreAsync(runId, null, treeHash, diff, stepCount, ct, now, treeContent);

    public Task PublishReviewReadyAsync(RunId runId, int expectedLifecycleGeneration, string treeHash,
        string diff, int stepCount, CancellationToken ct = default, DateTimeOffset? now = null) =>
        PublishReviewReadyAsync(runId, expectedLifecycleGeneration, treeHash, diff, stepCount, ct, now, null);

    public Task PublishReviewReadyAsync(
        RunId runId, int expectedLifecycleGeneration, string treeHash, string diff, int stepCount,
        CancellationToken ct, DateTimeOffset? now, byte[]? treeContent) =>
        PublishReviewReadyCoreAsync(runId, expectedLifecycleGeneration, treeHash, diff, stepCount, ct, now, treeContent);

    private async Task PublishReviewReadyCoreAsync(
        RunId runId, int? expectedLifecycleGeneration, string treeHash, string diff, int stepCount,
        CancellationToken ct, DateTimeOffset? now, byte[]? treeContent)
    {
        if (treeContent is not null)
           RunOutputTree.Decode(treeContent);
        var ts = (now ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var id = runId.ToString();
        var bytes = RunOutputRevision.EncodeDiff(diff);
        var digest = RunOutputRevision.Sha256(bytes);
        if (string.IsNullOrWhiteSpace(treeHash))
            throw new InvalidOperationException("Review-ready output requires a pinned tree.");
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Runs
            .Where(r => r.RunId == id && (r.Status == "in_progress" || r.Status == "awaiting_review"))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, r => r.Status), ct);
        if (locked != 1)
            throw new InvalidOperationException("Run cannot publish output from its current status.");

        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == id, ct);
        if (expectedLifecycleGeneration is { } expected && run.LifecycleGeneration != expected)
            throw new InvalidOperationException("Run lifecycle generation changed before output publication.");
        var existing = await db.RunOutputRevisions.AsNoTracking()
            .Where(r => r.RunId == id && r.LifecycleGeneration == run.LifecycleGeneration)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            var revision = ToOutputRevision(existing);
            if (run.Status != "awaiting_review" || run.CurrentOutputRevisionId != revision.RevisionId
                || revision.TreeHash != treeHash
                || revision.DiffSha256 != digest
                || revision.WorkflowDigest != run.ExecutableWorkflowContentDigest
                || !revision.DiffBytes.AsSpan().SequenceEqual(bytes)
                || !(revision.TreeContent ?? []).AsSpan().SequenceEqual(treeContent ?? []))
                throw new InvalidOperationException("Output revision already published with different content or generation is no longer review-ready.");
            await tx.CommitAsync(ct);
            return;
        }
        if (run.CurrentOutputRevisionId is not null || (run.Status == "awaiting_review" && run.ReviewReadyAt is not null))
            throw new InvalidOperationException("Review-ready run has no durable output revision.");

        var predecessor = await db.RunOutputRevisions.AsNoTracking()
            .Where(r => r.RunId == id && r.LifecycleGeneration < run.LifecycleGeneration)
            .OrderByDescending(r => r.LifecycleGeneration)
            .Select(r => r.RevisionId)
            .FirstOrDefaultAsync(ct);
        var revisionId = Guid.NewGuid().ToString("N");
        db.RunOutputRevisions.Add(new RunOutputRevisionRecord
        {
            RevisionId = revisionId,
            SchemaVersion = RunOutputRevision.CurrentSchemaVersion,
            RunId = id,
            LifecycleGeneration = run.LifecycleGeneration,
            WorkflowDigest = run.ExecutableWorkflowContentDigest,
            ManifestIncomplete = run.ExecutableWorkflowContentDigest is null,
            TreeHash = treeHash,
            DiffSha256 = digest,
            PredecessorRevisionId = predecessor,
            DiffBytes = bytes,
            TreeContent = treeContent,
            TreeContentSha256 = treeContent is null ? null : RunOutputRevision.Sha256(treeContent),
            CreatedAt = ts
        });
        var rows = await db.Runs
            .Where(r => r.RunId == id && r.LifecycleGeneration == run.LifecycleGeneration
                && (r.Status == "in_progress" || (r.Status == "awaiting_review" && r.ReviewReadyAt == null)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.TreeHash, treeHash)
                .SetProperty(r => r.Diff, diff)
                .SetProperty(r => r.Status, "awaiting_review")
                .SetProperty(r => r.CurrentOutputRevisionId, revisionId)
                .SetProperty(r => r.ReviewReadyAt, ts)
                .SetProperty(r => r.ApprovalGeneration,
                    r => r.Status == "in_progress" ? r.ApprovalGeneration + 1 : r.ApprovalGeneration), ct);
        if (rows != 1)
            throw new InvalidOperationException("Run generation changed during output publication.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static RunOutputRevision ToOutputRevision(RunOutputRevisionRecord record) =>
        new(record.RevisionId, record.SchemaVersion, new RunId(Guid.Parse(record.RunId)),
            record.LifecycleGeneration, record.WorkflowDigest, record.ManifestIncomplete,
            record.TreeHash, record.DiffSha256, record.PredecessorRevisionId, record.DiffBytes,
            record.CreatedAt, record.OutputKind, record.MergedCommitHash, record.WorkPlanId,
            record.MergeEffectId, record.AcceptedNoChange, record.TreeContent, record.TreeContentSha256);

    public async Task<RunOutputRevision> PublishCollectiveCandidateAsync(
        RunId runId, int generation, string workPlanId, string treeHash, string diff,
        byte[] treeContent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(treeHash) || string.IsNullOrWhiteSpace(workPlanId))
            throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
        RunOutputTree.Decode(treeContent);
        var id = runId.ToString();
        var bytes = RunOutputRevision.EncodeDiff(diff);
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({id}, 0));", ct);
        var run = await db.Runs.SingleOrDefaultAsync(r => r.RunId == id, ct);
        if (run is null || run.LifecycleGeneration != generation
            || run.Status is not ("in_progress" or "awaiting_review")
            || run.TreeHash != treeHash || run.Diff != diff)
            throw new RunOutputRevisionUnavailableException("stale_collective_candidate");
        RunOutputRevisionRecord? previous = null;
        if (run.CurrentOutputRevisionId is not null)
            previous = await db.RunOutputRevisions.AsNoTracking()
                .SingleOrDefaultAsync(r => r.RevisionId == run.CurrentOutputRevisionId && r.RunId == id, ct)
                ?? throw new RunOutputRevisionUnavailableException("missing_content");
        if (previous is not null)
        {
            var revision = ToOutputRevision(previous);
            if (revision.SchemaVersion == RunOutputRevision.CollectiveCandidateSchemaVersion
                && revision.LifecycleGeneration == generation && revision.WorkPlanId == workPlanId
                && revision.TreeHash == treeHash
                && revision.WorkflowDigest == run.ExecutableWorkflowContentDigest
                && revision.DiffBytes.AsSpan().SequenceEqual(bytes)
                && (revision.TreeContent ?? []).AsSpan().SequenceEqual(treeContent))
            {
                await tx.CommitAsync(ct);
                return revision;
            }
        }
        var revisionId = Guid.NewGuid().ToString("N");
        var record = new RunOutputRevisionRecord
        {
            RevisionId = revisionId,
            SchemaVersion = RunOutputRevision.CollectiveCandidateSchemaVersion,
            RunId = id,
            LifecycleGeneration = generation,
            WorkflowDigest = run.ExecutableWorkflowContentDigest,
            ManifestIncomplete = run.ExecutableWorkflowContentDigest is null,
            TreeHash = treeHash,
            DiffSha256 = RunOutputRevision.Sha256(bytes),
            PredecessorRevisionId = previous?.RevisionId,
            OutputKind = "collective",
            WorkPlanId = workPlanId,
            DiffBytes = bytes,
            TreeContent = treeContent,
            TreeContentSha256 = RunOutputRevision.Sha256(treeContent),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.RunOutputRevisions.Add(record);
        run.CurrentOutputRevisionId = revisionId;
        run.ApprovedOutputRevisionId = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToOutputRevision(record);
    }

    public async Task<bool> ApproveCollectiveCandidateAsync(
        RunId runId, int generation, string revisionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var revision = await db.RunOutputRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RevisionId == revisionId && r.RunId == runId.ToString()
                && r.LifecycleGeneration == generation && r.SchemaVersion == RunOutputRevision.CollectiveCandidateSchemaVersion, ct);
        if (revision is null) return false;
        var rows = await db.Runs.Where(r => r.RunId == runId.ToString()
                && r.LifecycleGeneration == generation
                && (r.Status == "in_progress" || r.Status == "awaiting_review")
                && r.CurrentOutputRevisionId == revisionId
                && r.TreeHash == revision.TreeHash
                && r.Diff == System.Text.Encoding.UTF8.GetString(revision.DiffBytes!)
                && r.ExecutableWorkflowContentDigest == revision.WorkflowDigest)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ApprovedOutputRevisionId, revisionId), ct);
        return rows == 1;
    }

    public async Task<RunOutputRevision?> GetOutputRevisionAsync(
        RunId runId, string revisionId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var record = await db.RunOutputRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RunId == runId.ToString() && r.RevisionId == revisionId, ct);
        return record is null ? null : ToOutputRevision(record);
    }

    public async Task<RunOutputRevision?> GetLatestOutputRevisionAsync(RunId runId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var record = await db.RunOutputRevisions.AsNoTracking()
            .Where(r => r.RunId == runId.ToString()).OrderByDescending(r => r.LifecycleGeneration)
            .ThenByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(ct);
        return record is null ? null : ToOutputRevision(record);
    }

    public async Task<IReadOnlyList<RunOutputRevision>> ListOutputRevisionsAsync(
        RunId runId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var records = await db.RunOutputRevisions.AsNoTracking()
            .Where(r => r.RunId == runId.ToString()).OrderByDescending(r => r.LifecycleGeneration)
            .ThenByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
        return records.Select(ToOutputRevision).ToArray();
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
                .SetProperty(r => r.ApprovedOutputRevisionId, (string?)null)
                .SetProperty(r => r.CurrentOutputRevisionId, (string?)null)
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
        var now = DateTimeOffset.UtcNow;
        var rows = await db.Runs
            .Where(r => r.RunId == id && terminalStatuses.Contains(r.Status)
                && (r.PreviewPublicationLeaseUntil == null
                    || r.PreviewPublicationLeaseUntil <= now))
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(r => r.Status, RunStatus.InProgress.ToApiString())
                .SetProperty(r => r.EndedAt, (DateTimeOffset?)null)
                .SetProperty(r => r.CurrentOutputRevisionId, (string?)null)
                .SetProperty(r => r.ApprovedOutputRevisionId, (string?)null)
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

    public Task<bool> TryTransitionToCommittingAsync(
        RunId runId, CancellationToken ct = default, DateTimeOffset? now = null) =>
        TryTransitionToCommittingCoreAsync(runId, null, ct);

    public Task<bool> TryTransitionToCommittingRevisionAsync(
        RunId runId, string revisionId, CancellationToken ct = default) =>
        TryTransitionToCommittingCoreAsync(runId, revisionId, ct);

    private async Task<bool> TryTransitionToCommittingCoreAsync(
        RunId runId, string? revisionId, CancellationToken ct)
    {
        var id = runId.ToString();
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({id}, 0));", ct);
        var rec = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.RunId == id, ct);
        if (rec is null || rec.Status != "awaiting_review"
            || !await MatchesReviewRevisionAsync(db, rec, revisionId, ct))
            return false;
        var rows = await db.Runs.Where(r => r.RunId == id && r.Status == "awaiting_review"
                && r.LifecycleGeneration == rec.LifecycleGeneration && r.TreeHash == rec.TreeHash && r.Diff == rec.Diff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "committing")
                .SetProperty(r => r.ReviewReadyAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ApprovedOutputRevisionId, revisionId), ct);
        if (rows != 1) return false;
        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<bool> MatchesReviewRevisionAsync(
        MemoryDbContext db, RunRecord rec, string? revisionId, CancellationToken ct)
    {
        var stored = await db.RunOutputRevisions.AsNoTracking()
            .SingleOrDefaultAsync(r => r.RunId == rec.RunId && r.RevisionId == rec.CurrentOutputRevisionId, ct);
        return stored is null
            ? revisionId is null && rec.CurrentOutputRevisionId is null
            : string.Equals(rec.CurrentOutputRevisionId, stored.RevisionId, StringComparison.Ordinal)
              && ToOutputRevision(stored).Matches(FromRecord(rec), revisionId);
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
        rec.ApprovedOutputRevisionId = null;
        if (treeHash is not null) rec.TreeHash = treeHash;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public Task<bool> TryStartMergingAsync(
        RunId runId, string? reviewer = null, CancellationToken ct = default, DateTimeOffset? now = null) =>
        TryStartMergingCoreAsync(runId, null, reviewer, ct);

    public Task<bool> TryStartMergingRevisionAsync(
        RunId runId, string revisionId, string? reviewer = null, CancellationToken ct = default) =>
        TryStartMergingCoreAsync(runId, revisionId, reviewer, ct);

    private async Task<bool> TryStartMergingCoreAsync(
        RunId runId, string? revisionId, string? reviewer, CancellationToken ct)
    {
        var id = runId.ToString();
        var mergingFromStates = new[] { "awaiting_review", "committing" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({id}, 0));", ct);
        var rec = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.RunId == id, ct);
        if (rec is null || !mergingFromStates.Contains(rec.Status)
            || (rec.Status == "committing"
                && !string.Equals(rec.ApprovedOutputRevisionId, revisionId, StringComparison.Ordinal))
            || !await MatchesReviewRevisionAsync(db, rec, revisionId, ct))
            return false;
        var rows = await db.Runs.Where(r => r.RunId == id && mergingFromStates.Contains(r.Status)
                && r.LifecycleGeneration == rec.LifecycleGeneration && r.TreeHash == rec.TreeHash && r.Diff == rec.Diff)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "merging")
                .SetProperty(r => r.ReviewedBy, reviewer ?? rec.ReviewedBy)
                .SetProperty(r => r.ReviewReadyAt, (DateTimeOffset?)null)
                .SetProperty(r => r.ApprovedOutputRevisionId, revisionId), ct);
        if (rows != 1) return false;
        await tx.CommitAsync(ct);
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
                .SetProperty(r => r.ApprovedOutputRevisionId, (string?)null)
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
            .Where(r => r.RunId == id && r.Status == "committing"
                && (r.ApprovedOutputRevisionId == null || r.TreeHash == newTreeHash))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TreeHash, newTreeHash), ct);
        if (rows == 0 && (await GetAsync(runId, ct).ConfigureAwait(false))?.ApprovedOutputRevisionId is not null)
            throw new InvalidOperationException("Committed tree differs from the approved output revision.");
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
        if (mutation.RequiredLease is { } fence
            && fence.LifecycleGeneration != mutation.Outcome.ExpectedLifecycleGeneration)
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
        if (mutation.CollectiveOutput is { } collective
            && record.TreeHash != collective.TreeHash)
            return false;
        RunOutputRevisionRecord? approvedCandidate = null;
        if (mutation.ApprovedCollectiveRevisionId is { } approved)
        {
            if (mutation.CollectiveOutput is null || mutation.NoChangeOutput is not null
                || mutation.Outcome.Status != RunStatus.Completed
                || mutation.TreeHash != record.TreeHash || string.IsNullOrWhiteSpace(mutation.MergedCommitHash)
                || record.CurrentOutputRevisionId != approved || record.ApprovedOutputRevisionId != approved)
                return false;
            approvedCandidate = await db.RunOutputRevisions.AsNoTracking()
                .SingleOrDefaultAsync(r => r.RevisionId == approved && r.RunId == record.RunId
                    && r.LifecycleGeneration == record.LifecycleGeneration && r.SchemaVersion == 4, ct);
            if (approvedCandidate is null || approvedCandidate.TreeHash != record.TreeHash
                || approvedCandidate.DiffSha256 != RunOutputRevision.Sha256(RunOutputRevision.EncodeDiff(record.Diff ?? ""))
                || approvedCandidate.WorkflowDigest != record.ExecutableWorkflowContentDigest)
                return false;
            ToOutputRevision(approvedCandidate).ResolveFiles();
        }

        var wasInProgress = record.Status == RunStatus.InProgress.ToApiString();
        if (mutation.RequiredLease is { } requiredLease)
        {
            var now = DateTimeOffset.UtcNow;
            var changed = await db.Runs
                .Where(r => r.RunId == runId.ToString()
                    && r.LifecycleGeneration == mutation.Outcome.ExpectedLifecycleGeneration
                    && r.OwnerId == requiredLease.OwnerId
                    && r.FencingToken == requiredLease.FencingToken
                    && r.LeaseExpiresAt > now
                    && r.Status == record.Status
                    && (r.PreviewPublicationLeaseUntil == null || r.PreviewPublicationLeaseUntil <= now)
                    && (mutation.ExpectedParentLifecycleGeneration == null
                        || db.Runs.Any(parent => parent.RunId == r.ParentRunId
                            && parent.LifecycleGeneration == mutation.ExpectedParentLifecycleGeneration
                            && parent.Status == "in_progress")))
                .ExecuteUpdateAsync(updates => updates
                    .SetProperty(r => r.Status, mutation.Outcome.Status.ToApiString())
                    .SetProperty(r => r.EndedAt, mutation.Outcome.OccurredAt)
                    .SetProperty(r => r.Result, mutation.Result)
                    .SetProperty(r => r.ReviewedBy, r => mutation.Reviewer ?? r.ReviewedBy)
                    .SetProperty(r => r.MergeConflicts, r => mutation.MergeConflicts ?? r.MergeConflicts)
                    .SetProperty(r => r.MergedCommitHash, r => mutation.MergedCommitHash ?? r.MergedCommitHash)
                    .SetProperty(r => r.TreeHash, r => mutation.TreeHash ?? r.TreeHash)
                    .SetProperty(r => r.WorktreeBranch, r => mutation.WorktreeBranch ?? r.WorktreeBranch)
                    .SetProperty(r => r.Diff, r => mutation.Diff ?? r.Diff)
                    .SetProperty(r => r.ApprovalGeneration, r => r.ApprovalGeneration + (wasInProgress ? 1 : 0)),
                    ct).ConfigureAwait(false);
            if (changed == 0)
                return false;
        }
        else
        {
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
        }
        if (mutation.CollectiveOutput is { } output)
        {
            if (output.TreeContent is null)
                throw new RunOutputRevisionUnavailableException("missing_content");
            RunOutputTree.Decode(output.TreeContent);
            if (approvedCandidate is not null
                && (approvedCandidate.WorkPlanId != output.WorkPlanId
                    || approvedCandidate.TreeContent is null
                    || !approvedCandidate.TreeContent.AsSpan().SequenceEqual(output.TreeContent)))
                throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
            if (mutation.Outcome.Status != RunStatus.Completed
                || string.IsNullOrWhiteSpace(output.CommitHash)
                || string.IsNullOrWhiteSpace(output.TreeHash)
                || string.IsNullOrWhiteSpace(output.WorkPlanId)
                || string.IsNullOrWhiteSpace(output.MergeEffectId)
                || (approvedCandidate is null
                    ? record.CurrentOutputRevisionId is not null
                    : record.CurrentOutputRevisionId != approvedCandidate.RevisionId)
                || record.Diff is null
                || (mutation.TreeHash is not null && mutation.TreeHash != output.TreeHash)
                || (record.ExecutableWorkflowPinRequired
                    && string.IsNullOrWhiteSpace(record.ExecutableWorkflowContentDigest)))
                throw new RunOutputRevisionUnavailableException("invalid_collective_manifest");
            var predecessor = approvedCandidate?.RevisionId
                ?? await db.RunOutputRevisions.AsNoTracking()
                    .Where(r => r.RunId == record.RunId && r.LifecycleGeneration < record.LifecycleGeneration)
                    .OrderByDescending(r => r.LifecycleGeneration)
                    .Select(r => r.RevisionId).FirstOrDefaultAsync(ct);
            var bytes = RunOutputRevision.EncodeDiff(record.Diff);
            var revisionId = Guid.NewGuid().ToString("N");
            db.RunOutputRevisions.Add(new RunOutputRevisionRecord
            {
                RevisionId = revisionId,
                SchemaVersion = RunOutputRevision.CollectiveSchemaVersion,
                RunId = record.RunId,
                LifecycleGeneration = record.LifecycleGeneration,
                WorkflowDigest = record.ExecutableWorkflowContentDigest,
                ManifestIncomplete = record.ExecutableWorkflowContentDigest is null,
                TreeHash = output.TreeHash,
                DiffSha256 = RunOutputRevision.Sha256(bytes),
                PredecessorRevisionId = predecessor,
                OutputKind = "collective",
                MergedCommitHash = output.CommitHash,
                WorkPlanId = output.WorkPlanId,
                MergeEffectId = output.MergeEffectId,
                AcceptedNoChange = output.AcceptedNoChange,
                DiffBytes = bytes,
                TreeContent = output.TreeContent,
                TreeContentSha256 = RunOutputRevision.Sha256(output.TreeContent),
                CreatedAt = mutation.Outcome.OccurredAt
            });
            record.CurrentOutputRevisionId = revisionId;
        }
        if (mutation.NoChangeOutput is { } receipt)
        {
            if (mutation.CollectiveOutput is not null || mutation.Outcome.Status != RunStatus.Completed
                || string.IsNullOrWhiteSpace(receipt.CommitHash) || string.IsNullOrWhiteSpace(receipt.TreeHash)
                || record.CurrentOutputRevisionId is not null
                || !string.IsNullOrEmpty(record.Diff)
                || (record.ExecutableWorkflowPinRequired
                    && string.IsNullOrWhiteSpace(record.ExecutableWorkflowContentDigest)))
                throw new RunOutputRevisionUnavailableException("invalid_no_change_manifest");
            RunOutputTree.Decode(receipt.TreeContent);
            var predecessor = await db.RunOutputRevisions.AsNoTracking()
                .Where(r => r.RunId == record.RunId)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => r.RevisionId).FirstOrDefaultAsync(ct);
            var revisionId = Guid.NewGuid().ToString("N");
            db.RunOutputRevisions.Add(new RunOutputRevisionRecord
            {
                RevisionId = revisionId,
                SchemaVersion = RunOutputRevision.NoChangeSchemaVersion,
                RunId = record.RunId,
                LifecycleGeneration = record.LifecycleGeneration,
                WorkflowDigest = record.ExecutableWorkflowContentDigest,
                ManifestIncomplete = record.ExecutableWorkflowContentDigest is null,
                TreeHash = receipt.TreeHash,
                DiffSha256 = RunOutputRevision.Sha256([]),
                PredecessorRevisionId = predecessor,
                OutputKind = "no_change",
                MergedCommitHash = receipt.CommitHash,
                AcceptedNoChange = true,
                DiffBytes = [],
                TreeContent = receipt.TreeContent,
                TreeContentSha256 = RunOutputRevision.Sha256(receipt.TreeContent),
                CreatedAt = mutation.Outcome.OccurredAt,
            });
            record.CurrentOutputRevisionId = revisionId;
            record.TreeHash = receipt.TreeHash;
            record.MergedCommitHash = receipt.CommitHash;
            record.Diff = "";
        }
        db.TerminalRunOutcomes.Add(new TerminalRunOutcomeRecord
        {
            RunId = record.RunId,
            LifecycleGeneration = record.LifecycleGeneration,
            Status = mutation.Outcome.Status.ToApiString(),
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
        var now = DateTimeOffset.UtcNow;
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id
                && r.PreviewPublicationLeaseOwner == ownerId
                && r.PreviewPublicationLeaseUntil > now
                && !terminalStatuses.Contains(r.Status))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)leaseUntil), ct);
        return rows > 0;
    }

    public async Task<bool> TryReserveTerminalPreviewCleanupAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var now = DateTimeOffset.UtcNow;
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && terminalStatuses.Contains(r.Status)
                && (r.PreviewPublicationLeaseOwner == null || r.PreviewPublicationLeaseOwner == ownerId)
                && (r.PreviewPublicationLeaseUntil == null || r.PreviewPublicationLeaseUntil <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseOwner, ownerId)
                .SetProperty(r => r.PreviewPublicationLeaseUntil, (DateTimeOffset?)leaseUntil), ct);
        return rows > 0;
    }

    public async Task<bool> TryReserveUnclaimedPreviewCleanupAsync(
        RunId runId, string ownerId, DateTimeOffset leaseUntil, CancellationToken ct = default)
    {
        var id = runId.ToString();
        var terminalStatuses = new[] { "merged", "declined", "failed", "completed", "merge_failed", "assemble_ready", "cancelled" };
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Runs
            .Where(r => r.RunId == id && !terminalStatuses.Contains(r.Status)
                && r.PreviewPublicationLeaseOwner == null && r.PreviewPublicationLeaseUntil == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.PreviewPublicationLeaseOwner, ownerId)
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
                .SetProperty(r => r.CurrentOutputRevisionId, (string?)null)
                .SetProperty(r => r.ApprovedOutputRevisionId, (string?)null)
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
        ExecutionInputRequired = r.ExecutionInputRequired,
        ExecutionInputSourceCommitHash = r.ExecutionInputSourceCommitHash,
        ExecutionInputCommitHash = r.ExecutionInputCommitHash,
        ExecutionInputCompositeId = r.ExecutionInputCompositeId,
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
        ApprovedOutputRevisionId = r.ApprovedOutputRevisionId,
        CurrentOutputRevisionId = r.CurrentOutputRevisionId,
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
        ExecutionInputRequired = r.ExecutionInputRequired,
        ExecutionInputSourceCommitHash = r.ExecutionInputSourceCommitHash,
        ExecutionInputCommitHash = r.ExecutionInputCommitHash,
        ExecutionInputCompositeId = r.ExecutionInputCompositeId,
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
        ApprovedOutputRevisionId = r.ApprovedOutputRevisionId,
        CurrentOutputRevisionId = r.CurrentOutputRevisionId,
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
