using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// UNIFIED AUTONOMOUS STEERING (rev8) — §10 tests for the coordinator-owned routing core: the
/// <see cref="SteeringSignal"/> envelope + <see cref="CoordinatorSteeringService.SubmitSteeringAsync"/>
/// (persist/queue/surface only — never auto-execute), the deterministic <see cref="SteeringPolicy"/>
/// table (A/B/C/D), the <see cref="CoordinatorSteeringDecider"/> atomic decision transaction (budget
/// CAS + <c>relayed→decided</c> + <c>steering_decision</c> event), the two-phase attempt-specific
/// <see cref="SteeringRevisionExecution"/> markers + recovery probe (§3d), and claim durability
/// (<see cref="CoordinatorSteeringService.ReclaimStaleRelayedDirectivesAsync"/>, §3c). Real EF over
/// in-memory SQLite, no mocks (Principle VII).
/// </summary>
public sealed class UnifiedSteeringTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunStreamStore _streamStore = new();
    private readonly RunWorkflowRegistry _registry = new();
    private readonly CoordinatorSteeringService _steering;
    private readonly CoordinatorSteeringDecider _decider;

    public UnifiedSteeringTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _steering = new CoordinatorSteeringService(
            _streamStore, _registry, _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance);
        _decider = new CoordinatorSteeringDecider(
            _scopeFactory, _steering, NullLogger<CoordinatorSteeringDecider>.Instance);
    }

    // ── §4.4 deterministic policy table — one test per row ─────────────────────────────────────

    [Fact]
    public void Policy_Advisory_YieldsD()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.Advisory, resumable: true))
            .Should().Be(SteeringDirection.Advisory);

    [Fact]
    public void Policy_RequestChanges_Resumable_UnderCap_YieldsA()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.RequestChanges, resumable: true))
            .Should().Be(SteeringDirection.InPlaceSteer);

    [Fact]
    public void Policy_RequestChanges_Unresumable_YieldsB()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.RequestChanges, resumable: false))
            .Should().Be(SteeringDirection.DispatchFresh);

    [Fact]
    public void Policy_Blocking_YieldsC()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.Blocking, resumable: true))
            .Should().Be(SteeringDirection.Proceed);

    [Fact]
    public void Policy_OverSubtaskBudget_YieldsC()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.RequestChanges, resumable: true, subtaskAttempts: 3))
            .Should().Be(SteeringDirection.Proceed);

    [Fact]
    public void Policy_OverPlanBudget_YieldsC()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.RequestChanges, resumable: true, planIterations: 6))
            .Should().Be(SteeringDirection.Proceed);

    [Fact]
    public void Policy_StaleTreeHash_YieldsC()
        => SteeringPolicy.Decide(Inputs(SteeringSeverity.RequestChanges, resumable: true, stale: true))
            .Should().Be(SteeringDirection.Proceed);

    // ── §2/§3 envelope normalization + SubmitSteeringAsync (persist/queue/surface only) ────────

    [Theory]
    [InlineData(SteeringSource.HumanReview)]
    [InlineData(SteeringSource.Rai)]
    [InlineData(SteeringSource.Rubberduck)]
    [InlineData(SteeringSource.BuildTest)]
    [InlineData(SteeringSource.Agent)]
    [InlineData(SteeringSource.Coordinator)]
    [InlineData(SteeringSource.Step)]
    public async Task Submit_FromEverySource_NormalizesAndSurfaces_NeverExecutes(string source)
    {
        _streamStore.Create("coord-1", "alice");
        var signal = SteeringSignal.Create(
            "coord-1", source, SteeringTargetScope.ForSubtasks(1, 2),
            feedback: "please address X", severity: SteeringSeverity.RequestChanges,
            verb: SteeringKind.Redirect, createdBy: $"gate:{source}", treeHash: "abc",
            targetFiles: new[] { "src/x.cs" });

        var view = await _steering.SubmitSteeringAsync(signal, default);

        // Persist + queue only — NEVER applied/executed on submission (BLOCKER-1).
        view.Status.Should().Be(SteeringStatus.Queued);
        var persisted = await GetDirectiveAsync(view.Id);
        persisted!.Source.Should().Be(source);
        persisted.Severity.Should().Be(SteeringSeverity.RequestChanges);
        persisted.Status.Should().Be(SteeringStatus.Queued, "submission must not auto-execute");
        persisted.TargetScopeJson.Should().NotBeNullOrEmpty();
        SteeringTargetScope.FromJson(persisted.TargetScopeJson)!.SubtaskIds.Should().BeEquivalentTo(new[] { 1, 2 });

        // steering_received emitted for immediate visibility, tagged with the source.
        var events = _streamStore.Get("coord-1")!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorSteeringReceived);
    }

    [Fact]
    public async Task Submit_UnknownSource_IsRejected()
    {
        var signal = SteeringSignal.Create(
            "coord-1", "martian", SteeringTargetScope.Run(), "x", SteeringSeverity.Advisory,
            SteeringKind.Send, "someone");
        var act = async () => await _steering.SubmitSteeringAsync(signal, default);
        await act.Should().ThrowAsync<SteeringValidationException>();
    }

    [Fact]
    public void DispatchFresh_Verb_IsSupported()
        => SteeringKind.IsSupported(SteeringKind.DispatchFresh).Should().BeTrue();

    // ── §3c/§6 decider atomic decision + budget CAS ────────────────────────────────────────────

    [Fact]
    public async Task Decide_RequestChanges_Resumable_ChoosesA_IncrementsBudgetOnce_EmitsDecision()
    {
        _streamStore.Create("coord-a", "alice");
        var (planId, subtaskId) = await SeedPlanWithSubtaskAsync("coord-a", childRunId: "child-a", recoveryAttempts: 0);
        var directiveId = await SubmitAndClaimAsync("coord-a", SteeringSeverity.RequestChanges, subtaskId);

        var decision = await _decider.DecideAsync(directiveId, autopilotOn: false,
            resumabilityProbe: Resumable(true), ct: default);

        decision!.Direction.Should().Be(SteeringDirection.InPlaceSteer);
        decision.Attempt.Should().Be(1);

        var d = await GetDirectiveAsync(directiveId);
        d!.Status.Should().Be(SteeringStatus.Decided, "decision stops at 'decided', NOT applied (§3c)");
        d.DecidedAction.Should().Be(SteeringDirection.InPlaceSteer);
        d.ActionAttempt.Should().Be(1);

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == subtaskId)).RecoveryAttempts
            .Should().Be(1, "budget increments exactly once per A decision");
        (await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == planId)).SteeringIterations
            .Should().Be(1);

        _streamStore.Get("coord-a")!.GetSnapshotSince(0).Events
            .Should().Contain(e => e.Type == EventTypes.CoordinatorSteeringDecision);
    }

    [Fact]
    public async Task Decide_Unresumable_ChoosesB_AndEmitsDecisionBeforeAnyReset()
    {
        _streamStore.Create("coord-b", "alice");
        var (_, subtaskId) = await SeedPlanWithSubtaskAsync("coord-b", childRunId: null, recoveryAttempts: 0);
        var directiveId = await SubmitAndClaimAsync("coord-b", SteeringSeverity.RequestChanges, subtaskId);

        var decision = await _decider.DecideAsync(directiveId, autopilotOn: false,
            resumabilityProbe: Resumable(false), ct: default);

        decision!.Direction.Should().Be(SteeringDirection.DispatchFresh);
        // The conscious fresh-dispatch decision is VISIBLE (fix for "felt like a glitch").
        var decisionEvent = _streamStore.Get("coord-b")!.GetSnapshotSince(0).Events
            .SingleOrDefault(e => e.Type == EventTypes.CoordinatorSteeringDecision);
        decisionEvent.Should().NotBeNull("dispatch_fresh must emit a visible decision event with a rationale");
    }

    [Fact]
    public async Task Decide_Advisory_ChoosesD_NoIncrement_NoReset()
    {
        _streamStore.Create("coord-d", "alice");
        var (planId, subtaskId) = await SeedPlanWithSubtaskAsync("coord-d", childRunId: "child-d", recoveryAttempts: 0);
        var directiveId = await SubmitAndClaimAsync("coord-d", SteeringSeverity.Advisory, subtaskId,
            verb: SteeringKind.Send);

        var decision = await _decider.DecideAsync(directiveId, autopilotOn: false,
            resumabilityProbe: Resumable(true), ct: default);

        decision!.Direction.Should().Be(SteeringDirection.Advisory);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == subtaskId)).RecoveryAttempts.Should().Be(0);
        (await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == planId)).SteeringIterations.Should().Be(0);
    }

    [Fact]
    public async Task Decide_SubtaskAtCap_YieldsC_NeverAnotherAB()
    {
        _streamStore.Create("coord-cap", "alice");
        var (_, subtaskId) = await SeedPlanWithSubtaskAsync("coord-cap", childRunId: "child-cap",
            recoveryAttempts: CoordinatorSteeringService.MaxRecoveryAttempts);
        var directiveId = await SubmitAndClaimAsync("coord-cap", SteeringSeverity.RequestChanges, subtaskId);

        var decision = await _decider.DecideAsync(directiveId, autopilotOn: false,
            resumabilityProbe: Resumable(true), ct: default);

        decision!.Direction.Should().Be(SteeringDirection.Proceed, "a subtask at the cap escalates to C, never A/B");
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == subtaskId)).RecoveryAttempts
            .Should().Be(CoordinatorSteeringService.MaxRecoveryAttempts, "over-budget C must NOT increment further");
    }

    [Fact]
    public async Task Decide_IsIdempotent_ReDecideReturnsSameDecision_NoDoubleIncrement()
    {
        _streamStore.Create("coord-idem", "alice");
        var (_, subtaskId) = await SeedPlanWithSubtaskAsync("coord-idem", childRunId: "child-i", recoveryAttempts: 0);
        var directiveId = await SubmitAndClaimAsync("coord-idem", SteeringSeverity.RequestChanges, subtaskId);

        var first = await _decider.DecideAsync(directiveId, false, resumabilityProbe: Resumable(true));
        var second = await _decider.DecideAsync(directiveId, false, resumabilityProbe: Resumable(true));

        second!.Direction.Should().Be(first!.Direction);
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == subtaskId)).RecoveryAttempts
            .Should().Be(1, "a re-decided directive must NOT double-increment the budget (exactly-once)");
    }

    // ── §3d two-phase attempt-specific marker + recovery probe ─────────────────────────────────

    [Fact]
    public async Task Initiate_IsUnique_SecondCallDoesNotOwnTheAttempt()
    {
        (await _decider.TryInitiateRevisionAsync("child-1", directiveId: 10, attempt: 1)).Should().BeTrue();
        (await _decider.TryInitiateRevisionAsync("child-1", directiveId: 10, attempt: 1)).Should()
            .BeFalse("a second launcher must NOT own the same (directiveId, attempt) — dedupe via the unique key");
    }

    [Fact]
    public async Task Probe_MarkerAbsent_ReDrive()
        => (await _decider.ProbeRevisionEffectAsync(directiveId: 99, attempt: 1, runId: "child-absent"))
            .Should().Be(RevisionRecoveryAction.ReDrive);

    [Fact]
    public async Task Probe_InitiatedButNotConfirmed_ReDrive_NeverAdvancesOnMarkerAlone()
    {
        await _decider.TryInitiateRevisionAsync("child-1", directiveId: 20, attempt: 1);
        (await _decider.ProbeRevisionEffectAsync(20, 1, "child-1")).Should()
            .Be(RevisionRecoveryAction.ReDrive, "an 'initiated' marker alone must NEVER confirm the effect (crash-before-launch)");
    }

    [Fact]
    public async Task Probe_EffectConfirmed_Advance_NoReInject()
    {
        await _decider.TryInitiateRevisionAsync("child-1", directiveId: 30, attempt: 1);
        await _decider.ConfirmRevisionEffectAsync(30, 1, "child-1"); // the running workflow's first-superstep write
        (await _decider.ProbeRevisionEffectAsync(30, 1, "child-1")).Should()
            .Be(RevisionRecoveryAction.Advance, "a confirmed attempt-keyed effect row advances to applied without re-inject");
    }

    [Fact]
    public async Task RevisionExecution_UniqueKey_RejectsDuplicateDirectiveAttempt()
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.SteeringRevisionExecutions.Add(new SteeringRevisionExecution
        {
            RunId = "child-1", SteeringDirectiveId = 40, ActionAttempt = 1,
            EffectState = RevisionEffectState.Initiated, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        db.SteeringRevisionExecutions.Add(new SteeringRevisionExecution
        {
            RunId = "child-1", SteeringDirectiveId = 40, ActionAttempt = 1,
            EffectState = RevisionEffectState.Initiated, CreatedAt = DateTimeOffset.UtcNow,
        });
        var act = async () => await db.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>("the UNIQUE (directiveId, attempt, runId) index serializes probe→launch");
    }

    // ── §3c claim durability — stale relayed reclaim ───────────────────────────────────────────

    [Fact]
    public async Task Reclaim_ResetsStaleRelayed_ToQueued_LeavesFreshAndDecidedAlone()
    {
        var now = DateTimeOffset.UtcNow;
        int stale = await InsertDirectiveAsync("coord-r", SteeringStatus.Relayed, relayedAt: now.AddMinutes(-30));
        int fresh = await InsertDirectiveAsync("coord-r", SteeringStatus.Relayed, relayedAt: now.AddSeconds(-2));
        int decided = await InsertDirectiveAsync("coord-r", SteeringStatus.Decided, relayedAt: now.AddMinutes(-30));

        var reclaimed = await _steering.ReclaimStaleRelayedDirectivesAsync(
            "coord-r", staleBefore: now.AddMinutes(-5), default);

        reclaimed.Should().Be(1);
        (await GetDirectiveAsync(stale))!.Status.Should().Be(SteeringStatus.Queued, "a dead claim is returned to queued");
        (await GetDirectiveAsync(fresh))!.Status.Should().Be(SteeringStatus.Relayed, "a live decider's fresh lease is not preempted");
        (await GetDirectiveAsync(decided))!.Status.Should().Be(SteeringStatus.Decided, "a committed decision is never reclaimed");
    }

    // ── §3d live A-path seam — the per-launch checkpoint decorator writes the durable effect ──────
    // These prove the effect marker is confirmed by the RUNNING revision's own first checkpoint (via
    // SteeringRevisionCheckpointStore), NOT a stub — so recovery's probe is backed by real wiring.

    [Fact]
    public async Task Decorator_FirstCheckpoint_ConfirmsEffect_ProbeAdvances_NoReInject()
    {
        // Phase-1: the launcher inserts the `initiated` marker under the unique key before launch.
        (await _decider.TryInitiateRevisionAsync("child-run-1", directiveId: 100, attempt: 1)).Should().BeTrue();
        (await _decider.ProbeRevisionEffectAsync(100, 1, "child-run-1")).Should()
            .Be(RevisionRecoveryAction.ReDrive, "initiated alone is never enough to advance");

        var inner = new FakeCheckpointStore();
        var decorated = new SteeringRevisionCheckpointStore(
            inner, directiveId: 100, attempt: 1, confirmer: _decider, logger: NullLogger.Instance);

        // The running revision reaches its first superstep and writes a checkpoint → effect_confirmed.
        await decorated.CreateCheckpointAsync("child-run-1", default, null);

        inner.CreateCalls.Should().Be(1, "the decorator delegates the real write to the shared store");
        (await _decider.ProbeRevisionEffectAsync(100, 1, "child-run-1")).Should()
            .Be(RevisionRecoveryAction.Advance, "the running revision's first checkpoint durably confirmed the effect");
    }

    [Fact]
    public async Task Decorator_CrashBeforeFirstCheckpoint_ProbeReDrives()
    {
        // initiated marker exists but the workflow crashed before any superstep/checkpoint.
        (await _decider.TryInitiateRevisionAsync("child-run-2", directiveId: 101, attempt: 1)).Should().BeTrue();
        // No CreateCheckpointAsync ever ran → no effect → recovery must re-drive, never advance.
        (await _decider.ProbeRevisionEffectAsync(101, 1, "child-run-2")).Should().Be(RevisionRecoveryAction.ReDrive);
    }

    [Fact]
    public async Task Decorator_PreExistingSameSessionCheckpoint_DoesNotFalseConfirm()
    {
        // Simulate the ORIGINAL child run (or a prior attempt) having already written checkpoints on the
        // SAME session (SessionId == RunId) via the UNDECORATED shared store.
        var shared = new FakeCheckpointStore();
        await shared.CreateCheckpointAsync("child-run-3", default, null);
        await shared.CreateCheckpointAsync("child-run-3", default, null);

        // A fresh in-place steer for a NEW (directiveId, attempt) inserts its initiated marker. Because
        // the effect proof is the attempt-specific SteeringRevisionExecution row (NOT a bare checkpoint
        // on the shared session), those pre-existing checkpoints can never false-confirm it.
        (await _decider.TryInitiateRevisionAsync("child-run-3", directiveId: 102, attempt: 1)).Should().BeTrue();
        (await _decider.ProbeRevisionEffectAsync(102, 1, "child-run-3")).Should()
            .Be(RevisionRecoveryAction.ReDrive, "pre-existing same-session checkpoints must NOT confirm this attempt");
    }

    [Fact]
    public async Task Decorator_ConfirmsOnce_EvenWithManyCheckpoints_Idempotent()
    {
        await _decider.TryInitiateRevisionAsync("child-run-4", directiveId: 103, attempt: 1);
        var inner = new FakeCheckpointStore();
        var decorated = new SteeringRevisionCheckpointStore(
            inner, directiveId: 103, attempt: 1, confirmer: _decider, logger: NullLogger.Instance);

        for (var i = 0; i < 4; i++)
            await decorated.CreateCheckpointAsync("child-run-4", default, null);

        inner.CreateCalls.Should().Be(4, "every checkpoint is delegated to the real store");
        (await _decider.ProbeRevisionEffectAsync(103, 1, "child-run-4")).Should().Be(RevisionRecoveryAction.Advance);
        // ConfirmRevisionEffectAsync is idempotent (initiated→effect_confirmed, then no-op).
        var func = async () => await _decider.ConfirmRevisionEffectAsync(103, 1, "child-run-4");
        await func.Should().NotThrowAsync();
    }

    // ── §6 RD#2/RD#5 bounded EXECUTION retry — the no-checkpoint liveness guard ────────────────
    // effect_confirmed depends on the running revision writing a checkpoint. A revision that finishes
    // or errors BEFORE any checkpoint never confirms → recovery would re-drive forever. The per-
    // directive EXECUTION retry CAS (separate from the decision budget) makes those re-drives
    // TERMINATE; on exhaustion the directive is parked needs_attention (visible), never looped.

    [Fact]
    public async Task ExecutionAttempts_BoundedByCas_ExhaustAfterMax_ThenNeedsAttention()
    {
        _streamStore.Create("coord-exec", "alice");
        var (_, subtaskId) = await SeedPlanWithSubtaskAsync("coord-exec", childRunId: "child-exec", recoveryAttempts: 0);
        var directiveId = await SubmitAndClaimAsync("coord-exec", SteeringSeverity.RequestChanges, subtaskId);

        // The execution retry CAS grants exactly MaxExecutionAttempts drives, then refuses.
        for (var i = 0; i < CoordinatorSteeringDecider.MaxExecutionAttempts; i++)
            (await _decider.TryIncrementExecutionAttemptAsync(directiveId)).Should()
                .BeTrue($"drive {i + 1} is within the execution bound");
        (await _decider.TryIncrementExecutionAttemptAsync(directiveId)).Should()
            .BeFalse("re-drives must terminate even when the revision never checkpoints");

        // On exhaustion the directive is parked in the terminal needs_attention state (visible).
        await _decider.MarkDirectiveNeedsAttentionAsync(directiveId);
        (await GetDirectiveAsync(directiveId))!.Status.Should().Be(SteeringStatus.NeedsAttention);
    }

    // ── §2/§3 RD#4 — every correction source routes through the coordinator decider ─────────────
    // build-test, RAI (and human-review) request-changes no longer force a reset+dispatch; each
    // normalizes into a SteeringSignal → SubmitSteeringAsync (steering_received) → DecideAsync
    // (steering_decision) and is budget-bounded, exactly like the rubberduck gate.

    [Theory]
    [InlineData(SteeringSource.BuildTest)]
    [InlineData(SteeringSource.Rai)]
    [InlineData(SteeringSource.HumanReview)]
    public async Task GateSource_RequestChanges_EmitsReceivedAndDecision_BudgetBounded(string source)
    {
        var runId = $"coord-{source}";
        _streamStore.Create(runId, "alice");
        var (planId, subtaskId) = await SeedPlanWithSubtaskAsync(runId, childRunId: $"child-{source}", recoveryAttempts: 0);

        // Submit from the gate source (steering_received) then claim + decide (steering_decision).
        var signal = SteeringSignal.Create(
            runId, source, SteeringTargetScope.ForSubtasks(subtaskId),
            feedback: "gate feedback", severity: SteeringSeverity.RequestChanges,
            verb: SteeringKind.Redirect, createdBy: $"gate:{source}");
        var view = await _steering.SubmitSteeringAsync(signal, default);
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var d = await db.SteeringDirectives.FirstAsync(x => x.Id == view.Id);
            d.Status = SteeringStatus.Relayed;
            d.RelayedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var decision = await _decider.DecideAsync(view.Id, autopilotOn: false, resumabilityProbe: Resumable(true));

        // The source does NOT get to force a reset+dispatch — it produces a coordinator decision.
        decision!.Direction.Should().Be(SteeringDirection.InPlaceSteer, "resumable request-changes → A, regardless of source");
        var types = _streamStore.Get(runId)!.GetSnapshotSince(0).Events.Select(e => e.Type).ToList();
        types.Should().Contain(EventTypes.CoordinatorSteeringReceived, "every source surfaces steering_received");
        types.Should().Contain(EventTypes.CoordinatorSteeringDecision, "the coordinator's conscious decision is visible");

        // Budget-bounded: the decision increments the per-plan iteration exactly once (not an unbounded reset).
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == planId)).SteeringIterations.Should().Be(1);
        }
    }

    // ── §3b/RD#1(CR#1) — AssemblySteering stale-lease crash recovery must NOT wedge the run ─────
    // A crash while the inline gate decision is in flight leaves the plan in AssemblySteering. The
    // reclaim CAS (heartbeat = AssemblyStartedAt) returns a STALE lease to AwaitingAssembly so the
    // restart-router can re-drive it, while a FRESH (live) lease is never preempted.

    [Fact]
    public async Task ReclaimStaleAssemblySteering_ReturnsStaleToAwaitingAssembly_LeavesFreshAlone()
    {
        var store = new CoordinatorAssemblyStore(_scopeFactory);
        var now = DateTimeOffset.UtcNow;
        var stalePlan = await SeedAssemblyPlanAsync("coord-wedge-1", WorkPlanStatus.AssemblySteering, assemblyStartedAt: now.AddMinutes(-30));
        var freshPlan = await SeedAssemblyPlanAsync("coord-wedge-2", WorkPlanStatus.AssemblySteering, assemblyStartedAt: now.AddSeconds(-2));

        (await store.TryReclaimStaleAssemblySteeringAsync(stalePlan, staleBefore: now.AddMinutes(-5), default))
            .Should().BeTrue("a dead AssemblySteering lease is reclaimed so recovery re-drives it");
        (await store.TryReclaimStaleAssemblySteeringAsync(freshPlan, staleBefore: now.AddMinutes(-5), default))
            .Should().BeFalse("a live decider's fresh lease is not preempted");

        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == stalePlan)).Status
            .Should().Be(WorkPlanStatus.AwaitingAssembly, "reclaimed → re-armable, run does NOT wedge");
        (await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == freshPlan)).Status
            .Should().Be(WorkPlanStatus.AssemblySteering, "the live lease keeps steering");
    }

    // ── Round-2 RD-A — crash-before-first-checkpoint → recovery relaunches exactly ONCE ────────────
    // The #4/#5 fix interaction regressed this: recovery's ProbeRevisionEffectAsync returns ReDrive for
    // an `initiated` marker, but the launch then no-oped on the ownership check (TryInitiateRevisionAsync
    // returned false because the marker already existed), so a transient crash before the first
    // checkpoint was never retried. ClaimRevisionLaunchAsync now distinguishes "relaunch the existing
    // initiated marker (lease-serialized recovery)" from "already confirmed → skip".

    [Fact]
    public async Task Recovery_CrashBeforeFirstCheckpoint_RelaunchesOnceAgainstExistingInitiatedMarker()
    {
        // The launcher inserted the `initiated` marker, then the pod crashed BEFORE the first checkpoint.
        (await _decider.TryInitiateRevisionAsync("child-a", directiveId: 200, attempt: 1)).Should().BeTrue();
        (await _decider.ProbeRevisionEffectAsync(200, 1, "child-a")).Should()
            .Be(RevisionRecoveryAction.ReDrive, "initiated with no checkpoint → recovery must re-drive");

        // FIX (RD-A): recovery is ALLOWED to relaunch against the EXISTING initiated marker. The old code
        // called TryInitiateRevisionAsync here, which returned false (marker exists) and NO-OPed → wedge.
        (await _decider.ClaimRevisionLaunchAsync("child-a", 200, 1)).Should()
            .Be(RevisionLaunchDecision.Launch, "recovery relaunches the crashed-before-checkpoint attempt");

        // The relaunched revision reaches its first superstep and writes a checkpoint → effect_confirmed.
        var decorated = new SteeringRevisionCheckpointStore(
            new FakeCheckpointStore(), directiveId: 200, attempt: 1, confirmer: _decider, logger: NullLogger.Instance);
        await decorated.CreateCheckpointAsync("child-a", default, null);

        (await _decider.ProbeRevisionEffectAsync(200, 1, "child-a")).Should().Be(RevisionRecoveryAction.Advance);
        // Exactly once: a confirmed child is NEVER relaunched again.
        (await _decider.ClaimRevisionLaunchAsync("child-a", 200, 1)).Should()
            .Be(RevisionLaunchDecision.Skip, "a confirmed effect is exactly-once — never relaunched");
    }

    // ── Round-2 RD-B — multi-target A: directive NOT applied until ALL target children confirmed ───
    // The effect marker is now PER TARGET CHILD (directiveId, attempt, runId). If the first child
    // confirms and the pod crashes before launching/checkpointing the second, recovery must NOT mark the
    // whole directive applied — the second child still needs its steering revision.

    [Fact]
    public async Task MultiTarget_FirstChildConfirmed_CrashBeforeSecond_DirectiveNotAppliedUntilAllConfirmed()
    {
        const int directiveId = 210, attempt = 1;
        var children = new[] { "child-x", "child-y" };

        // First child launches and confirms its effect (first checkpoint); the pod crashes before the 2nd.
        (await _decider.ClaimRevisionLaunchAsync("child-x", directiveId, attempt)).Should()
            .Be(RevisionLaunchDecision.Launch);
        var decoratedX = new SteeringRevisionCheckpointStore(
            new FakeCheckpointStore(), directiveId, attempt, _decider, NullLogger.Instance);
        await decoratedX.CreateCheckpointAsync("child-x", default, null);

        // RD-B: one confirmed child must NOT settle the whole directive.
        (await _decider.AreAllRevisionEffectsConfirmedAsync(directiveId, attempt, children)).Should()
            .BeFalse("the directive may advance only when EVERY target child is confirmed");
        (await _decider.ProbeRevisionEffectAsync(directiveId, attempt, "child-x")).Should()
            .Be(RevisionRecoveryAction.Advance);
        (await _decider.ProbeRevisionEffectAsync(directiveId, attempt, "child-y")).Should()
            .Be(RevisionRecoveryAction.ReDrive);
        // Recovery re-drives ONLY the unconfirmed child; the confirmed one is skipped (never re-injected).
        (await _decider.ClaimRevisionLaunchAsync("child-x", directiveId, attempt)).Should()
            .Be(RevisionLaunchDecision.Skip);
        (await _decider.ClaimRevisionLaunchAsync("child-y", directiveId, attempt)).Should()
            .Be(RevisionLaunchDecision.Launch);

        // The second child now confirms → the directive may finally advance to applied.
        var decoratedY = new SteeringRevisionCheckpointStore(
            new FakeCheckpointStore(), directiveId, attempt, _decider, NullLogger.Instance);
        await decoratedY.CreateCheckpointAsync("child-y", default, null);
        (await _decider.AreAllRevisionEffectsConfirmedAsync(directiveId, attempt, children)).Should()
            .BeTrue("all target children confirmed → directive may advance to applied");
    }

    // ── Round-2 CR-C — a reclaimed queued gate directive never leaks into an unrelated child's drain ─
    // A crash between the gate's inline claim (queued→relayed) and the DecideAsync commit lets recovery
    // reclaim the gate directive back to `queued` with TargetChildRunId==null. Without the guard, the
    // next-turn-boundary dispatch drain would apply that UN-DECIDED gate feedback to an arbitrary child.

    [Fact]
    public async Task ReclaimedQueuedGateDirective_NeverClaimedByDispatchDrain()
    {
        const string runId = "coord-orphan";
        _streamStore.Create(runId, "alice");
        await SeedAssemblyPlanAsync(runId, WorkPlanStatus.Dispatching, assemblyStartedAt: null);

        // A gate signal directive: Source set, subtask-scoped, TargetChildRunId==null — the reclaimed
        // (queued) orphan shape.
        var gate = await _steering.SubmitSteeringAsync(SteeringSignal.Create(
            runId, SteeringSource.Rubberduck, SteeringTargetScope.ForSubtasks(1, 2),
            "gate feedback", SteeringSeverity.RequestChanges, SteeringKind.Redirect, "gate:rubberduck"), default);

        // A legacy human /steer directive: Source==null broadcast (TargetChildRunId==null) — legitimately drainable.
        int legacy = await InsertLegacyBroadcastRedirectAsync(runId);

        // The dispatch drain for an UNRELATED child must claim ONLY the legacy directive, never the gate one.
        var queue = new CoordinatorSteeringQueue(_scopeFactory);
        var first = await queue.TryTakeForChildAsync(runId, "unrelated-child");
        first.Should().NotBeNull();
        first!.DirectiveId.Should().Be(legacy, "only the Source==null broadcast is drainable");

        var second = await queue.TryTakeForChildAsync(runId, "unrelated-child");
        second.Should().BeNull("the Source-tagged gate directive is EXCLUDED from the drain (CR-C invariant)");

        (await GetDirectiveAsync(gate.Id))!.Status.Should().Be(SteeringStatus.Queued,
            "the reclaimed gate directive is never applied to an unrelated child — it stays queued for the decider");
    }

    private async Task<int> InsertLegacyBroadcastRedirectAsync(string coordinatorRunId)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var d = new SteeringDirective
        {
            CoordinatorRunId = coordinatorRunId,
            TargetChildRunId = null,
            Kind = SteeringKind.Redirect,
            Instruction = "human steer",
            Status = SteeringStatus.Queued,
            CreatedBy = "human:alice",
            CreatedAt = DateTimeOffset.UtcNow,
            // Source left null — the legacy /steer path never tags a source.
        };
        db.SteeringDirectives.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    /// <summary>Minimal in-memory <see cref="JsonCheckpointStore"/> for exercising the decorator without a real MAF workflow.</summary>
    private sealed class FakeCheckpointStore : Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore
    {
        public int CreateCalls;
        private int _seq;
        public override ValueTask<Microsoft.Agents.AI.Workflows.CheckpointInfo> CreateCheckpointAsync(
            string sessionId, System.Text.Json.JsonElement value,
            Microsoft.Agents.AI.Workflows.CheckpointInfo? parent = null)
        {
            CreateCalls++;
            return ValueTask.FromResult(new Microsoft.Agents.AI.Workflows.CheckpointInfo(sessionId, $"cp-{++_seq}"));
        }

        public override ValueTask<System.Text.Json.JsonElement> RetrieveCheckpointAsync(
            string sessionId, Microsoft.Agents.AI.Workflows.CheckpointInfo key)
            => ValueTask.FromResult(default(System.Text.Json.JsonElement));

        public override ValueTask<IEnumerable<Microsoft.Agents.AI.Workflows.CheckpointInfo>> RetrieveIndexAsync(
            string sessionId, Microsoft.Agents.AI.Workflows.CheckpointInfo? withParent = null)
            => ValueTask.FromResult<IEnumerable<Microsoft.Agents.AI.Workflows.CheckpointInfo>>([]);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private static SteeringDecisionInputs Inputs(
        string severity, bool resumable, int subtaskAttempts = 0, int planIterations = 0, bool stale = false)
        => new(severity, resumable, subtaskAttempts, CoordinatorSteeringService.MaxRecoveryAttempts,
            planIterations, CoordinatorSteeringDecider.DefaultMaxPlanSteeringIterations, stale);

    private static ISteeringResumabilityProbe Resumable(bool value) => new StubProbe(value);

    private sealed class StubProbe(bool value) : ISteeringResumabilityProbe
    {
        public Task<bool> IsResumableAsync(
            MemoryDbContext db, SteeringDirective directive, IReadOnlyList<Subtask> subtasks, CancellationToken ct)
            => Task.FromResult(value);
    }

    private async Task<int> SubmitAndClaimAsync(
        string coordinatorRunId, string severity, int subtaskId, string? verb = null)
    {
        var signal = SteeringSignal.Create(
            coordinatorRunId, SteeringSource.Rubberduck, SteeringTargetScope.ForSubtasks(subtaskId),
            "fix it", severity, verb ?? SteeringKind.Redirect, "gate:rubberduck");
        var view = await _steering.SubmitSteeringAsync(signal, default);
        // Simulate the replica-safe queue claim (queued -> relayed) that precedes the decision.
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var d = await db.SteeringDirectives.FirstAsync(x => x.Id == view.Id);
        d.Status = SteeringStatus.Relayed;
        d.RelayedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        return view.Id;
    }

    private async Task<int> InsertDirectiveAsync(string coordinatorRunId, string status, DateTimeOffset? relayedAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var d = new SteeringDirective
        {
            CoordinatorRunId = coordinatorRunId,
            Kind = SteeringKind.Redirect,
            Instruction = "x",
            Status = status,
            CreatedBy = "gate:rubberduck",
            CreatedAt = DateTimeOffset.UtcNow,
            RelayedAt = relayedAt,
            Source = SteeringSource.Rubberduck,
            Severity = SteeringSeverity.RequestChanges,
        };
        db.SteeringDirectives.Add(d);
        await db.SaveChangesAsync();
        return d.Id;
    }

    private async Task<(int PlanId, int SubtaskId)> SeedPlanWithSubtaskAsync(
        string coordinatorRunId, string? childRunId, int recoveryAttempts)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1", CoordinatorRunId = coordinatorRunId, Goal = "g", DesiredOutcome = "o",
            Scope = "s", Assumptions = "a", Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id, ProjectId = "proj-1", CoordinatorRunId = coordinatorRunId,
            Status = WorkPlanStatus.Assembling, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        var subtask = new Subtask
        {
            WorkPlanId = plan.Id, Title = "t", Scope = "s", AssignedAgent = "morpheus",
            SelectedModelId = "gpt", Phase = "execution", IsolationStrategy = "worktree",
            Status = childRunId is null ? SubtaskStatus.Failed : SubtaskStatus.AssembleReady,
            ChildRunId = childRunId, RecoveryAttempts = recoveryAttempts,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Subtasks.Add(subtask);
        await db.SaveChangesAsync();
        return (plan.Id, subtask.Id);
    }

    private async Task<int> SeedAssemblyPlanAsync(
        string coordinatorRunId, string status, DateTimeOffset? assemblyStartedAt)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1", CoordinatorRunId = coordinatorRunId, Goal = "g", DesiredOutcome = "o",
            Scope = "s", Assumptions = "a", Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id, ProjectId = "proj-1", CoordinatorRunId = coordinatorRunId,
            Status = status, AssemblyStartedAt = assemblyStartedAt,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    private async Task<SteeringDirective?> GetDirectiveAsync(int id)
    {
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.SteeringDirectives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }
}
