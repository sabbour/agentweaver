using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
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
    /// in-flight dispatch, or reset any subtask — the message is persisted and applied immediately as
    /// a <c>coordinator.steering</c> event so the operator can observe it in the run timeline.
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

    public static bool IsSupported(string kind) => kind is Stop or Send or Redirect or Amend;

    /// <summary>True for the verbs that queue and apply at the child's next turn boundary.</summary>
    public static bool IsNextBoundary(string kind) => kind is Redirect or Amend;
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
    public const string Applied = "applied";
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
/// Cross-pod seam between the steering surface (an HTTP-thread call to
/// <see cref="CoordinatorSteeringService.SteerAsync"/>) and the dispatch loop that owns child-run
/// control. A <c>redirect</c>/<c>amend</c> directive is persisted as a <c>queued</c>
/// <see cref="SteeringDirective"/> row by the steering surface; the dispatch loop on the pod that
/// owns the coordinator run drains it from Postgres at the target child's next turn boundary and
/// injects a revised task turn. <c>stop</c> never goes through this queue — it is an immediate cancel.
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
        ClaimAsync(coordinatorRunId, childRunId, redirectOnly: false, ct);

    /// <summary>
    /// Like <see cref="TryTakeForChildAsync"/> but only claims a <see cref="SteeringKind.Redirect"/>
    /// directive. Used by the dispatch loop when a child has failed (rather than completed normally)
    /// and the caller needs to apply only a redirect — not an amend — as a re-dispatch override.
    /// </summary>
    public Task<QueuedSteering?> TryTakeRedirectForChildAsync(
        string coordinatorRunId, string childRunId, CancellationToken ct = default) =>
        ClaimAsync(coordinatorRunId, childRunId, redirectOnly: true, ct);

    /// <summary>
    /// Finds the oldest matching <c>queued</c> directive and claims it with a conditional
    /// <c>queued -&gt; relayed</c> <see cref="EntityFrameworkQueryableExtensions"/> update. Only one
    /// caller (across all pods) can win that update; a loser retries against the next candidate. This
    /// is the at-most-once mechanism that makes the queue replica-safe.
    /// </summary>
    private async Task<QueuedSteering?> ClaimAsync(
        string coordinatorRunId, string childRunId, bool redirectOnly, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        while (!ct.IsCancellationRequested)
        {
            var query = db.SteeringDirectives.AsNoTracking()
                .Where(d => d.CoordinatorRunId == coordinatorRunId
                    && d.Status == SteeringStatus.Queued
                    && (d.TargetChildRunId == null || d.TargetChildRunId == childRunId));
            if (redirectOnly)
                query = query.Where(d => d.Kind == SteeringKind.Redirect);

            var candidate = await query
                .OrderBy(d => d.Id)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (candidate is null)
                return null;

            // Atomic claim: only one writer (any pod) can flip this row queued -> relayed. The
            // conditional WHERE Status == queued is the gate that guarantees at-most-once delivery.
            DateTimeOffset? relayedAt = DateTimeOffset.UtcNow;
            var claimed = await db.SteeringDirectives
                .Where(d => d.Id == candidate.Id && d.Status == SteeringStatus.Queued)
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
    private readonly RunWorkflowFactory? _runWorkflowFactory;
    private readonly IRunStore? _runStore;
    private readonly IRunEventStream? _eventStream;
    private readonly ILogger<CoordinatorSteeringService> _logger;

    public CoordinatorSteeringService(
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        IServiceScopeFactory scopeFactory,
        ILogger<CoordinatorSteeringService> logger,
        RunWorkflowFactory? runWorkflowFactory = null,
        IRunStore? runStore = null,
        IRunEventStream? eventStream = null)
    {
        _streamStore = streamStore;
        _registry = registry;
        _scopeFactory = scopeFactory;
        _runStore = runStore;
        _eventStream = eventStream;
        _logger = logger;
        _runWorkflowFactory = runWorkflowFactory;
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
    /// <exception cref="SteeringValidationException">The kind is unsupported/descoped, or a required instruction is missing.</exception>
    public async Task<SteeringDirectiveView> SteerAsync(
        string coordinatorRunId,
        string kind,
        string? targetChildRunId,
        string instruction,
        string createdBy,
        CancellationToken ct)
    {
        var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();

        if (normalized == SteeringKind.Pause)
            throw new SteeringValidationException(
                "Steering verb 'pause' is descoped in Phase 2. Use 'stop' for an immediate halt, or 'redirect'/'amend' to change direction at the next turn boundary.");
        if (!SteeringKind.IsSupported(normalized))
            throw new SteeringValidationException(
                $"Unknown steering verb '{kind}'. Supported verbs: stop, send, redirect, amend.");
        if (SteeringKind.IsNextBoundary(normalized) && string.IsNullOrWhiteSpace(instruction))
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
            return await ApplySendAsync(
                coordinatorRunId, directiveId, resolvedInstruction, createdBy, createdAt, ct)
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

    // -----------------------------------------------------------------------
    // send — informational nudge, applied immediately, no plan/dispatch change.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Delivers an informational directive to the coordinator run timeline without altering the work
    /// plan or interrupting dispatch. The directive transitions pending → applied in a single step;
    /// no queue, no re-arm, no subtask reset. Useful for operator notes, mid-run context updates, or
    /// audit entries that do not require the coordinator to change direction.
    /// </summary>
    private async Task<SteeringDirectiveView> ApplySendAsync(
        string coordinatorRunId, int directiveId, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        var relayedAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, relayedAt, ct).ConfigureAwait(false);
        EmitSteering(coordinatorRunId, directiveId, SteeringKind.Send, targetChildRunId: null, SteeringStatus.Applied, instruction);

        _logger.LogInformation(
            "Steering send applied for coordinator {RunId} (directive {DirectiveId}); informational nudge delivered",
            coordinatorRunId, directiveId);

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, TargetChildRunId: null, SteeringKind.Send, instruction,
            SteeringStatus.Applied, createdBy, createdAt, relayedAt);
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
        }

        var relayedAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, relayedAt, ct).ConfigureAwait(false);
        EmitSteering(coordinatorRunId, directiveId, SteeringKind.Stop, targetChildRunId, SteeringStatus.Applied, instruction);

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
        EmitSteering(coordinatorRunId, directiveId, kind, targetChildRunId, SteeringStatus.Queued, instruction);

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
        //   redirect: override — resets rai_flagged + failed subtasks so the coordinator re-dispatches
        //     with the new instruction. Falls back to assembly-ready and then all subtasks for pure
        //     assembly-conflict recovery.
        //   amend: additive — only unblocks hard RAI gates (rai_flagged) without discarding failed
        //     work. If there are no RAI-blocked subtasks to unblock, falls through to queue so the
        //     instruction is applied at the next natural boundary (no completed work is discarded).
        List<Subtask> affected;
        if (kind == SteeringKind.Amend)
        {
            affected = subtasks.Where(s => s.Status == SubtaskStatus.RaiFlagged).ToList();
            if (affected.Count == 0)
                return null; // amend never discards completed/failed work; fall through to queue
        }
        else // redirect (and any future override verbs)
        {
            affected = subtasks
                .Where(s => s.Status is SubtaskStatus.RaiFlagged or SubtaskStatus.Failed)
                .ToList();
            if (affected.Count == 0)
                affected = subtasks.Where(s => s.Status == SubtaskStatus.AssembleReady).ToList();
            if (affected.Count == 0)
                affected = subtasks;
        }

        var eligible = affected.Where(s => s.RecoveryAttempts < MaxRecoveryAttempts).ToList();
        if (eligible.Count == 0)
            throw new SteeringRecoveryExhaustedException(
                $"Recovery attempt cap ({MaxRecoveryAttempts}) reached for every affected subtask " +
                $"[{string.Join(", ", affected.Select(s => s.Id))}]; the coordinator stays parked. " +
                "Use run retry to re-run the whole coordinator.");

        var now = DateTimeOffset.UtcNow;
        var resetIds = new List<int>();
        foreach (var subtask in eligible)
        {
            subtask.RecoveryGuidance = BuildRecoveryGuidance(subtask.Status, instruction, subtask.RecoveryAttempts + 1);
            subtask.Status = SubtaskStatus.Pending;
            subtask.RecoveryAttempts += 1;
            subtask.ChildRunId = null;
            subtask.UpdatedAt = now;
            resetIds.Add(subtask.Id);
        }

        // Move the plan back to dispatching (mirrors request-changes) so the synchronous state is
        // coherent before the loop spins up.
        plan.Status = WorkPlanStatus.Dispatching;
        plan.AssemblyStage = null;
        plan.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Un-terminalize the coordinator run so the project runs list/detail show it live again.
        if (runIsTerminalRecoverable)
            await runStore.UpdateStatusAsync(runId, RunStatus.InProgress, endedAt: null, ct).ConfigureAwait(false);

        // Re-open the coordinator stream (assembly's block had completed it) so the resumed dispatch /
        // assembly loops emit onto a live entry, then announce the recovery.
        _streamStore.Remove(coordinatorRunId);
        var entry = _streamStore.Create(coordinatorRunId, run.SubmittingUser);
        entry.RecordNext(EventTypes.CoordinatorRecovered, new
        {
            reason = "steering_resume",
            directiveId,
            resetSubtaskIds = resetIds,
            instruction,
        });

        // directive: collapse to applied (the resume took effect immediately, like stop).
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, now, ct).ConfigureAwait(false);
        EmitSteering(coordinatorRunId, directiveId, kind, targetChildRunId: null, SteeringStatus.Applied, instruction);

        // Re-arm dispatch (idempotent). The loop re-dispatches the reset frontier; when those children
        // finish it returns to awaiting_assembly and re-triggers assembly (DB CAS guards exactly-once).
        var context = new CoordinatorDispatchContext(
            CoordinatorRunId: coordinatorRunId,
            RepositoryPath: run.RepositoryPath,
            OriginatingBranch: run.OriginatingBranch,
            SubmittingUser: run.SubmittingUser,
            ProjectId: run.ProjectId);
        dispatch.StartDispatch(context);

        _logger.LogInformation(
            "Steering {Kind} resumed parked coordinator {RunId} (directive {DirectiveId}); reset subtasks [{Ids}] to pending and re-armed dispatch",
            kind, coordinatorRunId, directiveId, string.Join(",", resetIds));

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, TargetChildRunId: null, kind, instruction,
            SteeringStatus.Applied, createdBy, createdAt, RelayedAt: now);
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

    private void EmitSteering(
        string coordinatorRunId, int directiveId, string kind, string? targetChildRunId, string status, string instruction)
    {
        var entry = _streamStore.Get(coordinatorRunId);
        entry?.RecordNext(EventTypes.CoordinatorSteering,
            CoordinatorSteeringEvent.Payload(directiveId, kind, targetChildRunId, status, instruction));
    }
}
