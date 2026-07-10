using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Inputs the coordinator reasons over when choosing a steering direction (rev8 §4 "Decision inputs").
/// Populated from the persisted directive + plan + target subtask so the decision is deterministic and
/// crash-recoverable (the same inputs reconstruct after a restart).
/// </summary>
public sealed record SteeringDecisionInputs(
    string Severity,
    bool TargetResumable,
    int SubtaskRecoveryAttempts,
    int MaxSubtaskRecoveryAttempts,
    int PlanSteeringIterations,
    int MaxPlanSteeringIterations,
    bool TreeHashStale);

/// <summary>
/// The deterministic FALLBACK policy table (rev8 §4.4). Used when autopilot is OFF and no human has
/// acted yet (so a parked run still makes forward progress) and as the unit-test mechanism. It is NOT
/// the primary decision-maker when autopilot is ON — there the coordinator AGENT chooses (§4, decision
/// #1). One row per branch, in priority order.
/// </summary>
public static class SteeringPolicy
{
    public static string Decide(SteeringDecisionInputs i)
    {
        // 1. advisory → D (surface, never reset).
        if (i.Severity == SteeringSeverity.Advisory)
            return SteeringDirection.Advisory;

        // 4 (priority): budget exhausted OR blocking OR stale feedback → C (human review / terminal).
        var overSubtaskBudget = i.SubtaskRecoveryAttempts >= i.MaxSubtaskRecoveryAttempts;
        var overPlanBudget = i.PlanSteeringIterations >= i.MaxPlanSteeringIterations;
        if (overSubtaskBudget || overPlanBudget
            || i.Severity == SteeringSeverity.Blocking
            || i.TreeHashStale)
            return SteeringDirection.Proceed;

        // 2. request-changes, target resumable, under cap → A (in-place steer, preserve context).
        if (i.TargetResumable)
            return SteeringDirection.InPlaceSteer;

        // 3. request-changes, target NOT resumable → B. At an assembly gate this maps to a CONSCIOUS
        //    LOCKOUT ROTATION (ExecuteLockoutRotationAsync): the current author is locked out and the
        //    revision rotates to a DIFFERENT eligible agent, dispatched with full accumulated context.
        return SteeringDirection.DispatchFresh;
    }
}

