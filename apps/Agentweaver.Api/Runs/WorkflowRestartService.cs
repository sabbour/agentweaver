using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

using RunStatus = Agentweaver.Domain.RunStatus;
using WfRunStatus = Microsoft.Agents.AI.Workflows.RunStatus;

namespace Agentweaver.Api.Runs;

/// <summary>
/// On process startup, recovers runs that were active when the process died.
/// Uses MAF checkpoints to resume workflow runs at the review gate without
/// re-executing the agent turn. Replaces RunOrchestrator.RestartRecoveryAsync.
/// </summary>
public sealed class WorkflowRestartService
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly PendingRequestStore _pendingStore;
    private readonly RunWorkflowFactory _factory;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly RunWatchLoopService _watchLoop;
    private readonly ILogger<WorkflowRestartService> _logger;

    public WorkflowRestartService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        RunWorkflowFactory factory,
        IWorktreeOperations worktreeOps,
        RunWatchLoopService watchLoop,
        ILogger<WorkflowRestartService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _watchLoop = watchLoop;
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
            await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
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

            var checkpointInfo = _factory.GetLatestCheckpoint(runIdStr);
            if (checkpointInfo is null)
            {
                // No checkpoint — cannot resume via MAF. Auto-expire runs older than 24 hours
                // to prevent stale dev/test runs accumulating forever on every restart.
                if (DateTimeOffset.UtcNow - run.StartedAt > TimeSpan.FromHours(24))
                {
                    _logger.LogWarning(
                        "Auto-expiring stale no-checkpoint AwaitingReview run {RunId} (age={Age:g}); failing run",
                        run.Id, DateTimeOffset.UtcNow - run.StartedAt);
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
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
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
                    continue;
                }

                if (run.WorktreeBranch is null)
                {
                    _logger.LogError(
                        "WorktreeBranch missing for recovered AwaitingReview run {RunId}; failing run",
                        run.Id);
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
                    continue;
                }

                if (run.TreeHash is null)
                {
                    _logger.LogError(
                        "TreeHash missing for recovered AwaitingReview run {RunId}; failing run",
                        run.Id);
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
                    continue;
                }

                // Fail-closed: null means the worktree is unreadable/corrupt.
                var currentNoCheckpointHash = _worktreeOps.GetTreeHash(run.WorktreePath);
                if (currentNoCheckpointHash is null || !string.Equals(currentNoCheckpointHash, run.TreeHash, StringComparison.Ordinal))
                {
                    _logger.LogError(
                        "Worktree tree hash mismatch for recovered run {RunId}: expected={Expected} actual={Actual}; failing run",
                        run.Id, run.TreeHash, currentNoCheckpointHash);
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
                    continue;
                }

                // All prerequisites satisfied — emit synthetic review.requested so SSE clients
                // unblock. The /review endpoint handles runs without a live workflow via
                // ExecuteDirectReviewAsync, so approve/decline still works for these.
                entry.RecordNext(EventTypes.ReviewRequested, new { tree_hash = run.TreeHash, recovered = true });
                _logger.LogInformation(
                    "Recovered AwaitingReview run {RunId} without checkpoint; emitted synthetic review.requested for SSE clients.",
                    run.Id);
                continue;
            }

            // Guardrail 1: Validate worktree before resuming.
            if (run.WorktreePath is null || !_worktreeOps.WorktreeExists(run.WorktreePath))
            {
                _logger.LogError("Worktree missing for run {RunId} at {Path}; failing run", run.Id, run.WorktreePath);
                await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                _streamStore.Complete(runIdStr);
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
                    await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                    _streamStore.Complete(runIdStr);
                    continue;
                }
            }

            try
            {
                // Create the per-run CTS before resuming so the same token reaches both
                // the agent execution and the registry's Abandon path.
                var runCts = new CancellationTokenSource();
                var streamingRun = await _factory.ResumeAsync(checkpointInfo, runCts.Token).ConfigureAwait(false);
                var runCt = _registry.Register(runIdStr, streamingRun, runCts);

                // Re-populate PendingRequestStore from the resumed run's status.
                var status = await streamingRun.GetStatusAsync(ct).ConfigureAwait(false);
                if (status == WfRunStatus.PendingRequests)
                {
                    // The workflow is paused at the request port. We need to extract the
                    // pending request. Watch the stream briefly for a RequestInfoEvent.
                    // The resumed run should immediately emit it.
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    try
                    {
                        await foreach (var evt in streamingRun.WatchStreamAsync(cts.Token))
                        {
                            if (evt is RequestInfoEvent rie)
                            {
                                _pendingStore.Set(runIdStr, rie.Request, run.SubmittingUser);
                                break;
                            }
                        }
                    }
                    catch (OperationCanceledException) { /* timeout is acceptable */ }
                }

                // Start the supervised watch loop.
                _watchLoop.StartWatching(runIdStr, streamingRun, entry, run.SubmittingUser, runCt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resume workflow for run {RunId}; failing run", run.Id);
                await _runStore.UpdateStatusAsync(run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                _streamStore.Complete(runIdStr);
            }
        }
    }
}
