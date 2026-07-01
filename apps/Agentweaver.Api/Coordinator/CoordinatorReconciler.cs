using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
    private readonly IRunStore _runStore;
    private readonly RunStreamStore _streamStore;
    private readonly ICoordinatorDispatch _dispatch;
    private readonly ICoordinatorAssembly? _assembly;
    private readonly ILogger<CoordinatorReconciler> _logger;

    /// <summary>Pod name used as distributed lease owner identity (matches WorkPlan.CoordinatorPodId).</summary>
    private readonly string _myPodId;

    /// <summary>
    /// How long a pod's coordinator lease is considered fresh. Another pod's claim is only stolen
    /// after the owning pod has not updated the WorkPlan row for longer than this window.
    /// Configurable via <c>Coordinator:PodLeaseStaleTtlSeconds</c> (default 120 s — must exceed
    /// the 90 s /healthz probe timeout to prevent split-brain during the probe wait window).
    /// </summary>
    private readonly TimeSpan _staleLeaseTtl;

    public CoordinatorReconciler(
        IServiceScopeFactory scopeFactory,
        IRunStore runStore,
        RunStreamStore streamStore,
        ICoordinatorDispatch dispatch,
        ILogger<CoordinatorReconciler> logger,
        IConfiguration? configuration = null,
        ICoordinatorAssembly? assembly = null)
    {
        _scopeFactory = scopeFactory;
        _runStore = runStore;
        _streamStore = streamStore;
        _dispatch = dispatch;
        _assembly = assembly;
        _logger = logger;

        _myPodId = configuration?.GetValue<string>("App:PodId")
                   ?? Environment.GetEnvironmentVariable("HOSTNAME")
                   ?? Environment.MachineName;

        // Default 120 s (must exceed the 90 s /healthz probe timeout with margin to prevent
        // the non-owning replica from stealing the coordinator lease during the probe wait window).
        var staleSecs = configuration?.GetValue("Coordinator:PodLeaseStaleTtlSeconds", 120) ?? 120;
        _staleLeaseTtl = TimeSpan.FromSeconds(Math.Max(10, staleSecs));
    }

    /// <summary>
    /// Scans for orphaned coordinator dispatch (work plans still <see cref="WorkPlanStatus.Dispatching"/>
    /// with no active dispatch loop) and re-arms each. Idempotent: a coordinator whose loop is already
    /// active is skipped. Returns the number of coordinators re-armed by this sweep.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken ct)
    {
        List<PlanCandidate> candidates;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            candidates = await db.WorkPlans
                .AsNoTracking()
                .Where(w => w.Status == WorkPlanStatus.Dispatching
                         || w.Status == WorkPlanStatus.AwaitingAssembly
                         || w.Status == WorkPlanStatus.Assembling)
                .Select(w => new PlanCandidate(w.Id, w.CoordinatorRunId, w.Status, w.CoordinatorPodId, w.UpdatedAt))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var reArmed = 0;
        foreach (var plan in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                switch (plan.Status)
                {
                    case WorkPlanStatus.Dispatching:
                        if (!string.IsNullOrWhiteSpace(plan.CoordinatorRunId)
                            && _dispatch.IsDispatchActive(plan.CoordinatorRunId))
                            continue;
                        // Distributed lease guard: skip if another pod freshly owns this plan.
                        if (plan.CoordinatorPodId is not null
                            && plan.CoordinatorPodId != _myPodId
                            && (DateTimeOffset.UtcNow - plan.UpdatedAt) < _staleLeaseTtl)
                            continue;
                        // Atomically claim ownership before re-arming (prevents multi-pod race).
                        if (!await TryClaimCoordinatorPodAsync(plan.WorkPlanId, ct).ConfigureAwait(false))
                            continue;
                        if (await TryReArmDispatchAsync(plan, ct).ConfigureAwait(false))
                            reArmed++;
                        break;

                    case WorkPlanStatus.AwaitingAssembly:
                        if (await TryReArmAssemblyAsync(plan, resetToAwaitingAssembly: false, ct).ConfigureAwait(false))
                            reArmed++;
                        break;

                    case WorkPlanStatus.Assembling:
                        if (await TryReArmAssemblyAsync(plan, resetToAwaitingAssembly: true, ct).ConfigureAwait(false))
                            reArmed++;
                        break;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Isolated: one bad run never stalls the sweep.
                _logger.LogError(ex,
                    "Coordinator reconciler: failed to re-arm orphaned coordinator plan {PlanId} ({RunId})",
                    plan.WorkPlanId, plan.CoordinatorRunId);
            }
        }

        if (reArmed > 0)
            _logger.LogInformation("Coordinator reconciler: re-armed {Count} orphaned coordinator loop(s)", reArmed);

        return reArmed;
    }

    private async Task<bool> TryReArmDispatchAsync(PlanCandidate plan, CancellationToken ct)
    {
        var context = await TryBuildContextAsync(plan, ct).ConfigureAwait(false);
        if (context is null)
            return false;

        _logger.LogInformation(
            "Coordinator reconciler: re-arming orphaned coordinator dispatch for run {RunId}",
            context.CoordinatorRunId);
        _dispatch.StartDispatch(context);
        return true;
    }

    private async Task<bool> TryReArmAssemblyAsync(
        PlanCandidate plan,
        bool resetToAwaitingAssembly,
        CancellationToken ct)
    {
        if (_assembly is null)
        {
            _logger.LogError(
                "Coordinator reconciler: cannot re-arm assembly for corrupt/incomplete plan {PlanId} ({RunId}) because no assembly service is registered",
                plan.WorkPlanId, plan.CoordinatorRunId);
            return false;
        }

        var context = await TryBuildContextAsync(plan, ct).ConfigureAwait(false);
        if (context is null)
            return false;

        if (resetToAwaitingAssembly)
            await ResetAssemblyPlanAsync(plan.WorkPlanId, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Coordinator reconciler: re-arming orphaned coordinator assembly for run {RunId} (status was {Status})",
            context.CoordinatorRunId, plan.Status);
        _assembly.StartAssembly(context);
        return true;
    }

    private async Task<CoordinatorDispatchContext?> TryBuildContextAsync(PlanCandidate plan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.CoordinatorRunId))
        {
            await MarkPlanCorruptAsync(plan, "missing_coordinator_run_id", ct).ConfigureAwait(false);
            return null;
        }

        if (!RunId.TryParse(plan.CoordinatorRunId, out var runId))
        {
            await MarkPlanCorruptAsync(plan, "invalid_coordinator_run_id", ct).ConfigureAwait(false);
            return null;
        }

        var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            await MarkPlanCorruptAsync(plan, "missing_coordinator_run", ct).ConfigureAwait(false);
            return null;
        }

        // Ensure the coordinator stream exists so the re-armed loop's recovery audit event + topology
        // snapshot land on a live entry (the prior process's entry may have been evicted on restart).
        if (_streamStore.Get(plan.CoordinatorRunId) is null)
            _streamStore.Create(plan.CoordinatorRunId, run.SubmittingUser);

        return new CoordinatorDispatchContext(
            CoordinatorRunId: plan.CoordinatorRunId,
            RepositoryPath: run.RepositoryPath,
            OriginatingBranch: run.OriginatingBranch,
            SubmittingUser: run.SubmittingUser,
            ProjectId: run.ProjectId);
    }

    private async Task ResetAssemblyPlanAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId && w.Status == WorkPlanStatus.Assembling)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkPlanStatus.AwaitingAssembly)
                .SetProperty(w => w.AssemblyStage, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    private async Task MarkPlanCorruptAsync(PlanCandidate plan, string reason, CancellationToken ct)
    {
        _logger.LogError(
            "Coordinator reconciler: corrupt work plan {PlanId} has unusable coordinator run id '{RunId}' ({Reason}); marking failed",
            plan.WorkPlanId, plan.CoordinatorRunId, reason);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == plan.WorkPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkPlanStatus.AssemblyFailed)
                .SetProperty(w => w.AssemblyStage, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claims this pod as the coordinator owner for <paramref name="planId"/> by writing
    /// <c>CoordinatorPodId = _myPodId</c> only when no other fresh pod holds the lease. Uses an EF
    /// <c>ExecuteUpdateAsync</c> conditional UPDATE so only the winning replica proceeds to re-arm;
    /// any concurrent replica that also tries to claim simply gets 0 rows back and skips.
    /// </summary>
    private async Task<bool> TryClaimCoordinatorPodAsync(int planId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var staleThreshold = DateTimeOffset.UtcNow - _staleLeaseTtl;
        var now = DateTimeOffset.UtcNow;

        if (db.Database.IsSqlite())
        {
            var sqliteRows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "WorkPlans"
                   SET "CoordinatorPodId" = {_myPodId},
                       "UpdatedAt" = {now}
                 WHERE "Id" = {planId}
                   AND "Status" = {WorkPlanStatus.Dispatching}
                   AND ("CoordinatorPodId" IS NULL
                        OR "CoordinatorPodId" = {_myPodId}
                        OR "UpdatedAt" < {staleThreshold})
                """, ct).ConfigureAwait(false);

            return sqliteRows == 1;
        }

        int rows = await db.WorkPlans
            .Where(w => w.Id == planId
                     && w.Status == WorkPlanStatus.Dispatching
                     && (w.CoordinatorPodId == null
                         || w.CoordinatorPodId == _myPodId
                         || w.UpdatedAt < staleThreshold))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.CoordinatorPodId, _myPodId)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        return rows == 1;
    }

    private sealed record PlanCandidate(int WorkPlanId, string? CoordinatorRunId, string Status, string? CoordinatorPodId, DateTimeOffset UpdatedAt);
}
