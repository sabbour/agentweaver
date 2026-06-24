using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

using Run = Agentweaver.Domain.Run;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Re-dispatch seam used by Phase 3 collective assembly when a reviewer requests changes (D6). The
/// assembly service resolves this lazily (avoiding a constructor DI cycle) and re-runs the dispatch
/// engine over the reset frontier.
/// </summary>
public interface ICoordinatorDispatch
{
    /// <summary>Launches dispatch + observe for a coordinator run (fire-and-forget; idempotent).</summary>
    void StartDispatch(CoordinatorDispatchContext context);

    /// <summary>
    /// True while a dispatch loop is actively running for this coordinator run. Recovery paths use
    /// this as the SINGLE-WRITER guard: a parked-coordinator resume may only reset subtask rows when
    /// no loop is running (the loop is the sole writer of those rows while it is alive).
    /// </summary>
    bool IsDispatchActive(string coordinatorRunId);
}

/// <summary>
/// Feature 008 Phase 2 DISPATCH + OBSERVE engine. After the human confirms a coordinator outcome
/// spec and <see cref="CoordinatorOrchestratorExecutor"/> persists the work plan,
/// <see cref="CoordinatorRunService"/> hands the coordinator run off here. This service:
///
/// <list type="number">
/// <item>DISPATCHES the ready frontier of subtasks as real CHILD runs via
/// <see cref="RunOrchestrator.StartChildRunAsync"/> (the TRIMMED child pipeline, <c>isChild</c>),
/// tagged <c>ParentRunId</c> = coordinator run id and <c>SubtaskId</c>. Independent subtasks
/// dispatch in PARALLEL; a subtask whose dependencies are not yet assemble_ready/completed waits
/// (serial ordering). <see cref="Subtask.Status"/> advances pending -&gt; dispatched -&gt; running.</item>
/// <item>OBSERVES each child via the EXISTING run stream (the child's
/// <see cref="RunStreamEntry"/>, already populated by <see cref="RunWatchLoopService"/>). When a
/// child reaches assemble_ready / rai_flagged / failed, the subtask status is updated and any
/// now-unblocked dependents are dispatched, until every subtask is terminal.</item>
/// <item>EMITS, on the COORDINATOR run's stream, <c>subtask.*</c> lifecycle events and
/// <c>coordinator.topology</c> (a full snapshot at dispatch time, then a delta on every
/// transition) so the live topology view renders with no client-side computation.</item>
/// </list>
///
/// Phase 2 advances <see cref="WorkPlan.Status"/> planned -&gt; dispatching while children run; when
/// all subtasks reach a terminal state the plan moves to awaiting_assembly and hands off to Phase 3
/// collective assembly (<see cref="ICoordinatorAssembly"/>).
///
/// All EF writes happen on the single dispatch-loop task using a scoped
/// <see cref="MemoryDbContext"/> (the <see cref="IServiceScopeFactory"/> pattern), so parallel
/// child dispatch + observation never corrupt EF state. Observation tasks only READ the stream.
/// </summary>
public sealed class CoordinatorDispatchService : ICoordinatorDispatch
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunOrchestrator _orchestrator;
    private readonly CoordinatorSteeringQueue _steering;
    private readonly ICoordinatorAssembly _assembly;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunOptionsStore? _runOptions;
    private readonly ICoordinatorAutopilot? _autopilot;
    private readonly ILogger<CoordinatorDispatchService> _logger;
    private readonly CancellationToken _appStopping;

    /// <summary>
    /// How long an orphaned child run (terminal-less in the store, with NO live watch loop) may make
    /// no progress before the reconciling dispatch loop stall-fails its subtask instead of polling it
    /// forever. Configurable via <c>Coordinator:SubtaskStallTimeoutMinutes</c> (default 15 minutes).
    /// </summary>
    private readonly TimeSpan _stallTimeout;

    private readonly ConcurrentDictionary<string, byte> _active = new();

    public CoordinatorDispatchService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunOrchestrator orchestrator,
        CoordinatorSteeringQueue steering,
        ICoordinatorAssembly assembly,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<CoordinatorDispatchService> logger,
        IRunOptionsStore? runOptions = null,
        ICoordinatorAutopilot? autopilot = null,
        IConfiguration? configuration = null)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _orchestrator = orchestrator;
        _steering = steering;
        _assembly = assembly;
        _scopeFactory = scopeFactory;
        _runOptions = runOptions;
        _autopilot = autopilot;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;

        var stallMinutes = configuration?.GetValue("Coordinator:SubtaskStallTimeoutMinutes", 15.0) ?? 15.0;
        _stallTimeout = TimeSpan.FromMinutes(Math.Max(0, stallMinutes));
    }

    /// <summary>
    /// Launches dispatch + observe for a confirmed coordinator run on a supervised background task
    /// (mirrors <see cref="RunWatchLoopService.StartWatching"/>). Returns immediately. Idempotent:
    /// a second call for the same run id while one is active is a no-op.
    /// </summary>
    public void StartDispatch(CoordinatorDispatchContext context)
    {
        if (!_active.TryAdd(context.CoordinatorRunId, 0))
        {
            _logger.LogInformation(
                "Coordinator dispatch already active for run {RunId}; skipping", context.CoordinatorRunId);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await RunDispatchLoopAsync(context, _appStopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_appStopping.IsCancellationRequested)
            {
                // App shutting down — not an error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Coordinator dispatch loop failed for run {RunId}", context.CoordinatorRunId);
            }
            finally
            {
                _active.TryRemove(context.CoordinatorRunId, out _);
            }
        }, _appStopping);
    }

    /// <inheritdoc />
    public bool IsDispatchActive(string coordinatorRunId) => _active.ContainsKey(coordinatorRunId);

    // -----------------------------------------------------------------------
    // Dispatch + observe loop
    // -----------------------------------------------------------------------

    internal async Task RunDispatchLoopAsync(CoordinatorDispatchContext context, CancellationToken ct)
    {
        var (workPlanId, subtasks, edges) = await LoadPlanAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (workPlanId is null)
        {
            _logger.LogWarning(
                "Coordinator dispatch: no work plan for run {RunId}; nothing to dispatch", context.CoordinatorRunId);
            return;
        }

        var entry = _streamStore.Get(context.CoordinatorRunId);
        var statusById = subtasks.ToDictionary(s => s.Id, s => s.Status);
        var seq = new SeqCounter();

        // Advance the plan to dispatching and publish the FULL topology snapshot (reflecting the new
        // status) so the client can render the graph thin before any child has been launched.
        await SetWorkPlanStatusAsync(workPlanId.Value, WorkPlanStatus.Dispatching, ct).ConfigureAwait(false);
        var snapshotSubtasks = await ReloadSubtasksAsync(workPlanId.Value, ct).ConfigureAwait(false);
        entry?.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildSnapshot(
            context.CoordinatorRunId, workPlanId.Value, WorkPlanStatus.Dispatching, snapshotSubtasks, edges, seq.Current));
        await EmitCoordinatorGraphAsync(context.CoordinatorRunId, workPlanId.Value, ct).ConfigureAwait(false);

        if (subtasks.Count == 0)
        {
            _logger.LogInformation("Coordinator dispatch: work plan {WorkPlanId} has no subtasks", workPlanId.Value);
            return;
        }

        var inFlight = new Dictionary<int, Task<ChildResult>>();

        // Build a per-id lookup so DoSubtasksConflict can inspect Scope without a DB round-trip.
        var subtasksById = subtasks.ToDictionary(s => s.Id);

        // RECOVERY-AWARE RE-ARM. A re-armed loop (reconciler sweep, startup recovery, or a manual
        // steer-resume of an orphan) may find subtasks already dispatched/running from a PRIOR process
        // whose child runs have since reached a terminal state in the run store. Re-observe each so
        // ObserveChildAsync store-resolves the child (its in-memory stream entry may be gone after a
        // restart/eviction) and ApplyChildResultAsync advances the frontier, instead of the loop going
        // quiescent (inFlight empty -> break) and stranding them. The dispatch loop is the SINGLE
        // writer of these subtask rows and StartDispatch's _active guard confirms no other loop runs.
        var reArmed = subtasks
            .Where(s => (s.Status == SubtaskStatus.Dispatched || s.Status == SubtaskStatus.Running)
                && !string.IsNullOrEmpty(s.ChildRunId))
            .ToList();
        foreach (var s in reArmed)
            inFlight[s.Id] = ObserveChildAsync(context.CoordinatorRunId, s.Id, s.ChildRunId!, ct);

        if (reArmed.Count > 0)
        {
            _logger.LogInformation(
                "Coordinator dispatch re-armed for run {RunId}: re-observing {Count} in-flight subtask(s) [{Ids}] after recovery",
                context.CoordinatorRunId, reArmed.Count, string.Join(",", reArmed.Select(s => s.Id)));
            entry?.RecordNext(EventTypes.CoordinatorRecovered, new
            {
                reason = "dispatch_rearm",
                reObservedSubtaskIds = reArmed.Select(s => s.Id).ToList(),
            });
        }

        while (!ct.IsCancellationRequested)
        {
            // Dispatch the current frontier. Subtasks with non-overlapping file scopes run in
            // parallel; subtasks whose scopes conflict with any in-flight subtask run serially
            // (deferred until the conflicting in-flight task completes).
            foreach (var subtaskId in SubtaskFrontier.ReadyPending(statusById, edges))
            {
                if (inFlight.ContainsKey(subtaskId))
                    continue;

                // If any in-flight subtask conflicts with this one (overlapping or undeclared file
                // paths), defer it: running them concurrently on the shared worktree would clobber
                // each other's files. It will be dispatched in the next iteration after a slot frees.
                if (inFlight.Count > 0 && ConflictsWithAnyInFlight(subtaskId, inFlight.Keys, subtasksById))
                    continue;

                var dispatched = await DispatchOneAsync(
                    context, workPlanId.Value, subtaskId, statusById, seq, ct).ConfigureAwait(false);

                if (dispatched is { } childRunId)
                    inFlight[subtaskId] = ObserveChildAsync(context.CoordinatorRunId, subtaskId, childRunId, ct);
            }

            if (inFlight.Count == 0)
                break; // quiescent: nothing running and no ready frontier (all terminal or blocked)

            var finished = await Task.WhenAny(inFlight.Values).ConfigureAwait(false);
            var result = await finished.ConfigureAwait(false);
            inFlight.Remove(result.SubtaskId);

            // A re-observed orphaned child that has made no progress past the stall threshold (no live
            // watch loop, non-terminal in the store) is failed deterministically so the loop never
            // spins forever and the frontier can advance / the run can settle.
            if (result.Outcome == ChildOutcome.Stalled)
            {
                await ApplyStallFailureAsync(
                    context, workPlanId.Value, result, statusById, seq, ct).ConfigureAwait(false);
                continue;
            }

            // Honest next-turn-boundary steering: the child's current turn has just completed, so a
            // queued redirect/amend for this child can now be applied by injecting a revised task
            // turn (no mid-turn interrupt). Only a child that reached a clean boundary
            // (assemble_ready / completed) can carry a revised turn; a failed/cancelled child falls
            // through to normal finalization.
            if (result.Outcome is ChildOutcome.AssembleReady or ChildOutcome.Completed)
            {
                var directive = _steering.TryTakeForChild(context.CoordinatorRunId, result.ChildRunId);
                if (directive is not null
                    && await TryInjectSteeringRevisionAsync(
                        context, workPlanId.Value, result, directive, statusById, seq, ct).ConfigureAwait(false))
                {
                    inFlight[result.SubtaskId] = ObserveChildAsync(context.CoordinatorRunId, result.SubtaskId, result.ChildRunId, ct);
                    continue;
                }
            }
            // A redirect directive targeting this child can also apply when the child was force-cancelled
            // (by the steering service or the proactive reconciler sweep) to unblock a stuck child.
            // Amend is not applied on failure — it is additive and requires a clean boundary.
            else if (result.Outcome == ChildOutcome.Failed)
            {
                var redirect = _steering.TryTakeRedirectForChild(context.CoordinatorRunId, result.ChildRunId);
                if (redirect is not null
                    && await TryInjectSteeringRevisionAsync(
                        context, workPlanId.Value, result, redirect, statusById, seq, ct).ConfigureAwait(false))
                {
                    inFlight[result.SubtaskId] = ObserveChildAsync(context.CoordinatorRunId, result.SubtaskId, result.ChildRunId, ct);
                    continue;
                }
            }

            await ApplyChildResultAsync(
                context, workPlanId.Value, result, statusById, seq, ct).ConfigureAwait(false);
        }

        await FinalizeDispatchAsync(context, workPlanId.Value, statusById, edges, seq, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every child subtask is now terminal. This emits an explicit children-complete signal, moves
    /// the work plan to <see cref="WorkPlanStatus.AwaitingAssembly"/>, publishes a snapshot reflecting
    /// it, then HANDS OFF to Phase 3 collective assembly (<see cref="CoordinatorAssemblyService"/>).
    /// The coordinator stream is intentionally LEFT OPEN — the assembly pipeline continues to emit
    /// <c>coordinator.assembly_*</c> events on it and closes it at its own terminal (complete /
    /// blocked / failed / declined), or it is re-opened by a re-dispatch wave on request_changes.
    /// </summary>
    internal async Task FinalizeDispatchAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        Dictionary<int, string> statusById,
        IReadOnlyCollection<(int, int)> edges,
        SeqCounter seq,
        CancellationToken ct)
    {
        var terminalCounts = statusById.Values
            .GroupBy(s => s)
            .ToDictionary(g => g.Key, g => g.Count());
        _logger.LogInformation(
            "Coordinator dispatch complete for run {RunId}: {Summary}. Handing off to Phase 3 assembly.",
            context.CoordinatorRunId,
            string.Join(", ", terminalCounts.Select(kv => $"{kv.Key}={kv.Value}")));

        await SetWorkPlanStatusAsync(workPlanId, WorkPlanStatus.AwaitingAssembly, ct).ConfigureAwait(false);

        var finalEntry = _streamStore.Get(context.CoordinatorRunId);
        if (finalEntry is not null)
        {
            var completed = terminalCounts.GetValueOrDefault(SubtaskStatus.Completed, 0);
            var assembleReady = terminalCounts.GetValueOrDefault(SubtaskStatus.AssembleReady, 0);
            var failedCount = terminalCounts.GetValueOrDefault(SubtaskStatus.Failed, 0)
                + terminalCounts.GetValueOrDefault(SubtaskStatus.RaiFlagged, 0);

            finalEntry.RecordNext(EventTypes.CoordinatorChildrenComplete, new
            {
                workPlanId,
                completed,
                assembleReady,
                failed = failedCount,
                total = statusById.Count,
            });

            // FULL snapshot so the graph reflects the AwaitingAssembly hand-off point.
            var finalSubtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
            finalEntry.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildSnapshot(
                context.CoordinatorRunId, workPlanId, WorkPlanStatus.AwaitingAssembly,
                finalSubtasks, edges, seq.Next()));
            await EmitCoordinatorGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);
        }

        // Phase 3 hand-off: drive the ONE collective pipeline over the combined child output. The DB
        // CAS inside StartAssembly guarantees exactly-once even across re-dispatch waves.
        _assembly.StartAssembly(context);
    }

    private async Task<string?> DispatchOneAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int subtaskId,
        Dictionary<int, string> statusById,
        SeqCounter seq,
        CancellationToken ct)
    {
        // Idempotency guard: if an active (in_progress or assemble_ready) child run already exists
        // for this (coordinator, subtask) pair, re-use it instead of creating a second worker.
        // This can occur when recovery (steering redirect / reconciler re-arm) resets a subtask's
        // status to Pending with ChildRunId = null while the old child is still executing.
        var existingActive = await _runStore.FindActiveChildAsync(
            context.CoordinatorRunId, subtaskId.ToString(), ct).ConfigureAwait(false);
        if (existingActive is not null)
        {
            _logger.LogWarning(
                "Coordinator dispatch: subtask {SubtaskId} already has an active child run {ChildRunId} " +
                "(status {Status}); re-observing instead of dispatching a duplicate",
                subtaskId, existingActive.Id, existingActive.Status);

            var reattached = await UpdateSubtaskAsync(
                subtaskId, SubtaskStatus.Running, existingActive.Id.ToString(), ct).ConfigureAwait(false);
            statusById[subtaskId] = SubtaskStatus.Running;
            if (reattached is not null)
                EmitSubtask(context, workPlanId, reattached, EventTypes.SubtaskRunning, seq.Next());
            return existingActive.Id.ToString();
        }

        var childRunId = RunId.New();

        // Mark dispatched + record the child run id, then project the lifecycle + topology delta.
        var subtask = await UpdateSubtaskAsync(
            subtaskId, SubtaskStatus.Dispatched, childRunId.ToString(), ct).ConfigureAwait(false);
        if (subtask is null) return null;
        statusById[subtaskId] = SubtaskStatus.Dispatched;
        EmitSubtask(context, workPlanId, subtask, EventTypes.SubtaskDispatched, seq.Next());
        // A child run id is now assigned, so the unified coordinator graph SHAPE changed
        // (child_graph_ref appears) — re-emit the full shape-only snapshot.
        await EmitCoordinatorGraphAsync(context.CoordinatorRunId, workPlanId, ct).ConfigureAwait(false);

        var childRun = new Run
        {
            Id = childRunId,
            RepositoryPath = context.RepositoryPath,
            OriginatingBranch = context.OriginatingBranch,
            ModelSource = ModelSource.GitHubCopilot,
            Task = ComposeChildTask(subtask),
            SubmittingUser = context.SubmittingUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = context.ProjectId,
            ModelId = subtask.SelectedModelId,
            AgentName = subtask.AssignedAgent,
            ParentRunId = context.CoordinatorRunId,
            SubtaskId = subtaskId.ToString(),
        };

        // Cascade the coordinator's per-run options (auto-approve-tools + Autopilot) to the child so
        // the child's runner honors auto-approve and the child's bubbled questions are eligible for
        // Autopilot. Seeded before the child run starts so its first tool call reads the inherited value.
        CascadeOptionsToChild(context.CoordinatorRunId, childRunId.ToString());

        try
        {
            await _orchestrator.StartChildRunAsync(childRun, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Coordinator dispatch: failed to start child run for subtask {SubtaskId} (run {RunId})",
                subtaskId, context.CoordinatorRunId);

            // Defect B: StartChildRunAsync can throw before it persists the child run row (e.g. worktree
            // creation fails), which would leave the subtask pointing at a childRunId that GET
            // /api/runs/{childRunId} cannot find — an empty execution log. Persist a terminal FAILED run
            // + RunFailed event FIRST (defensive: never throws), so the failed child is retrievable,
            // THEN mark the subtask failed.
            await _orchestrator.MarkChildRunFailedAsync(childRun, ex, ct).ConfigureAwait(false);

            var failed = await UpdateSubtaskAsync(subtaskId, SubtaskStatus.Failed, childRunId.ToString(), ct)
                .ConfigureAwait(false);
            statusById[subtaskId] = SubtaskStatus.Failed;
            if (failed is not null)
                EmitSubtask(context, workPlanId, failed, EventTypes.SubtaskFailed, seq.Next());
            return null;
        }

        // The child workflow is now executing.
        var running = await UpdateSubtaskAsync(subtaskId, SubtaskStatus.Running, childRunId.ToString(), ct)
            .ConfigureAwait(false);
        statusById[subtaskId] = SubtaskStatus.Running;
        if (running is not null)
            EmitSubtask(context, workPlanId, running, EventTypes.SubtaskRunning, seq.Next());

        return childRunId.ToString();
    }

    private async Task ApplyChildResultAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        ChildResult result,
        Dictionary<int, string> statusById,
        SeqCounter seq,
        CancellationToken ct)
    {
        var (status, eventType) = result.Outcome switch
        {
            ChildOutcome.AssembleReady => (SubtaskStatus.AssembleReady, EventTypes.SubtaskAssembleReady),
            ChildOutcome.RaiFlagged => (SubtaskStatus.RaiFlagged, EventTypes.SubtaskRaiFlagged),
            ChildOutcome.Completed => (SubtaskStatus.Completed, EventTypes.SubtaskCompleted),
            _ => (SubtaskStatus.Failed, EventTypes.SubtaskFailed),
        };

        var subtask = await UpdateSubtaskAsync(result.SubtaskId, status, result.ChildRunId, ct).ConfigureAwait(false);
        statusById[result.SubtaskId] = status;
        if (subtask is not null)
            EmitSubtask(context, workPlanId, subtask, eventType, seq.Next());
    }

    /// <summary>
    /// Fails a subtask whose orphaned child run appears stalled (no live watch loop, non-terminal in
    /// the store, no progress past the stall threshold). Records recovery guidance and bumps the
    /// per-subtask recovery-attempt counter (capped at <see cref="CoordinatorSteeringService.MaxRecoveryAttempts"/>
    /// so a persistently stalled subtask cannot be re-dispatched forever). Best-effort: a missing row
    /// is treated as already failed.
    /// </summary>
    private async Task ApplyStallFailureAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        ChildResult result,
        Dictionary<int, string> statusById,
        SeqCounter seq,
        CancellationToken ct)
    {
        Subtask? updated = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == result.SubtaskId, ct).ConfigureAwait(false);
            if (row is not null)
            {
                var since = result.StaleSince ?? DateTimeOffset.UtcNow;
                row.Status = SubtaskStatus.Failed;
                row.RecoveryGuidance =
                    $"Child run appears stalled/orphaned; no progress since {since:O}. " +
                    "The coordinator failed this subtask during reconciliation.";
                row.RecoveryAttempts = Math.Min(
                    row.RecoveryAttempts + 1, CoordinatorSteeringService.MaxRecoveryAttempts);
                row.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.Entry(row).State = EntityState.Detached;
                updated = row;
            }
        }

        statusById[result.SubtaskId] = SubtaskStatus.Failed;
        if (updated is not null)
            EmitSubtask(context, workPlanId, updated, EventTypes.SubtaskFailed, seq.Next());

        _logger.LogWarning(
            "Coordinator dispatch: stall-failed subtask {SubtaskId} (child {ChildRunId}) during reconciliation for run {RunId}",
            result.SubtaskId, result.ChildRunId, context.CoordinatorRunId);
    }

    // -----------------------------------------------------------------------
    // Steering — apply a queued redirect/amend at the child's next turn boundary (Phase 2).
    // Reuses the revision-injection mechanism identified in the steering spike: the child resumes
    // its session and worktree with the steered instruction as a fresh trimmed-pipeline turn. There
    // is NO mid-turn interrupt — this only runs once the child's prior turn has fully completed.
    // Returns true when the revised turn was injected (the caller re-observes the child), false to
    // fall through to normal finalization.
    // -----------------------------------------------------------------------

    private async Task<bool> TryInjectSteeringRevisionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        ChildResult result,
        QueuedSteering directive,
        Dictionary<int, string> statusById,
        SeqCounter seq,
        CancellationToken ct)
    {
        if (!RunId.TryParse(result.ChildRunId, out var childRunId))
            return false;

        var childRun = await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false);
        if (childRun is null)
        {
            _logger.LogWarning(
                "Steering: child run {ChildRunId} not found; cannot inject directive {DirectiveId}",
                result.ChildRunId, directive.DirectiveId);
            return false;
        }

        // queued -> relayed: the directive is handed to the child's control seam.
        await UpdateDirectiveStatusAsync(directive.DirectiveId, SteeringStatus.Relayed, DateTimeOffset.UtcNow, ct)
            .ConfigureAwait(false);
        EmitSteering(context.CoordinatorRunId, directive, SteeringStatus.Relayed);

        try
        {
            // Drop the now-completed child stream entry and reset the run to in-progress so the
            // injected turn observes a clean, live run (the child reached an assemble_ready terminal,
            // which marked its stream completed). Same runId + worktree — the run is never restarted.
            _streamStore.Remove(result.ChildRunId);
            await _runStore.UpdateStatusAsync(childRunId, RunStatus.InProgress, null, ct).ConfigureAwait(false);
            await _orchestrator.StartRevisionAsync(childRun, directive.Instruction, ct, isChild: true)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Steering: failed to inject revised turn for child {ChildRunId} (directive {DirectiveId}); finalizing normally",
                result.ChildRunId, directive.DirectiveId);
            return false;
        }

        // The child is executing a new turn carrying the steered instruction.
        var running = await UpdateSubtaskAsync(result.SubtaskId, SubtaskStatus.Running, result.ChildRunId, ct)
            .ConfigureAwait(false);
        statusById[result.SubtaskId] = SubtaskStatus.Running;
        if (running is not null)
            EmitSubtask(context, workPlanId, running, EventTypes.SubtaskRunning, seq.Next());

        // relayed -> applied: the revised task turn is now running (the directive took effect).
        await UpdateDirectiveStatusAsync(directive.DirectiveId, SteeringStatus.Applied, relayedAt: null, ct)
            .ConfigureAwait(false);
        EmitSteering(context.CoordinatorRunId, directive, SteeringStatus.Applied);

        _logger.LogInformation(
            "Steering: applied {Kind} directive {DirectiveId} to child {ChildRunId} at its next turn boundary",
            directive.Kind, directive.DirectiveId, result.ChildRunId);
        return true;
    }

    private async Task UpdateDirectiveStatusAsync(
        int directiveId, string status, DateTimeOffset? relayedAt, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.SteeringDirectives.FirstOrDefaultAsync(d => d.Id == directiveId, ct).ConfigureAwait(false);
        if (row is null) return;
        row.Status = status;
        if (relayedAt is not null) row.RelayedAt = relayedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void EmitSteering(string coordinatorRunId, QueuedSteering directive, string status)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        entry?.RecordNext(EventTypes.CoordinatorSteering, CoordinatorSteeringEvent.Payload(
            directive.DirectiveId, directive.Kind, directive.TargetChildRunId, status, directive.Instruction));
    }

    // -----------------------------------------------------------------------
    // Observation — read the child's existing run stream (no double-consume).
    // -----------------------------------------------------------------------

    private async Task<ChildResult> ObserveChildAsync(string coordinatorRunId, int subtaskId, string childRunId, CancellationToken ct)
    {
        var entry = _streamStore.Get(childRunId);
        var lastSeq = 0;

        while (!ct.IsCancellationRequested)
        {
            entry ??= _streamStore.Get(childRunId);
            if (entry is null)
            {
                // Entry not visible yet (or already evicted) — fall back to the persisted run status.
                var byStore = await TryResolveFromStoreAsync(childRunId, ct).ConfigureAwait(false);
                if (byStore is { } outcomeNow)
                    return new ChildResult(subtaskId, childRunId, outcomeNow);

                // Non-terminal in the store with NO live stream entry (evicted / post-restart /
                // orphaned): nothing is driving this child. If it has made no progress past the stall
                // threshold, treat it as stalled/orphaned so the reconciling loop never polls forever.
                var staleSince = await StaleSinceAsync(childRunId, ct).ConfigureAwait(false);
                if (staleSince is { } since)
                    return new ChildResult(subtaskId, childRunId, ChildOutcome.Stalled, since);

                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var snapshot = entry.GetSnapshotSince(lastSeq);
            foreach (var evt in snapshot.Events)
            {
                lastSeq = evt.Sequence;

                // Bubble mid-run child questions / approval gates onto the coordinator stream so
                // the operator (and, later, Autopilot) can resolve them. The answer/approval flows
                // back to the CHILD run's gate, so we carry childRunId + requestId verbatim.
                BubbleChildInteraction(coordinatorRunId, subtaskId, childRunId, evt);

                if (TryMapTerminalEvent(evt, out var outcome))
                    return new ChildResult(subtaskId, childRunId, outcome);
            }

            if (snapshot.IsCompleted)
            {
                var byStore = await TryResolveFromStoreAsync(childRunId, ct).ConfigureAwait(false);
                return new ChildResult(subtaskId, childRunId, byStore ?? ChildOutcome.Failed);
            }

            await entry.WaitForChangeAsync(ct).ConfigureAwait(false);
        }

        return new ChildResult(subtaskId, childRunId, ChildOutcome.Failed);
    }

    /// <summary>
    /// Re-projects a child run's mid-run question (<see cref="EventTypes.AgentQuestionAsked"/>) or
    /// tool-approval gate (<see cref="EventTypes.ToolApprovalRequired"/>) onto the COORDINATOR run's
    /// stream. The re-emitted event carries the childRunId + subtaskId + requestId so a resolver can
    /// answer/approve against the child run. Terminal-event mapping is unaffected.
    /// </summary>
    internal void BubbleChildInteraction(string coordinatorRunId, int subtaskId, string childRunId, RunEvent evt)
    {
        if (evt.Type == EventTypes.AgentQuestionAsked)
        {
            var requestId = ReadString(evt.Payload, "requestId");
            var question = ReadString(evt.Payload, "question");

            var entry = _streamStore.Get(coordinatorRunId);
            entry?.RecordNext(EventTypes.CoordinatorChildQuestion, new
            {
                childRunId,
                subtaskId,
                requestId,
                question,
            });

            // Autopilot (questions only): if the coordinator's Autopilot option is ON, auto-answer
            // the bubbled question via the coordinator model on a supervised background task so the
            // observe loop is never blocked on a model call. Permissions are never auto-answered.
            if (_autopilot is not null
                && _runOptions?.Get(coordinatorRunId).Autopilot == true
                && !string.IsNullOrEmpty(requestId))
            {
                _ = Task.Run(() => _autopilot.TryAnswerChildQuestionAsync(
                    coordinatorRunId, childRunId, subtaskId, requestId, question ?? "", _appStopping));
            }
        }
        else if (evt.Type == EventTypes.ToolApprovalRequired)
        {
            var entry = _streamStore.Get(coordinatorRunId);
            entry?.RecordNext(EventTypes.CoordinatorChildApprovalRequired, new
            {
                childRunId,
                subtaskId,
                requestId = ReadString(evt.Payload, "requestId"),
                toolName = ReadString(evt.Payload, "toolName"),
                url = ReadString(evt.Payload, "url"),
                message = ReadString(evt.Payload, "message"),
            });
        }
    }

    /// <summary>
    /// Copies the coordinator run's per-run options (auto-approve-tools + Autopilot) onto a freshly
    /// dispatched child run so both flags inherit. No-op when no options store is wired.
    /// </summary>
    internal void CascadeOptionsToChild(string coordinatorRunId, string childRunId)
    {
        if (_runOptions is null) return;
        _runOptions.Set(childRunId, _runOptions.Get(coordinatorRunId));
    }

    private static bool TryMapTerminalEvent(RunEvent evt, out ChildOutcome outcome)
    {
        switch (evt.Type)
        {
            case EventTypes.RunAssembleReady:
                outcome = ReadBool(evt.Payload, "raiSafetyFlagged")
                    ? ChildOutcome.RaiFlagged
                    : ChildOutcome.AssembleReady;
                return true;
            case EventTypes.RunFailed:
                outcome = string.Equals(ReadString(evt.Payload, "reason"), "content_safety", StringComparison.Ordinal)
                    ? ChildOutcome.RaiFlagged
                    : ChildOutcome.Failed;
                return true;
            case EventTypes.RunCancelled:
                outcome = ChildOutcome.Failed;
                return true;
            case EventTypes.RunCompleted:
                outcome = ChildOutcome.Completed;
                return true;
            default:
                outcome = default;
                return false;
        }
    }

    private async Task<ChildOutcome?> TryResolveFromStoreAsync(string childRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(childRunId, out var parsed))
            return null;
        var run = await _runStore.GetAsync(parsed, ct).ConfigureAwait(false);
        return run?.Status switch
        {
            RunStatus.AssembleReady => ChildOutcome.AssembleReady,
            RunStatus.Completed => ChildOutcome.Completed,
            RunStatus.Merged => ChildOutcome.Completed,
            RunStatus.Failed => ChildOutcome.Failed,
            RunStatus.Declined => ChildOutcome.Failed,
            RunStatus.MergeFailed => ChildOutcome.Failed,
            _ => null, // still in progress
        };
    }

    /// <summary>
    /// Returns the last-activity timestamp when an orphaned child run (non-terminal in the store with
    /// no live watch loop) has made no progress for longer than <see cref="_stallTimeout"/>, or null
    /// when it is still within the threshold (or cannot be resolved). Last activity is the later of the
    /// child run's start time and its most recent persisted run event, so a child that emitted recent
    /// events is never considered stalled.
    /// </summary>
    private async Task<DateTimeOffset?> StaleSinceAsync(string childRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(childRunId, out var parsed))
            return null;
        var run = await _runStore.GetAsync(parsed, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        var lastActivity = run.StartedAt;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var latest = await db.RunEvents
                .Where(e => e.RunId == childRunId)
                .OrderByDescending(e => e.CreatedAt)
                .Select(e => (DateTime?)e.CreatedAt)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (latest is { } le)
            {
                var leOffset = new DateTimeOffset(DateTime.SpecifyKind(le, DateTimeKind.Utc));
                if (leOffset > lastActivity)
                    lastActivity = leOffset;
            }
        }

        return DateTimeOffset.UtcNow - lastActivity >= _stallTimeout ? lastActivity : null;
    }

    // -----------------------------------------------------------------------
    // EF access (scoped DbContext — all writes on the dispatch-loop task)
    // -----------------------------------------------------------------------

    private async Task<(int? WorkPlanId, List<Subtask> Subtasks, List<(int, int)> Edges)> LoadPlanAsync(
        string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var workPlan = await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
        if (workPlan is null)
            return (null, [], []);

        var subtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlan.Id)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        var ids = subtasks.Select(s => s.Id).ToHashSet();
        var edges = (await db.SubtaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.SubtaskId))
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => (d.SubtaskId, d.DependsOnSubtaskId))
            .ToList();

        return (workPlan.Id, subtasks, edges);
    }

    private async Task<List<Subtask>> ReloadSubtasksAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlanId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the UNIFIED <c>coordinator.graph</c> event (shape-only <see cref="GraphDescriptor"/>,
    /// variant <c>coordinator</c>) on the coordinator stream. Called whenever the topology SHAPE
    /// changes (a subtask child run is dispatched, or the plan reaches its terminal snapshot), in
    /// addition to the legacy <c>coordinator.topology</c> snapshot/delta which other consumers use.
    /// </summary>
    private async Task EmitCoordinatorGraphAsync(string coordinatorRunId, int workPlanId, CancellationToken ct)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is null) return;

        var subtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var ids = subtasks.Select(s => s.Id).ToHashSet();
        var deps = (await db.SubtaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.SubtaskId))
            .ToListAsync(ct).ConfigureAwait(false))
            .Select(d => (d.SubtaskId, d.DependsOnSubtaskId))
            .ToList();

        var assemblyStage = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => w.AssemblyStage)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var descriptor = CoordinatorGraphDescriptor.Build(coordinatorRunId, subtasks, deps, assemblyStage);
        entry.RecordNext(EventTypes.CoordinatorGraph, descriptor);
    }

    private async Task<Subtask?> UpdateSubtaskAsync(
        int subtaskId, string status, string? childRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == subtaskId, ct).ConfigureAwait(false);
        if (row is null) return null;

        row.Status = status;
        if (childRunId is not null) row.ChildRunId = childRunId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        db.Entry(row).State = EntityState.Detached;
        return row;
    }

    private async Task SetWorkPlanStatusAsync(int workPlanId, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.FirstOrDefaultAsync(w => w.Id == workPlanId, ct).ConfigureAwait(false);
        if (plan is null) return;
        plan.Status = status;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // Event projection on the coordinator stream
    // -----------------------------------------------------------------------

    private void EmitSubtask(
        CoordinatorDispatchContext context, int workPlanId, Subtask subtask, string eventType, long topologySeq)
    {
        var entry = _streamStore.Get(context.CoordinatorRunId);
        if (entry is null) return;

        entry.RecordNext(eventType, new
        {
            subtaskId = subtask.Id,
            childRunId = subtask.ChildRunId,
            assignedAgent = subtask.AssignedAgent,
            selectedModelId = subtask.SelectedModelId,
            status = subtask.Status,
            timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
        });

        entry.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildDelta(
            context.CoordinatorRunId,
            workPlanId,
            WorkPlanStatus.Dispatching,
            [CoordinatorTopology.SubtaskNode(subtask)],
            topologySeq));
    }

    private static string ComposeChildTask(Subtask subtask)
    {
        var baseTask = string.IsNullOrWhiteSpace(subtask.Scope)
            ? subtask.Title
            : $"{subtask.Title}\n\n{subtask.Scope}";

        // A re-dispatched subtask that was resumed by steering recovery carries guidance (the human's
        // steering instruction + the failure context). Append it so the worker re-does the work
        // against the latest state and addresses the feedback.
        return string.IsNullOrWhiteSpace(subtask.RecoveryGuidance)
            ? baseTask
            : $"{baseTask}\n\n{subtask.RecoveryGuidance}";
    }

    /// <summary>
    /// Returns true when the candidate subtask conflicts with at least one of the currently in-flight
    /// subtasks in the shared orchestration worktree. Delegates to
    /// <see cref="CoordinatorAssemblyService.DoSubtasksConflict"/> for the scope/file overlap check.
    /// When the candidate is unknown (not in <paramref name="subtasksById"/>), returns false so
    /// dispatch can proceed rather than stalling indefinitely.
    /// </summary>
    private static bool ConflictsWithAnyInFlight(
        int candidateId,
        IEnumerable<int> inFlightIds,
        IReadOnlyDictionary<int, Subtask> subtasksById)
    {
        if (!subtasksById.TryGetValue(candidateId, out var candidate))
            return false;

        foreach (var inFlightId in inFlightIds)
        {
            if (!subtasksById.TryGetValue(inFlightId, out var inFlight))
                continue;
            if (CoordinatorAssemblyService.DoSubtasksConflict(candidate, inFlight))
                return true;
        }

        return false;
    }

    private static bool ReadBool(object payload, string property)
    {
        var el = JsonSerializer.SerializeToElement(payload);
        return el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty(property, out var v)
            && v.ValueKind == JsonValueKind.True;
    }

    private static string? ReadString(object payload, string property)
    {
        var el = JsonSerializer.SerializeToElement(payload);
        return el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private enum ChildOutcome { AssembleReady, RaiFlagged, Completed, Failed, Stalled }

    private sealed record ChildResult(int SubtaskId, string ChildRunId, ChildOutcome Outcome, DateTimeOffset? StaleSince = null);

    /// <summary>Monotonic topology sequence: snapshot is <c>Current</c> (0), each delta is <c>Next()</c>.</summary>
    internal sealed class SeqCounter
    {
        private long _value;
        public long Current => _value;
        public long Next() => Interlocked.Increment(ref _value);
    }
}

