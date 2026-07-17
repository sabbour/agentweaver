using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
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
    private readonly IRunEventStream? _eventStream;
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
        ILogger<WorkflowRestartService> logger,
        IRunEventStream? eventStream = null)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _watchLoop = watchLoop;
        _scopeFactory = scopeFactory;
        _eventStream = eventStream;
        _logger = logger;
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        // 1. Fail stranded InProgress runs. Child turns stranded by a worker restart are safe to
        // redispatch as a fresh child: the coordinator owns their retry budget and will release
        // the old pod before dispatching. Root turns remain non-replayable.
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

            var retryableChildTransportFailure = run.ParentRunId is not null;
            var reason = retryableChildTransportFailure
                ? "a2a_transport_interrupted"
                : "stranded_in_progress";
            _logger.LogWarning(
                "Failing stranded InProgress run {RunId} (reason={Reason}, retryable={Retryable})",
                run.Id, reason, retryableChildTransportFailure);
            await FailRecoveredRunAsync(
                    run,
                    reason,
                    entry: null,
                    cleanupWorktree: true,
                    retryable: retryableChildTransportFailure,
                    ct: ct)
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
        foreach (var awaitingRun in awaiting)
        {
            // Mutable local shadow: reattach (P0-A, #246) may swap in a corrected WorktreePath/
            // WorktreeBranch mid-iteration; the foreach iteration variable itself can't be reassigned.
            var run = awaitingRun;
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
                    var reattached = await TryReattachWorktreeAsync(run, ct).ConfigureAwait(false);
                    if (reattached is not null)
                    {
                        run = reattached;
                    }
                    else
                    {
                        _logger.LogError(
                            "Worktree missing for recovered AwaitingReview run {RunId} at {Path}; failing run",
                            run.Id, run.WorktreePath);
                        await FailRecoveredRunAsync(run, "recovered_worktree_missing", entry, cleanupWorktree: false, ct: CancellationToken.None)
                            .ConfigureAwait(false);
                        continue;
                    }
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
                var currentNoCheckpointHash = _worktreeOps.GetTreeHash(run.WorktreePath!);
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
                var reattached = await TryReattachWorktreeAsync(run, ct).ConfigureAwait(false);
                if (reattached is not null)
                {
                    run = reattached;
                }
                else
                {
                    _logger.LogError("Worktree missing for run {RunId} at {Path}; failing run", run.Id, run.WorktreePath);
                    await FailRecoveredRunAsync(run, "recovered_worktree_missing", entry, cleanupWorktree: false, ct: CancellationToken.None)
                        .ConfigureAwait(false);
                    continue;
                }
            }

            if (run.TreeHash is not null)
            {
                var currentTreeHash = _worktreeOps.GetTreeHash(run.WorktreePath!);
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

    /// <summary>
    /// P0-A (GH #246): before terminalizing a recovered run purely because its worktree DIRECTORY is
    /// missing, attempt to reconstruct it from the durable run branch. A missing directory does not
    /// by itself mean lost work — a worker rollout/eviction wipes ephemeral PVC-backed worktree
    /// storage while the git branch (<c>agentweaver/&lt;runId&gt;</c>) and the DB run row persist.
    /// Delegates to <see cref="IWorktreeOperations.TryReattachWorktree"/>, which prunes any stale git
    /// admin entry and recreates the worktree checked out at the existing branch tip (idempotent —
    /// safe to call even if a concurrent recovery attempt already ran it). Since the reconstructed
    /// path is always derived deterministically from the run id and the configured worktree root, it
    /// matches the originally persisted <c>Run.WorktreePath</c> in the overwhelmingly common case; the
    /// best-effort <see cref="IRunStore.UpdateWorktreeAsync"/> call below only actually writes when the
    /// column was NULL to begin with (its guard is designed for first-time provisioning, e.g. the
    /// coordinator shared-orchestration worktree) — it is not a general path-correction primitive.
    /// Returns an updated <see cref="Run"/> reflecting the reattached path/branch for the CALLER to
    /// keep using for the rest of this recovery pass regardless of whether the DB write took effect.
    /// Existing tree-hash verification callers already perform is UNCHANGED — this only supplies a
    /// valid directory for that verification to run against; it never bypasses it. Returns null when
    /// reconstruction is impossible (e.g. the branch itself is gone), in which case the caller should
    /// proceed with its existing "recovered_worktree_missing" terminal-failure path.
    /// </summary>
    private async Task<DomainRun?> TryReattachWorktreeAsync(DomainRun run, CancellationToken ct)
    {
        var reattached = _worktreeOps.TryReattachWorktree(run.RepositoryPath, run.OriginatingBranch, run.Id.ToString());
        if (reattached is null) return null;

        var (worktreePath, branchName) = reattached.Value;
        _logger.LogWarning(
            "Reattached missing worktree for run {RunId} at '{Path}' from durable branch '{Branch}' (#246 P0-A)",
            run.Id, worktreePath, branchName);

        if (!string.Equals(worktreePath, run.WorktreePath, StringComparison.Ordinal)
            || !string.Equals(branchName, run.WorktreeBranch, StringComparison.Ordinal))
        {
            await _runStore.UpdateWorktreeAsync(run.Id, worktreePath, branchName, ct).ConfigureAwait(false);
        }

        return run with { WorktreePath = worktreePath, WorktreeBranch = branchName };
    }

    private async Task FailRecoveredRunAsync(
        DomainRun run,
        string reason,
        RunStreamEntry? entry,
        bool cleanupWorktree,
        CancellationToken ct,
        bool retryable = false)
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
        await RecordRecoveryEventAsync(runId, entry, EventTypes.RunFailed, new { reason, retryable }, ct)
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
        var stream = _eventStream;
        if (stream is null)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            stream = scope.ServiceProvider.GetService<IRunEventStream>();
        }

        int sequence;
        if (stream is null)
        {
            _logger.LogWarning(
                "Workflow recovery: no IRunEventStream available while recording {EventType} for run {RunId}; using in-memory fallback sequence only",
                eventType,
                runId);
            sequence = entry.RecordNext(eventType, payload);
            return;
        }

        sequence = await stream.AppendAsync(runId, new RunEvent(0, eventType, payload), ct).ConfigureAwait(false);
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
