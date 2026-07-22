using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Api.Sandbox;
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
    private readonly IRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly PendingRequestStore _pendingStore;
    private readonly RunWorkflowFactory _factory;
    private readonly IWorktreeOperations _worktreeOps;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunLeaseStore _leaseStore;
    private readonly ILogger<RunWatchLoopService> _logger;
    private readonly CancellationToken _appStopping;
    private readonly TimeSpan _watchLoopTimeout;
    private static readonly TimeSpan LeaseTtl = TimeSpan.FromMinutes(5);
    private readonly string _workerId = $"{Environment.MachineName}/{Guid.NewGuid():N}";
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string OwnerId, long FencingToken)> _activeLeases = new();
    // Pod-per-run lifecycle — null when AgentExecutionMode=in-api or not in Kubernetes.
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    private readonly SandboxRuntimeOptions _sandboxRuntime;

    public RunWatchLoopService(
        IRunStore runStore,
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        PendingRequestStore pendingStore,
        RunWorkflowFactory factory,
        IWorktreeOperations worktreeOps,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        IRunLeaseStore leaseStore,
        ILogger<RunWatchLoopService> logger,
        IAgentHostPodLifecycle? podLifecycle = null,
        IOptions<SandboxRuntimeOptions>? sandboxRuntime = null)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _registry = registry;
        _pendingStore = pendingStore;
        _factory = factory;
        _worktreeOps = worktreeOps;
        _scopeFactory = scopeFactory;
        _leaseStore = leaseStore;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
        _watchLoopTimeout = ResolveWatchLoopTimeout(configuration);
        _podLifecycle = podLifecycle;
        _sandboxRuntime = sandboxRuntime?.Value ?? new SandboxRuntimeOptions();
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
        CancellationToken runCt)
    {
        _ = Task.Run(async () =>
        {
            var (claimed, fencingToken) = await _leaseStore.TryClaimAsync(
                runId, _workerId, LeaseTtl, _appStopping).ConfigureAwait(false);
            if (!claimed)
            {
                _logger.LogInformation(
                    "Run {RunId}: lease already held by another worker; skipping (multi-replica dedup)", runId);
                return;
            }

            _activeLeases[runId] = (_workerId, fencingToken);

            using var renewCts = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(runCt, _appStopping);
            _ = Task.Run(async () =>
            {
                var interval = TimeSpan.FromMilliseconds(LeaseTtl.TotalMilliseconds / 2);
                while (!renewCts.Token.IsCancellationRequested)
                {
                    try { await Task.Delay(interval, renewCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    var renewed = await _leaseStore.TryRenewAsync(
                        runId, _workerId, fencingToken, LeaseTtl, CancellationToken.None).ConfigureAwait(false);
                    if (!renewed)
                        _logger.LogWarning(
                            "Lease renewal failed for run {RunId} (token={Token}); lease may have been stolen",
                            runId, fencingToken);
                }
            }, renewCts.Token);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCt, _appStopping);
            // The watchdog bounds only ACTIVE execution spans, not human-decision-wait time. It is
            // armed/paused from inside WatchAsync as the run moves between active execution and being
            // parked at a RequestPort awaiting a human — so a run parked for a human decision can no
            // longer be failed by the wall-clock timeout (see ExecutionWatchdog remarks). Genuine
            // stuck/runaway ACTIVE execution is still caught: while armed, an active span exceeding
            // _watchLoopTimeout cancels linkedCts exactly as before.
            var watchdog = new ExecutionWatchdog(linkedCts, _watchLoopTimeout);
            var durableStopMonitor = MonitorDurableSteeringStopAsync(runId, entry, linkedCts.Token);
            try
            {
                await WatchAsync(runId, streamingRun, entry, ownerUser, watchdog, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App is shutting down — not an error.
            }
            catch (OperationCanceledException) when (runCt.IsCancellationRequested && !_appStopping.IsCancellationRequested)
            {
                _logger.LogInformation("Old workflow abandoned for run {RunId}", runId);
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !_appStopping.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Watch loop timed out for run {RunId}: an active execution phase exceeded {Timeout} " +
                    "without yielding a terminal event (human-decision-wait time is not counted); transitioning to Failed",
                    runId, _watchLoopTimeout);
                await FailRunSafeAsync(runId, entry, "watch_loop_timeout").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Watch loop failed for run {RunId}; transitioning to Failed", runId);
                await FailRunSafeAsync(runId, entry, "watch_loop_error").ConfigureAwait(false);
            }
            finally
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);
                try { await durableStopMonitor.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Durable steering stop monitor failed for run {RunId}", runId);
                }
                renewCts.Cancel();
                _activeLeases.TryRemove(runId, out _);
                await _leaseStore.ReleaseAsync(runId, _workerId, fencingToken, CancellationToken.None).ConfigureAwait(false);
            }
        }, _appStopping);
    }

    private async Task PollDeferredReviewDecisionsAsync(
        string runId, StreamingRun streamingRun, RunStreamEntry entry, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_registry.Get(runId) is null)
                return;

            WorkflowReviewDecision? decision;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

                var row = await db.DeferredDecisions
                    .FirstOrDefaultAsync(d => d.RunId == runId, ct)
                    .ConfigureAwait(false);
                if (row is null)
                    continue;

                decision = JsonSerializer.Deserialize<WorkflowReviewDecision>(row.DecisionJson, JsonDefaults.Options);
                var deleted = await db.DeferredDecisions
                    .Where(d => d.RunId == runId)
                    .ExecuteDeleteAsync(ct)
                    .ConfigureAwait(false);
                if (deleted == 0 || decision is null)
                    continue;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error polling deferred review decisions for run {RunId}", runId);
                continue;
            }

            var pending = await _pendingStore.TryRemoveAsync(runId, ct).ConfigureAwait(false);
            if (pending is null)
            {
                _logger.LogWarning("Deferred review decision for run {RunId}: pending gate already consumed", runId);
                return;
            }

            RecordDeferredReviewDecisionEvents(runId, entry, decision);

            try
            {
                await streamingRun.SendResponseAsync(pending.Request.CreateResponse(decision)).ConfigureAwait(false);
                _logger.LogInformation("Deferred review decision for run {RunId} applied on owner replica", runId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deferred review SendResponseAsync failed for run {RunId}; transitioning to failed", runId);
                await FailRunSafeAsync(runId, entry, "send_response_failed").ConfigureAwait(false);
            }

            return;
        }
    }

    private async Task MonitorDurableSteeringStopAsync(string runId, RunStreamEntry entry, CancellationToken ct)
    {
        if (!RunId.TryParse(runId, out var parsedRunId))
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var run = await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false);
            if (run is not { Status: RunStatus.Failed, Result: "steering_stop" })
                continue;

            if (!entry.HasEventType(EventTypes.RunCancelled))
                entry.RecordNext(EventTypes.RunCancelled, new { reason = "steering_stop" });
            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            // #350: terminate the remote AgentHost pod, not just the local CancellationTokenSource —
            // pod deletion is a cluster-wide action so this is safe/idempotent even when the steering
            // stop's own release already ran on a different API replica.
            await ReleaseAgentHostPodOnTerminalSafeAsync(runId).ConfigureAwait(false);
            _registry.Abandon(runId);
            _logger.LogInformation("Run {RunId}: durable steering stop observed; local workflow abandoned", runId);
            return;
        }
    }

    private static void RecordDeferredReviewDecisionEvents(
        string runId, RunStreamEntry entry, WorkflowReviewDecision decision)
    {
        var reviewTs = DateTimeOffset.UtcNow.ToString("O");
        if (decision.Approved)
        {
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "completed", label = "Review", timestamp_utc = reviewTs, reviewer = decision.ReviewedBy });
            entry.RecordNext(EventTypes.MergeStarted, new { });
        }
        else if (decision.RequestChanges)
        {
            entry.ClearAwaitingReview();
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "revise", label = "Review", timestamp_utc = reviewTs, reviewer = decision.ReviewedBy });
            entry.RecordNext(EventTypes.ReviewChangesRequested, new { });
            entry.RecordNext(EventTypes.RevisionStarted, new { });
        }
        else
        {
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "completed", label = "Review", timestamp_utc = reviewTs, reviewer = decision.ReviewedBy });
        }
    }

    private async Task WatchAsync(
        string runId,
        StreamingRun streamingRun,
        RunStreamEntry entry,
        string ownerUser,
        ExecutionWatchdog watchdog,
        CancellationToken ct)
    {
        // #331 — build/preview subtask terminal-emission gap: the agent turn itself can complete
        // (agent.turn.end observed, #242's guard satisfied) and post-turn bookkeeping (commit/diff)
        // can succeed — a real, verified deliverable — yet the MAF workflow stream still ends
        // WITHOUT ever emitting the child-assemble-ready WorkflowOutputEvent (observed live: the
        // stream closed ~2s after ExecutorCompletedEvent(agent) fired, before the conditional edge's
        // downstream hop was observed). Track the last SUCCESSFUL AgentTurnOutput (TerminalFailureReason
        // null) from the "agent" node's ExecutorCompletedEvent so the stream-end fallback below can
        // recover a genuine success instead of discarding verified work as
        // `watch_stream_completed_without_terminal_event`.
        AgentTurnOutput? lastSuccessfulAgentTurnOutput = null;

        // Initialise the watchdog for THIS run's current phase. If the run is resumed straight into an
        // awaiting-human state (e.g. restored by WorkflowRestartService while parked at a review gate),
        // start PAUSED so the human-wait span is never counted; otherwise arm for the first active
        // span. The per-active-span deadline is (re)armed on resume and paused on suspend below.
        if (await _pendingStore.GetAsync(runId, ct).ConfigureAwait(false) is not null)
            watchdog.Pause();
        else
            watchdog.Arm();

        await foreach (var evt in streamingRun.WatchStreamAsync(ct))
        {
            // Any event means the workflow is actively executing again. If we were parked awaiting a
            // human decision, this is the resume signal (the operator responded and the workflow
            // re-emitted): the human-wait span just ended, so re-arm the active-phase watchdog for the
            // new span of real execution. Genuinely stuck ACTIVE spans remain bounded.
            if (watchdog.IsPaused)
                watchdog.Arm();

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
                    if (completed.Data is AgentTurnOutput { TerminalFailureReason: null } agentTurnOutput &&
                        _factory.TryGetExecutorMeta(runId, completed.ExecutorId, out var completedMeta) &&
                        completedMeta.LogicalNodeId == "agent")
                    {
                        lastSuccessfulAgentTurnOutput = agentTurnOutput;
                    }
                    break;

                case ExecutorFailedEvent failed:
                    EmitExecutorStep(runId, entry, failed.ExecutorId, "failed");
                    // STRUCTURAL ROOT CAUSE (in-place steering revision wedge): an executor throw
                    // halts the MAF workflow and yields NO WorkflowOutputEvent. The trimmed
                    // coordinator CHILD graph (agent -> child-assemble-ready) has no failure->terminal
                    // edge, so without this the stream would simply END with no terminal and the run
                    // would be failed as `watch_stream_completed_without_terminal_event` — fragile,
                    // uninformative, and only via the stream-end fallback. Terminalize a CHILD run as
                    // a VISIBLE Failure immediately so: (a) the watcher ALWAYS produces a terminal
                    // after an executor failure (never a hung stream), and (b) the subtask is marked
                    // Failed, so the coordinator's failed-target path consciously re-dispatches the
                    // revision (steering feedback preserved) rather than losing the work. Scoped to
                    // child runs: the child pipeline is strictly linear, so an executor failure there
                    // is definitively terminal (no fan-out/recovery node could still emit output).
                    if (await IsChildRunAsync(runId, ct).ConfigureAwait(false))
                    {
                        await FailRunSafeAsync(
                            runId, entry, $"child_executor_failed:{failed.ExecutorId}").ConfigureAwait(false);
                        return;
                    }
                    break;

                case RequestInfoEvent rie:
                    // Guard: if PendingRequestStore already has this run (e.g., restored by
                    // WorkflowRestartService before this consumer reads the event), skip to
                    // avoid double-processing. WatchStreamAsync is single-consumer per run;
                    // WorkflowRestartService only reads briefly on startup to repopulate.
                    if (await _pendingStore.GetAsync(runId, ct).ConfigureAwait(false) is null)
                    {
                        // Workflow paused at review-gate.
                        await _pendingStore.SetAsync(runId, rie.Request, ownerUser, ct).ConfigureAwait(false);

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
                    }

                    _ = PollDeferredReviewDecisionsAsync(runId, streamingRun, entry, ct);

                    // Q3 hybrid: checkpoint-and-release the AgentHost pod when the workflow
                    // suspends at a RequestPort gate, if ReleasePodOnSuspend=true (spec §9/§12.2).
                    // Resume correctness relies on the DB-backed ICheckpointStore + serialized
                    // session blob — not on A2A contextId state (§4.7.3).
                    await ReleasePodOnSuspendSafeAsync(runId).ConfigureAwait(false);

                    // Parked at a RequestPort awaiting the accountable human — STOP the watchdog clock
                    // so elapsed human-decision-wait time can never fail the run (standing rule: a run
                    // must not die because a human was not around to respond; mirrors AssemblyReviewGate's
                    // indefinite-safe wait). runCt/_appStopping stay live on the linked CTS, so run
                    // cancellation and host shutdown remain immediate — only the wall-clock timeout is
                    // suspended, and it is re-armed at the top of the loop when the workflow resumes.
                    watchdog.Pause();
                    break;

                case WorkflowOutputEvent woe:
                        var isTerminal = await HandleTerminalOutputAsync(runId, woe, entry, ct).ConfigureAwait(false);
                        if (isTerminal)
                        {
                            await StopPortForwardsSafeAsync(runId).ConfigureAwait(false);
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

        // #331 recovery: the agent turn completed cleanly (agent.turn.end observed, post-turn commit
        // succeeded, TerminalFailureReason null) but the stream still ended before the child graph's
        // conditional edge produced the child-assemble-ready WorkflowOutputEvent. For a coordinator
        // CHILD run this is unambiguous — the trimmed child graph's ONLY possible outcomes after a
        // successful agent turn are child-assemble-ready or child-turn-failed, and we already know
        // the turn did not fail. Recover the real, verified work as assemble-ready instead of
        // discarding it via the generic stream-ended fallback (which previously cascaded into
        // `assembly_blocked: ineligible_subtasks` for a subtask that had genuinely succeeded).
        // #331 recovery: the agent turn completed cleanly (agent.turn.end observed, post-turn commit
        // succeeded, TerminalFailureReason null) but the stream still ended before the child graph's
        // conditional edge produced the child-assemble-ready WorkflowOutputEvent. For a coordinator
        // CHILD run this is unambiguous — the trimmed child graph's ONLY possible outcomes after a
        // successful agent turn are child-assemble-ready or child-turn-failed, and we already know
        // the turn did not fail. Recover the real, verified work as assemble-ready instead of
        // discarding it via the generic stream-ended fallback (which previously cascaded into
        // `assembly_blocked: ineligible_subtasks` for a subtask that had genuinely succeeded).
        if (await TryRecoverChildAssembleReadyOnStreamEndAsync(
                runId, entry, lastSuccessfulAgentTurnOutput, ct).ConfigureAwait(false))
        {
            return;
        }

        _logger.LogWarning(
            "Workflow stream ended for run {RunId} without a terminal event; transitioning to Failed",
            runId);
        await FailRunSafeAsync(runId, entry, "watch_stream_completed_without_terminal_event").ConfigureAwait(false);
    }

    /// <summary>
    /// #331 — build/preview subtask terminal-emission gap: recovers a coordinator CHILD run whose
    /// agent turn completed successfully (no <see cref="AgentTurnOutput.TerminalFailureReason"/>) but
    /// whose workflow stream ended before the trimmed child graph's conditional edge produced the
    /// <c>child-assemble-ready</c> <see cref="WorkflowOutputEvent"/>. Root and non-child runs are left
    /// to the generic <c>watch_stream_completed_without_terminal_event</c> fallback — their graphs
    /// have additional stages (RAI/review/merge/scribe) after the agent turn, so a bare successful
    /// agent turn is NOT sufficient evidence the run is actually done.
    /// Returns true when the run was terminalized here (caller must stop watching); false when there
    /// is nothing to recover (caller falls through to the generic failure).
    /// </summary>
    internal async Task<bool> TryRecoverChildAssembleReadyOnStreamEndAsync(
        string runId,
        RunStreamEntry entry,
        AgentTurnOutput? lastSuccessfulAgentTurnOutput,
        CancellationToken ct)
    {
        if (lastSuccessfulAgentTurnOutput is not { } recoveredOutput)
            return false;

        if (!await IsChildRunAsync(runId, ct).ConfigureAwait(false))
            return false;

        _logger.LogWarning(
            "Workflow stream ended for run {RunId} without a terminal event, but the agent turn " +
            "completed successfully (no TerminalFailureReason) on a coordinator child run; recovering " +
            "as assemble-ready instead of discarding verified work (issue #331)",
            runId);

        var recoveredEvent = new WorkflowOutputEvent(
            new AssembleReadyOutput(
                RunId: recoveredOutput.RunId,
                WorktreeBranch: recoveredOutput.WorktreeBranch,
                TreeHash: recoveredOutput.TreeHash,
                Diff: recoveredOutput.Diff,
                HasChanges: !string.IsNullOrEmpty(recoveredOutput.Diff),
                StepCount: recoveredOutput.StepCount,
                RaiSafetyFlagged: recoveredOutput.ContentSafetyFlagged),
            "child-assemble-ready");

        if (!await HandleTerminalOutputAsync(runId, recoveredEvent, entry, ct).ConfigureAwait(false))
            return false;

        await StopPortForwardsSafeAsync(runId).ConfigureAwait(false);
        _registry.Abandon(runId);
        _factory.DeleteCheckpoints(runId);
        _factory.ClearRunExecutorMeta(runId);
        return true;
    }

    private static TimeSpan ResolveWatchLoopTimeout(IConfiguration configuration)
    {
        const string primaryKey = "Runs:WatchLoopTimeout";
        const string fallbackKey = "RunWatchLoop:Timeout";
        var configured = configuration[primaryKey] ?? configuration[fallbackKey];
        return TimeSpan.TryParse(configured, out var timeout) && timeout > TimeSpan.Zero
            ? timeout
            : TimeSpan.FromHours(4);
    }

    /// <summary>
    /// Releases the AgentHost pod for the given run on workflow suspension (Q3 hybrid).
    /// Best-effort: logs and swallows exceptions so a pod-release failure never disrupts
    /// the watch loop's HITL handling.
    /// </summary>
    private async Task ReleasePodOnSuspendSafeAsync(string runId)
    {
        if (_podLifecycle is null || !_sandboxRuntime.IsPodPerRun || !_sandboxRuntime.ReleasePodOnSuspend)
            return;

        try
        {
            await _podLifecycle.ReleaseAgentHostPodAsync(runId).ConfigureAwait(false);
            _logger.LogInformation(
                "RunWatchLoopService: AgentHost pod released on suspension for run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RunWatchLoopService: failed to release AgentHost pod on suspension for run {RunId} (best-effort)",
                runId);
        }
    }

    /// <summary>
    /// Logical nodes whose <c>workflow.step</c> lifecycle is owned by a dedicated, richer emitter, so
    /// the generic MAF-event translator must NOT also emit for them (double-emit / status clobber):
    /// agent, rai, merge, push-pr, and scribe self-emit from their executors (including
    /// revise/skipped/failed nuances MAF lifecycle cannot express); review is driven by the HITL
    /// RequestInfoEvent + the terminal handlers below.
    /// </summary>
    private static readonly HashSet<string> DedicatedStepNodes =
        new(StringComparer.Ordinal) { "agent", "rai", "merge", "push-pr", "scribe", "review", "policy-rai", "policy-rubberduck", "policy-human-review" };

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
        CancellationToken ct)
    {
        var parsedRunId = RunId.Parse(runId);

        if (_activeLeases.TryGetValue(runId, out var activeLease))
        {
            var isOwner = await _leaseStore.IsLeaseOwnerAsync(
                runId, activeLease.OwnerId, activeLease.FencingToken, CancellationToken.None).ConfigureAwait(false);
            if (!isOwner)
            {
                _logger.LogWarning(
                    "Terminal handler skipped for {RunId}: lease stolen (fencing token mismatch)", runId);
                return false;
            }
        }

        var now = DateTimeOffset.UtcNow;
        var currentRun = await _runStore.GetAsync(parsedRunId, CancellationToken.None).ConfigureAwait(false);

        if (woe.Is<MergeOutput>(out var mergeOutput))
        {
            if (mergeOutput.Status == "merged")
            {
                // Guardrail 3: conditional update — skip if already terminal.
                var changed = await _runStore.TrySetTerminalStatusAsync(
                    parsedRunId, RunStatus.Merged, now, mergeOutput.MergeResult, CancellationToken.None).ConfigureAwait(false);

                EmitTerminalMetrics(currentRun, now, "succeeded", changed: changed);
                entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "completed", label = "Review", timestamp_utc = now.ToString("O") });
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

            if (mergeOutput.Status == "completed")
            {
                var changed = await _runStore.TrySetTerminalStatusAsync(
                    parsedRunId, RunStatus.Completed, now, mergeOutput.MergeResult ?? "completed", CancellationToken.None).ConfigureAwait(false);

                EmitTerminalMetrics(currentRun, now, "succeeded", changed: changed);
                entry.RecordNext(EventTypes.RunCompleted, new { result = mergeOutput.MergeResult ?? "completed" });

                _streamStore.Complete(runId);
                _ = _factory.PersistRunEventsAsync(runId);
                _ = FirePostRunScribeAsync(runId);
                return true;
            }

            // merge_failed (conflict, lock failure, internal error)
            var mergeFailedChanged = await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.MergeFailed, now, mergeOutput.MergeResult, CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(currentRun, now, "failed", "merge_failed", mergeFailedChanged);
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "completed", label = "Review", timestamp_utc = now.ToString("O") });
            entry.RecordNext(EventTypes.ReviewApproved, new { });
            entry.RecordNext(EventTypes.MergeFailed, new { reason = mergeOutput.MergeResult });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            _ = FirePostRunScribeAsync(runId);
            return true;
        }

        if (woe.Is<NoChangesOutput>(out _))
        {
            // No-changes runs must not leak worktrees (Issue 5).
            // Cleanup before status update ensures pollers see a clean directory.
            await CleanupWorktreeAsync(parsedRunId, runId).ConfigureAwait(false);

            var changed = await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Completed, now, "no_changes", CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(currentRun, now, "succeeded", changed: changed);
            entry.RecordNext(EventTypes.RunCompleted, new { result = "no_changes" });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            _ = FirePostRunScribeAsync(runId);
            return true;
        }

        // Coordinator CHILD run (ParentRunId != null) assemble-ready terminal (B1).
        // The child completed its agent turn; it does NOT run its own RAI, review gate, merge, or scribe.
        // Persist the produced tree hash + worktree branch (the coordinator's hand-off contract),
        // emit run.assemble_ready on the child's existing stream, and preserve the worktree so the
        // coordinator can collect/assemble it in Phase 3. No scribe, no merge, no cleanup.
        if (woe.Is<AssembleReadyOutput>(out var assembleReady))
        {
            var changed = await _runStore.SetAssembleReadyAsync(
                parsedRunId,
                assembleReady.TreeHash ?? string.Empty,
                assembleReady.WorktreeBranch ?? string.Empty,
                assembleReady.Diff ?? string.Empty,
                assembleReady.StepCount,
                now,
                CancellationToken.None).ConfigureAwait(false);
            EmitTerminalMetrics(currentRun, now, "succeeded", changed: changed);

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

        // Root/full-pipeline graph-native failure terminal. Preserve the structured reason emitted
        // by AgentTurnExecutor instead of falling through to the generic stream-ended fallback.
        if (woe.Is<AgentTurnFailedOutput>(out var turnFailed))
        {
            var changed = await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Failed, now, turnFailed.Reason, CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(currentRun, now, "failed", turnFailed.Reason, changed);
            if (!entry.HasEventType(EventTypes.RunFailed))
            {
                entry.RecordNext(EventTypes.RunFailed, new
                {
                    reason = turnFailed.Reason,
                    errorCode = turnFailed.Reason,
                    message = turnFailed.Message,
                    evidence = turnFailed.Evidence,
                    retryable = turnFailed.Retryable,
                });
            }

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            return true;
        }

        // Coordinator child graph-native failure terminal. Known agent/provider/transport/workspace
        // and post-turn failures arrive with their original machine-readable reason, so the watcher
        // never collapses them to child_executor_failed:agent-turn or stream-ended-without-terminal.
        if (woe.Is<ChildTurnFailedOutput>(out var childFailed))
        {
            var changed = await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Failed, now, childFailed.Reason, CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(currentRun, now, "failed", childFailed.Reason, changed);
            if (!entry.HasEventType(EventTypes.RunFailed))
            {
                entry.RecordNext(EventTypes.RunFailed, new
                {
                    reason = childFailed.Reason,
                    errorCode = childFailed.Reason,
                    message = childFailed.Message,
                    evidence = childFailed.Evidence,
                    retryable = childFailed.Retryable,
                });
            }

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            return true;
        }

        if (woe.Is<DeclinedOutput>())
        {
            var changed = await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Declined, now, null, CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(currentRun, now, "failed", "declined", changed);
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "review", status = "declined", label = "Review", timestamp_utc = now.ToString("O") });
            entry.RecordNext(EventTypes.WorkflowStep, new { step = "merge", status = "skipped", label = "Merge", timestamp_utc = now.ToString("O") });
            entry.RecordNext(EventTypes.ReviewDeclined, new { });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            _ = FirePostRunScribeAsync(runId);
            return true;
        }

        if (woe.Is<ContentSafetyFailedOutput>())
        {
            // Content-safety-failed runs must not leak worktrees (Issue 5).
            // Cleanup must complete BEFORE status is set to "failed" so any poller
            // that detects the terminal status observes a clean worktree directory.
            await CleanupWorktreeAsync(parsedRunId, runId).ConfigureAwait(false);

            var changed = await _runStore.TrySetTerminalStatusAsync(
                parsedRunId, RunStatus.Failed, now, "content_safety", CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(currentRun, now, "failed", "content_safety", changed);
            entry.RecordNext(EventTypes.RunFailed, new { reason = "content_safety" });

            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            _ = FirePostRunScribeAsync(runId);
            return true;
        }

        _logger.LogError(
            "Unrecognized WorkflowOutputEvent type for run {RunId}; transitioning to Failed", runId);
        await FailRunSafeAsync(runId, entry, "unknown_workflow_output").ConfigureAwait(false);
        return true;
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

    private async Task<bool> IsChildRunAsync(string runId, CancellationToken ct)
    {
        try
        {
            var run = await _runStore.GetAsync(RunId.Parse(runId), ct).ConfigureAwait(false);
            return run?.ParentRunId is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine child-run status for {RunId}; treating as non-child", runId);
            return false;
        }
    }

    private async Task FailRunSafeAsync(string runId, RunStreamEntry entry, string reason)
    {
        if (_activeLeases.TryGetValue(runId, out var lease))
        {
            var isOwner = await _leaseStore.IsLeaseOwnerAsync(
                runId, lease.OwnerId, lease.FencingToken, CancellationToken.None).ConfigureAwait(false);
            if (!isOwner)
            {
                _logger.LogWarning(
                    "FailRun skipped for {RunId}: lease no longer owned by this worker (fencing token mismatch)", runId);
                return;
            }
        }

        try
        {
            var failedAt = DateTimeOffset.UtcNow;
            var run = await _runStore.GetAsync(RunId.Parse(runId), CancellationToken.None).ConfigureAwait(false);
            var changed = await _runStore.TrySetTerminalStatusAsync(
                RunId.Parse(runId), RunStatus.Failed, failedAt, reason, CancellationToken.None).ConfigureAwait(false);

            EmitTerminalMetrics(run, failedAt, "failed", reason, changed);
            if (!entry.HasEventType(EventTypes.RunFailed))
                entry.RecordNext(EventTypes.RunFailed, new { reason });
            _streamStore.Complete(runId);
            _ = _factory.PersistRunEventsAsync(runId);
            _ = FirePostRunScribeAsync(runId);
            await StopPortForwardsSafeAsync(runId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to transition run {RunId} to Failed state", runId);
        }
        finally
        {
            // #350: a run reaching this generic failure path (e.g.
            // watch_stream_completed_without_terminal_event, child_executor_failed) is terminal and
            // NEVER coming back — StopPortForwardsSafeAsync above only unregisters local bookkeeping
            // (IPodNameRegistry, port-forward sessions), it does NOT stop the remote AgentHost pod.
            // Without this the underlying process can keep executing tool calls and emitting
            // tool.approval_required for a run the system already considers dead.
            await ReleaseAgentHostPodOnTerminalSafeAsync(runId).ConfigureAwait(false);
            _registry.Abandon(runId);
            _factory.ClearRunExecutorMeta(runId);
        }
    }

    /// <summary>
    /// Releases the AgentHost pod for a run transitioning to a terminal Cancelled/Failed state
    /// (#350 — cancelled/failed run doesn't reliably tear down its AgentHost/sandbox process).
    /// Unlike <see cref="ReleasePodOnSuspendSafeAsync"/> (gated by <c>ReleasePodOnSuspend</c>, used
    /// only for HITL suspend/resume checkpointing), a terminal transition must ALWAYS tear the pod
    /// down when running pod-per-run: the run is never resuming, so leaving the pod alive lets a
    /// detached turn keep executing tool calls / requesting new approvals long after the run record
    /// is already terminal. Best-effort: logs and swallows exceptions (mirrors the release helpers in
    /// CoordinatorRunService/CoordinatorDispatchService/CoordinatorAssemblyService) so a release
    /// failure never blocks run finalization — the periodic AgentHostReaperService remains the
    /// belt-and-suspenders backstop for anything this misses.
    /// </summary>
    private async Task ReleaseAgentHostPodOnTerminalSafeAsync(string runId)
    {
        if (_podLifecycle is null || !_sandboxRuntime.IsPodPerRun)
            return;

        try
        {
            await _podLifecycle.ReleaseAgentHostPodAsync(runId).ConfigureAwait(false);
            _logger.LogInformation(
                "RunWatchLoopService: AgentHost pod released on terminal transition for run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "RunWatchLoopService: failed to release AgentHost pod on terminal transition for run {RunId} (best-effort)",
                runId);
        }
    }

    private Task StopPortForwardsSafeAsync(string runId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetService<IPodNameRegistry>()?.Unregister(runId);

            var portForwardService = scope.ServiceProvider.GetService<PortForwardService>();
            if (portForwardService is null)
                return Task.CompletedTask;

            foreach (var session in portForwardService.ListForRun(runId))
            {
                if (!portForwardService.Stop(runId, session.SessionId))
                {
                    _logger.LogWarning(
                        "Port-forward session {SessionId} for run {RunId} could not be stopped",
                        session.SessionId, runId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop port-forward sessions for run {RunId}", runId);
        }

        return Task.CompletedTask;
    }

    private static void EmitTerminalMetrics(
        Agentweaver.Domain.Run? run,
        DateTimeOffset endedAt,
        string status,
        string? errorType = null,
        bool changed = true)
    {
        if (!changed || run is null)
            return;

        var tags = BuildRunTags(run, ("status", status));
        AgentWeaverMetrics.RunsCompleted.Add(1, tags);
        AgentWeaverMetrics.ActiveRuns.Add(-1, BuildRunTags(run));

        if (run.StartedAt != default)
        {
            var durationMs = Math.Max(0d, (endedAt - run.StartedAt).TotalMilliseconds);
            AgentWeaverMetrics.RunDuration.Record(durationMs, BuildRunTags(run, ("status", status)));
        }

        if (string.Equals(status, "failed", StringComparison.Ordinal))
            AgentWeaverMetrics.RunErrors.Add(1, BuildRunTags(run, ("error_type", errorType ?? "failed")));
    }

    internal static KeyValuePair<string, object?>[] BuildRunTags(
        Agentweaver.Domain.Run run,
        params (string Key, object? Value)[] extraTags)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("agent_name", run.AgentName ?? "unknown"),
            new("run_type", string.IsNullOrEmpty(run.ParentRunId) ? "coordinator" : "child"),
            new("run_id", run.Id.ToString()),
            new("run.id", run.Id.ToString()),
        };
        if (run.ProjectId is { } projectId)
            tags.Add(new("project.id", projectId.ToString()));
        if (!string.IsNullOrWhiteSpace(run.ParentRunId))
            tags.Add(new("parent_run_id", run.ParentRunId));
        foreach (var (key, value) in extraTags)
            tags.Add(new(key, value));
        return tags.ToArray();
    }

    /// <summary>
    /// Bounds only spans of <b>active</b> workflow/agent execution — never human-decision-wait time.
    /// The watch-loop watchdog exists to catch a genuinely stuck/runaway ACTIVE phase (a hung agent
    /// turn that never yields a terminal event), NOT to fail a run that is correctly parked at a
    /// RequestPort awaiting the accountable human. Standing product rule: a run must never die simply
    /// because a human was not around to respond — it may sleep indefinitely and must stay resumable
    /// (mirrors <see cref="Agentweaver.Api.Coordinator.AssemblyReviewGate"/>'s indefinite-safe wait).
    ///
    /// <para>Mechanism: the same linked <see cref="CancellationTokenSource"/> that already ties the
    /// watch loop to run-cancellation and host-shutdown carries the timeout. <see cref="Arm"/>
    /// schedules the cancel after one active-span timeout; <see cref="Pause"/> reschedules it to
    /// infinite (disabling the wall-clock deadline) while parked. Because only the timer is touched,
    /// the CTS's linked parents (run cancel / <c>ApplicationStopping</c>) still fire immediately — a
    /// paused watchdog delays only the timeout, never cancellation or shutdown.</para>
    /// </summary>
    internal sealed class ExecutionWatchdog(CancellationTokenSource linkedCts, TimeSpan activeTimeout)
    {
        /// <summary>True while the wall-clock timeout is suspended (run parked awaiting a human).</summary>
        public bool IsPaused { get; private set; } = true;

        /// <summary>Bounds the next span of active execution: cancels the linked CTS if it elapses.</summary>
        public void Arm()
        {
            IsPaused = false;
            linkedCts.CancelAfter(activeTimeout);
        }

        /// <summary>Suspends the wall-clock timeout indefinitely while the run is parked on a human
        /// decision. Cancellation from the CTS's linked parents is unaffected.</summary>
        public void Pause()
        {
            IsPaused = true;
            linkedCts.CancelAfter(Timeout.InfiniteTimeSpan);
        }
    }
}