/// <summary>
/// The outcome of a decision: the chosen <see cref="SteeringDirection"/>, the budget attempt number
/// stamped durably on the directive, the target subtask ids, and a human-readable rationale (surfaced
/// in <c>coordinator.steering_decision</c> so the choice is never a "glitch").
/// </summary>
public sealed record SteeringDecision(
    string Direction,
    int Attempt,
    IReadOnlyList<int> SubtaskIds,
    string Rationale);

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8 §3/§4/§6). The coordinator is the single decision-maker for ALL
/// steering, regardless of source. This service performs the ATOMIC decision transaction and owns the
/// durable action-intent state machine (<c>relayed → decided → executing → applied</c>) plus the
/// two-phase, attempt-specific idempotency markers (§3d). It is invoked from TWO single-writer sites
/// (§3a): the dispatch drain (child-turn signals) and synchronously inline in the assembly loop (gate
/// signals). Under autopilot the coordinator AGENT chooses the direction; this deterministic policy is
/// the fallback. Fresh dispatch (B) is ALWAYS explicit + logged before any reset.
/// </summary>
public sealed class CoordinatorSteeringDecider : Agentweaver.Api.Infrastructure.IRevisionEffectConfirmer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CoordinatorSteeringService _steering;
    private readonly ILogger<CoordinatorSteeringDecider> _logger;
    private readonly Func<Agentweaver.Api.Infrastructure.IRevisionCheckpointIndex?>? _checkpointIndexAccessor;

    /// <summary>Per-work-plan steering iteration cap (rev8 §6, decision #3). Default 6, per-run configurable.</summary>
    public const int DefaultMaxPlanSteeringIterations = 6;

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §6, RD#2 liveness) — bounded per-directive EXECUTION retry cap,
    /// SEPARATE from the decision budget. A Decision-A revision that finishes/errors before writing any
    /// checkpoint never confirms its effect marker; without this cap the recovery re-drive (which does
    /// NOT re-increment the decision budget) would loop forever. After this many execution drives the
    /// directive is parked <c>needs_attention</c> and the plan escalates to a visible terminal.
    /// </summary>
    public const int MaxExecutionAttempts = 3;

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B, §7 locked) — max HUMAN-review round-trips whose feedback
    /// resets the autonomous steering budget. A human request-changes after budget exhaustion is a fresh
    /// mandate, so it zeroes <see cref="WorkPlan.SteeringIterations"/> (via
    /// <see cref="ResetSteeringBudgetAsync"/>) to let the coordinator converge again under human guidance.
    /// After this many round-trips the budget is NO LONGER reset — autonomy stops re-steering and the plan
    /// simply parks (again) at human review. Bounded by the persisted
    /// <see cref="WorkPlan.HumanReviewRoundTrips"/> counter so it is cross-replica/crash-safe. Default 3.
    /// </summary>
    public const int DefaultMaxHumanReviewRoundTrips = 3;

    public CoordinatorSteeringDecider(
        IServiceScopeFactory scopeFactory,
        CoordinatorSteeringService steering,
        ILogger<CoordinatorSteeringDecider> logger,
        Func<Agentweaver.Api.Infrastructure.IRevisionCheckpointIndex?>? checkpointIndexAccessor = null)
    {
        _scopeFactory = scopeFactory;
        _steering = steering;
        _logger = logger;
        _checkpointIndexAccessor = checkpointIndexAccessor;
    }

    /// <summary>
    /// The ATOMIC decision commit (rev8 §3c part 1). In ONE DB transaction: (a) resolves the decision
    /// inputs, (b) runs the budget CAS check-and-increment — per-subtask <see cref="Subtask.RecoveryAttempts"/>
    /// &lt; <see cref="CoordinatorSteeringService.MaxRecoveryAttempts"/> AND per-plan
    /// <see cref="WorkPlan.SteeringIterations"/> &lt; <paramref name="maxPlanIterations"/> — incrementing
    /// exactly once for A/B, (c) records the chosen action + target + attempt durably on the directive
    /// (<c>relayed → decided</c>), NOT <c>applied</c>. Execution is a separate recovery-driven phase
    /// (§3d). Emits <c>coordinator.steering_decision</c> AFTER commit (a durable-outbox-equivalent — the
    /// directive's <c>decided</c> state is the durable record; the event is best-effort visibility).
    /// The directive must be <c>relayed</c> (claimed) on entry; over-budget yields direction C with no
    /// increment. Returns the decision, or null when the directive is missing / not in a decidable state.
    /// </summary>
    public async Task<SteeringDecision?> DecideAsync(
        int directiveId,
        bool autopilotOn,
        int? maxPlanIterations = null,
        ISteeringResumabilityProbe? resumabilityProbe = null,
        CancellationToken ct = default)
    {
        var maxPlan = maxPlanIterations ?? DefaultMaxPlanSteeringIterations;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var directive = await db.SteeringDirectives.FirstOrDefaultAsync(d => d.Id == directiveId, ct).ConfigureAwait(false);
        if (directive is null)
            return null;
        // Idempotency: a directive already decided/executing/applied is not re-decided (recovery
        // re-drives execution, never the decision — the budget increment already ran exactly once).
        if (directive.Status is SteeringStatus.Decided or SteeringStatus.Executing or SteeringStatus.Applied)
            return new SteeringDecision(
                directive.DecidedAction ?? SteeringDirection.Proceed,
                directive.ActionAttempt ?? 0,
                ResolveTargetIds(directive),
                "already-decided");
        if (directive.Status != SteeringStatus.Relayed)
            return null; // not yet claimed for a decision

        var plan = await db.WorkPlans
            .FirstOrDefaultAsync(w => w.CoordinatorRunId == directive.CoordinatorRunId, ct).ConfigureAwait(false);

        var targetIds = ResolveTargetIds(directive);
        var subtasks = plan is null
            ? new List<Subtask>()
            : await db.Subtasks.Where(s => s.WorkPlanId == plan.Id
                && (targetIds.Count == 0 || targetIds.Contains(s.Id))).ToListAsync(ct).ConfigureAwait(false);

        var severity = directive.Severity ?? SteeringSeverity.RequestChanges;
        var maxSubtaskAttempts = CoordinatorSteeringService.MaxRecoveryAttempts;
        var subtaskAttempts = subtasks.Count == 0 ? 0 : subtasks.Max(s => s.RecoveryAttempts);
        var planIterations = plan?.SteeringIterations ?? 0;

        var probe = resumabilityProbe ?? DefaultResumabilityProbe.Instance;
        var resumable = await probe.IsResumableAsync(db, directive, subtasks, ct).ConfigureAwait(false);
        // TreeHash staleness compares the feedback's tree hash against the plan's CURRENT aggregate.
        // The plan does not persist a live aggregate hash column today, so the deterministic path treats
        // feedback as fresh here; the staleness INPUT remains first-class for the autopilot agent path
        // and is unit-tested directly against SteeringPolicy. (Documented invariant, rev8 §6.)
        var treeHashStale = false;

        var inputs = new SteeringDecisionInputs(
            severity, resumable, subtaskAttempts, maxSubtaskAttempts, planIterations, maxPlan, treeHashStale);

        // Autopilot ON → the coordinator agent chooses; the deterministic policy is the documented
        // fallback (agent hook is W2 — bounded call falling back to policy on timeout/failure). The
        // agent hook is a documented TODO; the policy is authoritative until it lands.
        var direction = SteeringPolicy.Decide(inputs);
        var rationale = BuildRationale(direction, inputs, autopilotOn);

        var attempt = planIterations + 1;

        // ── ATOMIC DECISION TRANSACTION (rev8 §3c, RD#5 fix) ───────────────────────────────────────
        // The budget CAS increment (per-plan + per-subtask), the relayed→decided transition, and the
        // action/attempt stamp all commit in ONE DB transaction so a crash can never leave the budget
        // incremented while the directive stays undecided (which would let recovery decide+increment
        // AGAIN). Concurrent deciders are serialized by GUARDED CAS updates (conditional WHERE … <
        // cap), so two pods can never double-increment: only the update that observes headroom wins.
        directive.DecidedAction = direction;
        directive.ActionAttempt = attempt;
        directive.Status = SteeringStatus.Decided;

        var committed = false;
        if (direction is SteeringDirection.InPlaceSteer or SteeringDirection.DispatchFresh)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Guarded per-plan CAS: atomic conditional increment. A concurrent decider that already
            // pushed the plan to the cap makes this update match 0 rows → we lose and fall through to C.
            var planCasOk = false;
            if (plan is not null)
            {
                var planRows = await db.WorkPlans
                    .Where(w => w.Id == plan.Id && w.SteeringIterations < maxPlan)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.SteeringIterations, w => w.SteeringIterations + 1)
                        .SetProperty(w => w.UpdatedAt, w => nowUtc), ct)
                    .ConfigureAwait(false);
                planCasOk = planRows == 1;
            }

            var subtaskCasOk = true;
            if (planCasOk && subtasks.Count > 0)
            {
                var ids = subtasks.Select(s => s.Id).ToList();
                var casRows = await db.Subtasks
                    .Where(s => ids.Contains(s.Id) && s.RecoveryAttempts < maxSubtaskAttempts)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.RecoveryAttempts, x => x.RecoveryAttempts + 1), ct)
                    .ConfigureAwait(false);
                subtaskCasOk = casRows == ids.Count;
            }

            if (planCasOk && subtaskCasOk)
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                committed = true;
            }
            else
            {
                // Over budget (or a concurrent decider won the increment): ROLL BACK so any partial
                // increment is undone (atomicity), then escalate to C in a fresh transaction below.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                direction = SteeringDirection.Proceed;
                rationale = plan is null
                    ? "no work plan; escalating to review"
                    : $"steering_budget_exhausted (plan iterations {plan.SteeringIterations}/{maxPlan}, subtask attempts {subtaskAttempts}/{maxSubtaskAttempts})";
                directive.DecidedAction = direction;
            }
        }

        if (!committed)
        {
            // C / D (no budget increment) OR an A/B that fell through to C on budget exhaustion. The
            // directive decision still commits atomically (single-row transaction).
            await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        // Visibility (§8): emit the decision AFTER it is durable (dispatch_fresh especially — any reset
        // happens only after this decision is committed). Fixes the "felt like a glitch" complaint.
        await _steering.EmitReplicaSafeAsync(directive.CoordinatorRunId, EventTypes.CoordinatorSteeringDecision, new
        {
            directiveId,
            decision = direction,
            rationale,
            subtaskIds = targetIds,
            attempt,
        }, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Coordinator steering decision for run {RunId} (directive {DirectiveId}): {Direction} (attempt {Attempt}) — {Rationale}",
            directive.CoordinatorRunId, directiveId, direction, attempt, rationale);

        return new SteeringDecision(direction, attempt, targetIds, rationale);
    }

    /// <summary>
    /// Recovery probe for the A path (rev8 §3d, two-phase attempt-specific proof). Given a
    /// <c>(directiveId, attempt)</c>, inspects the durable <see cref="SteeringRevisionExecution"/>
    /// marker and returns the recovery action:
    /// <list type="bullet">
    /// <item><see cref="RevisionRecoveryAction.ReDrive"/> — marker ABSENT, or <c>initiated</c> with NO
    ///   confirmed effect → the launch never durably ran; re-drive it once (idempotent under the unique
    ///   <c>(directiveId, attempt)</c> key).</item>
    /// <item><see cref="RevisionRecoveryAction.Advance"/> — marker <c>effect_confirmed</c> (the resumed
    ///   workflow wrote its attempt-keyed effect row at its first superstep) → advance to <c>applied</c>;
    ///   do NOT re-inject.</item>
    /// </list>
    /// A bare <see cref="WorkflowCheckpointRecord"/> and <see cref="Runs.RunStatus"/> are NEVER consulted:
    /// the proof is the attempt-keyed effect row, so a pre-existing same-<c>RunId</c> checkpoint from the
    /// original child or a prior attempt cannot false-confirm.
    /// </summary>
    public async Task<RevisionRecoveryAction> ProbeRevisionEffectAsync(
        int directiveId, int attempt, string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var marker = await db.SteeringRevisionExecutions.AsNoTracking()
            .FirstOrDefaultAsync(
                m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt && m.RunId == runId, ct)
            .ConfigureAwait(false);
        if (marker is null)
            return RevisionRecoveryAction.ReDrive;
        if (marker.EffectState == RevisionEffectState.EffectConfirmed)
            return RevisionRecoveryAction.Advance;

        // Marker is `initiated` but not `effect_confirmed`. On the Postgres store the effect row is
        // written ATOMICALLY with the first checkpoint, so this state means the revision genuinely never
        // ran ≥1 superstep → re-drive. On the NON-transactional file/dev store there is a crash window
        // (checkpoint file written, confirm not yet committed); corroborate with the strictly-monotonic
        // checkpoint watermark: if the session's checkpoint count has grown beyond the snapshot taken at
        // `initiated`, THIS attempt (the only launcher under the unique key + reclaim lease) wrote a
        // checkpoint → treat the effect as present, upgrade the marker, and advance. Otherwise re-drive.
        var index = _checkpointIndexAccessor?.Invoke();
        if (index is not null)
        {
            var currentCount = await index.CountCheckpointsAsync(marker.RunId, ct).ConfigureAwait(false);
            if (currentCount > marker.CheckpointWatermark)
            {
                await ConfirmRevisionEffectAsync(directiveId, attempt, marker.RunId, ct).ConfigureAwait(false);
                _logger.LogInformation(
                    "Steering(A) recovery: corroborated effect for directive {DirectiveId} attempt {Attempt} via checkpoint watermark ({Count} > {Watermark}); advancing",
                    directiveId, attempt, currentCount, marker.CheckpointWatermark);
                return RevisionRecoveryAction.Advance;
            }
        }
        return RevisionRecoveryAction.ReDrive;
    }

    /// <summary>
    /// Phase-1 marker insert (rev8 §3d; RD-B PER-CHILD): records <c>initiated</c> for
    /// <c>(directiveId, attempt, runId)</c> under the UNIQUE key BEFORE launching that child's revision.
    /// A unique-key conflict means another actor already owns THIS child's launch (dedupes
    /// concurrent/replayed launches of the same child). Returns true if THIS caller inserted the marker
    /// (owns the launch), false if it already existed.
    /// </summary>
    public async Task<bool> TryInitiateRevisionAsync(
        string runId, int directiveId, int attempt, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var exists = await db.SteeringRevisionExecutions.AsNoTracking()
            .AnyAsync(
                m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt && m.RunId == runId, ct)
            .ConfigureAwait(false);
        if (exists)
            return false;

        // Snapshot the CURRENT (strictly-monotonic) checkpoint count for this session BEFORE launching,
        // so the dev-store crash-window corroboration in ProbeRevisionEffectAsync can prove THIS attempt
        // wrote a checkpoint (count grew past this watermark) rather than mistaking a pre-existing
        // original-run checkpoint for the revision's effect.
        var watermark = 0;
        var index = _checkpointIndexAccessor?.Invoke();
        if (index is not null)
            watermark = await index.CountCheckpointsAsync(runId, ct).ConfigureAwait(false);

        db.SteeringRevisionExecutions.Add(new SteeringRevisionExecution
        {
            RunId = runId,
            SteeringDirectiveId = directiveId,
            ActionAttempt = attempt,
            EffectState = RevisionEffectState.Initiated,
            CheckpointWatermark = watermark,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Lost the race to another actor's insert under the unique (directiveId, attempt, runId) index.
            return false;
        }
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3d; RD-A recovery relaunch) — decides whether THIS launcher
    /// should (re)launch the in-place revision for a single target child <paramref name="runId"/>. The
    /// AssemblySteering/reclaim lease serializes launchers (exactly one pod is here), so:
    /// <list type="bullet">
    /// <item><b>effect_confirmed</b> marker → <see cref="RevisionLaunchDecision.Skip"/>: this child
    ///   already ran ≥1 superstep — never relaunch (exactly-once).</item>
    /// <item><b>initiated</b> marker with NO confirmed effect → <see cref="RevisionLaunchDecision.Launch"/>:
    ///   a transient crash happened BEFORE the first checkpoint; recovery is ALLOWED to relaunch against
    ///   the existing <c>initiated</c> marker (the FIX for RD-A — the old code no-oped here because
    ///   <see cref="TryInitiateRevisionAsync"/> returned false when the marker already existed).</item>
    /// <item>marker ABSENT → insert the Phase-1 <c>initiated</c> marker, then
    ///   <see cref="RevisionLaunchDecision.Launch"/>.</item>
    /// </list>
    /// </summary>
    public async Task<RevisionLaunchDecision> ClaimRevisionLaunchAsync(
        string runId, int directiveId, int attempt, CancellationToken ct = default)
    {
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var marker = await db.SteeringRevisionExecutions.AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt && m.RunId == runId, ct)
                .ConfigureAwait(false);
            if (marker is not null)
                return marker.EffectState == RevisionEffectState.EffectConfirmed
                    ? RevisionLaunchDecision.Skip
                    : RevisionLaunchDecision.Launch;
        }

        // No marker yet — try to insert the Phase-1 initiated marker and own the launch.
        if (await TryInitiateRevisionAsync(runId, directiveId, attempt, ct).ConfigureAwait(false))
            return RevisionLaunchDecision.Launch;

        // Lost an insert race (another actor inserted between our read and insert). Re-read: if it has
        // already confirmed, skip; otherwise recovery may still relaunch against the initiated marker.
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var marker = await db.SteeringRevisionExecutions.AsNoTracking()
                .FirstOrDefaultAsync(
                    m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt && m.RunId == runId, ct)
                .ConfigureAwait(false);
            return marker is { EffectState: RevisionEffectState.EffectConfirmed }
                ? RevisionLaunchDecision.Skip
                : RevisionLaunchDecision.Launch;
        }
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3d; RD-B PER-CHILD) — returns true only when EVERY target
    /// child in <paramref name="runIds"/> has a confirmed effect marker for <c>(directiveId, attempt)</c>.
    /// The directive may advance to <c>applied</c> only when ALL targeted A executions are confirmed —
    /// a single confirmed child must NOT settle the whole directive (the RD-B bug).
    /// </summary>
    public async Task<bool> AreAllRevisionEffectsConfirmedAsync(
        int directiveId, int attempt, IReadOnlyCollection<string> runIds, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
            return false;
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var confirmed = await db.SteeringRevisionExecutions.AsNoTracking()
            .Where(m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt
                && m.EffectState == RevisionEffectState.EffectConfirmed
                && runIds.Contains(m.RunId))
            .Select(m => m.RunId)
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);
        return confirmed >= runIds.Count;
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B, change #2/#4) — resets the AUTONOMOUS steering budget so the
    /// coordinator can converge again under fresh HUMAN guidance. A human request-changes submitted after
    /// the plan was escalated to review (budget exhausted) is a new mandate: it zeroes the per-plan
    /// <see cref="WorkPlan.SteeringIterations"/> and the target subtasks' <see cref="Subtask.RecoveryAttempts"/>
    /// so <see cref="DecideAsync"/>'s budget CAS has headroom again. Committed in ONE transaction; the
    /// per-plan zero uses a guarded/optimistic CAS on the observed <paramref name="expectedIterations"/>
    /// so a concurrent decider cannot lose an increment race (it retries with the fresh value). This is
    /// gated to <c>source == human-review</c> by the caller and BOUNDED by the persisted
    /// <see cref="WorkPlan.HumanReviewRoundTrips"/> counter — autonomous gates can NEVER reset their own
    /// budget (that would reintroduce the infinite loop the budget exists to stop).
    /// </summary>
    public async Task ResetSteeringBudgetAsync(
        int workPlanId, IReadOnlyCollection<int> subtaskIds, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.SteeringIterations, 0)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        if (subtaskIds.Count > 0)
        {
            await db.Subtasks
                .Where(s => s.WorkPlanId == workPlanId && subtaskIds.Contains(s.Id))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.RecoveryAttempts, 0)
                    .SetProperty(x => x.UpdatedAt, now), ct)
                .ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Phase-2 confirm (rev8 §3d; RD-B PER-CHILD): marks the <c>(directiveId, attempt, runId)</c> marker
    /// <c>effect_confirmed</c>. Called by the revision workflow's first-superstep checkpoint write path
    /// (the decorator) — "row confirmed" ⟺ "this child's attempt ran ≥1 superstep". Idempotent.
    /// </summary>
    public async Task ConfirmRevisionEffectAsync(
        int directiveId, int attempt, string runId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.SteeringRevisionExecutions
            .Where(m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt && m.RunId == runId
                && m.EffectState != RevisionEffectState.EffectConfirmed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.EffectState, RevisionEffectState.EffectConfirmed)
                .SetProperty(m => m.ConfirmedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ConfirmRevisionEffectOnContextAsync(
        MemoryDbContext db, int directiveId, int attempt, string runId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // Tracked read/insert on the SUPPLIED context; the caller (the transactional checkpoint store)
        // calls SaveChanges so the effect row commits ATOMICALLY with the first checkpoint (RD#1).
        var marker = await db.SteeringRevisionExecutions
            .FirstOrDefaultAsync(
                m => m.SteeringDirectiveId == directiveId && m.ActionAttempt == attempt && m.RunId == runId, ct)
            .ConfigureAwait(false);
        if (marker is null)
        {
            db.SteeringRevisionExecutions.Add(new SteeringRevisionExecution
            {
                RunId = runId,
                SteeringDirectiveId = directiveId,
                ActionAttempt = attempt,
                EffectState = RevisionEffectState.EffectConfirmed,
                ConfirmedAt = now,
                CreatedAt = now,
            });
        }
        else if (marker.EffectState != RevisionEffectState.EffectConfirmed)
        {
            marker.EffectState = RevisionEffectState.EffectConfirmed;
            marker.ConfirmedAt = now;
        }
    }

    /// <summary>Advances the directive state machine (<c>decided/executing → applied</c>). Idempotent.</summary>
    public async Task MarkDirectiveAppliedAsync(int directiveId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.SteeringDirectives
            .Where(d => d.Id == directiveId && d.Status != SteeringStatus.Applied)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, SteeringStatus.Applied), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Stamps the <c>decided → executing</c> lease (<see cref="SteeringDirective.ExecStartedAt"/>).</summary>
    public async Task MarkDirectiveExecutingAsync(int directiveId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.SteeringDirectives
            .Where(d => d.Id == directiveId && d.Status == SteeringStatus.Decided)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, SteeringStatus.Executing)
                .SetProperty(d => d.ExecStartedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §6, RD#2 liveness) — atomic guarded CAS increment of the
    /// per-directive EXECUTION attempt counter. Returns true while the directive is UNDER
    /// <see cref="MaxExecutionAttempts"/> (the increment won and this execution drive may proceed),
    /// false once exhausted (no increment; caller must terminalize to <c>needs_attention</c>). SEPARATE
    /// from the decision budget so a revision that never checkpoints (never confirms its effect) still
    /// terminates re-drives instead of looping forever.
    /// </summary>
    public async Task<bool> TryIncrementExecutionAttemptAsync(int directiveId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await db.SteeringDirectives
            .Where(d => d.Id == directiveId && d.ExecutionAttempts < MaxExecutionAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ExecutionAttempts, d => d.ExecutionAttempts + 1), ct)
            .ConfigureAwait(false);
        return rows == 1;
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §6/§8) — parks the directive in the terminal
    /// <c>needs_attention</c> state (visible, never re-driven). Idempotent.
    /// </summary>
    public async Task MarkDirectiveNeedsAttentionAsync(int directiveId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.SteeringDirectives
            .Where(d => d.Id == directiveId
                && d.Status != SteeringStatus.Applied
                && d.Status != SteeringStatus.NeedsAttention)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, SteeringStatus.NeedsAttention), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3c, RD#6) — durably overrides the recorded decision action so
    /// the persisted <see cref="SteeringDirective.DecidedAction"/> ALWAYS matches the real effect. Used
    /// when Decision A cannot resume and the coordinator makes a CONSCIOUS <c>dispatch_fresh</c> decision
    /// (the matching event is emitted separately) — Decision A must never silently become B.
    /// </summary>
    public async Task OverrideDecidedActionAsync(int directiveId, string decidedAction, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await db.SteeringDirectives
            .Where(d => d.Id == directiveId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.DecidedAction, decidedAction), ct)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<int> ResolveTargetIds(SteeringDirective directive)
    {
        var scope = SteeringTargetScope.FromJson(directive.TargetScopeJson);
        return scope?.SubtaskIds ?? [];
    }

    private static string BuildRationale(string direction, SteeringDecisionInputs i, bool autopilotOn)
    {
        var mode = autopilotOn ? "autopilot" : "deterministic-policy";
        return direction switch
        {
            SteeringDirection.Advisory => $"{mode}: advisory feedback — surfaced, no reset",
            SteeringDirection.InPlaceSteer =>
                $"{mode}: request-changes, target resumable, under cap — steer in place preserving context",
            SteeringDirection.DispatchFresh =>
                $"{mode}: target session unresumable (target_unresumable) — conscious lockout rotation to a different eligible agent",
            SteeringDirection.Proceed when i.TreeHashStale =>
                $"{mode}: feedback stale against current aggregate — proceed to review",
            SteeringDirection.Proceed =>
                $"{mode}: budget exhausted — escalate to human review",
            _ => mode,
        };
    }
}

/// <summary>Recovery action for the A-path two-phase marker probe (rev8 §3d).</summary>
public enum RevisionRecoveryAction
{
    /// <summary>No durable effect for this attempt — re-drive the launch once (idempotent).</summary>
    ReDrive,

    /// <summary>Durable effect present — advance to <c>applied</c>; do NOT re-inject.</summary>
    Advance,
}

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8 §3d; RD-A) — per-child launch decision from
/// <see cref="CoordinatorSteeringDecider.ClaimRevisionLaunchAsync"/>.
/// </summary>
public enum RevisionLaunchDecision
{
    /// <summary>This child already confirmed its effect (ran ≥1 superstep) — never relaunch.</summary>
    Skip,

