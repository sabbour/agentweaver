using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Canonical <see cref="SteeringDirective.Kind"/> values for Feature 008 Phase 2. Per the steering
/// spike (<c>specs/008-coordinator-agent/steering-spike.md</c>) only <see cref="Stop"/>,
/// <see cref="Send"/>, <see cref="Redirect"/>, and <see cref="Amend"/> are buildable with honest
/// semantics; <see cref="Pause"/> has no runtime primitive and is DESCOPED — it is rejected with a
/// validation error, never executed.
/// </summary>
public static class SteeringKind
{
    public const string Stop = "stop";

    /// <summary>
    /// Informational nudge delivered to the coordinator. Does not alter the work plan, interrupt
    /// in-flight dispatch, or reset any subtask — the message is queued for the next safe dispatch or
    /// assembly boundary so the owning coordinator loop can inject/observe it.
    /// </summary>
    public const string Send = "send";

    /// <summary>
    /// Interrupt/override the coordinator's current plan toward a new instruction. For a live
    /// coordinator, the directive is queued and applied at the target child's next turn boundary;
    /// for a parked coordinator, it resets failed/rai_flagged subtasks and re-arms dispatch.
    /// When targeting a specific in-progress child, the child is force-completed so the queued
    /// directive is applied without waiting for a natural boundary.
    /// </summary>
    public const string Redirect = "redirect";

    /// <summary>
    /// Additive change to the coordinator's work context without discarding in-flight work. For a
    /// live coordinator, queued for the target child's next boundary. For a parked coordinator,
    /// only unblocks RAI-flagged gates (does not reset failed subtasks — preserving completed work).
    /// </summary>
    public const string Amend = "amend";

    /// <summary>Descoped in Phase 2 — accepted only to produce an explicit rejection.</summary>
    public const string Pause = "pause";

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §4a) — the human mirror of the coordinator's conscious
    /// direction-B choice. Resets the target subtask(s) + relaunches fresh, always emitting
    /// <c>coordinator.steering_decision{decision:"dispatch_fresh"}</c> first. Routes to the same
    /// direction-B executor as an autopilot-chosen fresh dispatch — never an automatic reflex.
    /// </summary>
    public const string DispatchFresh = "dispatch-fresh";

    public static bool IsSupported(string kind) => kind is Stop or Send or Redirect or Amend or DispatchFresh;

    /// <summary>True for the verbs that queue and apply at the child's next safe boundary.</summary>
    public static bool IsNextBoundary(string kind) => kind is Send or Redirect or Amend;
}

/// <summary>
/// Canonical <see cref="SteeringDirective.Status"/> values (data-model.md). <c>pending</c> = persisted
/// but not yet picked up; <c>queued</c> = held for the target child's next turn boundary;
/// <c>relayed</c> = handed to the child's control seam; <c>applied</c> = took effect.
/// </summary>
public static class SteeringStatus
{
    public const string Pending = "pending";
    public const string Queued = "queued";
    public const string Relayed = "relayed";

    /// <summary>#226: a redirect/amend at the assembly human-review gate was durably persisted for the
    /// OWNING pod's deferred poller to drain (the gate was armed on a different replica). The human
    /// action is honestly reported as deferred-to-review (the endpoint maps this to HTTP 202), never
    /// left silently <c>queued</c>. Mirrors the 202 the <c>/assembly/review</c> endpoint returns for the
    /// same cross-replica case.</summary>
    public const string Deferred = "deferred";

    /// <summary>UNIFIED STEERING (rev8 §3c): the coordinator committed a direction (action + target +
    /// attempt recorded, budget incremented) but has NOT yet executed it. Recovery re-drives execution.</summary>
    public const string Decided = "decided";

    /// <summary>UNIFIED STEERING (rev8 §3d): the chosen action is executing (lease stamped via
    /// <c>ExecStartedAt</c>); a stale <c>executing</c> directive is re-driven idempotently by recovery.</summary>
    public const string Executing = "executing";

    public const string Applied = "applied";

    /// <summary>#227: a redirect/amend that reached the assembly human-review gate but found the gate
    /// had already moved past the pending decision — the concurrent-decision RACE-LOSER (the winner
    /// already drove <c>RouteAssemblyGateThroughSteeringAsync</c>) or the razor-thin ARM WINDOW between
    /// <c>run.Status = AwaitingReview</c> and the review request being armed. The directive is redundant,
    /// so it settles here as a TERMINAL no-op instead of falling through to <c>queued</c> (the #227 ghost
    /// row that nothing drains). Distinct from <c>applied</c> (which did drive an effect).</summary>
    public const string Superseded = "superseded";

    /// <summary>UNIFIED STEERING (rev8 §6, execution loop-bound) — a Decision-A directive whose bounded
    /// EXECUTION retries were exhausted (e.g. the resumed revision never wrote a checkpoint) is parked
    /// here (a visible terminal for the directive) instead of being re-driven forever. The plan is
    /// escalated to human review / terminal alongside.</summary>
    public const string NeedsAttention = "needs_attention";
}

/// <summary>
/// Thrown when a steering request is invalid (unsupported/descoped kind, or a missing instruction
/// for a verb that requires one). The HTTP wave (Tank) maps this to <c>400 Bad Request</c>.
/// </summary>
public sealed class SteeringValidationException(string message) : Exception(message);

/// <summary>
/// Thrown when a <c>redirect</c>/<c>amend</c> would resume a parked/failed coordinator but every
/// affected subtask has already hit the per-subtask recovery attempt cap. The orchestration stays
/// parked (no infinite re-dispatch loop); the HTTP wave maps this to <c>409 Conflict</c> so the
/// operator learns auto-recovery is exhausted (manual full-run retry remains available).
/// </summary>
public sealed class SteeringRecoveryExhaustedException(string message) : Exception(message);

/// <summary>
/// A redirect/amend directive parked in <see cref="CoordinatorSteeringQueue"/> until the dispatch
/// loop can inject it at the target child's next turn boundary.
/// </summary>
public sealed record QueuedSteering(int DirectiveId, string Kind, string? TargetChildRunId, string Instruction);

/// <summary>
/// Read model returned by <see cref="CoordinatorSteeringService.SteerAsync"/> and the
/// <c>POST /api/runs/{coordinatorRunId}/steer</c> endpoint (Tank's wave). Mirrors the persisted
/// <see cref="SteeringDirective"/> row.
/// </summary>
public sealed record SteeringDirectiveView(
    int Id,
    string CoordinatorRunId,
    string? TargetChildRunId,
    string Kind,
    string Instruction,
    string Status,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? RelayedAt);

/// <summary>
/// Builds the canonical <c>coordinator.steering</c> event payload so the steering surface and the
/// dispatch loop emit an identical shape (the topology view applies them uniformly).
/// </summary>
public static class CoordinatorSteeringEvent
{
    public static object Payload(int directiveId, string kind, string? targetChildRunId, string status, string instruction) =>
        new { directiveId, kind, targetChildRunId, status, instruction };
}

/// <summary>
/// Lightweight per-run wakeup registry used by the collective-assembly blocked wait loop.
/// The steering endpoint signals the affected coordinator run after persisting a directive so
/// an assembly-blocked wait can wake immediately instead of waiting for its next poll tick.
/// Polling still remains the durable fallback when steering lands on another replica.
/// </summary>
public sealed class CoordinatorSteeringWaitRegistry
{
    private sealed record WaitState(long Version, TaskCompletionSource<long> Signal);

    private readonly Dictionary<string, WaitState> _states = [];
    private readonly Lock _lock = new();

    public long GetVersion(string coordinatorRunId)
    {
        lock (_lock)
            return GetOrCreateLocked(coordinatorRunId).Version;
    }

