using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
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
/// steering at the next boundary. Genuinely stalled children are failed by the loop's TTL-based
/// stall detection in <see cref="CoordinatorDispatchService"/>.</para>
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

    public CoordinatorReconciler(
        IServiceScopeFactory scopeFactory,
        SqliteRunStore runStore,
        RunStreamStore streamStore,
        ICoordinatorDispatch dispatch,
        ILogger<CoordinatorReconciler> logger)
    {
        _scopeFactory = scopeFactory;
        _runStore = runStore;
        _streamStore = streamStore;
        _dispatch = dispatch;
        _logger = logger;
    }

    /// <summary>
    /// Scans for orphaned coordinator dispatch (work plans still <see cref="WorkPlanStatus.Dispatching"/>
    /// with no active dispatch loop) and re-arms each. Idempotent: a coordinator whose loop is already
    /// active is skipped. Returns the number of coordinators re-armed by this sweep.
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
                continue;

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
