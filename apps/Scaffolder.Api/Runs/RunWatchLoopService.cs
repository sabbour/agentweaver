using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;

using RunStatus = Scaffolder.Domain.RunStatus;

namespace Scaffolder.Api.Runs;

/// <summary>
/// Manages supervised watch loops for workflow runs. On exception the run is
/// transitioned to Failed, SSE run.failed is emitted, and the run is removed
/// from the registry. Never fire-and-forget unsupervised (Guardrail 5).
/// </summary>
public sealed class RunWatchLoopService
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly PendingRequestStore _pendingStore;
    private readonly RunWorkflowFactory _factory;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly ILogger<RunWatchLoopService> _logger;
    private readonly CancellationToken _appStopping;

    public RunWatchLoopService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        RunWorkflowFactory factory,
        IWorktreeOperations worktreeOps,
        IHostApplicationLifetime lifetime,
        ILogger<RunWatchLoopService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    /// <summary>
    /// Starts a supervised watch loop for the given streaming run. The loop monitors
    /// workflow events and translates them to SSE events + SQLite status updates.
    /// </summary>
    public void StartWatching(string runId, StreamingRun streamingRun, RunStreamEntry entry, string ownerUser)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await WatchAsync(runId, streamingRun, entry, ownerUser, _appStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App is shutting down — not an error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watch loop failed for run {RunId}; transitioning to Failed", runId);
                await FailRunSafeAsync(runId, entry, "watch_loop_error").ConfigureAwait(false);
            }
        }, _appStopping);
    }

    private async Task WatchAsync(
        string runId, StreamingRun streamingRun, RunStreamEntry entry, string ownerUser, CancellationToken ct)
    {
        await foreach (var evt in streamingRun.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                case RequestInfoEvent rie:
                    // Guard: if PendingRequestStore already has this run (e.g., restored by
                    // WorkflowRestartService before this consumer reads the event), skip to
                    // avoid double-processing. WatchStreamAsync is single-consumer per run;
                    // WorkflowRestartService only reads briefly on startup to repopulate.
                    if (_pendingStore.Get(runId) is not null)
                        break;

                    // Workflow paused at review-gate.
                    _pendingStore.Set(runId, rie.Request, ownerUser);

                    // Update SQLite: InProgress -> AwaitingReview.
                    // Retrieve agent output from the request data for the review-ready update.
                    if (rie.Request.TryGetDataAs<WorkflowReviewRequest>(out var reviewReq))
                    {
                        await _runStore.UpdateReviewReadyAsync(
                            RunId.Parse(runId), reviewReq.TreeHash, reviewReq.Diff,
                            reviewReq.StepCount, CancellationToken.None).ConfigureAwait(false);
                    }

                    entry.MarkAwaitingReview();

                    var reviewSeq = entry.NextSequence();
                    entry.Record(new RunEvent(reviewSeq, EventTypes.ReviewRequested, new
                    {
                        tree_hash = reviewReq?.TreeHash,
                        request_id = rie.Request.RequestId
                    }));
                    break;

                case WorkflowOutputEvent woe:
                        var isTerminal = await HandleTerminalOutputAsync(runId, woe, entry, ct).ConfigureAwait(false);
                        if (isTerminal)
                        {
                            _registry.Remove(runId);
                            _factory.DeleteCheckpoints(runId);
                            return;
                        }
                        // Non-terminal (e.g. leaked blocked output): preserve registry + checkpoints
                        // so the run can still be resumed/reviewed. Let the watch loop continue.
                        break;
            }
        }
    }

    /// <summary>
    /// Processes a workflow terminal output event. Returns true if the output is genuinely
    /// terminal (merged, merge_failed, no_changes, declined, content_safety) and the run
    /// should be cleaned up. Returns false for non-terminal leaked outputs (blocked) so the
    /// watch loop preserves the registry entry and checkpoints for recovery.
    /// </summary>
    internal async Task<bool> HandleTerminalOutputAsync(
        string runId, WorkflowOutputEvent woe, RunStreamEntry entry, CancellationToken ct)
    {
        var parsedRunId = RunId.Parse(runId);

        if (woe.Is<MergeOutput>(out var mergeOutput))
        {
            if (mergeOutput.Status == "merged")
            {
                // Guardrail 3: conditional update — skip if already terminal.
                await _runStore.TrySetTerminalStatusAsync(
                    parsedRunId, RunStatus.Merged, DateTimeOffset.UtcNow, mergeOutput.MergeResult, CancellationToken.None).ConfigureAwait(false);

                var approvedSeq = entry.NextSequence();
                entry.Record(new RunEvent(approvedSeq, EventTypes.ReviewApproved, new { }));
                var mergedSeq = entry.NextSequence();
                entry.Record(new RunEvent(mergedSeq, EventTypes.MergeCompleted, new { merged_commit_hash = mergeOutput.MergeResult }));

                _streamStore.Complete(runId);
                return true;
            }

            if (mergeOutput.Status == "blocked")
            {
                // Defensive: blocked outputs re-enter the review gate via the workflow graph
                // and should never reach terminal output. If they do, log and leave the run
                // at awaiting_review (RevertMergeAsync already restored it) — do NOT emit
                // merge.failed so the run remains retriable. Do NOT clean up.
                _logger.LogWarning(
                    "Unexpected blocked MergeOutput reached terminal handler for run {RunId}; ignoring", runId);
                return false;
            }

            // merge_failed (conflict, lock failure, internal error)
            await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.MergeFailed, DateTimeOffset.UtcNow, mergeOutput.MergeResult, CancellationToken.None).ConfigureAwait(false);

            var approvedSeqF = entry.NextSequence();
            entry.Record(new RunEvent(approvedSeqF, EventTypes.ReviewApproved, new { }));
            var failedSeq = entry.NextSequence();
            entry.Record(new RunEvent(failedSeq, EventTypes.MergeFailed, new { reason = mergeOutput.MergeResult }));

            _streamStore.Complete(runId);
            return true;
        }

        if (woe.Is<NoChangesOutput>(out _))
        {
            // No-changes runs must not leak worktrees (Issue 5).
            // Cleanup before status update ensures pollers see a clean directory.
            await CleanupWorktreeAsync(parsedRunId, runId).ConfigureAwait(false);

            await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Completed, DateTimeOffset.UtcNow, "no_changes", CancellationToken.None).ConfigureAwait(false);

            var seq = entry.NextSequence();
            entry.Record(new RunEvent(seq, EventTypes.RunCompleted, new { result = "no_changes" }));

            _streamStore.Complete(runId);
            return true;
        }

        if (woe.Is<DeclinedOutput>())
        {
            await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Declined, DateTimeOffset.UtcNow, null, CancellationToken.None).ConfigureAwait(false);

            var seq = entry.NextSequence();
            entry.Record(new RunEvent(seq, EventTypes.ReviewDeclined, new { }));

            _streamStore.Complete(runId);
            return true;
        }

        if (woe.Is<ContentSafetyFailedOutput>())
        {
            // Content-safety-failed runs must not leak worktrees (Issue 5).
            // Cleanup must complete BEFORE status is set to "failed" so any poller
            // that detects the terminal status observes a clean worktree directory.
            await CleanupWorktreeAsync(parsedRunId, runId).ConfigureAwait(false);

            await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Failed, DateTimeOffset.UtcNow, "content_safety", CancellationToken.None).ConfigureAwait(false);

            var seq = entry.NextSequence();
            entry.Record(new RunEvent(seq, EventTypes.RunFailed, new { reason = "content_safety" }));

            _streamStore.Complete(runId);
            return true;
        }

        // Unknown output type — treat as non-terminal to avoid data loss.
        _logger.LogWarning(
            "Unrecognized WorkflowOutputEvent type for run {RunId}; treating as non-terminal", runId);
        return false;
    }

    private async Task CleanupWorktreeAsync(RunId parsedRunId, string runId)
    {
        try
        {
            var run = await _runStore.GetAsync(parsedRunId, CancellationToken.None).ConfigureAwait(false);
            if (run?.WorktreePath is not null && run.WorktreeBranch is not null)
            {
                _worktreeOps.RemoveWorktree(run.RepositoryPath, run.WorktreePath, run.WorktreeBranch);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Best-effort worktree cleanup failed for run {RunId}", runId);
        }
    }

    private async Task FailRunSafeAsync(string runId, RunStreamEntry entry, string reason)
    {
        try
        {
            await _runStore.TrySetTerminalStatusAsync(
                RunId.Parse(runId), RunStatus.Failed, DateTimeOffset.UtcNow, reason, CancellationToken.None).ConfigureAwait(false);

            var seq = entry.NextSequence();
            entry.Record(new RunEvent(seq, EventTypes.RunFailed, new { reason }));
            _streamStore.Complete(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition run {RunId} to Failed state", runId);
        }
        finally
        {
            _registry.Remove(runId);
        }
    }
}
