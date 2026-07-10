using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Persistence seam for Phase 3 collective-assembly state on the <see cref="WorkPlan"/> row. The
/// exactly-once claim (D4) is a DB-level compare-and-swap implemented with EF
/// <c>ExecuteUpdateAsync</c> (a single guarded <c>UPDATE … WHERE Status = 'awaiting_assembly'</c>),
/// which is the source of truth — an in-memory guard alone cannot prevent a double-start across the
/// dispatch/observe loop, a re-dispatch wave, and the HITL review resume. Uses a scoped
/// <see cref="MemoryDbContext"/> per call (the <see cref="IServiceScopeFactory"/> pattern) so it is
/// safe to call from the coordinator's background tasks and the HTTP review endpoint alike.
/// </summary>
public sealed class CoordinatorAssemblyStore
{
    private readonly IServiceScopeFactory _scopeFactory;

    public CoordinatorAssemblyStore(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    /// <summary>
    /// D4 exactly-once CAS. Atomically transitions <c>awaiting_assembly → assembling</c>, stamps
    /// <see cref="WorkPlan.AssemblyStartedAt"/>, and persists <paramref name="integrationBranch"/>.
    /// The stage is intentionally NOT set here — <see cref="CoordinatorAssemblyService"/> drives the
    /// stage explicitly as each collective node starts (so the eligibility/integration-build phase,
    /// which precedes RAI, shows no node live yet). Returns <c>true</c> for the single winner;
    /// <c>false</c> if the plan already moved past <c>awaiting_assembly</c>.
    /// </summary>
    public async Task<bool> TryStartAssemblyAsync(int workPlanId, string integrationBranch, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.WorkPlans
            .Where(w => w.Id == workPlanId && w.Status == WorkPlanStatus.AwaitingAssembly)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Status, WorkPlanStatus.Assembling)
                .SetProperty(w => w.IntegrationBranch, integrationBranch)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.AssemblyStartedAt, now)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Clears a previously blocked assembly verdict once durable subtask state proves the plan is now
    /// assembly-eligible. Guarded by status so another replica cannot regress an already-owned phase.
    /// </summary>
    public async Task<bool> TryResetBlockedAssemblyAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.WorkPlans
            .Where(w => w.Id == workPlanId && w.Status == WorkPlanStatus.AssemblyBlocked)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Status, WorkPlanStatus.AwaitingAssembly)
                .SetProperty(w => w.AssemblyStage, (string?)null)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// Cross-pod idempotency guard for the reset path. An <c>assembling</c> plan is normally owned by a
    /// LIVE assembly loop on some replica. This reclaims it back to <c>awaiting_assembly</c> ONLY when
    /// the claim is stale — <see cref="WorkPlan.AssemblyStartedAt"/> is null or older than
    /// <paramref name="staleBefore"/> (the owning pod likely died). A FRESH claim (another replica is
    /// actively building the integration branch right now) is left untouched, so two pods never run the
    /// git integration merge concurrently and race each other's ref-lock files. Returns <c>true</c> only
    /// for the single caller that reclaimed the stale plan; that caller then re-runs assembly (whose
    /// <see cref="TryStartAssemblyAsync"/> CAS re-establishes the exactly-once claim).
    /// </summary>
    public async Task<bool> TryReclaimStaleAssemblyAsync(int workPlanId, DateTimeOffset staleBefore, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;

        // SQLite's ExecuteUpdate cannot translate a DateTimeOffset comparison in the WHERE clause
        // (same limitation worked around in CoordinatorReconciler.TryClaimCoordinatorPodAsync), so use
        // a raw interpolated UPDATE there and the LINQ form everywhere else.
        if (db.Database.IsSqlite())
        {
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "WorkPlans"
                   SET "Status" = {WorkPlanStatus.AwaitingAssembly},
                       "AssemblyStage" = NULL,
                       "AssemblyTerminalStage" = NULL,
                       "AssemblyStatusReason" = NULL,
                       "UpdatedAt" = {now}
                 WHERE "Id" = {workPlanId}
                   AND "Status" = {WorkPlanStatus.Assembling}
                   AND ("AssemblyStartedAt" IS NULL OR "AssemblyStartedAt" < {staleBefore})
                """, ct).ConfigureAwait(false);
            return rows > 0;
        }

        var updated = await db.WorkPlans
            .Where(w => w.Id == workPlanId
                     && w.Status == WorkPlanStatus.Assembling
                     && (w.AssemblyStartedAt == null || w.AssemblyStartedAt < staleBefore))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Status, WorkPlanStatus.AwaitingAssembly)
                .SetProperty(w => w.AssemblyStage, (string?)null)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return updated > 0;
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3b.4/§3c): reclaims a stale <see cref="WorkPlanStatus.AssemblySteering"/>
    /// decision-in-progress lease back to <c>awaiting_assembly</c> so a resurrected pod re-enters the
    /// assembly boundary and re-invokes the decider. Treated exactly like the <c>assembling</c> lease:
    /// only reclaimed when <see cref="WorkPlan.AssemblyStartedAt"/> is null or older than
    /// <paramref name="staleBefore"/> — a FRESH lease (a live decider on another replica, heartbeating)
    /// is left untouched, so at most one decider is active at a time. The caller that wins this reclaim
    /// also resets the run's stale <c>relayed</c> steering directives back to <c>queued</c> (via
    /// <see cref="CoordinatorSteeringService.ReclaimStaleRelayedDirectivesAsync"/>) in the SAME recovery
    /// step, closing the claim-durability window (§3c). Returns <c>true</c> for the single reclaim winner.
    /// </summary>
    public async Task<bool> TryReclaimStaleAssemblySteeringAsync(
        int workPlanId, DateTimeOffset staleBefore, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;

        if (db.Database.IsSqlite())
        {
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE "WorkPlans"
                   SET "Status" = {WorkPlanStatus.AwaitingAssembly},
                       "AssemblyStage" = NULL,
                       "AssemblyTerminalStage" = NULL,
                       "AssemblyStatusReason" = NULL,
                       "UpdatedAt" = {now}
                 WHERE "Id" = {workPlanId}
                   AND "Status" = {WorkPlanStatus.AssemblySteering}
                   AND ("AssemblyStartedAt" IS NULL OR "AssemblyStartedAt" < {staleBefore})
                """, ct).ConfigureAwait(false);
            return rows > 0;
        }

        var updated = await db.WorkPlans
            .Where(w => w.Id == workPlanId
                     && w.Status == WorkPlanStatus.AssemblySteering
                     && (w.AssemblyStartedAt == null || w.AssemblyStartedAt < staleBefore))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(w => w.Status, WorkPlanStatus.AwaitingAssembly)
                .SetProperty(w => w.AssemblyStage, (string?)null)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return updated > 0;
    }
    public async Task SetStatusAsync(int workPlanId, string status, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets a parked/terminal assembly status and snapshots the current <see cref="WorkPlan.AssemblyStage"/>
    /// into <see cref="WorkPlan.AssemblyTerminalStage"/> before any cleanup/scribe stage advances it.
    /// </summary>
    public async Task SetTerminalStatusAsync(int workPlanId, string status, string reason, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.AssemblyTerminalStage, w => w.AssemblyStage)
                .SetProperty(w => w.AssemblyStatusReason, reason)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Advances the collective-assembly stage (drives the coordinator graph node-flip).</summary>
    public async Task SetStageAsync(int workPlanId, string? stage, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.AssemblyStage, stage)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Sets status and stage together (e.g. in_review/review, assembling/merge).</summary>
    public async Task SetStatusAndStageAsync(int workPlanId, string status, string? stage, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.AssemblyStage, stage)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (rev8 §3b/§3c, RD#3/CR#1) — enters the
    /// <see cref="WorkPlanStatus.AssemblySteering"/> decision-in-progress lease AND stamps
    /// <see cref="WorkPlan.AssemblyStartedAt"/> as the lease heartbeat. The reclaim path
    /// (<see cref="TryReclaimStaleAssemblySteeringAsync"/>) keys fresh-vs-stale on
    /// <c>AssemblyStartedAt</c>; the generic <see cref="SetStatusAndStageAsync"/> does NOT stamp it, so
    /// a crash mid-steering would otherwise look permanently stale (or, if left from a prior phase,
    /// permanently fresh). Using a dedicated stamp here makes the heartbeat the reclaim relies on real.
    /// </summary>
    public async Task SetAssemblySteeringAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkPlanStatus.AssemblySteering)
                .SetProperty(w => w.AssemblyStage, (string?)null)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.AssemblyStartedAt, now)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B, change #3) — GUARDED escalation of an exhausted steering
    /// budget to the human-review gate. Atomically transitions the plan from the
    /// <see cref="WorkPlanStatus.AssemblySteering"/> decision-in-progress lease (or the
    /// <see cref="WorkPlanStatus.Assembling"/> phase, if the escalation runs outside the steering lease)
    /// to <see cref="WorkPlanStatus.InReview"/> with the canonical <see cref="AssemblyStage.Review"/>
    /// stage — the SAME state the normal human-review gate uses (so the review endpoint's
    /// <c>ValidatePendingRequest</c> and the <c>ResumeInReviewAsync</c> recovery both work unchanged).
    /// Returns <c>true</c> ONLY for the single replica that won the transition; a second replica that
    /// finds the plan already <c>InReview</c> gets <c>false</c> and NO-OPs (prevents double-escalation
    /// from clobbering an already-open review record).
    /// </summary>
    public async Task<bool> TryEscalateToInReviewAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var rows = await db.WorkPlans
            .Where(w => w.Id == workPlanId
                     && (w.Status == WorkPlanStatus.AssemblySteering
                         || w.Status == WorkPlanStatus.Assembling))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkPlanStatus.InReview)
                .SetProperty(w => w.AssemblyStage, AssemblyStage.Review)
                .SetProperty(w => w.AssemblyTerminalStage, (string?)null)
                .SetProperty(w => w.AssemblyStatusReason, (string?)null)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        return rows > 0;
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Fix-B, change #4) — atomically increments the persisted per-plan
    /// HUMAN-review round-trip counter and returns the NEW value. Persisted (not in-memory) so the
    /// source==human-review budget-reset + max-round-trip backstop is cross-replica/crash-safe. The
    /// increment and read happen in ONE transaction so concurrent human decisions cannot observe a
    /// torn/duplicate count.
    /// </summary>
    public async Task<int> IncrementHumanReviewRoundTripAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.HumanReviewRoundTrips, w => w.HumanReviewRoundTrips + 1)
                .SetProperty(w => w.UpdatedAt, now), ct)
            .ConfigureAwait(false);
        var count = await db.WorkPlans
            .Where(w => w.Id == workPlanId)
            .Select(w => w.HumanReviewRoundTrips)
            .FirstAsync(ct)
            .ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return count;
    }

    /// <summary>Reads the current assembly-relevant state of a work plan (null when not found).</summary>
    public async Task<WorkPlanAssemblyState?> GetAsync(int workPlanId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.WorkPlans.AsNoTracking()
            .Where(w => w.Id == workPlanId)
            .Select(w => new WorkPlanAssemblyState(
                w.Id,
                w.Status,
                w.AssemblyStage,
                w.IntegrationBranch,
                w.AssemblyTerminalStage,
                w.AssemblyStatusReason))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// UNIFIED AUTONOMOUS STEERING (Req-2, change #4) — the Strict-Lockout rotation transition. In ONE
    /// guarded transaction it (a) verifies the subtask's current author still equals
    /// <paramref name="expectedAuthor"/> (optimistic CAS on <c>AssignedAgent</c>), (b) APPENDS that
    /// author to the durable <c>LockedOutAgents</c> JSON set, and (c) persists the rotated author +
    /// model + charter — so the append and the dispatch-field swap can never tear across replicas. The
    /// CAS on the pre-rotation author is what prevents a lost update / double-rotation: the first
    /// replica moves <c>AssignedAgent</c> away from <paramref name="expectedAuthor"/>, so a concurrent
    /// second replica's guarded UPDATE matches 0 rows and NO-OPs (returns <c>Won=false</c>). Returns the
    /// resulting locked-out roster for the visible rotation event.
    /// </summary>
    public async Task<SubtaskRotationResult> TryRotateSubtaskAuthorAsync(
        int subtaskId,
        string expectedAuthor,
        string newAuthor,
        string newModel,
        string? newCharter,
        CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var currentJson = await db.Subtasks
            .Where(s => s.Id == subtaskId)
            .Select(s => s.LockedOutAgents)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var lockedSet = ParseLockedOut(currentJson);
        if (!lockedSet.Any(a => string.Equals(a, expectedAuthor, StringComparison.OrdinalIgnoreCase)))
            lockedSet.Add(expectedAuthor);
        var newJson = JsonSerializer.Serialize(lockedSet);

        // Guarded CAS on AssignedAgent == expectedAuthor: exactly one replica wins the rotation.
        var rows = await db.Subtasks
            .Where(s => s.Id == subtaskId && s.AssignedAgent == expectedAuthor)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.AssignedAgent, newAuthor)
                .SetProperty(s => s.SelectedModelId, newModel)
                .SetProperty(s => s.AgentCharter, newCharter)
                .SetProperty(s => s.LockedOutAgents, newJson)
                .SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        if (rows == 0)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            // Lost the CAS (a concurrent replica already rotated). Read back the durable locked set so
            // the caller still sees the authoritative roster; do NOT re-append or re-dispatch.
            var settledJson = await db.Subtasks
                .Where(s => s.Id == subtaskId)
                .Select(s => s.LockedOutAgents)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            return new SubtaskRotationResult(false, ParseLockedOut(settledJson));
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new SubtaskRotationResult(true, lockedSet);
    }

    /// <summary>Reads a subtask's durable locked-out author roster (empty when none/unknown).</summary>
    public async Task<IReadOnlyList<string>> GetLockedOutAgentsAsync(int subtaskId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var json = await db.Subtasks.AsNoTracking()
            .Where(s => s.Id == subtaskId)
            .Select(s => s.LockedOutAgents)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return ParseLockedOut(json);
    }

    private static List<string> ParseLockedOut(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>Outcome of a <see cref="CoordinatorAssemblyStore.TryRotateSubtaskAuthorAsync"/> attempt.</summary>
public sealed record SubtaskRotationResult(bool Won, IReadOnlyList<string> LockedOutRoster);

/// <summary>Assembly-relevant projection of a <see cref="WorkPlan"/> row.</summary>
public sealed record WorkPlanAssemblyState(
    int Id,
    string Status,
    string? AssemblyStage,
    string? IntegrationBranch,
    string? AssemblyTerminalStage = null,
    string? AssemblyStatusReason = null);