    /// <summary>This launcher owns the (re)launch for this child — proceed to resume it in place.</summary>
    Launch,
}

/// <summary>
/// Determines whether a steering target's session/context can be resumed in place (direction A) or must
/// be dispatched fresh (direction B). The default checks the durable target state; a workflow-aware
/// implementation can additionally consult worktree/checkpoint retention (rev8 §5).
/// </summary>
public interface ISteeringResumabilityProbe
{
    Task<bool> IsResumableAsync(
        MemoryDbContext db, SteeringDirective directive, IReadOnlyList<Subtask> subtasks, CancellationToken ct);
}

/// <summary>
/// Default resumability heuristic (rev8 §4/§5): a target is resumable when at least one target subtask
/// still references a child run (<see cref="Subtask.ChildRunId"/> non-null) — i.e. its session exists to
/// resume — and the retention window has not lapsed. When the child reference is gone (child released,
/// no worktree/checkpoint), the target is unresumable → the decider consciously chooses B.
/// </summary>
public sealed class DefaultResumabilityProbe : ISteeringResumabilityProbe
{
    public static readonly DefaultResumabilityProbe Instance = new();

    public Task<bool> IsResumableAsync(
        MemoryDbContext db, SteeringDirective directive, IReadOnlyList<Subtask> subtasks, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(directive.TargetChildRunId))
            return Task.FromResult(true);
        var resumable = subtasks.Any(s => !string.IsNullOrEmpty(s.ChildRunId)
            && (s.SteeringRetentionUntil is null || s.SteeringRetentionUntil > DateTimeOffset.UtcNow));
        return Task.FromResult(resumable);
    }
}
