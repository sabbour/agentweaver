using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;

using Run = Scaffolder.Domain.Run;
using RunStatus = Scaffolder.Domain.RunStatus;

namespace Scaffolder.Api.Coordinator;

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
/// Phase 2 advances <see cref="WorkPlan.Status"/> planned -&gt; dispatching while children run;
/// assembling / in_review / complete are owned by Phase 3, so when all subtasks reach a terminal
/// state the coordinator run is left in progress awaiting collective assembly.
///
/// All EF writes happen on the single dispatch-loop task using a scoped
/// <see cref="MemoryDbContext"/> (the <see cref="IServiceScopeFactory"/> pattern), so parallel
/// child dispatch + observation never corrupt EF state. Observation tasks only READ the stream.
/// </summary>
public sealed class CoordinatorDispatchService
{
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly RunOrchestrator _orchestrator;
    private readonly CoordinatorSteeringQueue _steering;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CoordinatorDispatchService> _logger;
    private readonly CancellationToken _appStopping;

    private readonly ConcurrentDictionary<string, byte> _active = new();

    public CoordinatorDispatchService(
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        RunOrchestrator orchestrator,
        CoordinatorSteeringQueue steering,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<CoordinatorDispatchService> logger)
    {
        _runStore = runStore;
        _streamStore = streamStore;
        _orchestrator = orchestrator;
        _steering = steering;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _appStopping = lifetime.ApplicationStopping;
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

    // -----------------------------------------------------------------------
    // Dispatch + observe loop
    // -----------------------------------------------------------------------

    private async Task RunDispatchLoopAsync(CoordinatorDispatchContext context, CancellationToken ct)
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

        if (subtasks.Count == 0)
        {
            _logger.LogInformation("Coordinator dispatch: work plan {WorkPlanId} has no subtasks", workPlanId.Value);
            return;
        }

        var inFlight = new Dictionary<int, Task<ChildResult>>();

        while (!ct.IsCancellationRequested)
        {
            // Dispatch the entire current frontier (parallel for independent subtasks).
            foreach (var subtaskId in SubtaskFrontier.ReadyPending(statusById, edges))
            {
                if (inFlight.ContainsKey(subtaskId))
                    continue;

                var dispatched = await DispatchOneAsync(
                    context, workPlanId.Value, subtaskId, statusById, seq, ct).ConfigureAwait(false);

                if (dispatched is { } childRunId)
                    inFlight[subtaskId] = ObserveChildAsync(subtaskId, childRunId, ct);
            }

            if (inFlight.Count == 0)
                break; // quiescent: nothing running and no ready frontier (all terminal or blocked)

            var finished = await Task.WhenAny(inFlight.Values).ConfigureAwait(false);
            var result = await finished.ConfigureAwait(false);
            inFlight.Remove(result.SubtaskId);

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
                    inFlight[result.SubtaskId] = ObserveChildAsync(result.SubtaskId, result.ChildRunId, ct);
                    continue;
                }
            }

            await ApplyChildResultAsync(
                context, workPlanId.Value, result, statusById, seq, ct).ConfigureAwait(false);
        }

        await FinalizeDispatchAsync(context, workPlanId.Value, statusById, edges, seq, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Defect D: every child subtask is now terminal. Phase 3 (collective assembly / merge) is
    /// intentionally NOT built yet, so we must NOT leave the coordinator run silently InProgress with
    /// an open stream — that made the run look hung (user issue #5). This emits an explicit
    /// children-complete signal, moves the work plan to the terminal-ish
    /// <see cref="WorkPlanStatus.AwaitingAssembly"/> status, publishes a final topology snapshot
    /// reflecting it, and closes the coordinator stream so the UI shows a clear "all children done,
    /// awaiting collective assembly (Phase 3)" state instead of an in-flight one. The coordinator Run
    /// row's final lifecycle (assembly, merge, completion) is owned by Phase 3 and is deliberately
    /// left untouched here.
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
            "Coordinator dispatch complete for run {RunId}: {Summary}. Awaiting Phase 3 assembly.",
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

            // Final FULL snapshot so the graph stops looking in-flight: every node + the terminal
            // AwaitingAssembly work-plan status, no client-side computation required.
            var finalSubtasks = await ReloadSubtasksAsync(workPlanId, ct).ConfigureAwait(false);
            finalEntry.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildSnapshot(
                context.CoordinatorRunId, workPlanId, WorkPlanStatus.AwaitingAssembly,
                finalSubtasks, edges, seq.Next()));

            // Close the coordinator stream so SSE clients receive a terminal [DONE] and stop polling.
            _streamStore.Complete(context.CoordinatorRunId);
        }
    }

    private async Task<string?> DispatchOneAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int subtaskId,
        Dictionary<int, string> statusById,
        SeqCounter seq,
        CancellationToken ct)
    {
        var childRunId = RunId.New();

        // Mark dispatched + record the child run id, then project the lifecycle + topology delta.
        var subtask = await UpdateSubtaskAsync(
            subtaskId, SubtaskStatus.Dispatched, childRunId.ToString(), ct).ConfigureAwait(false);
        if (subtask is null) return null;
        statusById[subtaskId] = SubtaskStatus.Dispatched;
        EmitSubtask(context, workPlanId, subtask, EventTypes.SubtaskDispatched, seq.Next());

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

    private async Task<ChildResult> ObserveChildAsync(int subtaskId, string childRunId, CancellationToken ct)
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
                await Task.Delay(200, ct).ConfigureAwait(false);
                continue;
            }

            var snapshot = entry.GetSnapshotSince(lastSeq);
            foreach (var evt in snapshot.Events)
            {
                lastSeq = evt.Sequence;
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
        });

        entry.RecordNext(EventTypes.CoordinatorTopology, CoordinatorTopology.BuildDelta(
            context.CoordinatorRunId,
            workPlanId,
            WorkPlanStatus.Dispatching,
            [CoordinatorTopology.SubtaskNode(subtask)],
            topologySeq));
    }

    private static string ComposeChildTask(Subtask subtask) =>
        string.IsNullOrWhiteSpace(subtask.Scope)
            ? subtask.Title
            : $"{subtask.Title}\n\n{subtask.Scope}";

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

    private enum ChildOutcome { AssembleReady, RaiFlagged, Completed, Failed }

    private sealed record ChildResult(int SubtaskId, string ChildRunId, ChildOutcome Outcome);

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
