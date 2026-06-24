using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Watchdog that recovers ORPHANED coordinator dispatch so a coordinator run "can't get stuck and
/// stay stuck". The dispatch + observe engine (<see cref="CoordinatorDispatchService"/>) is in-memory
/// and one-shot: its loop runs on a background task tied to <c>ApplicationStopping</c>, and
/// <see cref="ICoordinatorDispatch.IsDispatchActive"/> is backed by an in-memory set. If the API
/// restarts, or the loop dies between dispatch and child completion, nothing re-observes the in-flight
/// subtasks — the persisted terminal child status is never reconciled, the frontier never advances,
/// and queued steering directives never drain.
///
/// <para>An ORPHAN is a <see cref="WorkPlan"/> still in <see cref="WorkPlanStatus.Dispatching"/> whose
/// coordinator run has no active dispatch loop (<see cref="ICoordinatorDispatch.IsDispatchActive"/> is
/// false). <see cref="SweepAsync"/> re-arms each via <see cref="ICoordinatorDispatch.StartDispatch"/>
/// (idempotent). The re-armed loop is RECOVERY-AWARE: it re-observes already dispatched/running
/// subtasks, store-resolves their terminal children, advances the frontier, and drains queued
/// steering at the next boundary. Genuinely stalled children are failed by the loop's stall handling.</para>
///
/// <para>The sweep also proactively detects IN-PROGRESS child subtasks whose live stream shows no
/// progress past <c>Coordinator:SubtaskStallTimeoutMinutes</c>. For those, it force-completes the
/// child stream with <c>run.cancelled</c> so the active dispatch loop's observer resolves the child
/// and can re-dispatch or apply a queued redirect directive.</para>
///
/// <para>The sweep is hosted on the existing <see cref="CoordinatorHeartbeatService"/> cadence (~10s)
/// plus one immediate sweep at startup so a restart recovers fast. Each run is recovered under its own
/// try/catch so one bad run never stalls the sweep.</para>
/// </summary>
public sealed class CoordinatorReconciler
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly ICoordinatorDispatch _dispatch;
    private readonly ILogger<CoordinatorReconciler> _logger;
    private readonly TimeSpan _stallTimeout;
    private readonly RunWorkflowFactory? _runWorkflowFactory;

    public CoordinatorReconciler(
        IServiceScopeFactory scopeFactory,
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        ICoordinatorDispatch dispatch,
        ILogger<CoordinatorReconciler> logger,
        IConfiguration? configuration = null,
        RunWorkflowFactory? runWorkflowFactory = null)
    {
        _scopeFactory = scopeFactory;
        _runStore = runStore;
        _streamStore = streamStore;
        _dispatch = dispatch;
        _logger = logger;
        _runWorkflowFactory = runWorkflowFactory;
        var stallMinutes = configuration?.GetValue("Coordinator:SubtaskStallTimeoutMinutes", 15.0) ?? 15.0;
        _stallTimeout = TimeSpan.FromMinutes(Math.Max(0, stallMinutes));
    }

    /// <summary>
    /// Scans for orphaned coordinator dispatch (work plans still <see cref="WorkPlanStatus.Dispatching"/>
    /// with no active dispatch loop) and re-arms each. Also proactively detects in-progress child
    /// subtasks whose live stream shows no progress past the stall timeout and force-completes them.
    /// Idempotent: a coordinator whose loop is already active is skipped for orphan re-arm.
    /// Returns the number of coordinators re-armed by this sweep.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        List<string> orphanRunIds;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            orphanRunIds = await db.WorkPlans
                .AsNoTracking()
                .Where(w => w.Status == WorkPlanStatus.Dispatching)
                .Select(w => w.CoordinatorRunId)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var reArmed = 0;
        foreach (var coordinatorRunId in orphanRunIds)
        {
            ct.ThrowIfCancellationRequested();

            // Idempotency: a live loop already owns this run — leave it alone.
            if (_dispatch.IsDispatchActive(coordinatorRunId))
            {
                // Proactive stuck-child sweep for active loops: detect and force-complete stalled
                // children so the live dispatch loop resolves them without waiting for natural completion.
                await SweepStuckChildrenAsync(coordinatorRunId, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (await TryReArmAsync(coordinatorRunId, ct).ConfigureAwait(false))
                    reArmed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolated: one bad run never stalls the sweep.
                _logger.LogError(ex,
                    "Coordinator reconciler: failed to re-arm orphaned coordinator {RunId}", coordinatorRunId);
            }
        }

        if (reArmed > 0)
            _logger.LogInformation("Coordinator reconciler: re-armed {Count} orphaned coordinator dispatch loop(s)", reArmed);

        return reArmed;
    }

    /// <summary>
    /// For coordinators with an ACTIVE dispatch loop, proactively detects in-progress child subtasks
    /// whose stream shows no progress past <see cref="_stallTimeout"/>. Force-completes each stalled
    /// child stream with <c>run.cancelled</c> so the dispatch loop's observer resolves the child
    /// without waiting for a natural event. Emits <see cref="EventTypes.CoordinatorChildStallDetected"/>
    /// on the coordinator stream as an audit record.
    /// </summary>
    private async Task SweepStuckChildrenAsync(string coordinatorRunId, CancellationToken ct)
    {
        if (_stallTimeout == TimeSpan.Zero)
            return; // zero-timeout configured only in tests that drive stall directly

        List<(int SubtaskId, string ChildRunId)> candidates;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.AsNoTracking()
                .FirstOrDefaultAsync(w => w.CoordinatorRunId == coordinatorRunId, ct).ConfigureAwait(false);
            if (plan is null)
                return;

            candidates = await db.Subtasks.AsNoTracking()
                .Where(s => s.WorkPlanId == plan.Id
                    && s.ChildRunId != null
                    && (s.Status == SubtaskStatus.Running || s.Status == SubtaskStatus.Dispatched))
                .Select(s => new { s.Id, ChildRunId = s.ChildRunId! })
                .ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(x => (x.Id, x.ChildRunId)).ToList(), ct)
                .ConfigureAwait(false);
        }

        foreach (var (subtaskId, childRunId) in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await TryDetectAndUnblockStuckChildAsync(coordinatorRunId, subtaskId, childRunId, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Coordinator reconciler: error while probing stuck child {ChildRunId} for coordinator {RunId}",
                    childRunId, coordinatorRunId);
            }
        }
    }

    /// <summary>
    /// Checks whether <paramref name="childRunId"/> has made no progress past <see cref="_stallTimeout"/>.
    /// If stalled, emits <see cref="EventTypes.CoordinatorChildStallDetected"/> on the coordinator stream
    /// and force-completes the child's stream with <c>run.cancelled</c> so the active dispatch loop
    /// processes the failure. The dispatch loop is the single writer of subtask rows; this only
    /// writes to the event streams.
    /// </summary>
    private async Task TryDetectAndUnblockStuckChildAsync(
        string coordinatorRunId, int subtaskId, string childRunId, CancellationToken ct)
    {
        var staleSince = await StaleSinceAsync(childRunId, ct).ConfigureAwait(false);
        if (staleSince is null)
            return; // child is within the stall threshold

        _logger.LogWarning(
            "Coordinator reconciler: child {ChildRunId} (subtask {SubtaskId}) for coordinator {CoordRunId} " +
            "has been stuck since {StaleSince:O}; force-completing its stream",
            childRunId, subtaskId, coordinatorRunId, staleSince.Value);

        // Emit audit record on the coordinator stream.
        var coordEntry = _streamStore.Get(coordinatorRunId);
        coordEntry?.RecordNext(EventTypes.CoordinatorChildStallDetected, new
        {
            childRunId,
            subtaskId,
            staleSinceUtc = staleSince.Value.ToString("O"),
            stallTimeoutMinutes = _stallTimeout.TotalMinutes,
        });

        // Force-complete the child stream so the dispatch loop's ObserveChildAsync resolves.
        var childEntry = _streamStore.Get(childRunId);
        if (childEntry is not null && !childEntry.IsCompleted)
        {
            childEntry.RecordNext(EventTypes.RunCancelled, new { reason = "stall_detected" });
            _streamStore.Complete(childRunId);
            if (_runWorkflowFactory is not null)
                _ = _runWorkflowFactory.PersistRunEventsAsync(childRunId);

            // Terminalize the child run row in the DB so it no longer shows InProgress forever.
            // The stream-level signal unblocks the dispatch loop but the run store row stays
            // InProgress without this call.
            if (RunId.TryParse(childRunId, out var childId))
                _ = _runStore.TrySetTerminalStatusAsync(childId, RunStatus.Failed, DateTimeOffset.UtcNow, "stall_detected", CancellationToken.None);
        }
    }

    /// <summary>
    /// Returns the time since which a child run (non-terminal in the store, possibly with a live
    /// stream entry that is emitting no events) has been stalled, or null when still within the
    /// <see cref="_stallTimeout"/> threshold. Uses the later of the child run's start time and the
    /// most recently persisted run event as the last-activity marker.
    /// </summary>
    private async Task<DateTimeOffset?> StaleSinceAsync(string childRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(childRunId, out var parsed))
            return null;
        var run = await _runStore.GetAsync(parsed, ct).ConfigureAwait(false);
        if (run is null || run.Status != RunStatus.InProgress)
            return null; // already terminal — not our concern

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

    private async Task<bool> TryReArmAsync(string coordinatorRunId, CancellationToken ct)
    {
        if (!RunId.TryParse(coordinatorRunId, out var runId))
            return false;

        var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
            return false;

        // Ensure the coordinator stream exists so the re-armed loop's recovery audit event + topology
        // snapshot land on a live entry (the prior process's entry may have been evicted on restart).
        if (_streamStore.Get(coordinatorRunId) is null)
            _streamStore.Create(coordinatorRunId, run.SubmittingUser);

        var context = new CoordinatorDispatchContext(
            CoordinatorRunId: coordinatorRunId,
            RepositoryPath: run.RepositoryPath,
            OriginatingBranch: run.OriginatingBranch,
            SubmittingUser: run.SubmittingUser,
            ProjectId: run.ProjectId);

        _logger.LogInformation(
            "Coordinator reconciler: re-arming orphaned coordinator dispatch for run {RunId}", coordinatorRunId);
        _dispatch.StartDispatch(context);
        return true;
    }
}
