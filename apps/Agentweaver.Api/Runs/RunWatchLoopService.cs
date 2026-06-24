using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Domain;

using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Api.Runs;

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
    private readonly IServiceScopeFactory _scopeFactory;
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
        IServiceScopeFactory scopeFactory,
        ILogger<RunWatchLoopService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
    }

    /// <summary>
    /// Starts a supervised watch loop for the given streaming run. The loop monitors
    /// workflow events and translates them to SSE events + SQLite status updates.
    /// </summary>
    public void StartWatching(
        string runId,
        StreamingRun streamingRun,
        RunStreamEntry entry,
        string ownerUser,
        int expectedGeneration,
        CancellationToken runCt)
    {
        _ = Task.Run(async () =>
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCt, _appStopping);
            try
            {
                await WatchAsync(runId, streamingRun, entry, ownerUser, expectedGeneration, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App is shutting down — not an error.
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested && !_appStopping.IsCancellationRequested)
            {
                _logger.LogInformation("Old workflow abandoned for run {RunId}", runId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watch loop failed for run {RunId}; transitioning to Failed", runId);
                await FailRunSafeAsync(runId, entry, "watch_loop_error").ConfigureAwait(false);
            }
        }, _appStopping);
    }

    private async Task WatchAsync(
        string runId,
        StreamingRun streamingRun,
        RunStreamEntry entry,
        string ownerUser,
        int expectedGeneration,
        CancellationToken ct)
    {
        await foreach (var evt in streamingRun.WatchStreamAsync(ct))
        {
            switch (evt)
            {
                // Per-executor lifecycle (MAF) -> live workflow.step events. This makes the graph
                // dynamic for nodes WITHOUT a dedicated self-emitter (e.g. the child assemble-ready
                // terminal). Nodes with richer dedicated emissions (agent/rai/merge/scribe self-emit
                // from their executors; review is HITL-driven) are skipped by TryBuildExecutorStepEvent
                // so we never double-emit or clobber their statuses.
                case ExecutorInvokedEvent invoked:
                    EmitExecutorStep(runId, entry, invoked.ExecutorId, "started");
                    break;

                case ExecutorCompletedEvent completed:
                    EmitExecutorStep(runId, entry, completed.ExecutorId, "completed");
                    break;

                case ExecutorFailedEvent failed:
                    EmitExecutorStep(runId, entry, failed.ExecutorId, "failed");
                    break;

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

                    entry.RecordNext(EventTypes.ReviewRequested, new
                    {
                        tree_hash = reviewReq?.TreeHash,
                        request_id = rie.Request.RequestId
                    });
                    entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "started", label = "Review", timestamp_utc = DateTimeOffset.UtcNow.ToString("O") });
                    break;

                case WorkflowOutputEvent woe:
                        var isTerminal = await HandleTerminalOutputAsync(runId, woe, entry, expectedGeneration, ct).ConfigureAwait(false);
                        if (isTerminal)
                        {
                            _registry.Abandon(runId);
                            _factory.DeleteCheckpoints(runId);
                            _factory.ClearRunExecutorMeta(runId);
                            return;
                        }
                        // Non-terminal (e.g. leaked blocked output): preserve registry + checkpoints
                        // so the run can still be resumed/reviewed. Let the watch loop continue.
                        break;
            }
        }
    }

    /// <summary>
    /// Logical nodes whose <c>workflow.step</c> lifecycle is owned by a dedicated, richer emitter, so
    /// the generic MAF-event translator must NOT also emit for them (double-emit / status clobber):
    /// agent, rai, merge, scribe self-emit from their executors (including revise/skipped/failed
    /// nuances MAF lifecycle cannot express); review is driven by the HITL RequestInfoEvent + the
    /// terminal handlers below.
    /// </summary>
    private static readonly HashSet<string> DedicatedStepNodes =
        new(StringComparer.Ordinal) { "agent", "rai", "merge", "scribe", "review", "policy-rai", "policy-rubberduck", "policy-human-review" };

    private void EmitExecutorStep(string runId, RunStreamEntry entry, string executorId, string status)
    {
        if (!_factory.TryGetExecutorMeta(runId, executorId, out var meta))
            return;

        // Cheap, optional human-readable context for the one node this currently lights up. Never
        // do expensive work to compute a message (the frontend handles its absence).
        string? message = meta.LogicalNodeId == "assemble-ready"
            ? status switch
            {
                "started" => "Preparing child result for assembly",
                "completed" => "Child result ready for assembly",
                _ => null,
            }
            : null;

        var payload = TryBuildExecutorStepEvent(meta, status, message);
        if (payload is not null)
            entry.RecordNext(EventTypes.WorkflowStep, payload);
    }

    /// <summary>
    /// Pure translation of a MAF executor lifecycle transition into a <c>workflow.step</c> payload
    /// (or <c>null</c> when no event should be emitted). Returns <c>null</c> for unknown/hidden
    /// executors and for <see cref="DedicatedStepNodes"/> (owned by richer dedicated emitters).
    /// Extracted as a static method so the mapping is unit-testable without driving a workflow.
    /// </summary>
    internal static object? TryBuildExecutorStepEvent(ExecutorNodeMeta? meta, string status, string? message = null)
    {
        if (meta is null || meta.Hidden)
            return null;
        if (DedicatedStepNodes.Contains(meta.LogicalNodeId))
            return null;

        var timestampUtc = DateTimeOffset.UtcNow.ToString("O");
        return message is null
            ? new { step = meta.LogicalNodeId, status, label = meta.DisplayLabel, timestamp_utc = timestampUtc }
            : new { step = meta.LogicalNodeId, status, label = meta.DisplayLabel, timestamp_utc = timestampUtc, message };
    }

    /// <summary>
    /// Processes a workflow terminal output event. Returns true if the output is genuinely
    /// terminal (merged, merge_failed, no_changes, declined, content_safety) and the run
    /// should be cleaned up. Returns false for non-terminal leaked outputs (blocked) so the
    /// watch loop preserves the registry entry and checkpoints for recovery.
    /// </summary>
    internal async Task<bool> HandleTerminalOutputAsync(
        string runId,
        WorkflowOutputEvent woe,
        RunStreamEntry entry,
        int expectedGeneration,
        CancellationToken ct)
    {
        if (entry.Generation != expectedGeneration)
        {
            _logger.LogWarning(
                "Ignoring terminal output from stale workflow generation for run {RunId}. ExpectedGeneration={ExpectedGeneration} ActualGeneration={ActualGeneration}",
                runId, expectedGeneration, entry.Generation);
            return false;
        }

        var parsedRunId = RunId.Parse(runId);

        if (woe.Is<MergeOutput>(out var mergeOutput))
        {
            if (mergeOutput.Status == "merged")
            {
                // Guardrail 3: conditional update — skip if already terminal.
                await _runStore.TrySetTerminalStatusAsync(
                    parsedRunId, RunStatus.Merged, DateTimeOffset.UtcNow, mergeOutput.MergeResult, CancellationToken.None).ConfigureAwait(false);

                entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "completed", label = "Review", timestamp_utc = DateTimeOffset.UtcNow.ToString("O") });
                entry.RecordNext(EventTypes.ReviewApproved, new { });
                entry.RecordNext(EventTypes.MergeCompleted, new { merged_commit_hash = mergeOutput.MergeResult, merge_mode = mergeOutput.MergeMode });

                _streamStore.Complete(runId);
                _ = _factory.PersistRunEventsAsync(runId);
                _ = FirePostRunScribeAsync(runId);
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

            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "completed", label = "Review", timestamp_utc = DateTimeOffset.UtcNow.ToString("O") });
            entry.RecordNext(EventTypes.ReviewApproved, new { });
            entry.RecordNext(EventTypes.MergeFailed, new { reason = mergeOutput.MergeResult });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            return true;
        }

        if (woe.Is<NoChangesOutput>(out _))
        {
            // No-changes runs must not leak worktrees (Issue 5).
            // Cleanup before status update ensures pollers see a clean directory.
            await CleanupWorktreeAsync(parsedRunId, runId).ConfigureAwait(false);

            await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Completed, DateTimeOffset.UtcNow, "no_changes", CancellationToken.None).ConfigureAwait(false);

            entry.RecordNext(EventTypes.RunCompleted, new { result = "no_changes" });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            _ = FirePostRunScribeAsync(runId);
            return true;
        }

        // Coordinator CHILD run (ParentRunId != null) assemble-ready terminal (B1).
        // The child completed agent + RAI; it does NOT run its own review gate, merge, or scribe.
        // Persist the produced tree hash + worktree branch (the coordinator's hand-off contract),
        // emit run.assemble_ready on the child's existing stream, and preserve the worktree so the
        // coordinator can collect/assemble it in Phase 3. No scribe, no merge, no cleanup.
        if (woe.Is<AssembleReadyOutput>(out var assembleReady))
        {
            await _runStore.SetAssembleReadyAsync(
                parsedRunId,
                assembleReady.TreeHash ?? string.Empty,
                assembleReady.WorktreeBranch ?? string.Empty,
                assembleReady.Diff ?? string.Empty,
                assembleReady.StepCount,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(false);

            var child = await _runStore.GetAsync(parsedRunId, CancellationToken.None).ConfigureAwait(false);
            entry.RecordNext(EventTypes.RunAssembleReady, new
            {
                runId,
                subtaskId = child?.SubtaskId,
                parentRunId = child?.ParentRunId,
                worktreeBranch = assembleReady.WorktreeBranch,
                treeHash = assembleReady.TreeHash,
                hasChanges = assembleReady.HasChanges,
                stepCount = assembleReady.StepCount,
                raiSafetyFlagged = assembleReady.RaiSafetyFlagged,
            });

            // Emit an explicit no-changes signal when the worker produced nothing so the coordinator
            // and the UI can surface it clearly (the reviewer must not be sent to an empty diff with
            // no explanation — they need to know this subtask wrote no files to the repository).
            if (!assembleReady.HasChanges)
            {
                entry.RecordNext(EventTypes.RunNoChangesProduced, new
                {
                    runId,
                    subtaskId = child?.SubtaskId,
                    parentRunId = child?.ParentRunId,
                    message = "This subtask completed without writing any deliverables to the repository.",
                });
            }

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            return true;
        }

        if (woe.Is<DeclinedOutput>())
        {
            await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Declined, DateTimeOffset.UtcNow, null, CancellationToken.None).ConfigureAwait(false);

            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "declined", label = "Review", timestamp_utc = DateTimeOffset.UtcNow.ToString("O") });
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "merge", status = "skipped", label = "Merge", timestamp_utc = DateTimeOffset.UtcNow.ToString("O") });
            entry.RecordNext(EventTypes.ReviewDeclined, new { });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
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

            entry.RecordNext(EventTypes.RunFailed, new { reason = "content_safety" });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            return true;
        }

        // Unknown output type — treat as non-terminal to avoid data loss.
        _logger.LogWarning(
            "Unrecognized WorkflowOutputEvent type for run {RunId}; treating as non-terminal", runId);
        return false;
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

            entry.RecordNext(EventTypes.RunFailed, new { reason });
            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition run {RunId} to Failed state", runId);
        }
        finally
        {
            _registry.Abandon(runId);
            _factory.ClearRunExecutorMeta(runId);
        }
    }
}