    public void Signal(string coordinatorRunId)
    {
        TaskCompletionSource<long>? signal = null;
        long nextVersion = 0;
        lock (_lock)
        {
            var current = GetOrCreateLocked(coordinatorRunId);
            nextVersion = current.Version + 1;
            signal = current.Signal;
            _states[coordinatorRunId] = new WaitState(
                nextVersion,
                new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        signal.TrySetResult(nextVersion);
    }

    public async Task<long> WaitForSignalAsync(
        string coordinatorRunId,
        long lastSeenVersion,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        Task<long>? signalTask;
        lock (_lock)
        {
            var state = GetOrCreateLocked(coordinatorRunId);
            if (state.Version > lastSeenVersion)
                return state.Version;
            signalTask = state.Signal.Task;
        }

        var timeoutTask = Task.Delay(maxWait, ct);
        var completed = await Task.WhenAny(signalTask, timeoutTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return completed == signalTask
            ? await signalTask.ConfigureAwait(false)
            : lastSeenVersion;
    }

    private WaitState GetOrCreateLocked(string coordinatorRunId)
    {
        if (_states.TryGetValue(coordinatorRunId, out var state))
            return state;

        state = new WaitState(
            0,
            new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously));
        _states[coordinatorRunId] = state;
        return state;
    }
}

/// <summary>
/// Cross-pod seam between the steering surface (an HTTP-thread call to
/// <see cref="CoordinatorSteeringService.SteerAsync"/>) and the dispatch/assembly loops that own
/// coordinator control. A <c>send</c>/<c>redirect</c>/<c>amend</c> directive is persisted as a <c>queued</c>
/// <see cref="SteeringDirective"/> row by the steering surface; the dispatch loop on the pod that
/// owns the coordinator run drains it from Postgres at the target child's next turn boundary and
/// injects a revised task turn; the assembly-blocked loop owns queued sends while the plan is
/// <c>assembly_blocked</c>. <c>stop</c> never goes through this queue — it is an immediate cancel.
///
/// <para>This is REPLICA-SAFE: the queue is backed entirely by the <c>SteeringDirectives</c> table,
/// so a <c>/steer</c> request that lands on a different pod than the one running the dispatch loop is
/// never lost (the previous in-memory <see cref="Dictionary{TKey,TValue}"/> singleton silently
/// dropped such requests at <c>replicas:2</c>). Each take CLAIMS a directive atomically via a
/// conditional <c>queued -&gt; relayed</c> update, so a directive is applied AT MOST ONCE even when
/// the loop polls repeatedly or two pods race. FIFO ordering within a coordinator run is preserved by
/// claiming in ascending <see cref="SteeringDirective.Id"/> order.</para>
///
/// Registered as a singleton; it holds no per-run state — every operation opens a scoped
/// <see cref="MemoryDbContext"/>.
/// </summary>
public sealed class CoordinatorSteeringQueue(IServiceScopeFactory scopeFactory)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    /// <summary>
    /// Atomically claims and returns the oldest <c>queued</c> directive that targets
    /// <paramref name="childRunId"/> (an exact <c>TargetChildRunId</c> match, or a broadcast with a
    /// null target), transitioning it <c>queued -&gt; relayed</c> so it can never be claimed twice;
    /// returns null when none is queued for that child. FIFO within the coordinator run.
    /// </summary>
    public Task<QueuedSteering?> TryTakeForChildAsync(
        string coordinatorRunId, string childRunId, CancellationToken ct = default) =>
        ClaimAsync(coordinatorRunId, childRunId, redirectOnly: false, sendOnly: false,
            requiredPlanStatus: WorkPlanStatus.Dispatching, ct);

    /// <summary>
    /// Like <see cref="TryTakeForChildAsync"/> but only claims a <see cref="SteeringKind.Redirect"/>
    /// directive. Used by the dispatch loop when a child has failed (rather than completed normally)
    /// and the caller needs to apply only a redirect — not an amend — as a re-dispatch override.
    /// </summary>
    public Task<QueuedSteering?> TryTakeRedirectForChildAsync(
        string coordinatorRunId, string childRunId, CancellationToken ct = default) =>
        ClaimAsync(coordinatorRunId, childRunId, redirectOnly: true, sendOnly: false,
            requiredPlanStatus: WorkPlanStatus.Dispatching, ct);

    /// <summary>
    /// Atomically claims the oldest queued <c>send</c> while the work plan is assembly-blocked. This
    /// status-scoped ownership prevents the dispatch loop from consuming a send during the
    /// dispatch-to-assembly transition and starving the blocked assembly retry loop.
    /// </summary>
    public Task<QueuedSteering?> TryTakeAssemblySendAsync(
        string coordinatorRunId, CancellationToken ct = default) =>
        ClaimAsync(coordinatorRunId, childRunId: null, redirectOnly: false, sendOnly: true,
            requiredPlanStatus: WorkPlanStatus.AssemblyBlocked, ct);

    /// <summary>
    /// Finds the oldest matching <c>queued</c> directive and claims it with a conditional
    /// <c>queued -&gt; relayed</c> <see cref="EntityFrameworkQueryableExtensions"/> update. Only one
    /// caller (across all pods) can win that update; a loser retries against the next candidate. This
    /// is the at-most-once mechanism that makes the queue replica-safe.
    /// </summary>
    private async Task<QueuedSteering?> ClaimAsync(
        string coordinatorRunId,
        string? childRunId,
        bool redirectOnly,
        bool sendOnly,
        string? requiredPlanStatus,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        while (!ct.IsCancellationRequested)
        {
            // UNIFIED AUTONOMOUS STEERING (rev8, CR-C invariant): the next-turn-boundary drain must
            // NEVER deliver a unified-steering / assembly-gate directive to a child. Those directives
            // are routed EXCLUSIVELY through the coordinator decider (CoordinatorSteeringDecider) — the
            // gate mints them with a non-null Source (e.g. `gate:rubberduck`) and a subtask-scoped
            // TargetScope, and TargetChildRunId==null. A crash between the gate's inline claim
            // (queued->relayed) and the DecideAsync commit lets recovery reclaim such a directive back to
            // `queued`; without this guard the broadcast branch (TargetChildRunId==null) below would let
            // that orphaned, UN-DECIDED gate directive be applied as a redirect revision to whichever
            // unrelated child hits its next turn boundary first — exactly the "felt like a glitch" class
            // this feature removes. Legacy human /steer directives leave Source==null and remain
            // claimable, so coordinator-wide sends are unaffected.
            var query = db.SteeringDirectives.AsNoTracking()
                .Where(d => d.CoordinatorRunId == coordinatorRunId
                    && d.Status == SteeringStatus.Queued
                    && d.Source == null);
            if (requiredPlanStatus is not null)
                query = query.Where(d => db.WorkPlans.Any(w => w.CoordinatorRunId == coordinatorRunId && w.Status == requiredPlanStatus));
            if (childRunId is not null)
                query = query.Where(d => d.TargetChildRunId == null || d.TargetChildRunId == childRunId);
            if (redirectOnly)
                query = query.Where(d => d.Kind == SteeringKind.Redirect);
            if (sendOnly)
                query = query.Where(d => d.Kind == SteeringKind.Send);

            var candidate = await query
                .OrderBy(d => d.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (candidate is null)
                return null;

            // Atomic claim: only one writer (any pod) can flip this row queued -> relayed. The
            // conditional WHERE Status == queued is the gate that guarantees at-most-once delivery.
            DateTimeOffset? relayedAt = DateTimeOffset.UtcNow;
            var updateQuery = db.SteeringDirectives
                .Where(d => d.Id == candidate.Id && d.Status == SteeringStatus.Queued);
            if (requiredPlanStatus is not null)
                updateQuery = updateQuery.Where(d =>
                    db.WorkPlans.Any(w => w.CoordinatorRunId == coordinatorRunId && w.Status == requiredPlanStatus));
            var claimed = await updateQuery
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, SteeringStatus.Relayed)
                    .SetProperty(d => d.RelayedAt, relayedAt), ct)
                .ConfigureAwait(false);

            if (claimed == 1)
                return new QueuedSteering(
                    candidate.Id, candidate.Kind, candidate.TargetChildRunId, candidate.Instruction);

            // Lost the race (another pod/iteration claimed it) — try the next candidate.
        }

        return null;
    }
}

/// <summary>
/// Feature 008 Phase 2 STEERING surface. Exposes <see cref="SteerAsync"/>, the single method the HTTP
/// wave (Tank's <c>POST /api/runs/{coordinatorRunId}/steer</c>) calls to relay a human steering
/// directive to a running coordinator. Built on the mechanisms confirmed in the steering spike
/// (<c>specs/008-coordinator-agent/steering-spike.md</c>):
///
/// <list type="bullet">
/// <item><b>stop</b> — immediate hard cancel. Resolves the target child run (or every active child
/// when the target is null), cancels each via the existing
/// <see cref="RunWorkflowRegistry.Abandon"/> -&gt; <c>Cts.Cancel()</c> path, and emits a terminal
/// <c>run.cancelled</c> on the child's stream so the dispatch loop's observer resolves it and (as the
/// single writer of subtask rows) transitions the affected <see cref="Subtask"/> to <c>failed</c>.
/// The directive collapses <c>relayed -&gt; applied</c> immediately.</item>
/// <item><b>redirect</b>/<b>amend</b> — NO mid-turn interrupt. The directive is persisted
/// <c>pending -&gt; queued</c> and parked in <see cref="CoordinatorSteeringQueue"/>; the dispatch loop
/// injects it as a revised task turn at the target child's NEXT TURN BOUNDARY (<c>queued -&gt; relayed
/// -&gt; applied</c>).</item>
/// <item><b>pause</b> — DESCOPED in Phase 2 (no runtime primitive). Rejected with a
/// <see cref="SteeringValidationException"/>; never persisted, never executed.</item>
/// </list>
///
/// Every directive is persisted as a <see cref="SteeringDirective"/> row via a scoped
/// <see cref="MemoryDbContext"/>, with <see cref="SteeringDirective.CreatedBy"/> set to the steering
/// human and honest status transitions. The <c>relayed -&gt; applied</c> transitions for queued
/// redirect/amend directives happen on the dispatch loop's thread (single-writer discipline); this
/// surface only writes the initial <c>pending</c>/<c>queued</c>/<c>applied(stop)</c> states. A
/// <c>coordinator.steering</c> event is emitted on the coordinator stream for each transition.
/// </summary>
public sealed class CoordinatorSteeringService
{
    private readonly RunStreamStore _streamStore;
    private readonly RunWorkflowRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoordinatorSteeringWaitRegistry _waitRegistry;
    private readonly CoordinatorRunService? _coordinatorRunService;
    private readonly RunWorkflowFactory? _runWorkflowFactory;
    private readonly IRunStore? _runStore;
    private readonly IRunEventStream? _eventStream;
    private readonly AssemblyReviewGate? _reviewGate;
    private readonly IOutcomeSpecReplyClassifier? _replyClassifier;
    private readonly ILogger<CoordinatorSteeringService> _logger;
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    private readonly SandboxRuntimeOptions _sandboxRuntime;

    /// <summary>
    /// Hard deadline for the best-effort AgentHost pod release triggered by a steering stop. Deleting
    /// a <c>SandboxClaim</c> is a fast K8s API call under normal conditions, but if the cluster API is
    /// degraded (we have observed "Connection reset by peer"/reaper-sweep failures against it) the
    /// underlying delete can block for minutes with no intrinsic timeout. Because this release runs
    /// INLINE inside the synchronous <c>coordinator_steer</c> tool call, an unbounded block here is
    /// what wedged the calling operator turn forever (the tool never returned a result). Bounding it
    /// keeps the steer responsive; a timeout is swallowed as best-effort — the AgentHost reaper sweep
    /// is the durable backstop for an unreleased pod.
    /// </summary>
    private static readonly TimeSpan PodReleaseTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Statuses where the coordinator is still an operator-addressable control loop. In particular,
    /// AwaitingReview is not terminal for coordinator orchestration: it is the collective assembly
    /// human-review parking state, so steering and free-form messages must remain enabled.
    /// </summary>
    public static bool IsSteerableRunStatus(RunStatus status) =>
        status is RunStatus.InProgress or RunStatus.AwaitingReview;

    public CoordinatorSteeringService(
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<CoordinatorSteeringService> logger,
        CoordinatorSteeringWaitRegistry? waitRegistry = null,
        CoordinatorRunService? coordinatorRunService = null,
        RunWorkflowFactory? runWorkflowFactory = null,
        IRunStore? runStore = null,
        IRunEventStream? eventStream = null,
        AssemblyReviewGate? reviewGate = null,
        IOutcomeSpecReplyClassifier? replyClassifier = null,
        IAgentHostPodLifecycle? podLifecycle = null,
        IOptions<SandboxRuntimeOptions>? sandboxRuntime = null)
    {
        _streamStore = streamStore;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _waitRegistry = waitRegistry ?? new CoordinatorSteeringWaitRegistry();
        _coordinatorRunService = coordinatorRunService;
        _runStore = runStore;
        _eventStream = eventStream;
        _reviewGate = reviewGate;
        _replyClassifier = replyClassifier;
        _logger = logger;
        _runWorkflowFactory = runWorkflowFactory;
        _podLifecycle = podLifecycle;
        _sandboxRuntime = sandboxRuntime?.Value ?? new SandboxRuntimeOptions();
    }