/// <summary>Canonical <see cref="WorkPlan.Status"/> values (data-model.md).</summary>
public static class WorkPlanStatus
{
    public const string Planned = "planned";
    public const string Dispatching = "dispatching";
    public const string Assembling = "assembling";
    public const string InReview = "in_review";
    public const string Complete = "complete";

    /// <summary>
    /// Phase 2 terminal-ish status: every child subtask reached a terminal state and the work plan
    /// now awaits Phase 3 collective assembly (merge). Distinct from <see cref="Dispatching"/> (work
    /// in flight) and from the Phase-3-owned <see cref="Assembling"/>/<see cref="InReview"/>/<see
    /// cref="Complete"/>. The UI renders this as "all children done, awaiting collective assembly".
    /// </summary>
    public const string AwaitingAssembly = "awaiting_assembly";

    // ── Phase 3 collective-assembly terminal / parked states ──────────────────────────────────
    /// <summary>Assembly stopped with NO partial assembly: a subtask was not eligible, or merging
    /// child branches into the integration branch conflicted. Terminal/parked.</summary>
    public const string AssemblyBlocked = "assembly_blocked";

    /// <summary>The collective merge of the integration branch into origin failed. Terminal.</summary>
    public const string AssemblyFailed = "assembly_failed";

    /// <summary>The reviewer declined the collective output (not request-changes). Terminal.</summary>
    public const string AssemblyDeclined = "assembly_declined";
}

