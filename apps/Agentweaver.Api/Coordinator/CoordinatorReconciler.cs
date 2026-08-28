using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
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
/// <para>A run in <see cref="WorkPlanStatus.InReview"/> is NOT treated as an orphan while a human
/// review gate is pending. <c>in_review</c> means "awaiting a human decision", not "assembly failed":
/// the reconciler checks the DURABLE, cross-pod review record (a pending
/// <see cref="CoordinatorAssemblyReviewRecord"/> with no submitted decision) and, when present, simply
/// waits silently. Only an <c>in_review</c> plan with NO pending gate (the gate was never armed, or a
/// submitted decision's processing died) is re-armed. A 24 h escape hatch still terminalizes a review
/// left idle forever.</para>
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

    /// <summary>
    /// Escape hatch: a run parked in <see cref="WorkPlanStatus.InReview"/> with no operator action for
    /// longer than this window auto-resolves (terminalized as failed/abandoned) so it can never stay
    /// stuck forever — e.g. runs orphaned in <c>in_review</c> before the review gate was fully wired,
    /// whose collective assembly can no longer resolve the open gate. Configurable via
    /// <c>Runs:ReviewTimeoutHours</c> (default 24 h).
    /// </summary>
    private readonly TimeSpan _reviewAbandonTimeout;

    /// <summary>
    /// Per-run count of consecutive collective-assembly re-arm attempts by THIS pod. When a re-armed
    /// assembly keeps failing the same way (e.g. a persistent git integration error), re-arming forever
    /// is pointless. After <see cref="MaxAssemblyReArmAttempts"/> the run is terminalized as failed with
    /// a clear reason instead of looping. Pruned each sweep for runs no longer in an assembly-recovery
    /// state (so a legitimate re-dispatch wave starts fresh).
    /// </summary>
    private readonly ConcurrentDictionary<string, int> _assemblyReArmAttempts = new(StringComparer.Ordinal);

    /// <summary>Max consecutive assembly re-arms before the reconciler gives up and fails the run.</summary>
    internal const int MaxAssemblyReArmAttempts = 3;

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

        // Default 24 h: a run left in in_review with no operator action for this long is auto-resolved.
        var reviewHours = configuration?.GetValue("Runs:ReviewTimeoutHours", 24.0) ?? 24.0;
        _reviewAbandonTimeout = TimeSpan.FromHours(Math.Max(0.01, reviewHours));
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
                         || w.Status == WorkPlanStatus.Assembling
                         || w.Status == WorkPlanStatus.AssemblySteering
                         || w.Status == WorkPlanStatus.InReview
                         || w.Status == WorkPlanStatus.AssemblyBlocked)
                .Select(w => new PlanCandidate(w.Id, w.CoordinatorRunId, w.Status, w.CoordinatorPodId, w.UpdatedAt))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var reArmed = 0;

        // Prune re-arm counters for runs no longer in an assembly-recovery state (progressed to a
        // terminal state, or re-dispatched back to `dispatching`) so a legitimate re-dispatch wave
        // starts its re-arm budget fresh rather than inheriting a stale count.
        var assemblyRunIds = candidates
            .Where(c => c.Status is WorkPlanStatus.AwaitingAssembly
                                 or WorkPlanStatus.Assembling
                                 or WorkPlanStatus.AssemblySteering
                                 or WorkPlanStatus.InReview
                                 or WorkPlanStatus.AssemblyBlocked
                     && !string.IsNullOrWhiteSpace(c.CoordinatorRunId))
            .Select(c => c.CoordinatorRunId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in _assemblyReArmAttempts.Keys)
            if (!assemblyRunIds.Contains(key))
                _assemblyReArmAttempts.TryRemove(key, out _);

        foreach (var plan in candidates)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // A prior process can stop after the coordinator run becomes terminal but before its
                // work-plan status is settled. Work plans and runs use separate stores, so this check
                // cannot be part of the EF candidate query. Active children must first be recovered by
                // the dispatch loop; it re-observes and drains them without launching new children.
                if (await TrySetTerminalCoordinatorWorkPlanStatusAsync(plan, ct).ConfigureAwait(false))
                    continue;

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
                        if (await TryReArmAssemblyWithCapAsync(plan, ct).ConfigureAwait(false))
                            reArmed++;
                        break;

                    case WorkPlanStatus.InReview:
                        // Escape-hatch backstop: a run parked in in_review with no operator action past
                        // the review timeout is auto-resolved so it can never stay stuck forever (e.g.
                        // the in-process gate timer was lost to a pod restart).
                        if (await TryAbandonStaleReviewAsync(plan, ct).ConfigureAwait(false))
                        {
                            reArmed++;
                            continue;
                        }
                        // in_review means "awaiting a human review decision", NOT "assembly failed".
                        // A pending review gate is the authoritative, DURABLE, cross-pod signal that the
                        // wait is INTENTIONAL — the operator simply hasn't reviewed yet. Do NOT re-arm:
                        // re-arming would churn/cancel the open gate and log "already active; skipping"
                        // every ~10 s sweep forever (the infinite-loop bug). Just wait silently.
                        // (IsAssemblyActive is an in-memory per-pod fast-path; the persisted gate check
                        // is what makes this correct across replicas and restarts.)
                        if (await HasPendingReviewGateAsync(plan, ct).ConfigureAwait(false)
                            || IsAssemblyActive(plan))
                            continue;
                        // No pending gate and no active loop → genuinely orphaned (the gate was never
                        // armed, or a submitted decision's processing died). Re-arm so the assembly can
                        // resume review / apply the decision.
                        if (await TryReArmAssemblyWithCapAsync(plan, ct).ConfigureAwait(false))
                            reArmed++;
                        break;

                    case WorkPlanStatus.Assembling:
                        // assembling is a transient active state. If a loop already owns it in THIS pod,
                        // skip (same-pod fast path). A FRESH assembling plan is being actively built by a
                        // live loop (possibly on ANOTHER replica) — re-arming it would drive a second pod
                        // into the git integration merge (the ref-lock race) and burn the re-arm cap on a
                        // healthy run, so skip while the lease is fresh. Only a STALE assembling plan
                        // (owner likely dead) is a genuine orphan to re-arm.
                        if (IsAssemblyActive(plan))
                            continue;
                        if ((DateTimeOffset.UtcNow - plan.UpdatedAt) < _staleLeaseTtl)
                            continue;
                        if (await TryReArmAssemblyWithCapAsync(plan, ct).ConfigureAwait(false))
                            reArmed++;
                        break;

                    case WorkPlanStatus.AssemblySteering:
                        // UNIFIED AUTONOMOUS STEERING (rev8 §3b/§3c, RD#3/CR#1) — a plan wedged in
                        // assembly_steering means a decider crashed mid-decision; without this case the
                        // run never recovers. Same fresh-vs-stale discipline as `assembling`: skip if a
                        // live decider owns it (same-pod fast path OR a fresh lease). Otherwise reclaim
                        // the stale decision lease back to awaiting_assembly and — as the SINGLE reclaim
                        // winner — return this run's stale `relayed` directives to `queued` (§3c claim
                        // durability) before re-arming assembly, which re-drives the decision.
                        if (IsAssemblyActive(plan))
                            continue;
                        if ((DateTimeOffset.UtcNow - plan.UpdatedAt) < _staleLeaseTtl)
                            continue;
                        if (await TryReclaimStaleAssemblySteeringAsync(plan, ct).ConfigureAwait(false)
                            && await TryReArmAssemblyWithCapAsync(plan, ct).ConfigureAwait(false))
                            reArmed++;
                        break;

                    case WorkPlanStatus.AssemblyBlocked:
                        if (IsAssemblyActive(plan))
                            continue;
                        if (!await IsRecoverableAssemblyBlockedAsync(plan, ct).ConfigureAwait(false))
                            continue;
                        if (await TryReArmAssemblyWithCapAsync(plan, ct).ConfigureAwait(false))
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

    private bool IsAssemblyActive(PlanCandidate plan) =>
        !string.IsNullOrWhiteSpace(plan.CoordinatorRunId)
        && _assembly is not null
        && _assembly.IsAssemblyActive(plan.CoordinatorRunId);

    private async Task<bool> IsRecoverableAssemblyBlockedAsync(PlanCandidate plan, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var reason = await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == plan.WorkPlanId)
            .Select(w => w.AssemblyStatusReason)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        if (AssemblyPlanning.IsRetryableBuildTestInfraReason(reason))
            return true;

        if (!reason.Contains("ineligible_subtasks", StringComparison.Ordinal))
            return false;

        var statuses = await db.Subtasks.AsNoTracking()
            .Where(s => s.WorkPlanId == plan.WorkPlanId)
            .Select(s => new { s.Id, s.Status })
            .ToDictionaryAsync(s => s.Id, s => s.Status, ct)
            .ConfigureAwait(false);

        return AssemblyPlanning.AllEligible(statuses);
    }

    /// <summary>
    /// True when a DURABLE, cross-pod human review gate is pending for the run: a
    /// <see cref="CoordinatorAssemblyReviewRecord"/> exists with no decision submitted yet
    /// (<c>DecisionSubmittedAt is null</c>). This is the authoritative "the run is INTENTIONALLY
    /// waiting for a human decision" signal. Unlike the in-memory per-pod
    /// <see cref="ICoordinatorAssembly.IsAssemblyActive"/> guard, the persisted review record survives
    /// a pod restart and is visible to EVERY replica, so no replica mistakes a legitimately in-review
    /// run for an orphan and re-arms (which would churn/cancel the open gate). Once a decision has been
    /// submitted the row's <c>DecisionSubmittedAt</c> is set, so a still-<c>in_review</c> plan with a
    /// submitted decision is (correctly) NOT reported as pending — its stalled decision-processing is a
    /// real orphan the reconciler should re-arm.
    /// </summary>
    private async Task<bool> HasPendingReviewGateAsync(PlanCandidate plan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.CoordinatorRunId))
            return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.AssemblyReviews
            .AsNoTracking()
            .AnyAsync(r => r.CoordinatorRunId == plan.CoordinatorRunId
                        && r.DecisionSubmittedAt == null
                        && r.CoordinatorFailedAt == null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3b/§3c, RD#3/CR#1) — reclaims a STALE
    /// <see cref="WorkPlanStatus.AssemblySteering"/> decision lease back to <c>awaiting_assembly</c> and,
    /// for the single reclaim winner, returns this run's stale <c>relayed</c> steering directives to
    /// <c>queued</c> in the SAME recovery step (claim durability). Resolves the singleton stores via a
    /// scope (the reconciler holds neither directly). Returns true only for the reclaim winner.
    /// </summary>
    private async Task<bool> TryReclaimStaleAssemblySteeringAsync(PlanCandidate plan, CancellationToken ct)
    {
        var staleBefore = DateTimeOffset.UtcNow - _staleLeaseTtl;
        using var scope = _scopeFactory.CreateScope();
        var assemblyStore = scope.ServiceProvider.GetRequiredService<CoordinatorAssemblyStore>();
        var reclaimed = await assemblyStore
            .TryReclaimStaleAssemblySteeringAsync(plan.WorkPlanId, staleBefore, ct).ConfigureAwait(false);
        if (!reclaimed)
            return false;

        if (!string.IsNullOrWhiteSpace(plan.CoordinatorRunId))
        {
            var steering = scope.ServiceProvider.GetRequiredService<CoordinatorSteeringService>();
            await steering.ReclaimStaleRelayedDirectivesAsync(
                plan.CoordinatorRunId!, staleBefore, ct).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>
    /// Re-arms an orphaned collective assembly, but caps consecutive re-arms per run at
    /// <see cref="MaxAssemblyReArmAttempts"/>. When the cap is exceeded the assembly is clearly failing
    /// every re-arm (e.g. a persistent git integration error), so the run is terminalized as failed via
    /// <see cref="ICoordinatorAssembly.FailAssembly"/> instead of looping forever. Returns true when a
    /// recovery action (re-arm OR terminal fail) was taken this sweep.
    /// </summary>
    private async Task<bool> TryReArmAssemblyWithCapAsync(PlanCandidate plan, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.CoordinatorRunId) || _assembly is null)
            return await TryReArmAssemblyAsync(plan, ct).ConfigureAwait(false);

        var attempts = _assemblyReArmAttempts.AddOrUpdate(plan.CoordinatorRunId, 1, (_, n) => n + 1);
        if (attempts > MaxAssemblyReArmAttempts)
        {
            var context = await TryBuildContextAsync(plan, ct).ConfigureAwait(false);
            if (context is not null)
            {
                var reason = $"assembly_rearm_exhausted after {MaxAssemblyReArmAttempts} attempts";
                _logger.LogError(
                    "Coordinator reconciler: assembly re-arm exhausted for run {RunId} (status {Status}) after {Max} attempts; marking run failed",
                    context.CoordinatorRunId, plan.Status, MaxAssemblyReArmAttempts);
                _assembly.FailAssembly(context, reason);
            }
            _assemblyReArmAttempts.TryRemove(plan.CoordinatorRunId, out _);
            return context is not null;
        }

        return await TryReArmAssemblyAsync(plan, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Auto-resolves a run left in <see cref="WorkPlanStatus.InReview"/> with no operator action for
    /// longer than <see cref="_reviewAbandonTimeout"/>. Uses the plan's last-updated timestamp as the
    /// idle marker (unchanged for the whole review wait). Returns true when it triggered an abandon so
    /// the sweep can stop treating the run as an orphan to re-arm.
    /// </summary>
    private async Task<bool> TryAbandonStaleReviewAsync(PlanCandidate plan, CancellationToken ct)
    {
        if (_assembly is null || string.IsNullOrWhiteSpace(plan.CoordinatorRunId))
            return false;

        // NEVER auto-abandon a run whose human-review gate is genuinely OPEN (a pending review record
        // with no decision submitted). in_review with an open gate means "waiting for the human", NOT
        // "stuck" — the operator may take days. Abandoning here is exactly what produced the unwanted
        // "the review gate is no longer open" message. The idle timeout therefore only applies to runs
        // parked in in_review with NO open gate (e.g. the gate was dismissed/auto-closed but the status
        // never advanced) — a true orphan that would otherwise loop forever.
        if (await HasPendingReviewGateAsync(plan, ct).ConfigureAwait(false))
            return false;

        if (DateTimeOffset.UtcNow - plan.UpdatedAt < _reviewAbandonTimeout)
            return false;

        var context = await TryBuildContextAsync(plan, ct).ConfigureAwait(false);
        if (context is null)
            return false;

        _logger.LogWarning(
            "Coordinator reconciler: run {RunId} has been in_review with no operator action for over {TimeoutHours}h; abandoning",
            context.CoordinatorRunId, _reviewAbandonTimeout.TotalHours);
        _assembly.AbandonStaleReview(context);
        return true;
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

    private async Task<bool> TryReArmAssemblyAsync(PlanCandidate plan, CancellationToken ct)
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

    /// <summary>
    /// Settles a candidate whose coordinator run has reached a terminal result and whose children have
    /// all drained. The work-plan status is the durable orphan-scan cursor, so persisting this transition
    /// makes future scans skip the run even after this pod is replaced. Returns <c>true</c> when the
    /// candidate must not be re-armed.
    /// </summary>
    private async Task<bool> TrySetTerminalCoordinatorWorkPlanStatusAsync(
        PlanCandidate plan,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(plan.CoordinatorRunId)
            || !RunId.TryParse(plan.CoordinatorRunId, out var runId))
            return false;

        var run = await _runStore.GetAsync(runId, ct).ConfigureAwait(false);
        var terminalStatus = GetTerminalCoordinatorWorkPlanStatus(run?.Status);
        if (terminalStatus is null)
            return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var hasActiveSubtasks = await db.Subtasks
            .AsNoTracking()
            .AnyAsync(s => s.WorkPlanId == plan.WorkPlanId
                        && (s.Status == SubtaskStatus.Dispatched || s.Status == SubtaskStatus.Running), ct)
            .ConfigureAwait(false);
        if (hasActiveSubtasks)
            return false;

        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == plan.WorkPlanId && w.Status == plan.Status)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, terminalStatus)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Coordinator reconciler: settled stopped coordinator run {RunId} as work-plan status {Status}",
            plan.CoordinatorRunId, terminalStatus);
        return true;
    }

    private static string? GetTerminalCoordinatorWorkPlanStatus(RunStatus? status) => status switch
    {
        RunStatus.Completed or RunStatus.Merged => WorkPlanStatus.Complete,
        RunStatus.Declined => WorkPlanStatus.AssemblyDeclined,
        RunStatus.Failed or RunStatus.MergeFailed => WorkPlanStatus.AssemblyFailed,
        _ => null,
    };

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
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
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
                .SetProperty(w => w.AssemblyTerminalStage, w => w.AssemblyStage)
                .SetProperty(w => w.AssemblyStatusReason, reason)
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