    /// <summary>
    /// Releases the AgentHost pod for <paramref name="runId"/> when running pod-per-run (#350 —
    /// cancelled/failed run doesn't reliably tear down its AgentHost/sandbox process). A steering
    /// <c>stop</c>/<c>redirect</c> only cancelled a LOCAL <see cref="CancellationTokenSource"/> via
    /// <see cref="RunWorkflowRegistry.Abandon"/>, which has no effect on the remote AgentHost pod —
    /// the underlying process could keep executing tool calls and emitting new
    /// <c>tool.approval_required</c> events long after the child run was marked terminal. Best-effort:
    /// logs and swallows exceptions (mirrors the same helper in CoordinatorRunService /
    /// CoordinatorDispatchService / CoordinatorAssemblyService) so a release failure never blocks the
    /// steering directive. Pod deletion is a cluster-wide K8s action, so calling this from whichever
    /// replica handled the steer request is sufficient regardless of which replica owns the child's
    /// local workflow token.
    /// </summary>
    private async Task ReleaseAgentHostPodSafeAsync(string runId, CancellationToken ct)
    {
        if (_podLifecycle is null || !_sandboxRuntime.IsPodPerRun || string.IsNullOrEmpty(runId))
            return;

        // Bound the release so a degraded K8s API cannot block the inline steer (and therefore the
        // caller's tool call / turn) indefinitely. On the deadline the operation is abandoned as
        // best-effort; the AgentHost reaper sweep still tears the pod down out of band.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(PodReleaseTimeout);

        try
        {
            await _podLifecycle.ReleaseAgentHostPodAsync(runId, timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "CoordinatorSteeringService: AgentHost pod released for stopped child run {RunId}", runId);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "CoordinatorSteeringService: AgentHost pod release for run {RunId} exceeded {TimeoutSeconds}s and was abandoned " +
                "(best-effort; the AgentHost reaper will reclaim the pod).",
                runId, PodReleaseTimeout.TotalSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "CoordinatorSteeringService: failed to release AgentHost pod for run {RunId} (best-effort)",
                runId);
        }
    }

    /// <summary>
    /// Relays a human steering directive to a running coordinator. Validates the verb (rejecting the
    /// descoped <c>pause</c> and any unknown kind), persists the directive, then applies the
    /// per-verb mechanism described on the class. Returns the created directive's view.
    /// </summary>
    /// <param name="coordinatorRunId">The parent coordinator run being steered.</param>
    /// <param name="kind"><c>stop</c> | <c>redirect</c> | <c>amend</c> (case-insensitive).</param>
    /// <param name="targetChildRunId">A specific child run id, or null to broadcast to all active children.</param>
    /// <param name="instruction">The direction the coordinator relays (required for redirect/amend).</param>
    /// <param name="createdBy">GitHub login of the steering human.</param>
    /// <param name="createdByGitHubLogin">The caller's signed-in GitHub login, when known. Threaded to the
    /// assembly-review delivery so the gate/persistence ownership check matches a run whose
    /// <c>SubmittingUser</c> is a GitHub login (backlog-pickup runs). Null for callers/tests without one.</param>
    /// <exception cref="SteeringValidationException">The kind is unsupported/descoped, or a required instruction is missing.</exception>
    public async Task<SteeringDirectiveView> SteerAsync(
        string coordinatorRunId,
        string kind,
        string? targetChildRunId,
        string instruction,
        string createdBy,
        string? createdByGitHubLogin = null,
        CancellationToken ct = default)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized == SteeringKind.Pause)
            throw new SteeringValidationException(
                "Steering verb 'pause' is descoped in Phase 2. Use 'stop' for an immediate halt, or 'redirect'/'amend' to change direction at the next turn boundary.");
        if (!SteeringKind.IsSupported(normalized))
            throw new SteeringValidationException(
                $"Unknown steering verb '{kind}'. Supported verbs: stop, send, redirect, amend.");
        if (normalized is not SteeringKind.Send && SteeringKind.IsNextBoundary(normalized) && string.IsNullOrWhiteSpace(instruction))
            throw new SteeringValidationException(
                $"A '{normalized}' directive requires a non-empty instruction.");

        var resolvedInstruction = instruction ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow;

        // Persist the directive as pending via a scoped DbContext (this surface never touches the
        // Subtask/WorkPlan rows the dispatch loop owns, so there is no single-writer conflict).
        int directiveId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var directive = new SteeringDirective
            {
                CoordinatorRunId = coordinatorRunId,
                TargetChildRunId = targetChildRunId,
                Kind = normalized,
                Instruction = resolvedInstruction,
                Status = SteeringStatus.Pending,
                CreatedBy = createdBy,
                CreatedAt = createdAt,
            };
            db.SteeringDirectives.Add(directive);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            directiveId = directive.Id;
        }

        if (normalized == SteeringKind.Stop)
            return await ApplyStopAsync(
                coordinatorRunId, directiveId, targetChildRunId, resolvedInstruction, createdBy, createdAt, ct)
                .ConfigureAwait(false);

        if (normalized == SteeringKind.Send)
        {
            var outcomeSpecReply = await TryHandleOutcomeSpecReplyAsync(
                coordinatorRunId, directiveId, targetChildRunId, resolvedInstruction, createdBy, createdAt, ct)
                .ConfigureAwait(false);
            if (outcomeSpecReply is not null)
                return outcomeSpecReply;
        }

        // #226: at the collective assembly HUMAN-REVIEW gate the one-shot dispatch loop has already
        // handed off to the LIVE assembly loop (parked in AwaitReviewDecisionAsync). A redirect/amend/send
        // that falls through to QueueNextBoundary/QueueSend here persists `queued` but NOTHING drains it
        // (the assembly loop polls the review gate, not the steering queue) — the drain-into-void bug.
        // Intercept BEFORE the resume/queue fork and DELIVER the human's intent through the SAME mechanism
        // POST /assembly/review uses, then let the parked loop wake and own RouteAssemblyGateThroughSteeringAsync
        // as the single writer (B3: we NEVER run RouteAssembly from this HTTP thread). Returns null when the
        // run is NOT at the review gate, so the normal fork below still applies.
        if (normalized is SteeringKind.Redirect or SteeringKind.Amend or SteeringKind.Send)
        {
            var reviewGateView = await TryDeliverAtAssemblyReviewGateAsync(
                coordinatorRunId, directiveId, normalized, targetChildRunId, resolvedInstruction,
                createdBy, createdByGitHubLogin, createdAt, ct).ConfigureAwait(false);
            if (reviewGateView is not null)
                return reviewGateView;
        }

        if (normalized == SteeringKind.Send)
            return await QueueSendAsync(
                coordinatorRunId, directiveId, targetChildRunId, resolvedInstruction, createdBy, createdAt, ct)
                .ConfigureAwait(false);

        // redirect / amend. On a LIVE loop these queue and drain at the target child's next turn
        // boundary. But when the orchestration has dead-ended (rai_flagged subtask or assembly
        // conflict), the one-shot dispatch loop has already exited, so a queued directive would never
        // drain. Detect that settled/parked case and RESUME the coordinator instead.
        var resumed = await TryResumeParkedCoordinatorAsync(
            coordinatorRunId, directiveId, normalized, resolvedInstruction, createdBy, createdAt, ct)
            .ConfigureAwait(false);
        if (resumed is not null)
            return resumed;

        return await QueueNextBoundaryAsync(
            coordinatorRunId, directiveId, normalized, targetChildRunId, resolvedInstruction, createdBy, createdAt, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resumes a FAILED coordinator run in place from its last failure point for
    /// <c>POST /api/runs/{id}/retry</c> (#332), reusing the exact parked-coordinator recovery the
    /// <c>/steer redirect</c> path uses. Only the genuinely-incomplete subtasks
    /// (failed / rai_flagged / blocked) are re-dispatched; already-completed upstream work
    /// (research / proposal / PRD / UX) and the confirmed outcome spec are PRESERVED — the coordinator
    /// does NOT re-draft the outcome spec from scratch. Because the SAME run id is un-terminalized
    /// (never a fresh run), the run's original <see cref="RunOptions"/> (e.g. auto_approve_tools) are
    /// kept intact rather than reset to defaults.
    /// <para>
    /// Returns <c>true</c> when the run had a recoverable work plan and was resumed. Returns
    /// <c>false</c> when there is no recoverable work plan (e.g. the run failed at or before
    /// outcome-spec drafting, or a dispatch/assembly loop is still live) — the caller then mints a
    /// fresh coordinator run instead. Propagates <see cref="SteeringRecoveryExhaustedException"/> when
    /// every affected subtask is already over the per-subtask recovery cap; the caller treats that as
    /// "resume impossible" and falls back to a fresh full restart.
    /// </para>
    /// </summary>
    public async Task<bool> TryResumeFailedCoordinatorRunForRetryAsync(
        string coordinatorRunId, string createdBy, CancellationToken ct)
    {
        const string kind = SteeringKind.Redirect;
        const string instruction =
            "Retry: resume from the last failure point. Re-run only the failed/blocked subtask(s) and " +
            "preserve all already-completed work and the confirmed outcome spec — do not re-draft the spec.";
        var createdAt = DateTimeOffset.UtcNow;

        // Persist a synthetic redirect directive (same shape SteerAsync writes) so the shared
        // resume path has a directive row to collapse to `applied` on success.
        int directiveId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var directive = new SteeringDirective
            {
                CoordinatorRunId = coordinatorRunId,
                TargetChildRunId = null,
                Kind = kind,
                Instruction = instruction,
                Status = SteeringStatus.Pending,
                CreatedBy = createdBy,
                CreatedAt = createdAt,
                Source = SteeringSource.Coordinator,
            };
            db.SteeringDirectives.Add(directive);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            directiveId = directive.Id;
        }

        SteeringDirectiveView? resumed;
        try
        {
            resumed = await TryResumeParkedCoordinatorAsync(
                coordinatorRunId, directiveId, kind, instruction, createdBy, createdAt, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Recovery exhausted (or any resume failure): the synthetic directive never took effect,
            // so discard it before letting the caller fall back to a fresh restart.
            await DiscardDirectiveAsync(directiveId, ct).ConfigureAwait(false);
            throw;
        }

        if (resumed is not null)
            return true;

        // Not resumable (no work plan / still live): drop the synthetic directive so it does not
        // linger as an undrained `pending` row, then let the caller mint a fresh run.
        await DiscardDirectiveAsync(directiveId, ct).ConfigureAwait(false);
        return false;
    }

    private async Task DiscardDirectiveAsync(int directiveId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var directive = await db.SteeringDirectives
            .FirstOrDefaultAsync(d => d.Id == directiveId, ct).ConfigureAwait(false);
        if (directive is null)
            return;
        db.SteeringDirectives.Remove(directive);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    // -----------------------------------------------------------------------
    // UNIFIED AUTONOMOUS STEERING (rev8 §3) — the single façade every source normalizes into.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The ONE unified steering entry point (rev8 §3). Every correction-feedback source — human review,
    /// the RAI gate, the rubber-duck gate, build-test, another agent, the coordinator, or another step —
    /// normalizes into a <see cref="SteeringSignal"/> and calls this. It does ONLY three things and
    /// NEVER executes recovery (BLOCKER-1 fix): (1) persists the signal as a <see cref="SteeringDirective"/>
    /// (<c>pending</c>) reusing the same persistence as <see cref="SteerAsync"/>, (2) enqueues it for the
    /// coordinator (<c>queued</c>) via the replica-safe <see cref="CoordinatorSteeringQueue"/>, (3) emits
    /// <c>coordinator.steering_received</c> so the action is visible immediately, then wakes an
    /// assembly-blocked / idle coordinator loop. It MUST NOT call
    /// <see cref="TryResumeParkedCoordinatorAsync"/> — the conscious A/B/C/D decision (and any reset)
    /// happens later, in <c>CoordinatorSteeringDecider</c>, with a preceding, visible decision event.
    /// </summary>
    public async Task<SteeringDirectiveView> SubmitSteeringAsync(SteeringSignal signal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (!SteeringSource.IsKnown(signal.Source))
            throw new SteeringValidationException(
                $"Unknown steering source '{signal.Source}'. Known: human-review, rai, rubberduck, build-test, agent, coordinator, step.");
        if (!SteeringSeverity.IsKnown(signal.Severity))
            throw new SteeringValidationException(
                $"Unknown steering severity '{signal.Severity}'. Known: advisory, request-changes, blocking.");

        var verb = (signal.Verb ?? string.Empty).Trim().ToLowerInvariant();
        if (verb.Length == 0)
            verb = signal.Severity == SteeringSeverity.Advisory ? SteeringKind.Send : SteeringKind.Redirect;
        if (!SteeringKind.IsSupported(verb))
            throw new SteeringValidationException(
                $"Unknown steering verb '{signal.Verb}'. Supported: stop, send, redirect, amend, dispatch-fresh.");

        var targetChildRunId = signal.TargetScope?.ChildRunId;
        var targetScopeJson = signal.TargetScope?.ToJson();
        var feedback = signal.Feedback ?? string.Empty;

        int directiveId;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var directive = new SteeringDirective
            {
                CoordinatorRunId = signal.CoordinatorRunId,
                TargetChildRunId = targetChildRunId,
                Kind = verb,
                Instruction = feedback,
                Status = SteeringStatus.Pending,
                CreatedBy = signal.CreatedBy,
                CreatedAt = signal.Timestamp,
                Source = signal.Source,
                Severity = signal.Severity,
                TargetScopeJson = targetScopeJson,
                TreeHash = signal.TreeHash,
            };
            db.SteeringDirectives.Add(directive);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            directiveId = directive.Id;
        }

        // pending -> queued (persist/surface only; NEVER auto-execute).
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Queued, relayedAt: null, ct).ConfigureAwait(false);

        await EmitReplicaSafeAsync(signal.CoordinatorRunId, EventTypes.CoordinatorSteeringReceived, new
        {
            directiveId,
            source = signal.Source,
            severity = signal.Severity,
            verb,
            targetScope = signal.TargetScope,
            treeHash = signal.TreeHash,
            feedback,
        }, ct).ConfigureAwait(false);
        _waitRegistry.Signal(signal.CoordinatorRunId);

        _logger.LogInformation(
            "Unified steering received for coordinator {RunId} (directive {DirectiveId}) from source={Source} severity={Severity} verb={Verb}; queued for coordinator decision (no auto-execute)",
            signal.CoordinatorRunId, directiveId, signal.Source, signal.Severity, verb);

        return new SteeringDirectiveView(
            directiveId, signal.CoordinatorRunId, targetChildRunId, verb, feedback,
            SteeringStatus.Queued, signal.CreatedBy, signal.Timestamp, RelayedAt: null);
    }

    /// <summary>
    /// Claim-durability recovery (rev8 §3c): resets stale <c>relayed</c> directives back to
    /// <c>queued</c> so a decider that crashed AFTER the atomic <c>queued→relayed</c> claim but BEFORE
    /// committing its decision cannot strand (silently lose) the signal. A <c>relayed</c> directive
    /// whose <see cref="SteeringDirective.RelayedAt"/> is older than <paramref name="staleBefore"/> is a
    /// dead claim. A directive that DID commit is already <c>decided</c>/<c>executing</c>/<c>applied</c>
    /// (no longer <c>relayed</c>) so it is never reclaimed here; a live decider heartbeats its lease so
    /// its fresh <c>RelayedAt</c> keeps it out of range. Must be invoked in the SAME reclaim step as the
    /// plan-lease reclaim (§3b.4) so only the reclaim-winner pod resets directives — never stealing from
    /// a live decider. Returns the number of directives returned to <c>queued</c>.
    /// </summary>
    public async Task<int> ReclaimStaleRelayedDirectivesAsync(
        string coordinatorRunId, DateTimeOffset staleBefore, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        // Two-step (query candidate ids, then atomic conditional update by id) to stay translatable on
        // both SQLite and Postgres; the WHERE Status == relayed on the update keeps the claim atomic so a
        // directive a live decider just committed (no longer relayed) is never reclaimed.
        var candidateIds = await db.SteeringDirectives.AsNoTracking()
            .Where(d => d.CoordinatorRunId == coordinatorRunId && d.Status == SteeringStatus.Relayed)
            .Select(d => new { d.Id, d.RelayedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var staleIds = candidateIds
            .Where(d => d.RelayedAt == null || d.RelayedAt < staleBefore)
            .Select(d => d.Id)
            .ToList();
        if (staleIds.Count == 0)
            return 0;

        var reclaimed = await db.SteeringDirectives
            .Where(d => staleIds.Contains(d.Id) && d.Status == SteeringStatus.Relayed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, SteeringStatus.Queued)
                .SetProperty(d => d.RelayedAt, (DateTimeOffset?)null), ct)
            .ConfigureAwait(false);

        if (reclaimed > 0)
            _logger.LogWarning(
                "Claim-durability recovery: returned {Count} stale relayed steering directive(s) to queued for coordinator {RunId}",
                reclaimed, coordinatorRunId);
        return reclaimed;
    }

    /// <summary>
    /// Emits an arbitrary coordinator timeline event REPLICA-SAFELY, mirroring <see cref="EmitSteeringAsync"/>:
    /// records on the in-memory stream when this replica owns it, else appends directly to the durable
    /// <see cref="IRunEventStream"/> so cross-pod timelines stay correct. Used for the new
    /// <c>coordinator.steering_received</c> / <c>coordinator.steering_decision</c> events (§8).
    /// </summary>
    internal async Task EmitReplicaSafeAsync(
        string coordinatorRunId, string eventType, object payload, CancellationToken ct)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is not null)
        {
            entry.RecordNext(eventType, payload);
            return;
        }
        if (_eventStream is not null)
        {
            await _eventStream.AppendAsync(
                coordinatorRunId, new RunEvent(0, eventType, payload), ct).ConfigureAwait(false);
            return;
        }
        _logger.LogWarning(
            "Coordinator event {EventType} for run {RunId} could not be surfaced: this replica does not own the stream and no durable event stream is configured",
            eventType, coordinatorRunId);
    }

    // -----------------------------------------------------------------------
    // send — informational nudge queued for the coordinator-owned dispatch or assembly boundary.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Delivers an informational directive to the coordinator run timeline without altering the work
    /// plan or interrupting dispatch. The directive transitions pending → queued here; the owning
    /// dispatch loop applies it at a clean child turn boundary, or the assembly-blocked loop applies
    /// it as a retry signal.
    /// </summary>
    private async Task<SteeringDirectiveView> QueueSendAsync(
        string coordinatorRunId, int directiveId, string? targetChildRunId, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Queued, relayedAt: null, ct).ConfigureAwait(false);
        await EmitSteeringAsync(coordinatorRunId, directiveId, SteeringKind.Send, targetChildRunId, SteeringStatus.Queued, instruction, ct).ConfigureAwait(false);
        _waitRegistry.Signal(coordinatorRunId);

        _logger.LogInformation(
            "Steering send queued for coordinator {RunId} (directive {DirectiveId}); informational nudge awaits a safe boundary",
            coordinatorRunId, directiveId);

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, SteeringKind.Send, instruction,
            SteeringStatus.Queued, createdBy, createdAt, RelayedAt: null);
    }

    /// <summary>
    /// Issue #272: while the coordinator is still parked at the outcome-spec confirmation gate, the
    /// ordinary chat/steering composer has no dispatch loop to drain a plain <c>send</c>. Reuse the
    /// SAME confirm/revise resume seam the Outcome plan buttons already call: an obvious affirmative
    /// confirms the spec; anything else is treated as clarification feedback and re-drafts.
    /// Non-obvious messages fail closed to "revise" rather than silently auto-confirming.
    /// </summary>
    private async Task<SteeringDirectiveView?> TryHandleOutcomeSpecReplyAsync(
        string coordinatorRunId,
        int directiveId,
        string? targetChildRunId,
        string instruction,
        string createdBy,
        DateTimeOffset createdAt,
        CancellationToken ct)
    {
        if (_coordinatorRunService is null || string.IsNullOrWhiteSpace(instruction))
            return null;

        var spec = await _coordinatorRunService.GetOutcomeSpecAsync(coordinatorRunId, ct).ConfigureAwait(false);
        if (!string.Equals(spec?.Status, "awaiting_confirmation", StringComparison.Ordinal))
            return null;

        var replyKind = await ClassifyOutcomeSpecReplyAsync(coordinatorRunId, spec!, instruction, createdBy, ct)
            .ConfigureAwait(false);
        var outcome = replyKind == OutcomeSpecReplyKind.Confirm
            ? await _coordinatorRunService.ConfirmOutcomeSpecAsync(coordinatorRunId, createdBy, allowTaskPromotion: false, ct).ConfigureAwait(false)
            : await _coordinatorRunService.ReviseOutcomeSpecAsync(coordinatorRunId, instruction, createdBy, ct).ConfigureAwait(false);

        if (outcome != CoordinatorGateOutcome.Accepted)
        {
            _logger.LogInformation(
                "Outcome-spec chat reply for coordinator {RunId} could not be applied via the confirmation gate ({Outcome}); falling back to normal send semantics",
                coordinatorRunId, outcome);
            return null;
        }

        var appliedAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, appliedAt, ct).ConfigureAwait(false);
        await EmitSteeringAsync(
            coordinatorRunId, directiveId, SteeringKind.Send, targetChildRunId, SteeringStatus.Applied, instruction, ct)
            .ConfigureAwait(false);
        _waitRegistry.Signal(coordinatorRunId);

        _logger.LogInformation(
            "Outcome-spec chat reply for coordinator {RunId} applied as {ReplyKind} via the existing confirmation gate",
            coordinatorRunId, replyKind);

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, SteeringKind.Send, instruction,
            SteeringStatus.Applied, createdBy, createdAt, appliedAt);
    }

    /// <summary>
    /// Classifies a human's free-text reply at the outcome-spec confirmation gate as confirm vs
    /// revise by delegating to the LLM-backed <see cref="IOutcomeSpecReplyClassifier"/> (Ahmed's
    /// directive: "It shouldn't be a regex at all, we have the LLM for that").
    /// <para>
    /// Fails closed: if no classifier is wired, or the model is unavailable / returns an unparseable
    /// answer (<see cref="IOutcomeSpecReplyClassifier.ClassifyAsync"/> returns <see langword="null"/>
    /// or throws), the reply is treated as <see cref="OutcomeSpecReplyKind.Revise"/>. A transient LLM
    /// outage can therefore never silently confirm a spec the human did not clearly approve.
    /// </para>
    /// </summary>
    private async Task<OutcomeSpecReplyKind> ClassifyOutcomeSpecReplyAsync(
        string coordinatorRunId, OutcomeSpec spec, string instruction, string createdBy, CancellationToken ct)
    {
        if (_replyClassifier is null)
        {
            _logger.LogWarning(
                "No outcome-spec reply classifier is configured for coordinator {RunId}; failing closed to revise",
                coordinatorRunId);
            return OutcomeSpecReplyKind.Revise;
        }

        try
        {
            var context = new OutcomeSpecReplyClassificationContext(
                RunId: coordinatorRunId,
                ProjectId: spec.ProjectId,
                SubmittingUser: createdBy,
                Instruction: instruction,
                Goal: spec.Goal,
                DesiredOutcome: spec.DesiredOutcome,
                Scope: spec.Scope,
                Assumptions: spec.Assumptions,
                ClarifyingQuestions: spec.ClarifyingQuestions);

            var decision = await _replyClassifier.ClassifyAsync(context, ct).ConfigureAwait(false);
            if (decision is null)
            {
                _logger.LogInformation(
                    "Outcome-spec reply classifier returned no decision for coordinator {RunId}; failing closed to revise",
                    coordinatorRunId);
                return OutcomeSpecReplyKind.Revise;
            }

            return decision.Value;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Outcome-spec reply classification failed for coordinator {RunId}; failing closed to revise",
                coordinatorRunId);
            return OutcomeSpecReplyKind.Revise;
        }
    }

    // -----------------------------------------------------------------------
    // stop — immediate hard cancel (relayed -> applied collapses instantly).
    // -----------------------------------------------------------------------

    private async Task<SteeringDirectiveView> ApplyStopAsync(
        string coordinatorRunId, int directiveId, string? targetChildRunId, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        var targets = targetChildRunId is not null
            ? [targetChildRunId]
            : await ResolveActiveChildrenAsync(coordinatorRunId, ct).ConfigureAwait(false);

        foreach (var childRunId in targets)
        {
            // Real cancellation: cancel the in-flight turn's token (the only mid-turn control today).
            var abandoned = _registry.Abandon(childRunId);
            if (!abandoned)
            {
                _logger.LogInformation(
                    "Steering stop: child {ChildRunId} was not active in this replica; applying durable stop state",
                    childRunId);
            }

            await EmitChildCancelledAsync(childRunId, CancellationToken.None).ConfigureAwait(false);

            // Terminalize the child run row even when the request landed on a non-owner replica.
            // The owning watch loop polls this durable marker and abandons its local token.
            if (_runStore is not null && RunId.TryParse(childRunId, out var childId))
                await _runStore.TrySetTerminalStatusAsync(
                    childId, RunStatus.Failed, DateTimeOffset.UtcNow, "steering_stop", CancellationToken.None).ConfigureAwait(false);

            // #350: cancelling the local CancellationTokenSource above has NO effect on the remote
            // AgentHost pod — reliably stop the actual process so a detached turn cannot keep
            // executing tool calls / emitting new tool.approval_required against a run the system
            // already considers dead. Pod deletion is a cluster-wide K8s action, so this is safe to
            // call from whichever replica handled the steer request.
            await ReleaseAgentHostPodSafeAsync(childRunId, CancellationToken.None).ConfigureAwait(false);
        }

        var relayedAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, relayedAt, ct).ConfigureAwait(false);
        await EmitSteeringAsync(coordinatorRunId, directiveId, SteeringKind.Stop, targetChildRunId, SteeringStatus.Applied, instruction, ct).ConfigureAwait(false);
        _waitRegistry.Signal(coordinatorRunId);

        // For a broadcast stop (no specific child target) also terminalize the coordinator run itself.
        // Without this the coordinator's dispatch loop continues, dead-ends at assembly_blocked, and
        // the run stays InProgress — there is no clean cancellation path. StopCoordinatorRunAsync
        // uses the same TrySetTerminalStatusAsync CAS used by the assembly service for its terminal states.
        if (targetChildRunId is null)
            await StopCoordinatorRunAsync(coordinatorRunId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Steering stop applied for coordinator {RunId}: cancelled {Count} child run(s)",
            coordinatorRunId, targets.Count);

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, SteeringKind.Stop, instruction,
            SteeringStatus.Applied, createdBy, createdAt, relayedAt);
    }

    // -----------------------------------------------------------------------
    // #226 — deliver a human directive at the assembly HUMAN-REVIEW gate.
    // -----------------------------------------------------------------------

    /// <summary>
    /// #226: When the coordinator is parked at the collective assembly HUMAN-REVIEW gate
    /// (<see cref="RunStatus.AwaitingReview"/>), a human <c>redirect</c>/<c>amend</c>/<c>send</c> must NOT
    /// fall through to <see cref="QueueNextBoundaryAsync"/>/<see cref="QueueSendAsync"/> — the one-shot
    /// dispatch loop has exited and the LIVE assembly loop drains the review gate, not the steering queue,
    /// so a queued directive would drain into the void. This intercepts that case:
    /// <list type="bullet">
    /// <item><b>redirect / amend</b> → translated into an <see cref="AssemblyReviewDecision"/>
    /// (<c>RequestChanges</c>, feedback = instruction, scope per Q1) and DELIVERED through the SAME shared
    /// path <c>POST /assembly/review</c> uses (<see cref="CoordinatorAssemblyReviewPersistence.DeliverDecisionAsync"/>):
    /// the parked loop wakes and owns <c>RouteAssemblyGateThroughSteeringAsync</c> as the single writer,
    /// reusing #223 scoping + the cap-drop unconditional human budget reset + the A/B/C/D decider (B3: we
    /// only DELIVER a decision here; we never run RouteAssembly on this HTTP thread). <c>amend</c> maps to
    /// the same <c>request_changes</c> — its "never discard completed work" softens to "the decider prefers
    /// in-place": the decider picks InPlaceSteer when resumable, else DispatchFresh; we do NOT force-pin
    /// amend→InPlaceSteer (N1).</item>
    /// <item><b>send</b> at the gate is the same drain-into-void class but carries no change request. It is
    /// delivered as an ADVISORY timeline note (decider direction D): the message is surfaced via the
    /// steering event and the directive settles <c>applied</c> — no gate decision, no budget reset, and
    /// crucially NOT left <c>queued</c> forever (Q4/N3).</item>
    /// </list>
    /// Returns <c>null</c> when the run is NOT at the review gate (or the gate/run store is unavailable),
    /// so the caller's normal resume/queue fork still applies. Never persists a <c>queued</c> directive for
    /// the handled case (N2): the canonical <see cref="SteeringDirective"/> is created later by the parked
    /// loop's <c>SubmitSteeringAsync</c>; this row is settled to a non-queued terminal marker.
    /// </summary>
    private async Task<SteeringDirectiveView?> TryDeliverAtAssemblyReviewGateAsync(
        string coordinatorRunId, int directiveId, string kind, string? targetChildRunId, string instruction,
        string createdBy, string? createdByGitHubLogin, DateTimeOffset createdAt, CancellationToken ct)
    {
        // The AwaitingReview interception needs the run store (to confirm the parking state) and the
        // review gate (to deliver). Lightweight unit tests register neither; fall through to the normal
        // path so their behavior is unchanged.
        if (_runStore is null || _reviewGate is null)
            return null;
        if (!RunId.TryParse(coordinatorRunId, out var runId))
            return null;

        var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.Status != RunStatus.AwaitingReview)
            return null;

        // send: advisory note on the review timeline (decider direction D — no change request, no reset).
        if (kind == SteeringKind.Send)
            return await DeliverAdvisorySendAtReviewGateAsync(
                coordinatorRunId, directiveId, targetChildRunId, instruction, createdBy, createdAt, ct)
                .ConfigureAwait(false);

        // redirect / amend → request_changes at the gate. Q1: default to the broad all-contributors
        // fallback (TargetFiles = null) — a bare human redirect is semantically identical to
        // /assembly/review {request_changes, feedback} with no target_files, which already reverse-maps to
        // ScopeFallbackNoField (all contributors), fail-safe and observable via EmitImplicatedScopeFallback.
        // We do NOT parse files/subtasks out of the prose, nor reuse a prior reviewer's stale scope.
        // Optional clean narrowing: when a specific child run is targeted, resolve that subtask's touched
        // files and pass them so they flow through the SAME ScopeImplicatedSubtasks reverse-map.
        var targetFiles = await ResolveTargetFilesForChildAsync(coordinatorRunId, targetChildRunId, ct)
            .ConfigureAwait(false);

        var decision = new AssemblyReviewDecision(
            Approved: false,
            RequestChanges: true,
            Feedback: instruction,
            TargetFiles: targetFiles,
            Reviewer: createdBy);

        var delivery = await CoordinatorAssemblyReviewPersistence.DeliverDecisionAsync(
            _scopeFactory, _reviewGate, coordinatorRunId, decision, createdBy, createdByGitHubLogin, ct)
            .ConfigureAwait(false);

        switch (delivery)
        {
            case AssemblyReviewDeliveryResult.Accepted:
            {
                // Delivered into the LIVE armed gate on this pod; the parked loop will route it.
                var relayedAt = DateTimeOffset.UtcNow;
                await UpdateDirectiveAsync(directiveId, SteeringStatus.Relayed, relayedAt, ct).ConfigureAwait(false);
                await EmitSteeringAsync(
                    coordinatorRunId, directiveId, kind, targetChildRunId, SteeringStatus.Relayed, instruction, ct)
                    .ConfigureAwait(false);
                _waitRegistry.Signal(coordinatorRunId);
                _logger.LogInformation(
                    "Steering {Kind} (directive {DirectiveId}) delivered to the assembly review gate for coordinator {RunId}; the parked assembly loop will route it as request-changes",
                    kind, directiveId, coordinatorRunId);
                return new SteeringDirectiveView(
                    directiveId, coordinatorRunId, targetChildRunId, kind, instruction,
                    SteeringStatus.Relayed, createdBy, createdAt, relayedAt);
            }

            case AssemblyReviewDeliveryResult.Deferred:
            {
                // Gate armed on a DIFFERENT replica: durably persisted for the owning pod's poller (B2).
                var relayedAt = DateTimeOffset.UtcNow;
                await UpdateDirectiveAsync(directiveId, SteeringStatus.Deferred, relayedAt, ct).ConfigureAwait(false);
                await EmitSteeringAsync(
                    coordinatorRunId, directiveId, kind, targetChildRunId, SteeringStatus.Deferred, instruction, ct)
                    .ConfigureAwait(false);
                _waitRegistry.Signal(coordinatorRunId);
                _logger.LogInformation(
                    "Steering {Kind} (directive {DirectiveId}) deferred durably at the assembly review gate for coordinator {RunId}; the owning replica's poller will route it",
                    kind, directiveId, coordinatorRunId);
                return new SteeringDirectiveView(
                    directiveId, coordinatorRunId, targetChildRunId, kind, instruction,
                    SteeringStatus.Deferred, createdBy, createdAt, relayedAt);
            }

            case AssemblyReviewDeliveryResult.Forbidden:
                // Ownership was already validated by the /steer endpoint (IsOwner) before this runs, so a
                // gate-level Forbidden is an inconsistency rather than an expected outcome. Fall through to
                // the normal fork rather than silently swallowing the directive.
                _logger.LogWarning(
                    "Steering {Kind} (directive {DirectiveId}) for coordinator {RunId} was rejected by the review gate ownership check despite endpoint ownership validation; falling back to the normal steering path",
                    kind, directiveId, coordinatorRunId);
                return null;

            default:
                // #227 ROOT CAUSE FIX. NotPending / AlreadySubmitted: the run still says AwaitingReview
                // but the gate has already moved PAST the pending decision — the concurrent-decision
                // RACE-LOSER (a winner already delivered request_changes and drove RouteAssemblyGate...),
                // or the razor-thin ARM WINDOW between setting run.Status = AwaitingReview and arming the
                // review request. Previously this returned null and fell through to QueueNextBoundaryAsync,
                // which persisted a `queued` directive that NOTHING drains (the assembly loop polls the
                // review gate, not the steering queue) — the #227 ghost row. The winner already carries the
                // human's intent (request_changes), so this loser is redundant: settle it as the TERMINAL
                // `superseded` no-op rather than leaving it queued-into-void. B3 still holds — we never run
                // RouteAssembly from this HTTP thread; we only mark our own directive terminal.
                return await SettleSupersededAtReviewGateAsync(
                    coordinatorRunId, directiveId, kind, targetChildRunId, instruction,
                    createdBy, createdAt, delivery, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// #227: settle a redirect/amend that reached the assembly review gate but lost the delivery race (or
    /// landed in the arm window) as a TERMINAL <c>superseded</c> no-op. The winning decision already carries
    /// the human's request-changes intent and drives the single RouteAssembly pass, so this directive is
    /// redundant. Emitting a terminal marker (never <c>queued</c>) closes the "never leave a directive
    /// queued-into-void" invariant for every path. Does not wake the gate, reset any subtask, or touch budget.
    /// </summary>
    private async Task<SteeringDirectiveView> SettleSupersededAtReviewGateAsync(
        string coordinatorRunId, int directiveId, string kind, string? targetChildRunId, string instruction,
        string createdBy, DateTimeOffset createdAt, AssemblyReviewDeliveryResult delivery, CancellationToken ct)
    {
        var settledAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Superseded, settledAt, ct).ConfigureAwait(false);
        await EmitSteeringAsync(
            coordinatorRunId, directiveId, kind, targetChildRunId, SteeringStatus.Superseded, instruction, ct)
            .ConfigureAwait(false);
        _logger.LogInformation(
            "Steering {Kind} (directive {DirectiveId}) reached the assembly review gate for coordinator {RunId} but the gate had already moved past pending ({Delivery}); settled terminal as superseded (no-op, #227) instead of queuing a ghost row",
            kind, directiveId, coordinatorRunId, delivery);
        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, kind, instruction,
            SteeringStatus.Superseded, createdBy, createdAt, settledAt);
    }

    /// <summary>
    /// #226 (Q4/N3): a <c>send</c> at the assembly review gate has no running child and no change request,
    /// so it cannot drain as a review decision. Rather than leave it <c>queued</c> forever (drain-into-void)
    /// it is delivered as an ADVISORY note: the message is surfaced on the coordinator timeline and the
    /// directive settles <c>applied</c>. It does not wake the gate, reset the budget, or reset any subtask.
    /// </summary>
    private async Task<SteeringDirectiveView> DeliverAdvisorySendAtReviewGateAsync(
        string coordinatorRunId, int directiveId, string? targetChildRunId, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        var relayedAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, relayedAt, ct).ConfigureAwait(false);
        await EmitSteeringAsync(
            coordinatorRunId, directiveId, SteeringKind.Send, targetChildRunId, SteeringStatus.Applied, instruction, ct)
            .ConfigureAwait(false);
        _waitRegistry.Signal(coordinatorRunId);
        _logger.LogInformation(
            "Steering send (directive {DirectiveId}) delivered as an advisory note at the assembly review gate for coordinator {RunId}; no review decision or budget reset",
            directiveId, coordinatorRunId);
        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, SteeringKind.Send, instruction,
            SteeringStatus.Applied, createdBy, createdAt, relayedAt);
    }

    /// <summary>
    /// #226 Q1 optional narrowing: resolves the <see cref="AssemblyReviewDecision.TargetFiles"/> for a
    /// targeted child run. When <paramref name="targetChildRunId"/> is a subtask of this coordinator's work
    /// plan, its child run's touched files (parsed from the run diff, the same source as the assembly's
    /// <c>touchedFilesBySubtask</c>) are returned so they flow through the SAME
    /// <c>ScopeImplicatedSubtasks</c> reverse-map (that subtask ∪ any co-touching subtasks). Returns
    /// <c>null</c> (the broad all-contributors fallback) when no child is targeted, the child is not a
    /// subtask of this plan, or it touched no files.
    /// </summary>
    private async Task<IReadOnlyList<string>?> ResolveTargetFilesForChildAsync(
        string coordinatorRunId, string? targetChildRunId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetChildRunId) || _runStore is null)
            return null;

        bool belongsToPlan;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            belongsToPlan = await db.Subtasks
                .AsNoTracking()
                .AnyAsync(
                    s => s.ChildRunId == targetChildRunId
                        && db.WorkPlans.Any(w => w.Id == s.WorkPlanId && w.CoordinatorRunId == coordinatorRunId),
                    ct)
                .ConfigureAwait(false);
        }

        if (!belongsToPlan || !RunId.TryParse(targetChildRunId, out var childId))
            return null;

        var childRun = await _runStore.GetAsync(childId, ct).ConfigureAwait(false);
        if (childRun is null)
            return null;

        var touched = AssemblyPlanning.ExtractTouchedFiles(childRun.Diff);
        return touched.Count > 0 ? touched.OrderBy(f => f, StringComparer.Ordinal).ToList() : null;
    }

    // -----------------------------------------------------------------------
    // redirect / amend — queue for the child's next turn boundary.
    // -----------------------------------------------------------------------

    private async Task EmitChildCancelledAsync(string childRunId, CancellationToken ct)
    {
        // Emit a terminal run.cancelled so observers resolve the child as failed. If this replica owns
        // the in-memory stream, record there so local subscribers wake; otherwise append directly to
        // the durable event stream so reconnect/replay and non-owner stops still expose the terminal.
        var childEntry = _streamStore.Get(childRunId);
        if (childEntry is not null)
        {
            if (!childEntry.HasEventType(EventTypes.RunCancelled))
                childEntry.RecordNext(EventTypes.RunCancelled, new { reason = "steering_stop" });
            _streamStore.Complete(childRunId);
            if (_runWorkflowFactory is not null)
                _ = _runWorkflowFactory.PersistRunEventsAsync(childRunId);
            return;
        }

        if (_eventStream is not null)
        {
            await _eventStream.AppendAsync(
                childRunId,
                new RunEvent(0, EventTypes.RunCancelled, new { reason = "steering_stop" }),
                ct).ConfigureAwait(false);
            await _eventStream.CompleteAsync(childRunId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Terminates the coordinator run row as Failed/stopped. Called by <see cref="ApplyStopAsync"/>
    /// for broadcast stops so the coordinator run exits cleanly instead of continuing to the dispatch
    /// loop and dead-ending at <c>assembly_blocked</c>. Mirrors the <c>TerminalizeCoordinatorRunAsync</c>
    /// pattern in <see cref="CoordinatorAssemblyService"/>: uses the same CAS guard so it is a no-op
    /// if the run row is already terminal or absent.
    /// </summary>
    private async Task StopCoordinatorRunAsync(string coordinatorRunId, CancellationToken ct)
    {
        if (_runStore is null || !RunId.TryParse(coordinatorRunId, out var id))
            return;
        await _runStore.TrySetTerminalStatusAsync(id, RunStatus.Failed, DateTimeOffset.UtcNow, "steering_stop", ct)
            .ConfigureAwait(false);
        // #350: the coordinator's own AgentHost pod (when pod-per-run) also needs reliable teardown —
        // mirrors the child-release call in ApplyStopAsync above.
        await ReleaseAgentHostPodSafeAsync(coordinatorRunId, ct).ConfigureAwait(false);
        _logger.LogInformation("Steering stop: coordinator run {RunId} terminated as stopped", coordinatorRunId);
    }

    private async Task<SteeringDirectiveView> QueueNextBoundaryAsync(
        string coordinatorRunId, int directiveId, string kind, string? targetChildRunId, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        // Persist the directive as queued; the durable SteeringDirectives row IS the queue. The
        // dispatch loop on the pod owning this coordinator run drains it from Postgres at the target
        // child's next turn boundary (replica-safe — no in-memory hand-off that a second pod misses).
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Queued, relayedAt: null, ct).ConfigureAwait(false);
        await EmitSteeringAsync(coordinatorRunId, directiveId, kind, targetChildRunId, SteeringStatus.Queued, instruction, ct).ConfigureAwait(false);
        _waitRegistry.Signal(coordinatorRunId);

        // For redirect targeting a specific in-progress child: force-complete that child's stream so
        // the active dispatch loop immediately processes a failure and applies this queued directive
        // without waiting for a natural turn boundary (which may never arrive for a stuck child).
        if (kind == SteeringKind.Redirect && targetChildRunId is not null)
            TryForceCompleteChildForRedirect(coordinatorRunId, targetChildRunId, directiveId);

        _logger.LogInformation(
            "Steering {Kind} queued for coordinator {RunId} (directive {DirectiveId}); applies at the target child's next turn boundary",
            kind, coordinatorRunId, directiveId);

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, kind, instruction,
            SteeringStatus.Queued, createdBy, createdAt, RelayedAt: null);
    }

    /// <summary>
    /// For a redirect directive targeting a specific in-progress child, force-completes the child's
    /// stream with <c>run.cancelled</c> so the dispatch loop's observer resolves the child as failed
    /// and immediately picks up the queued redirect directive (via <see cref="CoordinatorSteeringQueue.TryTakeRedirectForChildAsync"/>).
    /// Only acts when the child stream entry exists and is not already completed. Does not cancel the
    /// workflow token (that is <see cref="ApplyStopAsync"/>'s job) — this is a stream-level signal.
    /// </summary>
    private void TryForceCompleteChildForRedirect(string coordinatorRunId, string childRunId, int directiveId)
    {
        var childEntry = _streamStore.Get(childRunId);
        if (childEntry is null || childEntry.IsCompleted)
            return;

        childEntry.RecordNext(EventTypes.RunCancelled, new { reason = "steering_redirect", directiveId });
        _streamStore.Complete(childRunId);
        if (_runWorkflowFactory is not null)
            _ = _runWorkflowFactory.PersistRunEventsAsync(childRunId);

        // Terminalize the child run row in the DB so it no longer shows InProgress forever.
        // Mirrors the same fix in ApplyStopAsync — the stream-level signal alone does not update
        // the run store row.
        if (_runStore is not null && RunId.TryParse(childRunId, out var childId))
            _ = _runStore.TrySetTerminalStatusAsync(childId, RunStatus.Failed, DateTimeOffset.UtcNow, "steering_redirect", CancellationToken.None);

        // Also abandon the workflow token so the watch loop exits cleanly.
        _registry.Abandon(childRunId);

        // #350: as in ApplyStopAsync, the local token cancel above has no effect on the remote
        // AgentHost pod — reliably tear it down so a detached turn cannot keep running/emitting
        // tool.approval_required for a child the coordinator already considers redirected away from.
        _ = ReleaseAgentHostPodSafeAsync(childRunId, CancellationToken.None);

        _logger.LogInformation(
            "Steering redirect (directive {DirectiveId}): force-completed stuck child {ChildRunId} for coordinator {CoordRunId}",
            directiveId, childRunId, coordinatorRunId);
    }

    // -----------------------------------------------------------------------
    // redirect / amend on a PARKED/FAILED coordinator — auto-resume recovery.
    // -----------------------------------------------------------------------

    /// <summary>Per-subtask recovery attempt cap — a flagged/failed subtask cannot auto-resume forever.</summary>
    internal const int MaxRecoveryAttempts = 3;

    /// <summary>
    /// When a coordinator has dead-ended — a <c>rai_flagged</c> subtask blocked assembly, or a
    /// collective-assembly conflict parked the run — the one-shot dispatch loop has already exited and
    /// a queued redirect/amend would never drain. This resumes the coordinator: it resets the affected
    /// subtasks to <c>pending</c> with the steering instruction + failure context as guidance, bumps
    /// each subtask's recovery-attempt counter (capped), un-terminalizes the coordinator run, re-opens
    /// its stream, and re-arms <see cref="ICoordinatorDispatch.StartDispatch"/> so the loop re-dispatches
    /// the reset frontier. The reset is single-writer-safe ONLY because the loop is confirmed not running
    /// (no active children + <see cref="ICoordinatorDispatch.IsDispatchActive"/> is false), mirroring the
    /// request-changes precedent.
    /// </summary>
    /// <returns>
    /// The applied directive view when the coordinator was parked and resumed; <c>null</c> when the
    /// coordinator is NOT in a recoverable settled state (the caller then falls back to queueing).
    /// </returns>
    /// <exception cref="SteeringRecoveryExhaustedException">Every affected subtask is over the attempt cap.</exception>
    private async Task<SteeringDirectiveView?> TryResumeParkedCoordinatorAsync(
        string coordinatorRunId, int directiveId, string kind, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<MemoryDbContext>();

        var plan = await db.WorkPlans
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null)
            return null; // No work plan — nothing to recover; fall back to legacy queue behavior.

        if (!RunId.TryParse(coordinatorRunId, out var runId))
            return null;

        // A plan exists — this MIGHT be a recoverable parked coordinator, so resolve the run store and
        // dispatch engine now (kept out of the no-plan path so the lightweight steering unit tests,
        // which register only MemoryDbContext, never need them).
        var runStore = sp.GetRequiredService<IRunStore>();
        var dispatch = sp.GetRequiredService<ICoordinatorDispatch>();

        var run = await runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return null;

        // Only resume a SETTLED, recoverable orchestration: the coordinator run terminalized
        // (Failed/MergeFailed), or the plan parked at an assembly-blocked/failed state. A mid-flight
        // dispatch or assembly is left alone (fall back to queue).
        var runIsTerminalRecoverable = run.Status is RunStatus.Failed or RunStatus.MergeFailed;
        var planIsParked = plan.Status is WorkPlanStatus.AssemblyBlocked or WorkPlanStatus.AssemblyFailed;
        if (!runIsTerminalRecoverable && !planIsParked)
        {
            // ORPHANED DISPATCH. The work plan is still dispatching (or the run is in_progress) but no
            // loop is running for it (IsDispatchActive is false) — the in-memory dispatch loop died
            // without a restart, or a restart never re-armed it. A queued redirect/amend would drain
            // into the void. Re-arm dispatch so the recovery-aware loop reconciles the in-flight
            // subtasks and advances the frontier, then fall through to QueueNextBoundaryAsync so the
            // directive drains at the next boundary. Subtasks are NOT reset here: the re-armed loop
            // re-observes them, preserving any terminal child result the orphan already produced.
            var planDispatching = plan.Status is WorkPlanStatus.Dispatching;
            var runInProgress = run.Status is RunStatus.InProgress;
            if ((planDispatching || runInProgress) && !dispatch.IsDispatchActive(coordinatorRunId))
            {
                var orphanContext = new CoordinatorDispatchContext(
                    CoordinatorRunId: coordinatorRunId,
                    RepositoryPath: run.RepositoryPath,
                    OriginatingBranch: run.OriginatingBranch,
                    SubmittingUser: run.SubmittingUser,
                    ProjectId: run.ProjectId);
                dispatch.StartDispatch(orphanContext);
                _logger.LogInformation(
                    "Steering {Kind} on orphaned coordinator {RunId} (directive {DirectiveId}); re-armed dispatch and queued the directive to drain at the next boundary",
                    kind, coordinatorRunId, directiveId);
            }

            return null; // fall back to queueing; the live (or re-armed) loop drains the directive
        }

        // Single-writer guard: the dispatch loop must be confirmed NOT running before we mutate
        // subtask rows (it is the sole writer while alive).
        if (dispatch.IsDispatchActive(coordinatorRunId))
            return null;

        var subtasks = await db.Subtasks
            .Where(s => s.WorkPlanId == plan.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        // Distinct behavior per verb:
        //   redirect: SURGICAL override (#309) — re-dispatches ONLY the genuinely-incomplete subtasks
        //     (failed / rai_flagged / blocked). Subtasks already assemble_ready/completed are PRESERVED
        //     and never re-run. When EVERY subtask already succeeded the park is an assembly-PHASE
        //     issue: a build/test-infra failure OR a now-stale ineligible_subtasks eligibility-gate block
        //     (#309 follow-up — FitTrackE2E-v12) re-arms ASSEMBLY against the existing children (no
        //     re-dispatch), while a genuine integration conflict regenerates the conflicting
        //     assemble_ready children — never a full-workplan restart (the pre-#309 bug).
        //   amend: additive — only unblocks hard RAI gates (rai_flagged) without discarding failed
        //     work. If there are no RAI-blocked subtasks to unblock, falls through to queue so the
        //     instruction is applied at the next natural boundary (no completed work is discarded).
        var now = DateTimeOffset.UtcNow;
        var resetIds = new List<int>();
        var reArmAssemblyOnly = false;

        if (kind == SteeringKind.Amend)
        {
            var flagged = subtasks.Where(s => s.Status == SubtaskStatus.RaiFlagged).ToList();
            if (flagged.Count == 0)
                return null; // amend never discards completed/failed work; fall through to queue
            ResetSubtasksForRedispatch(flagged, instruction, now, resetIds);
        }
        else // redirect (and any future override verbs)
        {
            // SURGICAL re-dispatch (#309). A redirect on a parked coordinator re-runs ONLY the subtasks
            // that reached a terminal-but-UNSATISFIED state (failed / rai_flagged / blocked). Subtasks
            // that succeeded (assemble_ready / completed) are PRESERVED — never a full-workplan restart.
            var terminalUnsatisfied = subtasks
                .Where(s => SubtaskStatus.IsTerminal(s.Status) && !SubtaskStatus.Satisfies(s.Status))
                .ToList();
            var allSatisfied = subtasks.All(s => SubtaskStatus.Satisfies(s.Status));

            if (terminalUnsatisfied.Count > 0)
            {
                // Scoped retry: reset only the failed/flagged/blocked children (e.g. Skyler+Hank),
                // leaving already-successful ones (Walt+Jesse) untouched.
                ResetSubtasksForRedispatch(terminalUnsatisfied, instruction, now, resetIds);
            }
            else if (allSatisfied
                && (AssemblyPlanning.IsRetryableBuildTestInfraReason(plan.AssemblyStatusReason)
                    || AssemblyPlanning.IsStaleIneligibleSubtasksReason(plan.AssemblyStatusReason)))
            {
                // Every subtask already succeeded and the park reason is either an assembly-PHASE
                // infrastructure failure (build/test-infra timeout, etc.) or a STALE eligibility-gate
                // block (ineligible_subtasks) whose named subtasks have since gone green — the children
                // are fine either way. Retry ASSEMBLY against them; re-running any subtask would only
                // discard completed work (#309 — the FitTrack wedge where a redirect re-ran all green
                // subtasks then never advanced). The ineligible_subtasks branch specifically closes the
                // #309 follow-up gap (FitTrackE2E-v12): without it, a stale ineligible_subtasks reason
                // fell through to the "integration conflict" branch below and reset every assemble_ready
                // subtask a second time even though nothing actually conflicted.
                reArmAssemblyOnly = true;
            }
            else if (allSatisfied)
            {
                // All children assemble_ready but their OUTPUTS conflicted during collective assembly
                // (integration conflict). Re-arming assembly alone re-hits the same conflict, so the
                // conflicting subtasks must regenerate against the latest integration branch. Only
                // assemble_ready children are reset; a completed no-change subtask is left intact.
                var ready = subtasks.Where(s => s.Status == SubtaskStatus.AssembleReady).ToList();
                if (ready.Count > 0)
                    ResetSubtasksForRedispatch(ready, instruction, now, resetIds);
                else
                    reArmAssemblyOnly = true; // every child completed no-change — nothing to regenerate
            }
            // else: only pending / in-flight children remain (no terminal failure) — reset nothing and
            // just re-arm dispatch below so the loop picks up the existing frontier.
        }

        // Move the plan to the correct phase before the loop spins up (single-writer safe: dispatch is
        // confirmed not running above). A scoped re-dispatch returns to dispatching; an assembly-only
        // re-arm goes straight back to awaiting_assembly so StartAssembly's CAS can re-claim it.
        plan.Status = reArmAssemblyOnly ? WorkPlanStatus.AwaitingAssembly : WorkPlanStatus.Dispatching;
        plan.AssemblyStage = null;
        plan.AssemblyTerminalStage = null;
        plan.AssemblyStatusReason = null;
        plan.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Un-terminalize the coordinator run so the project runs list/detail show it live again.
        if (runIsTerminalRecoverable)
            await runStore.UpdateStatusAsync(runId, RunStatus.InProgress, endedAt: null, ct).ConfigureAwait(false);

        // Re-open the coordinator stream IN PLACE (assembly's block had completed it) so the resumed
        // dispatch/assembly loops emit onto a live entry again. Reopening (issue #388) clears the
        // completed/awaiting-review flags WITHOUT discarding the history already recorded, so the
        // recovery event is APPENDED after the coordinator's prior messages instead of replacing them
        // (removing + recreating the entry would have started a blank history).
        var entry = _streamStore.Reopen(coordinatorRunId)
            ?? _streamStore.Create(coordinatorRunId, run.SubmittingUser);
        entry.RecordNext(EventTypes.CoordinatorRecovered, new
        {
            reason = reArmAssemblyOnly ? "steering_resume_assembly" : "steering_resume",
            directiveId,
            resetSubtaskIds = resetIds,
            instruction,
        });

        // directive: collapse to applied (the resume took effect immediately, like stop).
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, now, ct).ConfigureAwait(false);
        await EmitSteeringAsync(coordinatorRunId, directiveId, kind, targetChildRunId: null, SteeringStatus.Applied, instruction, ct).ConfigureAwait(false);
        _waitRegistry.Signal(coordinatorRunId);

        // Re-arm the correct engine (idempotent). For a scoped re-dispatch the loop re-runs ONLY the
        // reset frontier; when those children finish it returns to awaiting_assembly and re-triggers
        // assembly (DB CAS guards exactly-once). For an assembly-only re-arm (every child already
        // assemble_ready) we drive assembly directly against the preserved children — completed work
        // is never re-run, and the plan advances past the block to RAI/review/merge.
        var context = new CoordinatorDispatchContext(
            CoordinatorRunId: coordinatorRunId,
            RepositoryPath: run.RepositoryPath,
            OriginatingBranch: run.OriginatingBranch,
            SubmittingUser: run.SubmittingUser,
            ProjectId: run.ProjectId);
        if (reArmAssemblyOnly)
        {
            var assembly = sp.GetRequiredService<ICoordinatorAssembly>();
            assembly.StartAssembly(context);
        }
        else
        {
            dispatch.StartDispatch(context);
        }

        _logger.LogInformation(
            "Steering {Kind} resumed parked coordinator {RunId} (directive {DirectiveId}); {Action}",
            kind, coordinatorRunId, directiveId,
            reArmAssemblyOnly
                ? "preserved all completed subtasks and re-armed assembly"
                : $"reset subtasks [{string.Join(",", resetIds)}] to pending and re-armed dispatch");

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, TargetChildRunId: null, kind, instruction,
            SteeringStatus.Applied, createdBy, createdAt, RelayedAt: now);
    }

    /// <summary>
    /// Resets the given genuinely-incomplete subtasks to <c>pending</c> for a scoped re-dispatch:
    /// stamps recovery guidance, bumps each subtask's recovery-attempt counter, clears its child-run
    /// id, and records the reset id. Enforces the per-subtask <see cref="MaxRecoveryAttempts"/> cap —
    /// throws <see cref="SteeringRecoveryExhaustedException"/> when EVERY affected subtask is already
    /// over the cap so the coordinator stays parked rather than looping forever. Only subtasks under
    /// the cap are reset; already-satisfied subtasks are never passed here (caller filters them out).
    /// </summary>
    private static void ResetSubtasksForRedispatch(
        List<Subtask> affected, string instruction, DateTimeOffset now, List<int> resetIds)
    {
        var eligible = affected.Where(s => s.RecoveryAttempts < MaxRecoveryAttempts).ToList();
        if (eligible.Count == 0)
            throw new SteeringRecoveryExhaustedException(
                $"Recovery attempt cap ({MaxRecoveryAttempts}) reached for every affected subtask " +
                $"[{string.Join(", ", affected.Select(s => s.Id))}]; the coordinator stays parked. " +
                "Use run retry to re-run the whole coordinator.");

        foreach (var subtask in eligible)
        {
            subtask.RecoveryGuidance = BuildRecoveryGuidance(subtask.Status, instruction, subtask.RecoveryAttempts + 1);
            subtask.Status = SubtaskStatus.Pending;
            subtask.RecoveryAttempts += 1;
            subtask.ChildRunId = null;
            subtask.UpdatedAt = now;
            resetIds.Add(subtask.Id);
        }
    }

    /// <summary>
    /// Builds the guidance text appended to a re-dispatched worker's task: the human's steering
    /// instruction plus a short failure-context line derived from the prior terminal status.
    /// </summary>
    private static string BuildRecoveryGuidance(string priorStatus, string instruction, int attempt)
    {
        var context = priorStatus switch
        {
            SubtaskStatus.RaiFlagged =>
                "A prior attempt was flagged by the Responsible AI reviewer and was not shipped.",
            SubtaskStatus.Failed =>
                "A prior attempt failed before producing shippable changes.",
            SubtaskStatus.AssembleReady =>
                "A prior attempt's changes conflicted during collective assembly with another subtask.",
            _ => "A prior attempt did not complete successfully.",
        };

        return
            $"Recovery guidance from the coordinator (attempt {attempt}): {instruction}\n\n" +
            $"Context: {context} Re-do this work against the latest repository state and address the feedback above.";
    }

    // -----------------------------------------------------------------------
    // EF + stream helpers.
    // -----------------------------------------------------------------------

    private async Task<List<string>> ResolveActiveChildrenAsync(string coordinatorRunId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var plan = await db.WorkPlans.AsNoTracking()
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
        if (plan is null)
            return [];

        return await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == plan.Id
                && s.ChildRunId != null
                && (s.Status == SubtaskStatus.Dispatched || s.Status == SubtaskStatus.Running))
            .Select(s => s.ChildRunId!)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private async Task UpdateDirectiveAsync(int directiveId, string status, DateTimeOffset? relayedAt, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.SteeringDirectives.FirstOrDefaultAsync(d => d.Id == directiveId, ct).ConfigureAwait(false);
        if (row is null)
            return;
        row.Status = status;
        if (relayedAt is not null)
            row.RelayedAt = relayedAt;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Emits the <c>coordinator.steering</c> timeline event REPLICA-SAFELY. When this replica owns the
    /// coordinator's in-memory stream, it records there (which best-effort mirrors to the durable
    /// <c>RunEvents</c> table via <see cref="RunStreamEntry.RecordNext"/>). When it does not — i.e. the
    /// <c>/steer</c> POST was load-balanced to a replica other than the one running the coordinator's
    /// dispatch/assembly loop — it appends the event DIRECTLY to the durable <see cref="IRunEventStream"/>
    /// so the operator's timeline still surfaces the message. Without this fallback the event was
    /// silently dropped by the <c>entry?.RecordNext</c> null-conditional at <c>replicas:2</c>, so a
    /// steered message never appeared in the session (the same cross-pod class of bug that
    /// <see cref="CoordinatorSteeringQueue"/> already fixed for redirect/amend delivery). Mirrors the
    /// durable fallback in <see cref="EmitChildCancelledAsync"/>.
    /// </summary>
    private async Task EmitSteeringAsync(
        string coordinatorRunId, int directiveId, string kind, string? targetChildRunId, string status, string instruction,
        CancellationToken ct)
    {
        var payload = CoordinatorSteeringEvent.Payload(directiveId, kind, targetChildRunId, status, instruction);

        var entry = _streamStore.Get(coordinatorRunId);
        if (entry is not null)
        {
            entry.RecordNext(EventTypes.CoordinatorSteering, payload);
            return;
        }

        if (_eventStream is not null)
        {
            await _eventStream.AppendAsync(
                coordinatorRunId,
                new RunEvent(0, EventTypes.CoordinatorSteering, payload),
                ct).ConfigureAwait(false);
            return;
        }

        _logger.LogWarning(
            "Steering {Kind} for coordinator {RunId} (directive {DirectiveId}) could not be surfaced: this replica does not own the stream and no durable event stream is configured",
            kind, coordinatorRunId, directiveId);
    }
}
