using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Git;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
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
    private readonly IRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunOrchestrator _orchestrator;
    private readonly WorktreeManager? _worktreeManager;
    private readonly CoordinatorSteeringQueue _steering;
    private readonly ICoordinatorAssembly _assembly;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunOptionsStore? _runOptions;
    private readonly ICoordinatorAutopilot? _autopilot;
    private readonly IRunEventStream? _eventStream;
    private readonly IToolApprovalGate? _approvalGate;
    private readonly IPodNameRegistry? _podRegistry;
    private readonly IKubernetesEnvironment? _k8sEnv;
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    private readonly SandboxRuntimeOptions _sandboxRuntime;
    private readonly ILogger<CoordinatorDispatchService> _logger;
    private readonly CancellationToken _appStopping;

    /// <summary>
    /// How long a child run may emit no events before the observation loop treats it as stalled.
    /// Configurable via <c>Coordinator:SubtaskStallTimeoutMinutes</c> (default 5 minutes).
    /// </summary>
    private readonly TimeSpan _stallTimeout;

    /// <summary>
    /// Maximum number of times a subtask parked in <see cref="SubtaskStatus.PendingCapacity"/> is
    /// retried before it is failed with reason <c>capacity_unavailable</c>. With
    /// <see cref="CapacityRetryDelay"/> this caps capacity-waiting at ~10 minutes.
    /// </summary>
    private const int MaxCapacityRetries = 10;

    /// <summary>Back-off between agent-pod capacity retries for a parked subtask.</summary>
    private static readonly TimeSpan CapacityRetryDelay = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, byte> _active = new();

    /// <summary>Pod name / hostname used as the distributed lease owner identity.</summary>
    private readonly string _myPodId;

    public CoordinatorDispatchService(
        IRunStore runStore,
        RunStreamStore streamStore,
        RunOrchestrator orchestrator,
        WorktreeManager? worktreeManager,
        CoordinatorSteeringQueue steering,
        ICoordinatorAssembly assembly,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<CoordinatorDispatchService> logger,
        IRunOptionsStore? runOptions = null,
        ICoordinatorAutopilot? autopilot = null,
        IConfiguration? configuration = null,
        IRunEventStream? eventStream = null,
        IToolApprovalGate? approvalGate = null,
        IPodNameRegistry? podRegistry = null,
        IAgentHostPodLifecycle? podLifecycle = null,
        IOptions<SandboxRuntimeOptions>? sandboxRuntime = null,
        IKubernetesEnvironment? k8sEnv = null)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _orchestrator = orchestrator;
        _worktreeManager = worktreeManager;
        _steering = steering;
        _assembly = assembly;
        _scopeFactory = scopeFactory;
        _runOptions = runOptions;
        _autopilot = autopilot;
        _eventStream = eventStream;
        _approvalGate = approvalGate;
        _podRegistry = podRegistry;
        _k8sEnv = k8sEnv;
        _podLifecycle = podLifecycle;
        _sandboxRuntime = sandboxRuntime?.Value ?? new SandboxRuntimeOptions();
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;

        var stallMinutes = configuration?.GetValue("Coordinator:SubtaskStallTimeoutMinutes", 5.0) ?? 5.0;
        _stallTimeout = TimeSpan.FromMinutes(Math.Max(0, stallMinutes));

        _myPodId = configuration?.GetValue<string>("App:PodId")
                   ?? Environment.GetEnvironmentVariable("HOSTNAME")
                   ?? Environment.MachineName;
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
        edges = await SerializeDeclaredOutputConflictsAsync(workPlanId.Value, subtasks, edges, ct)
            .ConfigureAwait(false);

        var entry = _streamStore.Get(context.CoordinatorRunId);
        var statusById = subtasks.ToDictionary(s => s.Id, s => s.Status);
        var seq = new SeqCounter();
        var coordinatorStopped = await IsCoordinatorDispatchStoppedAsync(context.CoordinatorRunId, ct).ConfigureAwait(false);
        if (coordinatorStopped && !HasActiveSubtasks(subtasks))
        {
            _logger.LogInformation(
                "Coordinator dispatch: run {RunId} is already terminal/stopped; no subtasks will be dispatched",
                context.CoordinatorRunId);
            return;
        }

        // Advance the plan to dispatching and publish the FULL topology snapshot (reflecting the new
        // status) so the client can render the graph thin before any child has been launched.
        await SetWorkPlanStatusAsync(workPlanId.Value, WorkPlanStatus.Dispatching, ct, coordinatorPodId: _myPodId).ConfigureAwait(false);
        var snapshotSubtasks = await ReloadSubtasksAsync(workPlanId.Value, ct).ConfigureAwait(false);
        entry?.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildSnapshot(
            context.CoordinatorRunId, workPlanId.Value, WorkPlanStatus.Dispatching, snapshotSubtasks, edges, seq.Current, _podRegistry, _k8sEnv?.PodName));
        await EmitCoordinatorGraphAsync(context.CoordinatorRunId, workPlanId.Value, ct).ConfigureAwait(false);

        if (subtasks.Count == 0)
        {
            _logger.LogInformation("Coordinator dispatch: work plan {WorkPlanId} has no subtasks", workPlanId.Value);
            return;
        }

        var inFlight = new Dictionary<int, Task<ChildResult>>();

        // Tracks subtasks parked in PendingCapacity (no agent-pod CPU headroom) and when each is next
        // eligible to retry. Bounded by MaxCapacityRetries so a persistently saturated cluster fails
        // the subtask with capacity_unavailable instead of retrying forever.
        var capacityRetry = new Dictionary<int, CapacityRetryState>();

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
            inFlight[s.Id] = ObserveChildAsync(context.CoordinatorRunId, workPlanId.Value, s.Id, s.ChildRunId!, seq, ct);

        // Recovery: a prior process may have parked subtasks in PendingCapacity. Treat them as
        // pending in this fresh loop so the frontier re-attempts them (the retry budget restarts
        // with the new process) rather than stranding them as a non-terminal, non-frontier status.
        foreach (var s in subtasks.Where(s => s.Status == SubtaskStatus.PendingCapacity))
            statusById[s.Id] = SubtaskStatus.Pending;

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
            if (coordinatorStopped || await IsCoordinatorDispatchStoppedAsync(context.CoordinatorRunId, ct).ConfigureAwait(false))
            {
                coordinatorStopped = true;
                if (inFlight.Count == 0)
                {
                    _logger.LogInformation(
                        "Coordinator dispatch: run {RunId} is terminal/stopped; stopping before dispatching pending subtasks",
                        context.CoordinatorRunId);
                    return;
                }
            }

            // Thaw capacity-parked subtasks whose back-off window elapsed: flip them back to pending
            // (in-memory) so the frontier re-dispatches them — capacity may have freed up via the
            // reaper or a node scale-out. The retry count is preserved in capacityRetry.
            ThawDueCapacityRetries(capacityRetry, statusById);

            // Dispatch the current frontier. Subtasks with non-overlapping file scopes run in
            // parallel; subtasks whose scopes conflict with any in-flight subtask run serially
            // (deferred until the conflicting in-flight task completes).
            foreach (var subtaskId in SubtaskFrontier.ReadyPending(statusById, edges))
            {
                if (coordinatorStopped || await IsCoordinatorDispatchStoppedAsync(context.CoordinatorRunId, ct).ConfigureAwait(false))
                {
                    coordinatorStopped = true;
                    _logger.LogInformation(
                        "Coordinator dispatch: run {RunId} stopped while evaluating frontier; no further children will be launched",
                        context.CoordinatorRunId);
                    break;
                }

                if (inFlight.ContainsKey(subtaskId))
                    continue;

                // If any in-flight subtask conflicts with this one (overlapping or undeclared file
                // paths), defer it: running them concurrently on the shared worktree would clobber
                // each other's files. It will be dispatched in the next iteration after a slot frees.
                if (inFlight.Count > 0 && ConflictsWithAnyInFlight(subtaskId, inFlight.Keys, subtasksById))
                    continue;

                // Pre-flight capacity gate (pod-per-run only). If the namespace can't admit another
                // agent pod, the subtask is parked in PendingCapacity and retried with back-off
                // instead of launching a pod the controller would reject with "exceeded quota".
                if (!await TryPassCapacityGateAsync(
                        context, workPlanId.Value, subtaskId, capacityRetry, statusById, edges, seq, ct)
                        .ConfigureAwait(false))
                    continue;

                if (await IsCoordinatorDispatchStoppedAsync(context.CoordinatorRunId, ct).ConfigureAwait(false))
                {
                    coordinatorStopped = true;
                    _logger.LogInformation(
                        "Coordinator dispatch: run {RunId} stopped after capacity gate; subtask {SubtaskId} will remain pending",
                        context.CoordinatorRunId, subtaskId);
                    break;
                }

                var dispatched = await DispatchOneAsync(
                    context, workPlanId.Value, subtaskId, statusById, edges, seq, ct).ConfigureAwait(false);

                if (dispatched is { } childRunId)
                {
                    capacityRetry.Remove(subtaskId);
                    inFlight[subtaskId] = ObserveChildAsync(context.CoordinatorRunId, workPlanId.Value, subtaskId, childRunId, seq, ct);
                }
            }

            if (inFlight.Count == 0)
            {
                if (coordinatorStopped)
                    return;

                // If subtasks are parked awaiting capacity, don't go quiescent — wait for the soonest
                // retry window (bounded) and loop so they are re-attempted once the reaper frees quota.
                var nextRetry = capacityRetry.Count == 0
                    ? (DateTimeOffset?)null
                    : capacityRetry.Values.Min(r => r.NextRetryAt);
                if (nextRetry is { } due)
                {
                    var wait = due - DateTimeOffset.UtcNow;
                    if (wait > CapacityRetryDelay) wait = CapacityRetryDelay;
                    if (wait > TimeSpan.Zero)
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
                break; // quiescent: nothing running and no ready frontier (all terminal or blocked)
            }

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
                await PropagateBlockedDependentsAsync(
                    context, workPlanId.Value, result.SubtaskId, "dependency_stalled", statusById, edges, seq, ct)
                    .ConfigureAwait(false);
                continue;
            }

            // Honest next-turn-boundary steering: the child's current turn has just completed, so a
            // queued redirect/amend for this child can now be applied by injecting a revised task
            // turn (no mid-turn interrupt). Only a child that reached a clean boundary
            // (assemble_ready / completed) can carry a revised turn; a failed/cancelled child falls
            // through to normal finalization.
            if (!coordinatorStopped && result.Outcome is (ChildOutcome.AssembleReady or ChildOutcome.Completed))
            {
                var directive = await _steering.TryTakeForChildAsync(context.CoordinatorRunId, result.ChildRunId, ct)
                    .ConfigureAwait(false);
                if (directive is not null
                    && await TryInjectSteeringRevisionAsync(
                        context, workPlanId.Value, result, directive, statusById, seq, ct).ConfigureAwait(false))
                {
                    inFlight[result.SubtaskId] = ObserveChildAsync(context.CoordinatorRunId, workPlanId.Value, result.SubtaskId, result.ChildRunId, seq, ct);
                    continue;
                }
            }
            // A redirect directive targeting this child can also apply when the child was force-cancelled
            // (by the steering service or the proactive reconciler sweep) to unblock a stuck child.
            // Amend is not applied on failure — it is additive and requires a clean boundary.
            else if (!coordinatorStopped && result.Outcome == ChildOutcome.Failed)
            {
                var redirect = await _steering.TryTakeRedirectForChildAsync(context.CoordinatorRunId, result.ChildRunId, ct)
                    .ConfigureAwait(false);
                if (redirect is not null
                    && await TryInjectSteeringRevisionAsync(
                        context, workPlanId.Value, result, redirect, statusById, seq, ct).ConfigureAwait(false))
                {
                    inFlight[result.SubtaskId] = ObserveChildAsync(context.CoordinatorRunId, workPlanId.Value, result.SubtaskId, result.ChildRunId, seq, ct);
                    continue;
                }
            }

            await ApplyChildResultAsync(
                context, workPlanId.Value, result, statusById, edges, seq, ct).ConfigureAwait(false);
        }

        await FinalizeDispatchAsync(context, workPlanId.Value, statusById, edges, seq, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsCoordinatorDispatchStoppedAsync(string coordinatorRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(coordinatorRunId, out var runId))
            return false;

        var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
        return run is not null && IsTerminalRunStatus(run.Status);
    }

    private static bool HasActiveSubtasks(IEnumerable<Subtask> subtasks) =>
        subtasks.Any(s => s.Status is SubtaskStatus.Dispatched or SubtaskStatus.Running);

    private static bool IsTerminalRunStatus(RunStatus status) => status is
        RunStatus.Completed or
        RunStatus.Failed or
        RunStatus.Merged or
        RunStatus.Declined or
        RunStatus.MergeFailed or
        RunStatus.AssembleReady;

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
                finalSubtasks, edges, seq.Next(), _podRegistry, _k8sEnv?.PodName));
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
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
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

        var childTask = await ComposeChildTaskAsync(context, workPlanId, subtask, ct).ConfigureAwait(false);

        var childBaseBranch = await ResolveChildBaseBranchAsync(context, subtaskId, edges, ct)
            .ConfigureAwait(false);

        var childRun = new Run
        {
            Id = childRunId,
            RepositoryPath = context.RepositoryPath,
            OriginatingBranch = childBaseBranch,
            ModelSource = ModelSource.GitHubCopilot,
            Task = childTask,
            SubmittingUser = context.SubmittingUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = context.ProjectId,
            ModelId = subtask.SelectedModelId,
            AgentName = subtask.AssignedAgent,
            AgentCharter = subtask.AgentCharter,
            ParentRunId = context.CoordinatorRunId,
            SubtaskId = subtaskId.ToString(),
        };

        // Cascade the coordinator's per-run options (auto-approve-tools + Autopilot) to the child so
        // the child's runner honors auto-approve and the child's bubbled questions are eligible for
        // Autopilot. Seeded before the child run starts so its first tool call reads the inherited value.
        CascadeOptionsToChild(context.CoordinatorRunId, childRunId.ToString());

        // Scope child approval-policy inheritance to this exact project/run/subtask so sibling
        // children never inherit each other's run-scoped approvals.
        _approvalGate?.RegisterParentRun(
            childRunId.ToString(),
            ApprovalScopeKey(context.ProjectId?.Value.ToString(), context.CoordinatorRunId, subtaskId));

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
        IReadOnlyCollection<(int, int)> edges,
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

        if (status is SubtaskStatus.AssembleReady or SubtaskStatus.Completed)
            await RebuildDependencyBaseBranchAsync(context, workPlanId, statusById, edges, ct).ConfigureAwait(false);

        if (status is SubtaskStatus.Failed or SubtaskStatus.RaiFlagged)
        {
            var reason = status == SubtaskStatus.RaiFlagged ? "dependency_rai_flagged" : "dependency_failed";
            await PropagateBlockedDependentsAsync(
                context, workPlanId, result.SubtaskId, reason, statusById, edges, seq, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> ResolveChildBaseBranchAsync(
        CoordinatorDispatchContext context,
        int subtaskId,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
        CancellationToken ct)
    {
        if (!edges.Any(e => e.SubtaskId == subtaskId))
            return context.OriginatingBranch;
        if (_worktreeManager is null)
            return context.OriginatingBranch;

        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(context.CoordinatorRunId);
        try
        {
            return _worktreeManager.BranchExists(context.RepositoryPath, integrationBranch)
                ? integrationBranch
                : context.OriginatingBranch;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coordinator dispatch: could not inspect integration branch {IntegrationBranch} for subtask {SubtaskId}; using origin {Origin}",
                integrationBranch, subtaskId, context.OriginatingBranch);
            return context.OriginatingBranch;
        }
    }

    private async Task RebuildDependencyBaseBranchAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        IReadOnlyDictionary<int, string> statusById,
        IReadOnlyCollection<(int, int)> edges,
        CancellationToken ct)
    {
        if (_worktreeManager is null)
            return;

        var subtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
        var orderedIds = AssemblyPlanning.TopologicalOrder(subtasks.Select(s => s.Id).ToList(), edges);
        var subtasksById = subtasks.ToDictionary(s => s.Id);

        var branches = new List<string>();
        foreach (var id in orderedIds)
        {
            if (!statusById.TryGetValue(id, out var status) || !SubtaskStatus.Satisfies(status))
                continue;
            if (!subtasksById.TryGetValue(id, out var subtask) ||
                string.IsNullOrEmpty(subtask.ChildRunId) ||
                !RunId.TryParse(subtask.ChildRunId, out var childRunId))
                continue;

            var run = await _runStore.GetAsync(childRunId, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(run?.WorktreeBranch) && !string.IsNullOrEmpty(run.Diff))
                branches.Add(run.WorktreeBranch);
        }

        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName(context.CoordinatorRunId);
        var result = _worktreeManager.BuildIntegrationBranch(
            context.RepositoryPath, context.OriginatingBranch, integrationBranch, branches);

        if (result.Outcome == IntegrationBranchOutcome.Conflict)
        {
            _logger.LogWarning(
                "Coordinator dispatch: dependency-base merge for run {RunId} conflicted while adding {Branch}; final assembly will require resolution. Files: {Files}",
                context.CoordinatorRunId,
                result.ConflictingBranch,
                string.Join(", ", result.ConflictingFiles ?? []));
        }
    }

    private async Task PropagateBlockedDependentsAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int failedDependencyId,
        string reason,
        Dictionary<int, string> statusById,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
        SeqCounter seq,
        CancellationToken ct)
    {
        var toFail = new Queue<int>(edges
            .Where(e => e.DependsOnSubtaskId == failedDependencyId)
            .Select(e => e.SubtaskId)
            .Distinct()
            .OrderBy(id => id));
        var seen = new HashSet<int>();

        while (toFail.Count > 0)
        {
            var dependentId = toFail.Dequeue();
            if (!seen.Add(dependentId))
                continue;
            if (!statusById.TryGetValue(dependentId, out var status) || status != SubtaskStatus.Pending)
                continue;

            Subtask? updated = null;
            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var row = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == dependentId, ct).ConfigureAwait(false);
                if (row is not null && row.Status == SubtaskStatus.Pending)
                {
                    row.Status = SubtaskStatus.Failed;
                    row.RecoveryGuidance =
                        $"Skipped by coordinator: dependency subtask {failedDependencyId} ended with {reason}.";
                    row.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    db.Entry(row).State = EntityState.Detached;
                    updated = row;
                }
            }

            statusById[dependentId] = SubtaskStatus.Failed;
            if (updated is not null)
                EmitSubtask(context, workPlanId, updated, EventTypes.SubtaskFailed, seq.Next());

            foreach (var next in edges
                .Where(e => e.DependsOnSubtaskId == dependentId)
                .Select(e => e.SubtaskId)
                .Distinct()
                .OrderBy(id => id))
                toFail.Enqueue(next);
        }
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

        // CRITICAL (orphan cleanup): the stalled child has no live watch loop, so nothing else will
        // release its AgentHost pod (2 CPU / 4 Gi). Release it here so the namespace CPU quota is not
        // exhausted by accumulating orphaned pods across failed runs. Best-effort.
        await ReleaseAgentHostPodSafeAsync(result.ChildRunId, ct).ConfigureAwait(false);

        // Record a precise FailureReason on the stalled child run so the run row (and any
        // run_not_active response) explains the stall instead of the generic message. Best-effort +
        // CAS-guarded: a missing row or an already-terminal run is a silent no-op.
        if (RunId.TryParse(result.ChildRunId, out var stalledRunId))
        {
            try
            {
                await _runStore.TrySetTerminalStatusAsync(
                    stalledRunId, RunStatus.Failed, DateTimeOffset.UtcNow, "agent_stall_timeout", ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Coordinator dispatch: failed to record agent_stall_timeout for child run {ChildRunId}",
                    result.ChildRunId);
            }
        }

        _logger.LogWarning(
            "Coordinator dispatch: stall-failed subtask {SubtaskId} (child {ChildRunId}) during reconciliation for run {RunId}",
            result.SubtaskId, result.ChildRunId, context.CoordinatorRunId);
    }

    /// <summary>
    /// Releases the AgentHost pod for <paramref name="runId"/> when running pod-per-run. Best-effort:
    /// logs and swallows exceptions so a release failure never disrupts dispatch/observe. No-op when
    /// not in pod-per-run mode or no lifecycle is wired (in-api / non-Kubernetes).
    /// </summary>
    private async Task ReleaseAgentHostPodSafeAsync(string runId, CancellationToken ct)
    {
        if (_podLifecycle is null || !_sandboxRuntime.IsPodPerRun || string.IsNullOrEmpty(runId))
            return;

        try
        {
            await _podLifecycle.ReleaseAgentHostPodAsync(runId, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "CoordinatorDispatchService: AgentHost pod released for orphaned/stalled run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CoordinatorDispatchService: failed to release AgentHost pod for run {RunId} (best-effort)",
                runId);
        }
    }

    // -----------------------------------------------------------------------
    // Agent-pod capacity gate — park-and-retry when the namespace quota is exhausted (Change to
    // Task 2). When pod-per-run cannot admit another agent pod, the subtask is queued in
    // PendingCapacity and retried with back-off rather than failing the run hard. The reaper frees
    // orphaned-pod quota periodically, so parked subtasks eventually succeed (virtuous cycle).
    // -----------------------------------------------------------------------

    /// <summary>Per-subtask agent-pod capacity retry bookkeeping (in-loop, not persisted).</summary>
    private readonly record struct CapacityRetryState(int Attempts, DateTimeOffset NextRetryAt);

    /// <summary>
    /// Flips capacity-parked subtasks whose back-off elapsed back to <see cref="SubtaskStatus.Pending"/>
    /// in <paramref name="statusById"/> so the dispatch frontier re-attempts them. Purely in-memory:
    /// the persisted row stays PendingCapacity until the subtask is actually dispatched (or fails),
    /// avoiding churn for a subtask that is about to be re-parked.
    /// </summary>
    private static void ThawDueCapacityRetries(
        Dictionary<int, CapacityRetryState> capacityRetry, Dictionary<int, string> statusById)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (subtaskId, state) in capacityRetry)
        {
            if (state.NextRetryAt <= now
                && statusById.TryGetValue(subtaskId, out var s)
                && s == SubtaskStatus.PendingCapacity)
                statusById[subtaskId] = SubtaskStatus.Pending;
        }
    }

    /// <summary>
    /// Pre-flight agent-pod capacity gate. Returns <see langword="true"/> when the subtask may be
    /// dispatched (capacity available, or not pod-per-run so the gate is a no-op). Returns
    /// <see langword="false"/> when there is no agent-pod headroom: the subtask is parked in
    /// PendingCapacity for retry, or — once the retry budget is exhausted — failed with reason
    /// <c>capacity_unavailable</c> and its dependents propagated.
    /// </summary>
    private async Task<bool> TryPassCapacityGateAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int subtaskId,
        Dictionary<int, CapacityRetryState> capacityRetry,
        Dictionary<int, string> statusById,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
        SeqCounter seq,
        CancellationToken ct)
    {
        // No capacity gating outside pod-per-run (in-api / non-Kubernetes) — always pass.
        if (_podLifecycle is null || !_sandboxRuntime.IsPodPerRun)
            return true;

        try
        {
            await _podLifecycle.CheckAgentHostCapacityAsync(ct).ConfigureAwait(false);
            return true; // capacity available
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AgentHostCapacityPendingException cap)
        {
            await ParkOrFailForCapacityAsync(
                context, workPlanId, subtaskId, cap, capacityRetry, statusById, edges, seq, ct)
                .ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Parks a subtask in <see cref="SubtaskStatus.PendingCapacity"/> with a back-off, or — when the
    /// retry budget (<see cref="MaxCapacityRetries"/>) is exhausted — fails it with reason
    /// <c>capacity_unavailable</c> and propagates the failure to its blocked dependents.
    /// </summary>
    private async Task ParkOrFailForCapacityAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int subtaskId,
        AgentHostCapacityPendingException cap,
        Dictionary<int, CapacityRetryState> capacityRetry,
        Dictionary<int, string> statusById,
        IReadOnlyCollection<(int SubtaskId, int DependsOnSubtaskId)> edges,
        SeqCounter seq,
        CancellationToken ct)
    {
        var attempts = (capacityRetry.TryGetValue(subtaskId, out var existing) ? existing.Attempts : 0) + 1;

        if (attempts > MaxCapacityRetries)
        {
            capacityRetry.Remove(subtaskId);
            await FailSubtaskCapacityUnavailableAsync(
                context, workPlanId, subtaskId, statusById, seq, ct).ConfigureAwait(false);
            await PropagateBlockedDependentsAsync(
                context, workPlanId, subtaskId, "dependency_failed", statusById, edges, seq, ct)
                .ConfigureAwait(false);
            return;
        }

        capacityRetry[subtaskId] = new CapacityRetryState(attempts, DateTimeOffset.UtcNow.Add(CapacityRetryDelay));

        var parked = await SetSubtaskCapacityPendingAsync(subtaskId, cap.Reason, ct).ConfigureAwait(false);
        statusById[subtaskId] = SubtaskStatus.PendingCapacity;
        if (parked is not null)
            EmitSubtask(context, workPlanId, parked, EventTypes.SubtaskPendingCapacity, seq.Next());

        _logger.LogWarning(
            "Subtask {SubtaskId}: agent pod capacity unavailable, retry {Attempt}/{Max} in {Delay}s",
            subtaskId, attempts, MaxCapacityRetries, (int)CapacityRetryDelay.TotalSeconds);
    }

    private async Task<Subtask?> SetSubtaskCapacityPendingAsync(int subtaskId, string reason, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == subtaskId, ct).ConfigureAwait(false);
        if (row is null) return null;

        row.Status = SubtaskStatus.PendingCapacity;
        row.RecoveryGuidance =
            $"Agent pod capacity pending ({reason}); subtask queued for retry until namespace CPU frees up.";
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Entry(row).State = EntityState.Detached;
        return row;
    }

    private async Task FailSubtaskCapacityUnavailableAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int subtaskId,
        Dictionary<int, string> statusById,
        SeqCounter seq,
        CancellationToken ct)
    {
        Subtask? updated;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.Subtasks.FirstOrDefaultAsync(s => s.Id == subtaskId, ct).ConfigureAwait(false);
            if (row is not null)
            {
                row.Status = SubtaskStatus.Failed;
                row.RecoveryGuidance =
                    "Agent pod capacity remained unavailable after the retry budget was exhausted " +
                    "(capacity_unavailable).";
                row.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.Entry(row).State = EntityState.Detached;
            }
            updated = row;
        }

        statusById[subtaskId] = SubtaskStatus.Failed;
        if (updated is not null)
            EmitSubtask(context, workPlanId, updated, EventTypes.SubtaskFailed, seq.Next());

        _logger.LogError(
            "Subtask {SubtaskId}: agent pod capacity unavailable after {Max} retries — failing with capacity_unavailable",
            subtaskId, MaxCapacityRetries);
    }


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
    // Observation — subscribe to the child's IRunEventStream (push-based, no polling).
    // -----------------------------------------------------------------------

    /// <summary>
    /// Subscribes to the child run's event stream via <see cref="IRunEventStream.SubscribeAsync"/>
    /// and <c>await foreach</c>es events, bubbling interaction gates onto the coordinator stream via
    /// <see cref="BubbleChildInteraction"/> and returning on the first terminal event.
    ///
    /// <para>Stall detection: if no event arrives within <see cref="_stallTimeout"/> from the last
    /// received event (or from subscription start), the loop returns <see cref="ChildOutcome.Stalled"/>
    /// so the coordinator can reconcile without an unbounded wait.</para>
    ///
    /// <para>Crash / restart safety: <see cref="IRunEventStream.SubscribeAsync"/> replays all
    /// persisted events from <paramref name="lastSeq"/> before tailing the live channel, so
    /// observation resumes correctly even when the child's process has restarted.</para>
    ///
    /// <para>When <see cref="_eventStream"/> is not wired (test harnesses that do not inject
    /// <see cref="IRunEventStream"/>), falls back to the legacy <see cref="RunStreamStore"/>
    /// snapshot poll so existing tests continue to pass unmodified.</para>
    /// </summary>
    private async Task<ChildResult> ObserveChildAsync(
        string coordinatorRunId,
        int workPlanId,
        int subtaskId,
        string childRunId,
        SeqCounter topologySeq,
        CancellationToken ct)
    {
        // Fast path: child already reached a terminal state before observation begins.
        if (await TryResolveFromStoreAsync(childRunId, ct).ConfigureAwait(false) is { } alreadyDone)
            return new ChildResult(subtaskId, childRunId, alreadyDone);

        // When IRunEventStream is available use the push-based path; otherwise fall back to the
        // legacy RunStreamStore snapshot+poll path so existing tests that do not inject the stream
        // continue to work without modification.
        if (_eventStream is not null)
            return await ObserveViaEventStreamAsync(coordinatorRunId, workPlanId, subtaskId, childRunId, topologySeq, ct)
                .ConfigureAwait(false);

        return await ObserveViaStreamStoreAsync(coordinatorRunId, subtaskId, childRunId, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Push-based observation via <see cref="IRunEventStream.SubscribeAsync"/>. Uses a
    /// <see cref="CancellationTokenSource"/> reset per event so the stall TTL measures time since
    /// the last received event, not time since subscription start.
    /// </summary>
    private async Task<ChildResult> ObserveViaEventStreamAsync(
        string coordinatorRunId,
        int workPlanId,
        int subtaskId,
        string childRunId,
        SeqCounter topologySeq,
        CancellationToken ct)
    {
        var lastSeq = 0;
        object? lastPartialOutput = null;

        while (!ct.IsCancellationRequested)
        {
            // Per-iteration linked CTS: fires after _stallTimeout from the moment we start waiting
            // for the NEXT event. Broken and recreated on every non-terminal event so the stall
            // timer resets to a fresh window each time activity is observed.
            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (_stallTimeout > TimeSpan.Zero)
                stallCts.CancelAfter(_stallTimeout);

            bool receivedEvent = false;
            ChildOutcome? terminalOutcome = null;

            try
            {
                await foreach (var evt in _eventStream!.SubscribeAsync(childRunId, lastSeq, stallCts.Token)
                    .ConfigureAwait(false))
                {
                    lastSeq = evt.Sequence;
                    receivedEvent = true;

                    await ReEmitPodBindingDeltaAsync(
                        coordinatorRunId, workPlanId, subtaskId, evt, topologySeq, ct).ConfigureAwait(false);
                    BubbleChildInteraction(coordinatorRunId, subtaskId, childRunId, evt);
                    if (IsPartialOutputEvent(evt))
                        lastPartialOutput = evt.Payload;

                    if (TryMapTerminalEvent(evt, out var outcome))
                    {
                        terminalOutcome = outcome;
                        break;
                    }

                    // Non-terminal event received — break and restart with a fresh stall timer.
                    break;
                }
            }
            catch (OperationCanceledException) when (stallCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                if (await TryResolveFromStoreAsync(childRunId, ct).ConfigureAwait(false) is { } resolved)
                    return new ChildResult(subtaskId, childRunId, resolved);

                // Stall TTL expired: child emitted no event within the configured window.
                _logger.LogWarning(
                    "Coordinator observation: child {ChildRunId} (subtask {SubtaskId}) emitted no event " +
                    "within the stall TTL ({Timeout}); treating as stalled",
                    childRunId, subtaskId, _stallTimeout);
                await PersistPartialOutputCheckpointAsync(
                    childRunId, subtaskId, lastSeq, lastPartialOutput, "event_stream_stalled", ct)
                    .ConfigureAwait(false);
                return new ChildResult(subtaskId, childRunId, ChildOutcome.Stalled, DateTimeOffset.UtcNow);
            }

            if (terminalOutcome.HasValue)
                return new ChildResult(subtaskId, childRunId, terminalOutcome.Value);

            if (!receivedEvent)
            {
                // SubscribeAsync completed with no events (channel was closed without a terminal
                // event) — fall back to the store for a definitive status.
                await PersistPartialOutputCheckpointAsync(
                    childRunId, subtaskId, lastSeq, lastPartialOutput, "event_stream_closed", ct)
                    .ConfigureAwait(false);
                var storeOutcome = await TryResolveFromStoreAsync(childRunId, ct).ConfigureAwait(false);
                return new ChildResult(subtaskId, childRunId, storeOutcome ?? ChildOutcome.Failed);
            }

            // Non-terminal event received, stall timer reset — continue the outer loop.
        }

        return new ChildResult(subtaskId, childRunId, ChildOutcome.Failed);
    }

    /// <summary>
    /// Legacy observation path via <see cref="RunStreamStore"/> snapshot + <see cref="RunStreamEntry.WaitForChangeAsync"/>.
    /// Used as a fallback when <see cref="_eventStream"/> is not injected (existing test harnesses).
    /// Retains the original stall detection (via DB last-activity) for orphaned children.
    /// </summary>
    private async Task<ChildResult> ObserveViaStreamStoreAsync(
        string coordinatorRunId, int subtaskId, string childRunId, CancellationToken ct)
    {
        var entry = _streamStore.Get(childRunId);
        var lastSeq = 0;
        object? lastPartialOutput = null;

        while (!ct.IsCancellationRequested)
        {
            entry ??= _streamStore.Get(childRunId);
            if (entry is null)
            {
                // Non-terminal in the store with no live stream entry: check the stall threshold.
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
                BubbleChildInteraction(coordinatorRunId, subtaskId, childRunId, evt);
                if (IsPartialOutputEvent(evt))
                    lastPartialOutput = evt.Payload;
                if (TryMapTerminalEvent(evt, out var outcome))
                    return new ChildResult(subtaskId, childRunId, outcome);
            }

            if (snapshot.IsCompleted)
            {
                await PersistPartialOutputCheckpointAsync(
                    childRunId, subtaskId, lastSeq, lastPartialOutput, "stream_store_completed_without_terminal", ct)
                    .ConfigureAwait(false);
                var byStore = await TryResolveFromStoreAsync(childRunId, ct).ConfigureAwait(false);
                return new ChildResult(subtaskId, childRunId, byStore ?? ChildOutcome.Failed);
            }

            await entry.WaitForChangeAsync(ct).ConfigureAwait(false);
        }

        return new ChildResult(subtaskId, childRunId, ChildOutcome.Failed);
    }

    /// <summary>
    /// Returns the last-activity time when an orphaned child run (non-terminal in the store with
    /// no live stream entry) has made no progress for longer than <see cref="_stallTimeout"/>, or
    /// null when still within the threshold. Used by the legacy store-poll fallback only.
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

    private async Task PersistPartialOutputCheckpointAsync(
        string childRunId,
        int subtaskId,
        int lastSequence,
        object? partialOutput,
        string reason,
        CancellationToken ct)
    {
        if (partialOutput is null)
            return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var nextSeq = ((await db.RunEvents
                .Where(e => e.RunId == childRunId)
                .MaxAsync(e => (int?)e.Sequence, ct).ConfigureAwait(false)) ?? 0) + 1;

            db.RunEvents.Add(new RunEventRecord
            {
                RunId = childRunId,
                Sequence = nextSeq,
                EventType = "run.partial_output",
                PayloadJson = JsonSerializer.Serialize(new
                {
                    subtaskId,
                    lastSequence,
                    reason,
                    partialOutput,
                    timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
                }),
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Coordinator observation: failed to persist partial output checkpoint for child {ChildRunId}",
                childRunId);
        }
    }

    private static bool IsPartialOutputEvent(RunEvent evt) =>
        evt.Type is EventTypes.AgentMessage or EventTypes.AgentMessageDelta;

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

    private async Task ReEmitPodBindingDeltaAsync(
        string coordinatorRunId,
        int workPlanId,
        int subtaskId,
        RunEvent evt,
        SeqCounter topologySeq,
        CancellationToken ct)
    {
        if (evt.Type != RunEventExecutionPodNameStore.EventType
            || RunEventExecutionPodNameStore.ReadPodName(evt.Payload) is null)
            return;

        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is null)
            return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new { w.Status })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var subtask = await db.Subtasks.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == subtaskId, ct)
            .ConfigureAwait(false);

        if (subtask is null)
            return;

        entry.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildDelta(
            coordinatorRunId,
            workPlanId,
            plan?.Status ?? WorkPlanStatus.Dispatching,
            [CoordinatorTopology.SubtaskNode(subtask, _podRegistry)],
            topologySeq.Next()));
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

    private async Task<List<(int, int)>> SerializeDeclaredOutputConflictsAsync(
        int workPlanId,
        IReadOnlyList<Subtask> subtasks,
        List<(int, int)> edges,
        CancellationToken ct)
    {
        var existing = edges.ToHashSet();
        var additions = new List<(int SubtaskId, int DependsOnSubtaskId)>();
        var byOutput = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var subtask in subtasks.OrderBy(s => s.Id))
        {
            foreach (var output in AssemblyPlanning.ExtractFileTokens(subtask.Scope))
            {
                if (!byOutput.TryGetValue(output, out var owner))
                {
                    byOutput[output] = subtask.Id;
                    continue;
                }

                var edge = (subtask.Id, owner);
                if (subtask.Id != owner && !existing.Contains(edge) && !additions.Contains(edge))
                    additions.Add(edge);
            }
        }

        if (additions.Count == 0)
            return edges;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        foreach (var (subtaskId, dependsOnSubtaskId) in additions)
        {
            db.SubtaskDependencies.Add(new SubtaskDependency
            {
                SubtaskId = subtaskId,
                DependsOnSubtaskId = dependsOnSubtaskId,
            });
            edges.Add((subtaskId, dependsOnSubtaskId));
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Coordinator dispatch: serialized {Count} output-file conflict(s) in work plan {WorkPlanId}",
            additions.Count, workPlanId);
        return edges;
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

    private async Task SetWorkPlanStatusAsync(int workPlanId, string status, CancellationToken ct, string? coordinatorPodId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.FirstOrDefaultAsync(w => w.Id == workPlanId, ct).ConfigureAwait(false);
        if (plan is null) return;
        plan.Status = status;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        if (coordinatorPodId is not null)
            plan.CoordinatorPodId = coordinatorPodId;
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
            [CoordinatorTopology.SubtaskNode(subtask, _podRegistry)],
            topologySeq));
    }

    private async Task<string> ComposeChildTaskAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        Subtask subtask,
        CancellationToken ct)
    {
        var baseTask = string.IsNullOrWhiteSpace(subtask.Scope)
            ? subtask.Title
            : $"{subtask.Title}\n\n{subtask.Scope}";

        if (!string.IsNullOrWhiteSpace(subtask.RecoveryGuidance))
            baseTask = $"{baseTask}\n\n{subtask.RecoveryGuidance}";

        var sb = new StringBuilder(baseTask);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Coordinator context");

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workPlanId, ct).ConfigureAwait(false);
        if (plan is not null)
        {
            var outcome = await db.OutcomeSpecs.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == plan.OutcomeSpecId, ct).ConfigureAwait(false);
            if (outcome is not null)
            {
                sb.AppendLine("### Parent outcome spec");
                sb.AppendLine($"Desired outcome: {TrimForPrompt(outcome.DesiredOutcome, 1200)}");
                sb.AppendLine($"Scope: {TrimForPrompt(outcome.Scope, 1200)}");
                if (!string.IsNullOrWhiteSpace(outcome.Assumptions))
                    sb.AppendLine($"Assumptions: {TrimForPrompt(outcome.Assumptions, 800)}");
            }
        }

        var allSubtasks = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == workPlanId)
            .OrderBy(s => s.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = allSubtasks.Select(s => s.Id).ToHashSet();
        var deps = await db.SubtaskDependencies.AsNoTracking()
            .Where(d => ids.Contains(d.SubtaskId))
            .ToListAsync(ct).ConfigureAwait(false);

        var byId = allSubtasks.ToDictionary(s => s.Id);
        var dependencyIds = deps
            .Where(d => d.SubtaskId == subtask.Id)
            .Select(d => d.DependsOnSubtaskId)
            .Distinct()
            .OrderBy(id => id)
            .ToList();
        if (dependencyIds.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Dependencies already completed");
            foreach (var depId in dependencyIds)
            {
                if (!byId.TryGetValue(depId, out var dep)) continue;
                sb.Append("- Subtask ").Append(dep.Id).Append(": ").Append(dep.Title)
                    .Append(" [").Append(dep.Status).Append(']');
                var summary = await CompletionSummaryAsync(dep, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summary))
                    sb.Append(" — ").Append(summary);
                sb.AppendLine();
            }
        }

        var completedSiblings = allSubtasks
            .Where(s => s.Id != subtask.Id && s.Status is SubtaskStatus.AssembleReady or SubtaskStatus.Completed)
            .OrderBy(s => s.Id)
            .ToList();
        if (completedSiblings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Completed sibling outputs");
            foreach (var sibling in completedSiblings)
            {
                sb.Append("- Subtask ").Append(sibling.Id).Append(": ").Append(sibling.Title);
                var summary = await CompletionSummaryAsync(sibling, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(summary))
                    sb.Append(" — ").Append(summary);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private async Task<string?> CompletionSummaryAsync(Subtask subtask, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subtask.ChildRunId)
            || !RunId.TryParse(subtask.ChildRunId, out var childId))
            return null;

        var run = await _runStore.GetAsync(childId, ct).ConfigureAwait(false);
        if (run is null) return null;
        if (!string.IsNullOrWhiteSpace(run.Result))
            return TrimForPrompt(run.Result, 700);
        var files = AssemblyPlanning.ExtractTouchedFiles(run.Diff).Take(8).ToList();
        if (files.Count > 0)
            return "Touched files: " + string.Join(", ", files);
        return run.Status.ToString();
    }

    private static string TrimForPrompt(string value, int maxChars)
    {
        var compact = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
        return compact.Length <= maxChars ? compact : compact[..maxChars] + "…";
    }

    private static string ApprovalScopeKey(string? projectId, string coordinatorRunId, int subtaskId) =>
        $"{projectId ?? "no-project"}:{coordinatorRunId}:subtask:{subtaskId}";

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

    /// <summary>Collective RAI flagged the aggregate diff; human override is required before merge.</summary>
    public const string RaiBlocked = "rai_blocked";

    /// <summary>Merge conflicts need explicit human resolution before the coordinator can proceed.</summary>
    public const string NeedsResolution = "needs_resolution";
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
