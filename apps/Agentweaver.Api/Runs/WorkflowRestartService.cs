using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

using RunStatus = Agentweaver.Domain.RunStatus;
using WfRunStatus = Microsoft.Agents.AI.Workflows.RunStatus;
using DomainRun = Agentweaver.Domain.Run;

namespace Agentweaver.Api.Runs;

/// <summary>
/// On process startup, recovers runs that were active when the process died.
/// Uses MAF checkpoints to resume workflow runs at the review gate without
/// re-executing the agent turn. Replaces RunOrchestrator.RestartRecoveryAsync.
/// </summary>
public sealed class WorkflowRestartService
{
    private readonly IRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly PendingRequestStore _pendingStore;
    private readonly RunWorkflowFactory _factory;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly RunWatchLoopService _watchLoop;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WorkflowRestartService> _logger;

    public WorkflowRestartService(
        IRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        RunWorkflowFactory factory,
        IWorktreeOperations worktreeOps,
        RunWatchLoopService watchLoop,
        IServiceScopeFactory scopeFactory,
        ILogger<WorkflowRestartService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _watchLoop = watchLoop;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        // 1. Fail stranded InProgress runs (agent was mid-execution; non-replayable).
        var inProgress = await _runStore.GetByStatusAsync(RunStatus.InProgress, ct).ConfigureAwait(false);
        foreach (var run in inProgress)
        {
            // A coordinator (parent) run is intentionally left InProgress while it dispatches children
            // and runs collective assembly (its stream stays open across that window). Those engines
            // are NOT MAF-checkpointed (D3 — service-driven), but every bit of their state is persisted
            // in the work plan, so they are recovered separately by
            // CoordinatorRunService.RecoverInterruptedRunsAsync (invoked right after this sweep). Leave
            // the run InProgress here so that recovery can re-arm the correct engine.
            if (run.ParentRunId is null && string.Equals(run.AgentName, "Coordinator", StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "Deferring interrupted coordinator run {RunId} to coordinator restart recovery", run.Id);
                continue;
            }

            _logger.LogWarning("Failing stranded InProgress run {RunId}", run.Id);
            await FailRecoveredRunAsync(run, "stranded_in_progress", entry: null, cleanupWorktree: true, ct: ct)
                .ConfigureAwait(false);
        }

        // 2. Revert Committing -> AwaitingReview (commit was started but did not complete).
        // A crash after CommitChanges but before ExecuteMergeAsync leaves the run in Committing.
        // Update tree_hash from the current worktree HEAD so the user can retry /commit.
        var committing = await _runStore.GetByStatusAsync(RunStatus.Committing, ct).ConfigureAwait(false);
        foreach (var run in committing)
        {
            _logger.LogWarning("Reverting interrupted commit for run {RunId} back to awaiting_review", run.Id);
            string? recoveredTreeHash = null;
            if (run.WorktreePath is not null && _worktreeOps.WorktreeExists(run.WorktreePath))
                recoveredTreeHash = _worktreeOps.GetTreeHash(run.WorktreePath);
            var reverted = await _runStore.TryRevertCommittingAsync(run.Id, recoveredTreeHash, CancellationToken.None).ConfigureAwait(false);
            if (!reverted)
                _logger.LogWarning("TryRevertCommittingAsync was a no-op for run {RunId} — status may have changed concurrently", run.Id);
        }

        // 3. Revert Merging -> AwaitingReview (merge did not complete).
        var merging = await _runStore.GetByStatusAsync(RunStatus.Merging, ct).ConfigureAwait(false);
        foreach (var run in merging)
        {
            _logger.LogWarning("Reverting interrupted merge for run {RunId} back to awaiting_review", run.Id);
            await _runStore.RevertMergingAsync(run.Id, CancellationToken.None).ConfigureAwait(false);
        }

        // 4. Resume AwaitingReview runs from checkpoint.
        var awaiting = await _runStore.GetByStatusAsync(RunStatus.AwaitingReview, ct).ConfigureAwait(false);
        foreach (var run in awaiting)
        {
            var runIdStr = run.Id.ToString();
            var entry = _streamStore.Create(runIdStr, run.SubmittingUser);
            entry.MarkAwaitingReview();

            var checkpointInfo = await _factory.GetLatestCheckpointAsync(runIdStr, ct).ConfigureAwait(false);
            if (checkpointInfo is null)
            {
                // No checkpoint — cannot resume via MAF. Auto-expire runs older than 24 hours
                // to prevent stale dev/test runs accumulating forever on every restart.
                if (DateTimeOffset.UtcNow - run.StartedAt > TimeSpan.FromHours(24))
                {
                    _logger.LogWarning(
                        "Auto-expiring stale no-checkpoint AwaitingReview run {RunId} (age={Age:g}); failing run",
                        run.Id, DateTimeOffset.UtcNow - run.StartedAt);
                    await FailRecoveredRunAsync(run, "stale_no_checkpoint", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                // Before emitting a synthetic review.requested, all prerequisites must pass.
                // These mirror the checks in ExecuteDirectReviewAsync so we never surface an approve action that is
                // guaranteed to 500 on the /review endpoint.

                if (run.WorktreePath is null || !_worktreeOps.WorktreeExists(run.WorktreePath))
                {
                    _logger.LogError(
                        "Worktree missing for recovered AwaitingReview run {RunId} at {Path}; failing run",
                        run.Id, run.WorktreePath);
                    await FailRecoveredRunAsync(run, "recovered_worktree_missing", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                if (run.WorktreeBranch is null)
                {
                    _logger.LogError(
                        "WorktreeBranch missing for recovered AwaitingReview run {RunId}; failing run",
                        run.Id);
                    await FailRecoveredRunAsync(run, "recovered_worktree_branch_missing", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                if (run.TreeHash is null)
                {
                    _logger.LogError(
                        "TreeHash missing for recovered AwaitingReview run {RunId}; failing run",
                        run.Id);
                    await FailRecoveredRunAsync(run, "recovered_tree_hash_missing", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                // Fail-closed: null means the worktree is unreadable/corrupt.
                var currentNoCheckpointHash = _worktreeOps.GetTreeHash(run.WorktreePath);
                if (currentNoCheckpointHash is null || !string.Equals(currentNoCheckpointHash, run.TreeHash, StringComparison.Ordinal))
                {
                    _logger.LogError(
                        "Worktree tree hash mismatch for recovered run {RunId}: expected={Expected} actual={Actual}; failing run",
                        run.Id, run.TreeHash, currentNoCheckpointHash);
                    await FailRecoveredRunAsync(run, "recovered_tree_hash_mismatch", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }

                // All prerequisites satisfied — emit synthetic review.requested so SSE clients
                // unblock. The /review endpoint handles runs without a live workflow via
                // ExecuteDirectReviewAsync, so approve/decline still works for these.
                await RecordRecoveryEventAsync(
                    runIdStr, entry, EventTypes.ReviewRequested, new { tree_hash = run.TreeHash, recovered = true },
                    CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation(
                    "Recovered AwaitingReview run {RunId} without checkpoint; emitted synthetic review.requested for SSE clients.",
                    run.Id);
                continue;
            }

            // Guardrail 1: Validate worktree before resuming.
            if (run.WorktreePath is null || !_worktreeOps.WorktreeExists(run.WorktreePath))
            {
                _logger.LogError("Worktree missing for run {RunId} at {Path}; failing run", run.Id, run.WorktreePath);
                await FailRecoveredRunAsync(run, "recovered_worktree_missing", entry, cleanupWorktree: false, ct: CancellationToken.None)
                    .ConfigureAwait(false);
                continue;
            }

            if (run.TreeHash is not null)
            {
                var currentTreeHash = _worktreeOps.GetTreeHash(run.WorktreePath);
                // Fail-closed: null means the worktree is unreadable/corrupt (FIX 2).
                if (currentTreeHash is null || !string.Equals(currentTreeHash, run.TreeHash, StringComparison.Ordinal))
                {
                    _logger.LogError("Worktree tree hash mismatch for run {RunId}: expected={Expected} actual={Actual}; failing run",
                        run.Id, run.TreeHash, currentTreeHash);
                    await FailRecoveredRunAsync(run, "recovered_tree_hash_mismatch", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            try
            {
                // Create the per-run CTS before resuming so the same token reaches both
                // the agent execution and the registry's Abandon path.
                var runCts = new CancellationTokenSource();
                var ctsRegistered = false;
                try
                {
                    var streamingRun = await _factory.ResumeAsync(checkpointInfo, runCts.Token).ConfigureAwait(false);
                    var runCt = _registry.Register(runIdStr, streamingRun, runCts);
                    ctsRegistered = true;

                    // Start the supervised watch loop.
                    _watchLoop.StartWatching(runIdStr, streamingRun, entry, run.SubmittingUser, runCt);
                }
                catch
                {
                    if (ctsRegistered)
                        _registry.Abandon(runIdStr);
                    else
                        runCts.Dispose();
                    throw;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume workflow for run {RunId}; failing run", run.Id);
                await FailRecoveredRunAsync(run, "workflow_resume_failed", entry, cleanupWorktree: false, ct: CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task FailRecoveredRunAsync(
        DomainRun run,
        string reason,
        RunStreamEntry? entry,
        bool cleanupWorktree,
        CancellationToken ct)
    {
        var runId = run.Id.ToString();
        var changed = await _runStore.TrySetTerminalStatusAsync(
            run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, reason, ct).ConfigureAwait(false);
        if (!changed)
        {
            _logger.LogWarning(
                "Recovery failure transition skipped for run {RunId}; status already terminal or changed concurrently",
                run.Id);
            return;
        }

        entry ??= _streamStore.Get(runId) ?? _streamStore.Create(runId, run.SubmittingUser);
        await RecordRecoveryEventAsync(runId, entry, EventTypes.RunFailed, new { reason }, ct)
            .ConfigureAwait(false);
        _streamStore.Complete(runId);
        _ = FirePostRunScribeAsync(runId);

        if (cleanupWorktree)
            CleanupWorktreeSafe(run);
    }

    private async Task RecordRecoveryEventAsync(
        string runId,
        RunStreamEntry entry,
        string eventType,
        object payload,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var maxSequence = await db.RunEvents
            .Where(e => e.RunId == runId)
            .Select(e => (int?)e.Sequence)
            .MaxAsync(ct)
            .ConfigureAwait(false) ?? 0;
        var sequence = maxSequence + 1;

        db.RunEvents.Add(new RunEventRecord
        {
            RunId = runId,
            Sequence = sequence,
            EventType = eventType,
            PayloadJson = JsonSerializer.Serialize(payload),
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        entry.Record(new RunEvent(sequence, eventType, payload));
    }

    private void CleanupWorktreeSafe(DomainRun run)
    {
        if (string.IsNullOrEmpty(run.WorktreePath) || string.IsNullOrEmpty(run.WorktreeBranch))
            return;

        try
        {
            _worktreeOps.RemoveWorktree(run.RepositoryPath, run.WorktreePath, run.WorktreeBranch);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clean up stranded worktree for run {RunId}", run.Id);
        }
    }

    private async Task FirePostRunScribeAsync(string runId)
    {
        try
        {
            var run = await _runStore.GetAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
            if (run is null) return;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<PostRunScribeService>();
            await service.RunAsync(run).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PostRunScribe fire-and-forget failed for run {RunId}", runId);
        }
    }
}
