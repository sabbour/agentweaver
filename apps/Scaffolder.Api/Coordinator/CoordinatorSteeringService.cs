using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;

namespace Scaffolder.Api.Coordinator;

/// <summary>
/// Canonical <see cref="SteeringDirective.Kind"/> values for Feature 008 Phase 2. Per the steering
/// spike (<c>specs/008-coordinator-agent/steering-spike.md</c>) only <see cref="Stop"/>,
/// <see cref="Redirect"/>, and <see cref="Amend"/> are buildable with honest semantics; <see cref="Pause"/>
/// has no runtime primitive and is DESCOPED — it is rejected with a validation error, never executed.
/// </summary>
public static class SteeringKind
{
    public const string Stop = "stop";
    public const string Redirect = "redirect";
    public const string Amend = "amend";

    /// <summary>Descoped in Phase 2 — accepted only to produce an explicit rejection.</summary>
    public const string Pause = "pause";

    public static bool IsSupported(string kind) => kind is Stop or Redirect or Amend;

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
/// Cross-thread seam between the steering surface (an HTTP-thread call to
/// <see cref="CoordinatorSteeringService.SteerAsync"/>) and the dispatch loop that owns child-run
/// control. A <c>redirect</c>/<c>amend</c> directive is enqueued here; the dispatch loop drains it at
/// the target child's next turn boundary and injects a revised task turn. <c>stop</c> never goes
/// through this queue — it is an immediate cancel. Registered as a singleton so the same instance is
/// shared by every coordinator run.
/// </summary>
public sealed class CoordinatorSteeringQueue
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<QueuedSteering>> _pending = new();

    public void Enqueue(string coordinatorRunId, QueuedSteering directive)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(coordinatorRunId, out var list))
                _pending[coordinatorRunId] = list = [];
            list.Add(directive);
        }
    }

    /// <summary>
    /// Removes and returns the oldest queued directive that targets <paramref name="childRunId"/>
    /// (an exact <c>TargetChildRunId</c> match, or a broadcast with a null target), or null when
    /// none is pending for that child. FIFO within the coordinator run.
    /// </summary>
    public QueuedSteering? TryTakeForChild(string coordinatorRunId, string childRunId)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(coordinatorRunId, out var list) || list.Count == 0)
                return null;

            var idx = list.FindIndex(d =>
                d.TargetChildRunId is null ||
                string.Equals(d.TargetChildRunId, childRunId, StringComparison.Ordinal));
            if (idx < 0)
                return null;

            var directive = list[idx];
            list.RemoveAt(idx);
            return directive;
        }
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
    private readonly CoordinatorSteeringQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoordinatorSteeringService> _logger;

    public CoordinatorSteeringService(
        RunStreamStore streamStore,
        RunWorkflowRegistry registry,
        CoordinatorSteeringQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<CoordinatorSteeringService> logger)
    {
        _streamStore = streamStore;
        _registry = registry;
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
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
                $"Unknown steering verb '{kind}'. Supported verbs: stop, redirect, amend.");
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

        return normalized == SteeringKind.Stop
            ? await ApplyStopAsync(coordinatorRunId, directiveId, targetChildRunId, resolvedInstruction, createdBy, createdAt, ct)
                .ConfigureAwait(false)
            : await QueueNextBoundaryAsync(coordinatorRunId, directiveId, normalized, targetChildRunId, resolvedInstruction, createdBy, createdAt, ct)
                .ConfigureAwait(false);
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
                    "Steering stop: child {ChildRunId} was not active (already terminal); nothing to cancel", childRunId);
                continue;
            }

            // Emit a terminal run.cancelled so the dispatch observer resolves and the dispatch loop
            // (single writer of subtask rows) transitions the affected subtask to failed. The watch
            // loop swallows the cancellation as an abandon and emits nothing, so this is authoritative.
            var childEntry = _streamStore.Get(childRunId);
            if (childEntry is not null)
            {
                childEntry.RecordNext(EventTypes.RunCancelled, new { reason = "steering_stop" });
                _streamStore.Complete(childRunId);
            }
        }

        var relayedAt = DateTimeOffset.UtcNow;
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Applied, relayedAt, ct).ConfigureAwait(false);
        EmitSteering(coordinatorRunId, directiveId, SteeringKind.Stop, targetChildRunId, SteeringStatus.Applied, instruction);

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

    private async Task<SteeringDirectiveView> QueueNextBoundaryAsync(
        string coordinatorRunId, int directiveId, string kind, string? targetChildRunId, string instruction,
        string createdBy, DateTimeOffset createdAt, CancellationToken ct)
    {
        await UpdateDirectiveAsync(directiveId, SteeringStatus.Queued, relayedAt: null, ct).ConfigureAwait(false);
        _queue.Enqueue(coordinatorRunId, new QueuedSteering(directiveId, kind, targetChildRunId, instruction));
        EmitSteering(coordinatorRunId, directiveId, kind, targetChildRunId, SteeringStatus.Queued, instruction);

        _logger.LogInformation(
            "Steering {Kind} queued for coordinator {RunId} (directive {DirectiveId}); applies at the target child's next turn boundary",
            kind, coordinatorRunId, directiveId);

        return new SteeringDirectiveView(
            directiveId, coordinatorRunId, targetChildRunId, kind, instruction,
            SteeringStatus.Queued, createdBy, createdAt, RelayedAt: null);
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