/// <summary>
/// Canonical <see cref="WorkPlan.AssemblyStage"/> values (Phase 3). Drives the coordinator graph
/// node-flip: each planned collective-assembly node (<c>planned:assembly-{stage}</c>) renders with
/// kind="live" once its stage has started, computed from the persisted stage. A stage is sticky —
/// it only advances forward (rai -&gt; review -&gt; merge -&gt; scribe -&gt; done) so every node up to and
/// including the current stage renders live.
/// </summary>
public static class AssemblyStage
{
    public const string Rai = "rai";
    public const string Review = "review";
    public const string Merge = "merge";
    public const string Scribe = "scribe";
    public const string Done = "done";

    /// <summary>Forward ordinal of a stage (0 = not started). Used for the sticky node-flip.</summary>
    public static int Ordinal(string? stage) => stage switch
    {
        Rai => 1,
        Review => 2,
        Merge => 3,
        Scribe => 4,
        Done => 5,
        _ => 0,
    };
}

/// <summary>
/// Immutable context the dispatch engine needs to launch + tag child runs for a coordinator run.
/// </summary>
public sealed record CoordinatorDispatchContext(
    string CoordinatorRunId,
    string RepositoryPath,
    string OriginatingBranch,
    string SubmittingUser,
    ProjectId? ProjectId);
