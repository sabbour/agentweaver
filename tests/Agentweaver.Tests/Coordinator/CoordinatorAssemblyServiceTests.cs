using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Api.Sandbox;
using Agentweaver.Tests.Helpers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Run = Agentweaver.Domain.Run;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// End-to-end tests for the Phase 3 collective-assembly orchestrator
/// (<see cref="CoordinatorAssemblyService.RunAssemblyAsync"/>). The heavy git + agent operations are
/// faked through <see cref="ICollectiveAssemblyPipeline"/> so the test exercises the coordinator-owned
/// logic: the D2 eligibility gate, the assembly_* event sequence + node-flip stage progression, and
/// the D6 request_changes inference + re-dispatch hand-off. Real EF <see cref="MemoryDbContext"/> and a
/// real <see cref="SqliteRunStore"/> back the reads.
/// </summary>
public sealed class CoordinatorAssemblyServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly AssemblyReviewGate _reviewGate = new();
    private readonly CoordinatorAssemblyStore _assemblyStore;
    private readonly FakePipeline _pipeline = new();
    private readonly FakeDispatch _dispatch = new();
    private readonly CoordinatorSteeringWaitRegistry _steeringWaits = new();
    private readonly ConfigurableRotationSelector _rotation = new();
    private readonly FakeChildRevisionHandoff _handoff;
    private readonly CoordinatorAssemblyService _sut;
    private readonly CoordinatorSteeringService _steering;

    public CoordinatorAssemblyServiceTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        services.AddSingleton<ICoordinatorDispatch>(_dispatch);
        services.AddSingleton<IRunStore>(_runStore);
        // The assembly service resolves CoordinatorSteeringDecider (and its CoordinatorSteeringService
        // dependency) from the provider when it must drive an outstanding steering directive
        // (DriveOutstandingSteeringExecutionAsync). Existing tests never seed a Decided/Executing
        // directive so this path was previously unexercised; the unified-steering regression tests do.
        services.AddSingleton(sp => new CoordinatorSteeringService(
            _streamStore,
            new RunWorkflowRegistry(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CoordinatorSteeringService>.Instance,
            waitRegistry: _steeringWaits,
            runStore: _runStore));
        services.AddSingleton(sp => new CoordinatorSteeringDecider(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<CoordinatorSteeringService>(),
            NullLogger<CoordinatorSteeringDecider>.Instance));
        // Req-2 (Strict Lockout): the assembly service resolves IAssemblyAuthorRotationSelector to pick
        // a DIFFERENT eligible agent on a reviewer rejection. The harness repo path ("repo") has no team
        // roster, so a real SquadReader would always deadlock; register a configurable fake whose default
        // returns a rotated author (preserving "steers again" intent). Deadlock/single-eligible tests set
        // _rotation.Impl to return null.
        services.AddSingleton<IAssemblyAuthorRotationSelector>(_rotation);
        // Fix-A(3a) Path-2: the lockout rotation hands off to a DIFFERENT agent via the
        // IChildRevisionHandoff seam. The harness constructs no live RunOrchestrator, so register a fake
        // that records the handoff bundle and inserts the new child run (mirroring the real InsertAsync).
        _handoff = new FakeChildRevisionHandoff(_runStore);
        services.AddSingleton<IChildRevisionHandoff>(_handoff);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _assemblyStore = new CoordinatorAssemblyStore(_scopeFactory);

        _sut = new CoordinatorAssemblyService(
            _runStore,
            _streamStore,
            _assemblyStore,
            _reviewGate,
            _pipeline,
            _scopeFactory,
            _provider,
            new TestHostApplicationLifetime(),
            NullLogger<CoordinatorAssemblyService>.Instance,
            steeringWaits: _steeringWaits);
        _steering = new CoordinatorSteeringService(
            _streamStore,
            new RunWorkflowRegistry(),
            _scopeFactory,
            NullLogger<CoordinatorSteeringService>.Instance,
            waitRegistry: _steeringWaits,
            runStore: _runStore);
    }

    // ── D2 eligibility gate ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAssembly_BlocksAndWaitsForSteering_WhenASubtaskIsIneligible()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");
        await SeedCoordinatorRunAsync(coordinatorRunId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);

        var types = EventTypes_(coordinatorRunId);
        types.Should().Contain(EventTypes.CoordinatorAssemblyBlocked);
        types.Should().NotContain(EventTypes.CoordinatorAssemblyRaiStarted,
            "an ineligible plan must not proceed to collective RAI");
        _pipeline.IntegrationBuilds.Should().Be(0, "no integration branch is built when blocked");

        var state = await _assemblyStore.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.AssemblyBlocked);
        _streamStore.Get(coordinatorRunId)!.IsCompleted.Should().BeFalse("assembly_blocked now pauses for steering");
        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.InProgress, "the coordinator remains live while awaiting steering");

        // The blocked subtask is the second one (status "failed"); the first is "completed".
        var blockedId = subtaskIds[1];

        // The emitted block payload names WHICH subtasks blocked (id + title + status + agent), and
        // keeps the back-compat id-only list.
        var blockedEvent = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.CoordinatorAssemblyBlocked);
        var payload = System.Text.Json.JsonSerializer.SerializeToNode(blockedEvent.Payload)!.AsObject();
        payload["reason"]!.GetValue<string>().Should().Be("ineligible_subtasks");
        payload["ineligibleSubtaskIds"]!.AsArray().Select(n => n!.GetValue<int>())
            .Should().Equal(blockedId);
        var detail = payload["ineligibleSubtasks"]!.AsArray();
        detail.Should().HaveCount(1);
        var entry = detail[0]!.AsObject();
        entry["id"]!.GetValue<int>().Should().Be(blockedId);
        entry["title"]!.GetValue<string>().Should().Be("t1");
        entry["status"]!.GetValue<string>().Should().Be("failed");
        entry["agent"]!.GetValue<string>().Should().Be("morpheus");

        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", default);
        await run;

        // The block event is persisted when the paused stream eventually completes.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var persisted = await db.RunEvents
            .Where(e => e.RunId == coordinatorRunId && e.EventType == EventTypes.CoordinatorAssemblyBlocked)
            .ToListAsync();
        persisted.Should().HaveCount(1, "the blocked detail must survive in-memory stream eviction");
        using var doc = System.Text.Json.JsonDocument.Parse(persisted[0].PayloadJson);
        var persistedDetail = doc.RootElement.GetProperty("ineligibleSubtasks");
        persistedDetail.GetArrayLength().Should().Be(1);
        var persistedEntry = persistedDetail[0];
        persistedEntry.GetProperty("id").GetInt32().Should().Be(blockedId);
        persistedEntry.GetProperty("title").GetString().Should().Be("t1");
        persistedEntry.GetProperty("status").GetString().Should().Be("failed");
        persistedEntry.GetProperty("agent").GetString().Should().Be("morpheus");
    }

    // ── UNIFIED AUTONOMOUS STEERING (live v0.9.12-rc1 regression): an in-place revision whose child
    //    run ends WITHOUT a clean assemble_ready terminal (watch_stream_completed_without_terminal_event)
    //    left the target subtask FAILED. Advancing the directive to `applied` on the durable effect
    //    marker alone then let the FAILED subtask fall through the eligibility gate → assembly_blocked
    //    (ineligible_subtasks) → terminal assembly_failed, with NO visible steering action ("a glitch").
    //    DriveOutstandingSteeringExecutionAsync must instead detect the FAILED target and make a
    //    CONSCIOUS, VISIBLE dispatch_fresh decision so the subtask re-enters assembly — never a silent
    //    wedge. ───────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task RunAssembly_InPlaceSteer_TargetSubtaskFailed_ConsciouslyDispatchesFresh_NeverWedges()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // Two targeted subtasks: s0 healthy (the revision re-reached assemble_ready), s1 the in-place
        // revision that ran a full agent turn but ended FAILED (its child stream closed without a
        // terminal event, so RunWatchLoopService marked the run — and the subtask — failed).
        var childRunIds = new string?[] { RunId.New().ToString(), RunId.New().ToString() };
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId,
            new[] { SubtaskStatus.AssembleReady, SubtaskStatus.Failed },
            childRunIds);

        // An outstanding in-place steer directive is mid-execution (Status=executing) targeting BOTH
        // subtasks — exactly the live rubberduck-gate in_place_steer over subtasks [14,15].
        var directiveId = await SeedExecutingInPlaceDirectiveAsync(
            coordinatorRunId, subtaskIds, attempt: 1,
            instruction: "Fix the two server.js bugs the rubberduck found.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);

        var types = EventTypes_(coordinatorRunId);

        // The failed in-place revision must NOT silently wedge assembly.
        types.Should().NotContain(EventTypes.CoordinatorAssemblyBlocked,
            "a failed in-place revision must consciously fall back to dispatch_fresh, never wedge on ineligible_subtasks");
        types.Should().NotContain(EventTypes.CoordinatorAssemblyFailed,
            "the run must not terminate assembly_failed for a recoverable failed in-place revision");

        // A CONSCIOUS, VISIBLE dispatch_fresh decision is emitted (Ahmed's "never a glitch").
        var decisions = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .ToList();
        decisions.Should().Contain(
            d => d["decision"] != null
                 && d["decision"]!.GetValue<string>() == SteeringDirection.DispatchFresh,
            "the coordinator must emit a conscious dispatch_fresh decision for the failed in-place revision");
        // The visible CAUSE event names the failed subtask so the transition is never a glitch.
        decisions.Should().Contain(
            d => d["phase"] != null
                 && d["phase"]!.GetValue<string>() == "in_place_revision_failed_terminal",
            "the CAUSE (revision ended without a clean terminal) is surfaced before the fresh dispatch");

        // The failed target is reset to pending (never left failed) and a fresh pod is re-dispatched;
        // the healthy subtask keeps its assemble_ready result.
        var s0 = await GetSubtaskStatusAsync(subtaskIds[0]);
        var s1 = await GetSubtaskStatusAsync(subtaskIds[1]);
        s0.Should().Be(SubtaskStatus.AssembleReady, "the healthy target's result is preserved");
        s1.Should().Be(SubtaskStatus.Pending, "the failed target is reset for a conscious fresh dispatch");
        _dispatch.StartDispatchCalls.Should().NotBeEmpty("a fresh pod is re-dispatched for the failed subtask");

        // The persisted decision matches the real effect (dispatch_fresh) and the directive settles.
        var directive = await GetDirectiveAsync(directiveId);
        directive!.DecidedAction.Should().Be(SteeringDirection.DispatchFresh,
            "the durable DecidedAction must match the actual effect");
        directive.Status.Should().Be(SteeringStatus.Applied);
    }

    // ── UNIFIED STEERING (rubber-duck RD-B round-3, crash-BEFORE-launch): the directive is flipped to
    //    `executing` (MarkDirectiveExecutingAsync) BEFORE ExecuteInPlaceSteerAsync launches the revision
    //    and flips the targets to Running. A crash in that gap leaves the targets holding their PRE-steer
    //    assemble_ready/completed status with NO per-child effect marker written. A STATUS-ONLY advance
    //    gate would then read allEligible=true and mark the directive `applied` WITHOUT any revision ever
    //    having run — silently DROPPING the steering feedback. The corrected gate requires BOTH subtask
    //    eligibility AND every per-child effect marker confirmed, so a crash-before-launch is NOT falsely
    //    applied; it re-drives through ExecuteInPlaceSteerAsync (which here, with no resumable child run
    //    in the store, makes a CONSCIOUS dispatch_fresh) — steering is never silently dropped. ─────────
    [Fact]
    public async Task RunAssembly_InPlaceSteer_CrashBeforeLaunch_EffectUnconfirmed_DoesNotFalselyApply()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // Both targets still hold their PRE-steer eligible status (assemble_ready) — the crash happened
        // after the directive was flipped to `executing` but BEFORE the revision launched / flipped them
        // to Running. Crucially: NO SteeringRevisionExecution effect marker is seeded for either child.
        var childRunIds = new string?[] { RunId.New().ToString(), RunId.New().ToString() };
        var (_, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId,
            new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady },
            childRunIds);

        var directiveId = await SeedExecutingInPlaceDirectiveAsync(
            coordinatorRunId, subtaskIds, attempt: 1,
            instruction: "Fix the rubberduck findings.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);

        var decisions = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .ToList();

        // THE REGRESSION GUARD: the directive must NOT be falsely settled `applied` on subtask status
        // alone while its per-child effect marker is unconfirmed (that would silently drop the steer).
        decisions.Should().NotContain(
            d => d["phase"] != null && d["phase"]!.GetValue<string>() == "effect_confirmed_applied",
            "an eligible-status target whose per-child effect marker is NOT confirmed (crash-before-launch) " +
            "must NOT be marked applied — that would silently drop the steering feedback");

        // The steering is NOT dropped: it re-drives through ExecuteInPlaceSteerAsync. With no resumable
        // child run persisted (the launch never happened), the re-drive makes a CONSCIOUS, VISIBLE
        // dispatch_fresh decision (RD#6) rather than degrading silently.
        decisions.Should().Contain(
            d => d["decision"] != null
                 && d["decision"]!.GetValue<string>() == SteeringDirection.DispatchFresh,
            "the unconfirmed crash-before-launch directive is re-driven, not silently applied");

        var directive = await GetDirectiveAsync(directiveId);
        directive!.DecidedAction.Should().Be(SteeringDirection.DispatchFresh,
            "the persisted decision must match the real effect — never a silent in_place `applied`");

        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyFailed,
            "an unconfirmed in-place directive must be re-driven, not wedge assembly");
    }

    // ── UNIFIED AUTONOMOUS STEERING (Fix-B): resilient assembly-review loop ────────────────────────
    //    When the autonomous steering budget is exhausted the run must ESCALATE to the human-review gate
    //    (open awaiting_review so a human can approve / decline / steer) instead of latching terminal
    //    WorkPlanStatus.AssemblyBlocked and hanging with no way to intervene. Escalation is a durable,
    //    idempotent, recoverable effect; a human request-changes resets the autonomous budget bounded by
    //    a persisted round-trip counter. ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RouteAssembly_BudgetExhausted_EscalatesToHumanReview_NotTerminal()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        // Autonomous budget already exhausted → the decider returns Proceed.
        await SetPlanSteeringStateAsync(workPlanId, steeringIterations: 6);

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Route a rubberduck request-changes through steering. Budget-exhausted → escalate → park at
        // review, then the escalation live-awaits the human. Wait until the review is durably OPEN, then
        // cancel the (indefinite) human wait so the test does not hang; the durable escalation state is
        // already committed BEFORE the await.
        var route = InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Two server.js bugs remain.", touched, "tree-abc", cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, cts.Token);
        cts.Cancel();
        try { await route; } catch (OperationCanceledException) { }

        var types = EventTypes_(coordinatorRunId);
        types.Should().NotContain(EventTypes.CoordinatorAssemblyBlocked,
            "budget exhaustion must escalate to human review, NEVER latch terminal AssemblyBlocked");

        var escalation = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorAssemblyReviewRequested)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .Single();
        escalation["escalated"]!.GetValue<bool>().Should().BeTrue("the escalation must be visible, never a glitch");
        escalation["reason"]!.GetValue<string>().Should().Contain("budget");

        var (_, _, status, stage) = await GetPlanSteeringStateAsync(workPlanId);
        status.Should().Be(WorkPlanStatus.InReview, "the plan parks at human review");
        stage.Should().Be(AssemblyStage.Review, "the canonical review stage opens the human gate");

        var record = await CoordinatorAssemblyReviewPersistence.GetAsync(_scopeFactory, coordinatorRunId, default);
        record.Should().NotBeNull("a durable review request must back the escalated gate");

        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.AwaitingReview, "the coordinator run parks awaiting human review");

        var directive = await GetLatestDirectiveAsync(coordinatorRunId);
        directive!.DecidedAction.Should().Be(SteeringDirection.Proceed);
        directive.Status.Should().Be(SteeringStatus.Applied,
            "the escalation directive settles only AFTER the review is durably open");
    }

    [Fact]
    public async Task RouteAssembly_BudgetExhausted_Escalate_HumanApproves_Completes()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        await SetPlanSteeringStateAsync(workPlanId, steeringIterations: 6);

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var route = InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Fix remaining issues.", touched, "tree-approve", cts.Token);

        // The escalation opens the human-review gate; the human APPROVES → assembly completes (merge).
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null, TargetFiles: null, Reviewer: "alice"));
        await route;

        var types = EventTypes_(coordinatorRunId);
        types.Should().Contain(EventTypes.CoordinatorAssemblyReviewApproved,
            "the escalated review gate honors an approve exactly like a normal human gate");
        types.Should().Contain(EventTypes.CoordinatorAssemblyCompleted, "approve → merge → complete");
        _pipeline.Merges.Should().BeGreaterThan(0);
        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.Completed);
    }

    [Fact]
    public async Task RouteAssembly_HumanRequestChanges_ResetsBudget_SteersAgain()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        // No resumable child runs → after the budget reset the decider steers via CONSCIOUS dispatch_fresh.
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        // Budget exhausted; a HUMAN request-changes is a supervised action that ALWAYS grants a fresh
        // convergence mandate (no cap), so it resets the autonomous budget.
        await SetPlanSteeringStateAsync(workPlanId, steeringIterations: 6, humanReviewRoundTrips: 0);

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.HumanReview,
            "Please fix the signup validation.", touched, "tree-human", cts.Token);

        var (steeringIterations, roundTrips, _, _) = await GetPlanSteeringStateAsync(workPlanId);
        roundTrips.Should().Be(1, "the human round-trip is persisted (cross-replica/crash-safe)");
        // The exhausted budget (6) was reset to 0 by the human mandate, then the single conscious steer
        // decision re-incremented it to 1 — proving the reset happened (without it, it would stay ≥6).
        steeringIterations.Should().Be(1, "a human request-changes resets the autonomous budget");

        var reset = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteering)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .FirstOrDefault(o => o["humanReviewRoundTrip"] != null);
        reset.Should().NotBeNull("the budget reset must be a visible steering event");
        reset!["humanReviewRoundTrip"]!.GetValue<int>().Should().Be(1);
        reset["note"]!.GetValue<string>().Should()
            .Be("human request-changes: autonomous steering budget reset for a fresh convergence pass");

        // Budget had headroom after the reset → the coordinator STEERS again (not Proceed/escalate).
        var decisions = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .ToList();
        decisions.Should().NotContain(
            d => d["decision"] != null && d["decision"]!.GetValue<string>() == SteeringDirection.Proceed,
            "a human request-changes resets the budget so the coordinator converges again");
    }

    [Fact]
    public async Task RouteAssembly_HumanRequestChanges_HighRoundTripCount_StillResetsBudget_SteersAgain()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        // Many prior human round-trips (5) AND an exhausted autonomous budget (6). There is NO cap on
        // human round-trips: a supervised human request-changes must STILL reset the budget and let the
        // coordinator converge again — never a silent dead-end.
        await SetPlanSteeringStateAsync(workPlanId, steeringIterations: 6, humanReviewRoundTrips: 5);

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.HumanReview,
            "Still not right — please fix.", touched, "tree-high", cts.Token);

        var (steeringIterations, roundTrips, status, _) = await GetPlanSteeringStateAsync(workPlanId);
        roundTrips.Should().Be(6, "the round-trip counter still increments as pure telemetry");
        // Reset to 0 (unconditional) then re-incremented by the single conscious steer → 1. Without the
        // reset it would have stayed at 6 (over budget) and the decider would have Proceeded/re-parked.
        steeringIterations.Should().Be(1, "a high human round-trip count does NOT stop the budget reset");

        var reset = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteering)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .FirstOrDefault(o => o["humanReviewRoundTrip"] != null);
        reset.Should().NotBeNull("the budget reset must be a visible steering event");
        reset!["humanReviewRoundTrip"]!.GetValue<int>().Should().Be(6);
        reset["note"]!.GetValue<string>().Should()
            .Be("human request-changes: autonomous steering budget reset for a fresh convergence pass");
        reset.ContainsKey("budgetReset").Should().BeFalse("the cap-gated budgetReset field was removed");
        reset.ContainsKey("maxHumanReviewRoundTrips").Should().BeFalse("there is no human round-trip cap");

        // Budget had headroom after the reset → the coordinator STEERS again; it does NOT re-park at
        // review and never latches a terminal AssemblyBlocked.
        status.Should().NotBe(WorkPlanStatus.InReview);
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked);
        var decisions = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .ToList();
        decisions.Should().NotContain(
            d => d["decision"] != null && d["decision"]!.GetValue<string>() == SteeringDirection.Proceed,
            "a human request-changes ALWAYS resets the budget so the coordinator converges again");
    }

    [Fact]
    public async Task DriveOutstanding_ProceedDirective_CrashBeforeReviewOpen_ReDrivesEscalation()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        // Simulate a crash AFTER MarkDirectiveExecuting but BEFORE the review opened: the plan is still
        // in the AssemblySteering lease, NO durable review request exists, and the Proceed directive is
        // left `executing`. A status-only recovery would silently mark it applied (drop the escalation).
        await SetPlanSteeringStateAsync(workPlanId, status: WorkPlanStatus.AssemblySteering, steeringIterations: 6);
        var directiveId = await SeedExecutingProceedDirectiveAsync(coordinatorRunId, subtaskIds, "tree-crash");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var redrove = await InvokeDriveOutstandingSteeringExecutionAsync(
            Context(coordinatorRunId), workPlanId, cts.Token);

        redrove.Should().BeTrue("recovery re-drives the unfinished escalation and stops the assembly pass");
        var (_, _, status, stage) = await GetPlanSteeringStateAsync(workPlanId);
        status.Should().Be(WorkPlanStatus.InReview, "the escalation is completed on recovery, never dropped");
        stage.Should().Be(AssemblyStage.Review);
        (await CoordinatorAssemblyReviewPersistence.GetAsync(_scopeFactory, coordinatorRunId, default))
            .Should().NotBeNull("recovery writes the durable review request that the crash skipped");
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyReviewRequested);
        (await GetDirectiveAsync(directiveId))!.Status.Should().Be(SteeringStatus.Applied,
            "the directive settles only after the review is durably open");
    }

    [Fact]
    public async Task DriveOutstanding_ProceedDirective_ReviewAlreadyOpen_SettlesWithoutReDriving()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        // The escalation already completed before the crash: plan InReview + durable review request.
        await SetPlanSteeringStateAsync(workPlanId, status: WorkPlanStatus.InReview);
        await SetPlanReviewStateAsync(workPlanId);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice",
            IntegrationBranchName_(coordinatorRunId), "tree-open", default);
        var directiveId = await SeedExecutingProceedDirectiveAsync(coordinatorRunId, subtaskIds, "tree-open");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var redrove = await InvokeDriveOutstandingSteeringExecutionAsync(
            Context(coordinatorRunId), workPlanId, cts.Token);

        redrove.Should().BeFalse("a durably-open escalation is simply settled, not re-driven");
        (await GetDirectiveAsync(directiveId))!.Status.Should().Be(SteeringStatus.Applied);
        var (_, _, status, _) = await GetPlanSteeringStateAsync(workPlanId);
        status.Should().Be(WorkPlanStatus.InReview, "recovery must not disturb the already-open review");
    }

    private static string IntegrationBranchName_(string coordinatorRunId) =>
        CoordinatorAssemblyService.IntegrationBranchName(coordinatorRunId);

    // ── UNIFIED AUTONOMOUS STEERING (Req-1 context-carry + Req-2 Strict Lockout) ───────────────────
    //    Req-1: every revision re-trigger (conscious fresh dispatch AND in-place resume) must hand the
    //    revising agent FULL context — the ACCUMULATED reviewer feedback across ALL prior rejection
    //    rounds (not just the latest) plus a pointer to the prior work — so repeated rejections reflect
    //    genuine quality problems, not agent amnesia. Req-2: a CONTEXT-COMPLETE reviewer rejection locks
    //    the author out and rotates the revision to a DIFFERENT eligible agent (conscious, visible); a
    //    single-eligible-agent domain / deadlock escalates to human review, never rotates blind, never
    //    terminal. Req-2 is mechanically gated on Req-1. ────────────────────────────────────────────

    [Fact]
    public async Task RouteAssembly_Rejection_DispatchFresh_CarriesAccumulatedFeedbackAndPriorWork()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // One rejected subtask that already has PRIOR work (a child run) to preserve a pointer to. The
        // prior child run carries the worktree branch the fresh/rotated agent will REUSE (new session).
        var priorChildRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady }, new string?[] { priorChildRunId });
        await SeedChildRunAsync(RunId.Parse(priorChildRunId), "agentweaver/wt/child-hero", DiffTouching("index.html"));
        // DECIDER-OWNED ROUTING: lapse retention so the decider judges the target UNRESUMABLE →
        // DispatchFresh → lockout rotation (the prior child remains the handoff source).
        await LapseSteeringRetentionAsync(subtaskIds[0]);

        // Two PRIOR rejection rounds already recorded for this target (accumulated history).
        await SeedPriorRejectionDirectiveAsync(
            coordinatorRunId, subtaskIds, SteeringSource.Rubberduck, "gate:rubberduck",
            "Round1: the signup form is missing client-side validation.");
        await SeedPriorRejectionDirectiveAsync(
            coordinatorRunId, subtaskIds, SteeringSource.BuildTest, "gate:build-test",
            "Round2: the build fails — lint errors in server.js.");

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // A fresh rubberduck rejection (round 3). The default rotation fake returns a different eligible
        // author → conscious dispatch_fresh, which (because a reusable prior child exists) re-dispatches
        // via the CONTEXT-CARRYING handoff (StartChildRevisionHandoffAsync), NOT a plain fresh dispatch.
        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Round3: the landing page hero is not visually stunning.", touched, "tree-r3", cts.Token);

        var (_, _, priorPointer, _) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        priorPointer.Should().Be(priorChildRunId,
            "the prior child run pointer is captured BEFORE ChildRunId is repointed (change #1)");

        // The re-dispatch went through the context-carrying handoff exactly once, threading the
        // ACCUMULATED feedback + the prior worktree branch to a NEW (non-locked-out) agent session.
        _handoff.Calls.Should().ContainSingle("the reusable prior child re-dispatches via the handoff, not a plain fresh dispatch");
        var call = _handoff.Calls[0];
        call.NewAgentRun.Id.ToString().Should().NotBe(priorChildRunId,
            "the handoff mints a NEW run id ⇒ a new deterministic session ⇒ lockout-correct");
        call.NewAgentRun.AgentName.Should().Be("rotated-morpheus",
            "the handoff dispatches under the ROTATED (non-locked-out) author");
        call.PriorChild.Id.ToString().Should().Be(priorChildRunId,
            "the prior (locked-out author's) child run is handed off as the worktree/branch source");
        call.Feedback.PriorWorktreeBranch.Should().Be("agentweaver/wt/child-hero",
            "the handoff reuses the prior worktree branch while minting a NEW session");
        call.Feedback.RenderedGuidance.Should().NotBeNullOrEmpty(
            "RenderedGuidance is prompt-ready so the consumer need not re-derive it");
        call.Feedback.RenderedGuidance!.Should().Contain("Round1").And.Contain("Round2").And.Contain("Round3",
            "the handoff carries ACCUMULATED feedback across ALL prior rounds, not just the latest (change #2)");
        call.Feedback.RenderedGuidance!.Should().Contain("agentweaver/wt/child-hero",
            "the guidance points the new agent at the prior worktree branch so it builds on prior work (Req-1)");

        // The STABLE AccumulatedReviewFeedback handoff contract (Morpheus 3b) is populated correctly.
        var bundle = await _sut.BuildAccumulatedReviewFeedbackAsync(
            coordinatorRunId, subtaskIds[0], "Round3: the landing page hero is not visually stunning.",
            priorChildRunId, default);
        bundle.SubtaskId.Should().Be(subtaskIds[0].ToString());
        bundle.PriorWorktreeBranch.Should().Be("agentweaver/wt/child-hero",
            "the consumer reuses the prior worktree branch while minting a NEW session for the non-locked-out agent");
        bundle.PriorRounds.Should().HaveCountGreaterThanOrEqualTo(3, "PriorRounds is target+rejection-scoped across all rounds");
        bundle.RenderedGuidance.Should().NotBeNullOrEmpty("RenderedGuidance is prompt-ready so the consumer need not re-derive it");

        var freshDecision = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .FirstOrDefault(d => d["decision"]?.GetValue<string>() == SteeringDirection.DispatchFresh
                && d["disposition"]?.GetValue<string>() == "rejection");
        freshDecision.Should().NotBeNull("a reviewer rejection rotates via a VISIBLE conscious dispatch_fresh");
    }

    [Fact]
    public async Task InPlaceRetryGuidance_CarriesAccumulatedFeedback_PreservesSession_NoPriorPodPointer()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (_, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady });

        await SeedPriorRejectionDirectiveAsync(
            coordinatorRunId, subtaskIds, SteeringSource.Rubberduck, "gate:rubberduck",
            "Round1: add a password strength meter.");
        await SeedPriorRejectionDirectiveAsync(
            coordinatorRunId, subtaskIds, SteeringSource.Rai, "gate:rai",
            "Round2: sanitize the email input.");

        // The in-place resume path (ExecuteInPlaceSteerAsync) builds its guidance from the SAME
        // accumulated-feedback + guidance builders, with priorChildRunId:null (the session is preserved,
        // not a fresh pod). Assert both: accumulated feedback is threaded, and NO fresh-pod pointer.
        var guidance = await InvokeBuildAccumulatedRetryGuidanceAsync(
            coordinatorRunId, subtaskIds, "Latest: tighten the validation copy.",
            priorChildRunId: null, integrationBranch: null);

        guidance.Should().Contain("Round1").And.Contain("Round2",
            "the in-place resume explicitly carries accumulated feedback (the stream is removed before restart, change #6)");
        guidance.Should().Contain("Latest: tighten the validation copy.");
        guidance.Should().NotContain("Prior work",
            "in-place resume preserves the child session — it does not thread a fresh prior-pod pointer");
    }

    [Fact]
    public async Task RouteAssembly_Rejection_LocksOutAuthor_RotatesToDifferentEligibleAgent()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady });

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Default rotation fake returns a different eligible author ("rotated-morpheus").
        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "The hero section is broken.", touched, "tree-rot", cts.Token);

        var (assigned, lockedOut, _, _) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        assigned.Should().Be("rotated-morpheus",
            "a reviewer rejection rotates the revision to a DIFFERENT eligible agent (Strict Lockout)");
        lockedOut.Should().NotBeNull();
        lockedOut!.Should().Contain("morpheus",
            "the rejected author is durably locked out of the artifact (change #4)");

        var rotation = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .Single(d => d["decision"]?.GetValue<string>() == SteeringDirection.DispatchFresh
                && d["rotatedTo"] != null);
        rotation["rotatedFrom"]!.GetValue<string>().Should().Be("morpheus");
        rotation["rotatedTo"]!.GetValue<string>().Should().Be("rotated-morpheus");
        rotation["disposition"]!.GetValue<string>().Should().Be("rejection");
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked);
    }

    [Fact]
    public async Task RouteAssembly_Rejection_Lockout_DispatchesToDifferentAgentViaContextCarryingHandoff()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // The rejected subtask has PRIOR work (a child run under the locked-out author "morpheus"). Its
        // worktree branch is the source the rotated (different) agent REUSES while minting a NEW session.
        var priorChildRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady }, new string?[] { priorChildRunId });
        await SeedChildRunAsync(RunId.Parse(priorChildRunId), "agentweaver/wt/child-prior", DiffTouching("app.ts"));
        // DECIDER-OWNED ROUTING: lapse retention so the target is UNRESUMABLE → DispatchFresh → lockout.
        await LapseSteeringRetentionAsync(subtaskIds[0]);

        await SeedPriorRejectionDirectiveAsync(
            coordinatorRunId, subtaskIds, SteeringSource.Rubberduck, "gate:rubberduck",
            "Round1: accessibility labels are missing.");

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Default rotation fake rotates "morpheus" → "rotated-morpheus" (different eligible agent).
        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Round2: contrast ratios fail WCAG AA.", touched, "tree-handoff", cts.Token);

        // The different-agent lockout rotation dispatches through StartChildRevisionHandoffAsync — NOT a
        // plain fresh dispatch that would provision a blank worktree and discard the prior work.
        _handoff.Calls.Should().ContainSingle("the reviewer rejection with reusable prior work rotates via the context-carrying handoff");
        var call = _handoff.Calls[0];

        // new run id ≠ prior child (a NEW deterministic session ⇒ lockout-correct)
        call.NewAgentRun.Id.ToString().Should().NotBe(priorChildRunId);
        call.PriorChild.Id.ToString().Should().Be(priorChildRunId,
            "the locked-out author's child run is the worktree/branch source handed to the new agent");

        // rotated agent ≠ locked-out author
        call.NewAgentRun.AgentName.Should().Be("rotated-morpheus");
        call.NewAgentRun.AgentName.Should().NotBe("morpheus", "the locked-out author may not produce the next version");

        // feedback threaded (target+rejection-scoped accumulated feedback + prior worktree branch)
        call.Feedback.SubtaskId.Should().Be(subtaskIds[0].ToString());
        call.Feedback.PriorWorktreeBranch.Should().Be("agentweaver/wt/child-prior");
        call.Feedback.RenderedGuidance.Should().NotBeNullOrEmpty();
        call.Feedback.RenderedGuidance!.Should().Contain("Round1").And.Contain("Round2",
            "the new agent receives ACCUMULATED feedback across all prior rejection rounds");

        // The subtask now points at the NEW child and retains the prior pointer; the author is rotated.
        var (assigned, lockedOut, priorPointer, recoveryGuidance) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        assigned.Should().Be("rotated-morpheus");
        lockedOut!.Should().Contain("morpheus");
        priorPointer.Should().Be(priorChildRunId, "the prior pointer is retained for provenance / worktree source");
        recoveryGuidance.Should().BeNull(
            "the handoff injects guidance into the new agent's prompt directly — it is NOT re-carried via RecoveryGuidance");

        // Re-dispatch went back through the loop, and never latched terminal.
        _dispatch.StartDispatchCalls.Should().NotBeEmpty("the plan returns to dispatching so the loop re-observes the handoff child");
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked);
    }

    [Fact]
    public async Task RouteAssembly_Rejection_Lockout_TwoRotations_Round1GuidanceAppearsExactlyOnce()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // The rejected subtask starts with a prior child under the original author "morpheus".
        var originalChildRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady }, new string?[] { originalChildRunId });
        await SeedChildRunAsync(RunId.Parse(originalChildRunId), "agentweaver/wt/child-prior", DiffTouching("app.ts"));
        // DECIDER-OWNED ROUTING: lapse retention so BOTH rejections route DispatchFresh → lockout
        // rotation (SetSubtaskHandoffRunningAsync never re-arms retention, so it stays lapsed for round 2).
        await LapseSteeringRetentionAsync(subtaskIds[0]);

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // A distinctive round-1 marker that must appear in the FINAL child's task EXACTLY once (never
        // doubled by chaining a prior handoff child's already-guidance-embedding Task).
        const string round1Marker = "ROUND1_UNIQUE_MARKER_a11y";

        // ── Rotation 1: morpheus locked out → rotated-morpheus; guidance carries round-1's feedback. ──
        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            $"{round1Marker}: accessibility labels are missing.", touched, "tree-r1", cts.Token);

        _handoff.Calls.Should().ContainSingle();
        var firstRotationChildId = _handoff.Calls[0].NewAgentRun.Id;
        // The 1st rotation's child DOES embed round-1 guidance in its persisted Task — it becomes the
        // 2nd rotation's priorChild, which is exactly the chaining source the fix must NOT re-carry.
        (await GetRunTaskAsync(firstRotationChildId))!.Should().Contain(round1Marker);

        // Simulate the rotated child being rejected AGAIN: return the subtask to a routable state while
        // KEEPING ChildRunId pointing at the 1st rotation's child (the 2nd rotation's priorChild).
        await ResetSubtaskForNextRejectionKeepingChildAsync(subtaskIds[0]);

        // ── Rotation 2: rotated-morpheus locked out → a further different agent; priorChild = child1. ──
        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "ROUND2_UNIQUE_MARKER: contrast ratios fail WCAG AA.", touched, "tree-r2", cts.Token);

        _handoff.Calls.Should().HaveCount(2);
        var secondRotation = _handoff.Calls[1];
        secondRotation.PriorChild.Id.Should().Be(firstRotationChildId,
            "the 2nd rotation reuses the 1st rotation's child as its prior work source");
        secondRotation.NewAgentRun.AgentName.Should().NotBe("morpheus").And.NotBe("rotated-morpheus",
            "each rejection additionally locks out that revision's author (Strict Lockout step 6)");

        // ROOT-CAUSE ASSERTION: the FINAL child's persisted Task carries round-1's guidance EXACTLY once.
        // The handoff appends the single accumulated RenderedGuidance onto a GUIDANCE-FREE canonical base
        // (BuildCanonicalSubtaskTask), so the 1st rotation's already-embedded round-1 guidance is NOT
        // chained via priorChild.Task — no compounding duplication across rotations.
        var finalTask = await GetRunTaskAsync(secondRotation.NewAgentRun.Id);
        (finalTask!.Split(round1Marker).Length - 1).Should().Be(1,
            "round-1 guidance appears once (via accumulated prior rounds), never doubled by chaining priorChild.Task");
    }

    // ── #233 — SINGLE-ELIGIBLE-AGENT DOMAIN DEGRADES (does NOT dead-end to a human on round 1) ────────
    //    The live incident (staging run 825ea158): at the collective-assembly review gate, a rubberduck
    //    request-changes scoped to a subtask whose domain has only ONE eligible agent used to dead-end
    //    autopilot to human review on the FIRST rejection (the strict cross-agent lockout had no other
    //    eligible agent, and SquadAuthorRotationSelector returned null → lockout_deadlock → escalate).
    //    With the fix, when there IS context to carry, the strict lockout DEGRADES to a SAME-AUTHOR fresh
    //    re-dispatch: same author, NO lockout roster mutation, prior worktree branch reused, and the plan
    //    returns to dispatching (NOT parked at InReview) on round 1.
    [Fact]
    public async Task RouteAssembly_Rejection_SingleEligibleDomain_DegradesToSameAuthorFreshDispatch_NotEscalate()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // The rejected subtask has PRIOR work (a child run under the SOLE eligible author "morpheus").
        // Its worktree branch is the source the SAME author reuses on the degraded fresh re-dispatch.
        var priorChildRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady }, new string?[] { priorChildRunId });
        await SeedChildRunAsync(RunId.Parse(priorChildRunId), "agentweaver/wt/child-single", DiffTouching("index.html"));
        // DECIDER-OWNED ROUTING: lapse retention so the target is UNRESUMABLE → DispatchFresh → lockout
        // rotation (the prior child remains the context source the degraded re-dispatch reuses).
        await LapseSteeringRetentionAsync(subtaskIds[0]);

        // Single-eligible-agent domain: no eligible agent outside the current author → the rotation
        // selector returns null. Pre-#233 this dead-ended to human review on the FIRST rejection.
        _rotation.Impl = (_, _, _) => null;

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Needs a specialist we do not have.", touched, "tree-single", cts.Token);

        // NOT terminal, and NOT a cross-agent handoff — the degrade is a same-author reset-to-pending.
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked,
            "a single-eligible-agent domain no longer latches terminal AssemblyBlocked");
        _handoff.Calls.Should().BeEmpty(
            "the degrade is a SAME-author fresh re-dispatch (reset-to-pending), NOT a cross-agent handoff");

        // SAME author, NO lockout roster mutation — the strict cross-agent lockout is degraded, not applied.
        var (assigned, lockedOut, priorPointer, recoveryGuidance) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        assigned.Should().Be("morpheus",
            "the degrade keeps the SOLE eligible author (a same-author fresh re-dispatch, never a rotation)");
        lockedOut.Should().BeNull(
            "the degrade does NOT lock out the sole eligible author — locking it would re-create the deadlock");

        // The prior worktree/branch is reused so the same author BUILDS ON prior work (context carried,
        // no amnesia) — exactly the ConsciousDispatchFreshFallbackAsync → ResetSubtasksToPendingAsync path.
        priorPointer.Should().Be(priorChildRunId,
            "the prior child pointer is captured so the degraded fresh dispatch reuses the branch (Req-1)");
        recoveryGuidance.Should().NotBeNullOrEmpty(
            "the accumulated feedback + prior worktree branch is carried via RecoveryGuidance");
        recoveryGuidance!.Should().Contain("agentweaver/wt/child-single",
            "the guidance points the same author at the prior worktree branch (builds on prior work)");

        // The plan does NOT park at human review on round 1 — it returns to dispatching for the re-drive.
        var (_, _, status, _) = await GetPlanSteeringStateAsync(workPlanId);
        status.Should().Be(WorkPlanStatus.Dispatching,
            "the degraded directive re-dispatches; it does NOT dead-end to human review on round 1 (#233)");

        // The degrade is a VISIBLE conscious dispatch_fresh carrying the single_eligible_agent rationale.
        var degrade = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .FirstOrDefault(d => d["decision"]?.GetValue<string>() == SteeringDirection.DispatchFresh
                && d["rationale"] != null
                && d["rationale"]!.GetValue<string>().Contains("single_eligible_agent"));
        degrade.Should().NotBeNull(
            "the degrade is surfaced as a conscious dispatch_fresh with a single_eligible_agent rationale");
    }

    // ── #233 — the same-author degrade loop is BOUNDED (never infinite). A single-eligible-agent domain
    //    where the reviewer keeps rejecting degrades to a same-author fresh re-dispatch each round, but
    //    ResetSubtasksToPendingAsync does NOT reset Subtask.RecoveryAttempts, so the decider's per-subtask
    //    recovery budget (MaxRecoveryAttempts) still bounds it: once exhausted the policy flips to Proceed
    //    and the gate ESCALATES to human review. ───────────────────────────────────────────────────────
    [Fact]
    public async Task RouteAssembly_Rejection_SingleEligibleDomain_RepeatedRejection_BoundedByRecoveryBudget_ThenEscalates()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady });
        var subtaskId = subtaskIds[0];

        // Single-eligible-agent domain: no eligible agent outside the current author for EVERY round.
        _rotation.Impl = (_, _, _) => null;

        // Rounds 1..MaxRecoveryAttempts: each rejection DEGRADES to a same-author fresh re-dispatch (no
        // escalation, no lockout), consuming exactly ONE per-subtask recovery attempt per round. The
        // subtask's RecoveryAttempts is NOT reset by the degrade → the budget monotonically approaches
        // the cap, proving the loop is BOUNDED (it cannot spin forever).
        for (var round = 1; round <= CoordinatorSteeringService.MaxRecoveryAttempts; round++)
        {
            var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
            using var roundCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            await InvokeRouteAssemblyGateThroughSteeringAsync(
                Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
                $"Round{round}: still not acceptable.", touched, $"tree-round-{round}", roundCts.Token);

            EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked,
                $"round {round} (≤ budget) degrades — it never latches terminal AssemblyBlocked");
            var (assigned, lockedOut, _, _) = await GetSubtaskFieldsAsync(subtaskId);
            assigned.Should().Be("morpheus", $"round {round} keeps the same author (degrade, never a rotation)");
            lockedOut.Should().BeNull($"round {round} never locks out the sole eligible author");
            (await GetSubtaskRecoveryAttemptsAsync(subtaskId)).Should().Be(round,
                "each degrade consumes exactly one recovery attempt (the reset does NOT clear RecoveryAttempts)");
            var (_, _, midStatus, _) = await GetPlanSteeringStateAsync(workPlanId);
            midStatus.Should().NotBe(WorkPlanStatus.InReview, $"round {round} (≤ budget) does NOT escalate to human review");
        }

        // Round MaxRecoveryAttempts+1: the per-subtask recovery budget is now exhausted → the decider's
        // policy flips to Proceed → this gate ESCALATES to human review (the bounded loop terminates).
        var finalTouched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var route = InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Final: still rejecting.", finalTouched, "tree-final", cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyReviewRequested, cts.Token);
        cts.Cancel();
        try { await route; } catch (OperationCanceledException) { }

        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked,
            "the budget-exhausted escalation parks at human review, never a terminal wedge");
        var (_, _, status, stage) = await GetPlanSteeringStateAsync(workPlanId);
        status.Should().Be(WorkPlanStatus.InReview,
            "once the recovery budget is exhausted the bounded same-author loop escalates to a human");
        stage.Should().Be(AssemblyStage.Review);
        (await CoordinatorAssemblyReviewPersistence.GetAsync(_scopeFactory, coordinatorRunId, default))
            .Should().NotBeNull("a durable review card backs the budget-exhausted escalation");

        // The terminal escalation is a conscious Proceed decision (budget exhausted), never an infinite
        // same-author loop and never a no-context amnesia escalation on round 1.
        var proceed = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .Any(d => d["decision"]?.GetValue<string>() == SteeringDirection.Proceed);
        proceed.Should().BeTrue("the bounded loop terminates in a conscious Proceed (budget-exhausted) escalation");
    }

    // ── DECIDER-OWNED ROUTING (Fix-B, run 19cec519) ───────────────────────────────────────────────
    //    The coordinator's decider is the SINGLE authority on how gate feedback is applied. There is NO
    //    post-decision override that force-rotates every RequestChanges to a different agent. A RESUMABLE
    //    target routes to an IN-PLACE steer (SAME author, context preserved) — NOT a lockout rotation.
    [Fact]
    public async Task RouteAssembly_Rejection_ResumableTarget_SteersInPlace_SameAuthor_NoLockoutRosterMutation()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // A RESUMABLE target: it references a child run and retention is NOT lapsed → the decider chooses
        // in_place_steer. (The child run is intentionally absent from the run store so the in-place path
        // makes a conscious fallback rather than launching via an unavailable RunOrchestrator — either way
        // the point stands: the author is NOT rotated and NO lockout roster is mutated.)
        var childRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady }, new string?[] { childRunId });

        var touched = subtaskIds.ToDictionary(id => id, _ => (IReadOnlySet<string>)new HashSet<string>());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.BuildTest,
            "The build fails — fix the compile error in server.js.", touched, "tree-inplace", cts.Token);

        // The decider chose in_place_steer (NOT lockout rotation) — the pre-fix override forced rotation.
        var decisions = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .ToList();
        decisions.Should().Contain(
            d => d["decision"] != null && d["decision"]!.GetValue<string>() == SteeringDirection.InPlaceSteer,
            "a RESUMABLE request-changes routes to in_place_steer, not a forced lockout rotation");

        // SAME author, NO lockout roster mutation, and NO context-carrying handoff to a different agent.
        var (assigned, lockedOut, _, _) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        assigned.Should().Be("morpheus", "in-place steer keeps the SAME author (context preserved)");
        lockedOut.Should().BeNull("in-place steer must NOT lock out the author or mutate the roster");
        _handoff.Calls.Should().BeEmpty("in-place steer never rotates to a different agent via the handoff");
        decisions.Should().NotContain(
            d => d["rotatedTo"] != null,
            "no lockout rotation occurs on a resumable in-place steer");
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked);
    }

    // ── CRASH RECOVERY (rubber-duck change #1): a DispatchFresh directive left `executing` by a crash
    //    after MarkDirectiveExecutingAsync but before the lockout rotation/handoff completed must be
    //    RE-DRIVEN by DriveOutstandingSteeringExecutionAsync (the rotation actually happens), NOT silently
    //    marked `applied` (which would drop the rotation). ───────────────────────────────────────────
    [Fact]
    public async Task DriveOutstanding_DispatchFreshExecuting_CrashBeforeEffect_ReDrivesRotation_NotSilentlyApplied()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        var priorChildRunId = RunId.New().ToString();
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady }, new string?[] { priorChildRunId });
        await SeedChildRunAsync(RunId.Parse(priorChildRunId), "agentweaver/wt/child-crash", DiffTouching("app.ts"));

        // A DispatchFresh directive stuck `executing` — the crash happened after MarkDirectiveExecutingAsync
        // but BEFORE any rotation ran (the author is still "morpheus", no lockout, no handoff).
        var directiveId = await SeedExecutingDispatchFreshDirectiveAsync(
            coordinatorRunId, subtaskIds, attempt: 1,
            instruction: "The target session is unresumable — rotate the author.");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reDrove = await InvokeDriveOutstandingSteeringExecutionAsync(
            Context(coordinatorRunId), workPlanId, cts.Token);

        reDrove.Should().BeTrue("a DispatchFresh directive stuck executing must be RE-DRIVEN, not settled blindly");

        // The rotation actually happened (never a silent apply): author rotated + rejected author locked out.
        var (assigned, lockedOut, priorPointer, _) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        assigned.Should().Be("rotated-morpheus", "the re-drive performs the lockout rotation that the crash dropped");
        lockedOut!.Should().Contain("morpheus", "the rejected author is locked out by the re-driven rotation");
        priorPointer.Should().Be(priorChildRunId, "the prior child is retained as the handoff source");
        _handoff.Calls.Should().ContainSingle("the re-drive dispatches the rotated author via the context-carrying handoff");

        var directive = await GetDirectiveAsync(directiveId);
        directive!.DecidedAction.Should().Be(SteeringDirection.DispatchFresh, "the persisted decision matches the real effect");
        directive.Status.Should().Be(SteeringStatus.Applied, "the re-drive settles the directive after the effect completes");
    }

    // ── #233 — PARTIAL-ROTATION CRASH → SINGLE-ELIGIBLE ON THE SURVIVING TARGET: a MULTI-target
    //    DispatchFresh directive can crash AFTER rotating+stamping target A but BEFORE target B. On
    //    re-drive, target A matches its (directiveId, attempt) stamp (already-rotated, carried), while
    //    target B has a SINGLE eligible agent (SelectRotationAuthor returns null). BEFORE #233 this
    //    escalated the whole directive to human review to avoid silently dropping target B. AFTER #233,
    //    because there IS context to carry (a non-empty instruction), the directive DEGRADES to a
    //    SAME-AUTHOR fresh re-dispatch for ALL its targets (via ConsciousDispatchFreshFallbackAsync →
    //    RequestChangesAsync, which resets the FULL target set). The invariant this test protects still
    //    holds — target B is NEVER silently dropped — now via a same-author re-dispatch (reset-to-pending
    //    with full context) instead of an escalation. The loop stays BOUNDED by the recovery budget.
    [Fact]
    public async Task DispatchFreshReDrive_PartialRotationThenSingleEligible_DegradesToSameAuthorFreshDispatch_NoSilentDrop()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // Two rejection targets (A, B) under a single multi-target DispatchFresh directive.
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });
        var targetA = subtaskIds[0];
        var targetB = subtaskIds[1];

        // The directive crashed mid-effect: it is still `executing` with a non-empty instruction (so the
        // re-drive HAS context to carry → the single-eligible deadlock DEGRADES, it does NOT escalate).
        const int attempt = 1;
        var directiveId = await SeedExecutingDispatchFreshDirectiveAsync(
            coordinatorRunId, subtaskIds, attempt,
            instruction: "Both artifacts were rejected — rotate the authors.");
        // Mid-DispatchFresh execution the plan sits in the AssemblySteering lease; the degrade's
        // RequestChangesAsync returns it to Dispatching.
        await SetPlanSteeringStateAsync(workPlanId, status: WorkPlanStatus.AssemblySteering, steeringIterations: 6);

        // Target A was ALREADY rotated + durably stamped by THIS (directiveId, attempt) before the crash:
        // it now carries the durable (LastResetDirectiveId, LastResetAttempt) stamp, so the re-drive treats
        // it as already-rotated (carried under its current author, never re-selected).
        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var a = await db.Subtasks.FirstAsync(s => s.Id == targetA);
            a.AssignedAgent = "rotated-morpheus";
            a.LastResetDirectiveId = directiveId;
            a.LastResetAttempt = attempt;
            a.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        // Target B has NOT been rotated yet, and its domain has a SINGLE eligible agent → the rotation
        // selector returns null for it. Target A is skipped by the stamp, so this null only affects B.
        _rotation.Impl = (_, _, _) => null;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reDrove = await InvokeDriveOutstandingSteeringExecutionAsync(
            Context(coordinatorRunId), workPlanId, cts.Token);

        reDrove.Should().BeTrue("the re-drive degrades the single-eligible target and stops the assembly pass");

        // DEGRADE (not escalate): no terminal block, no human-review park, no cross-agent handoff.
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyBlocked,
            "a single-eligible deadlock WITH context degrades — it never latches terminal AssemblyBlocked");
        _handoff.Calls.Should().BeEmpty(
            "the degrade is a same-author reset-to-pending re-dispatch, NOT a cross-agent handoff");
        var degrade = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorSteeringDecision)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .FirstOrDefault(d => d["decision"]?.GetValue<string>() == SteeringDirection.DispatchFresh
                && d["rationale"] != null
                && d["rationale"]!.GetValue<string>().Contains("single_eligible_agent"));
        degrade.Should().NotBeNull("the degrade is a VISIBLE conscious dispatch_fresh with a single_eligible_agent rationale");

        // The plan returned to dispatching for the same-author re-drive — it did NOT park at human review.
        var (_, _, status, _) = await GetPlanSteeringStateAsync(workPlanId);
        status.Should().Be(WorkPlanStatus.Dispatching,
            "the degraded directive re-dispatches; it does NOT dead-end to human review");

        // INVARIANT (still holds): target B is NEVER silently dropped. It is reset to pending under its
        // SAME author (no lockout) so the sole eligible agent revises it with full context.
        var (assignedB, lockedOutB, _, recoveryGuidanceB) = await GetSubtaskFieldsAsync(targetB);
        assignedB.Should().Be("morpheus", "target B keeps its sole eligible author (same-author fresh re-dispatch)");
        lockedOutB.Should().BeNull("target B's sole eligible author is NOT locked out (would re-deadlock)");
        recoveryGuidanceB.Should().NotBeNullOrEmpty("target B is re-dispatched WITH the accumulated feedback (not dropped)");
        (await GetSubtaskStatusAsync(targetB)).Should().Be(SubtaskStatus.Pending,
            "target B is reset to pending for its same-author re-dispatch — never silently applied/dropped");

        // Target A (already rotated) is also swept into the same-author fresh re-dispatch, keeping its
        // already-rotated author — never double-rotated, never dropped.
        var (assignedA, _, _, _) = await GetSubtaskFieldsAsync(targetA);
        assignedA.Should().Be("rotated-morpheus", "target A keeps its already-rotated author (never double-rotated)");

        // The directive is settled (applied) with the persisted DispatchFresh effect — no re-drive loop.
        var directive = await GetDirectiveAsync(directiveId);
        directive!.DecidedAction.Should().Be(SteeringDirection.DispatchFresh, "the persisted decision matches the real effect");
        directive.Status.Should().Be(SteeringStatus.Applied, "the degrade settles the directive after the effect completes");
    }

    // ── NO WHOLE-ROSTER LOCKOUT (rubber-duck must-preserve): a single-target rejection rotates ONLY that
    //    subtask's author — sibling subtasks are untouched. ─────────────────────────────────────────────
    [Fact]
    public async Task RouteAssembly_Rejection_RotatesOnlyTargetSubtask_NeverWholeRoster()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // Two subtasks; ONLY the first is a rejection target (no ChildRunId → unresumable → DispatchFresh).
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady });

        var touched = new Dictionary<int, IReadOnlySet<string>>
        {
            [subtaskIds[0]] = new HashSet<string>(),
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await InvokeRouteAssemblyGateThroughSteeringAsync(
            Context(coordinatorRunId), workPlanId, SteeringSource.Rubberduck,
            "Only the first artifact is broken.", touched, "tree-target-only", cts.Token);

        var (assigned0, lockedOut0, _, _) = await GetSubtaskFieldsAsync(subtaskIds[0]);
        var (assigned1, lockedOut1, _, _) = await GetSubtaskFieldsAsync(subtaskIds[1]);
        assigned0.Should().Be("rotated-morpheus", "the TARGET subtask's author is rotated");
        lockedOut0!.Should().Contain("morpheus", "the target's rejected author is locked out");
        assigned1.Should().Be("morpheus", "a NON-target sibling's author is NOT rotated (never whole-roster)");
        lockedOut1.Should().BeNull("a NON-target sibling's lockout roster is NOT mutated");
    }

    [Fact]
    public async Task TryRotateSubtaskAuthor_ConcurrentReplicas_ExactlyOneWins_NoDoubleAppend()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (_, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.AssembleReady });
        var subtaskId = subtaskIds[0];

        // Two replicas race to rotate the SAME rejected author. The guarded CAS (WHERE AssignedAgent ==
        // expectedAuthor) must let exactly ONE win; the loser no-ops (Won=false) and does not double-append.
        var t1 = _assemblyStore.TryRotateSubtaskAuthorAsync(subtaskId, "morpheus", "neo", "model-neo", null, default);
        var t2 = _assemblyStore.TryRotateSubtaskAuthorAsync(subtaskId, "morpheus", "trinity", "model-trin", null, default);
        var results = await Task.WhenAll(t1, t2);

        results.Count(r => r.Won).Should().Be(1, "exactly one replica wins the guarded CAS rotation");
        results.Count(r => !r.Won).Should().Be(1, "the losing replica no-ops (already rotated by a peer)");

        var (assigned, lockedOut, _, _) = await GetSubtaskFieldsAsync(subtaskId);
        new[] { "neo", "trinity" }.Should().Contain(assigned, "the winner's author is persisted");
        var locked = System.Text.Json.JsonSerializer.Deserialize<List<string>>(lockedOut ?? "[]")!;
        locked.Count(a => string.Equals(a, "morpheus", StringComparison.OrdinalIgnoreCase))
            .Should().Be(1, "the rejected author is appended exactly once, never duplicated across replicas");
    }

    [Fact]
    public async Task RunAssembly_BlockedSend_AcknowledgesDirectiveWithoutRetryingAssembly()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);

        var send = await _steering.SteerAsync(
            coordinatorRunId, "send", null, "Retry assembly with the updated context.", "alice", default);
        send.Status.Should().Be(SteeringStatus.Queued);

        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorRecovered, cts.Token);
        (await GetDirectiveAsync(send.Id))!.Status.Should().Be(SteeringStatus.Applied);

        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", default);
        await run;

        EventTypes_(coordinatorRunId).Count(t => t == EventTypes.CoordinatorAssemblyBlocked)
            .Should().Be(1, "send is not a durable state change and must not retry blocked assembly");
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorSteering);
    }

    [Fact]
    public async Task RunAssembly_QueuedSendBeforeBlockedWait_AcknowledgesDirectiveWithoutRetryingAssembly()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        var send = await _steering.SteerAsync(
            coordinatorRunId, "send", null, "Retry as soon as assembly blocks.", "alice", default);
        send.Status.Should().Be(SteeringStatus.Queued);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);

        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorRecovered, cts.Token);
        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", default);
        await run;

        EventTypes_(coordinatorRunId).Count(t => t == EventTypes.CoordinatorAssemblyBlocked).Should().Be(1,
            "a queued send must be claimed by assembly_blocked ownership but must not re-enter without state change");
        (await GetDirectiveAsync(send.Id))!.Status.Should().Be(SteeringStatus.Applied);
    }

    [Fact]
    public async Task RunAssembly_AssemblyBlockedThenAllChildrenReady_ClearsBlockAndContinuesWithoutSteering()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var childRunId = RunId.New();
        await SeedChildRunAsync(childRunId, "child/recovered", DiffTouching("src/recovered.cs"));
        var (workPlanId, subtaskIds) = await SeedPlanAsync(
            coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.Failed },
            new[] { null, childRunId.ToString() });
        _streamStore.Create(coordinatorRunId, "alice");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);

        await SetSubtaskStatusAsync(subtaskIds[1], SubtaskStatus.AssembleReady);

        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Complete);
        _pipeline.IntegrationBuilds.Should().Be(1);
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorRecovered);
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyCompleted);
    }

    [Fact]
    public async Task RunAssembly_RetriesTransientIntegrationBuildFailures_AndContinues()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.IntegrationBuildThrowsRemaining = 2;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);

        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        _pipeline.IntegrationBuilds.Should().Be(3);
        _pipeline.IntegrationRetryPreparations.Should().Be(2);
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyCompleted);
    }

    [Fact]
    public async Task RunAssembly_PersistentIntegrationBuildError_BlocksOnce_AndDoesNotAutoRetrySameEligibleChildren()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.IntegrationBuildThrowsRemaining = 3;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);

        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);
        await Task.Delay(TimeSpan.FromSeconds(4), cts.Token);

        _pipeline.IntegrationBuilds.Should().Be(3,
            "a persistent integration_build_error must park for steering instead of immediately reusing the same eligible children");
        EventTypes_(coordinatorRunId).Count(t => t == EventTypes.CoordinatorAssemblyBlocked)
            .Should().Be(1, "no state changed while blocked, so assembly must not storm duplicate block events");
        EventTypes_(coordinatorRunId).Count(t => t == EventTypes.CoordinatorRecovered)
            .Should().Be(0, "integration_build_error is not recovered without a state-changing directive or eligibility change");
        EventTypes_(coordinatorRunId).Count(t => t == EventTypes.CoordinatorGraph)
            .Should().Be(2, "only the assembly-start and assembly-blocked snapshots are emitted while parked");
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyRaiStarted);
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyReviewRequested);

        var graph = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorGraph)
            .Select(e => (GraphDescriptor)e.Payload)
            .Last();
        NodeKind(graph, CoordinatorGraphDescriptor.AssemblyRaiNodeId).Should().Be("planned");
        NodeKind(graph, CoordinatorGraphDescriptor.AssemblyReviewNodeId).Should().Be("planned");
        NodeKind(graph, CoordinatorGraphDescriptor.AssemblyMergeNodeId).Should().Be("planned");
        NodeKind(graph, CoordinatorGraphDescriptor.AssemblyScribeNodeId).Should().Be("planned");

        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", default);
        await run;
    }

    [Fact]
    public async Task RunAssembly_BlockedRedirect_ReEntersDispatch()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);

        var redirect = await _steering.SteerAsync(
            coordinatorRunId, "redirect", null, "Re-run the failed subtask against the latest base.", "alice", default);
        redirect.Status.Should().Be(SteeringStatus.Applied);
        await run;

        _dispatch.StartDispatchCalls.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coordinatorRunId);
        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Dispatching);
    }

    [Fact]
    public async Task PersistAssemblyReviewDecision_WritesLatestDecisionToDurableReviewState()
    {
        const string coordinatorRunId = "coord-deferred-duplicate";
        var decision = new AssemblyReviewDecision(
            Approved: true,
            RequestChanges: false,
            Feedback: null,
            TargetFiles: null,
            Reviewer: "alice");

        await InvokePersistAssemblyReviewDecisionAsync(coordinatorRunId, decision);
        await InvokePersistAssemblyReviewDecisionAsync(coordinatorRunId, decision with
        {
            Approved = false,
            Feedback = "duplicate decline",
        });

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await db.AssemblyReviews.AsNoTracking()
            .Where(d => d.CoordinatorRunId == coordinatorRunId)
            .ToListAsync();
        rows.Should().ContainSingle();
        rows[0].DecisionJson.Should().Contain("\"Approved\":false");
        rows[0].DecisionJson.Should().Contain("duplicate decline");
    }

    // ── Happy path: event sequence + node-flip ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAssembly_ReviewGate_KeepsCoordinatorAwaitingReviewUntilDecisionArrives()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);

        run.IsCompleted.Should().BeFalse("the coordinator must stay active while the collective review gate is open");
        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.AwaitingReview);
        _streamStore.Get(coordinatorRunId)!.IsAwaitingReview.Should().BeTrue();

        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.Completed);
    }

    // ── #226: a human /steer at the LIVE assembly review gate must DRAIN (not queue into the void) ──

    /// <summary>
    /// #226 end-to-end drain proof: while the assembly loop is parked at the LIVE human-review gate, a
    /// human <c>/steer redirect</c> must be DELIVERED into that gate (not persisted as a <c>queued</c>
    /// directive that nothing drains). The parked loop then wakes and routes it through
    /// <c>RouteAssemblyGateThroughSteeringAsync</c> as request-changes with the cap-drop unconditional
    /// human budget reset. Without the fix the redirect fell to <c>QueueNextBoundaryAsync</c> and drained
    /// into the void (run d8ab6b1c).
    /// </summary>
    [Fact]
    public async Task Steer_Redirect_AtLiveAssemblyReviewGate_DeliversRequestChanges_ResetsBudget_NotQueued()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        // No resumable child runs → after the budget reset the decider steers via CONSCIOUS dispatch_fresh,
        // so the gate loop stops and RunAssemblyAsync returns deterministically.
        var (workPlanId, _) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        var steering = NewSteeringWithReviewGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitUntilArmedAsync(coordinatorRunId);
        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.AwaitingReview);

        // Exhaust the autonomous budget so the human reset is observable (6 → reset 0 → one steer → 1).
        await SetPlanSteeringStateAsync(workPlanId, steeringIterations: 6, humanReviewRoundTrips: 0);

        var view = await steering.SteerAsync(
            coordinatorRunId, "redirect", null, "Please fix the signup validation.", "alice", ct: cts.Token);

        view.Kind.Should().Be("redirect");
        view.Status.Should().Be(SteeringStatus.Relayed,
            "the redirect was DELIVERED into the armed review gate on this pod, not queued into the void");

        await run; // the parked loop consumed the decision and routed request-changes to completion.

        var (steeringIterations, roundTrips, _, _) = await GetPlanSteeringStateAsync(workPlanId);
        roundTrips.Should().Be(1, "a human request-changes at the review gate is persisted as telemetry");
        steeringIterations.Should().Be(1,
            "the human redirect ALWAYS resets the exhausted autonomous budget (6→0); the single conscious steer re-incremented to 1");

        // Q5/N2: the delivered redirect must NEVER be left as a queued directive that nothing drains.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.SteeringDirectives.CountAsync(d =>
                d.CoordinatorRunId == coordinatorRunId && d.Status == SteeringStatus.Queued))
            .Should().Be(0, "a redirect delivered to the review gate must not persist a queued directive");
    }

    /// <summary>
    /// #226 N1: <c>amend</c> at the live review gate maps to request-changes exactly like <c>redirect</c>
    /// (the decider prefers in-place when resumable, else dispatch-fresh — not force-pinned). It must
    /// likewise DRAIN through the gate with the unconditional human budget reset, never queue into the void.
    /// </summary>
    [Fact]
    public async Task Steer_Amend_AtLiveAssemblyReviewGate_DeliversRequestChanges_ResetsBudget_NotQueued()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(
            coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        var steering = NewSteeringWithReviewGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitUntilArmedAsync(coordinatorRunId);
        await SetPlanSteeringStateAsync(workPlanId, steeringIterations: 6, humanReviewRoundTrips: 0);

        var view = await steering.SteerAsync(
            coordinatorRunId, "amend", null, "Also cover the empty-email edge case.", "alice", ct: cts.Token);

        view.Kind.Should().Be("amend");
        view.Status.Should().Be(SteeringStatus.Relayed);

        await run;

        var (steeringIterations, roundTrips, _, _) = await GetPlanSteeringStateAsync(workPlanId);
        roundTrips.Should().Be(1);
        steeringIterations.Should().Be(1, "amend at the review gate also resets the autonomous budget unconditionally");

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.SteeringDirectives.CountAsync(d =>
                d.CoordinatorRunId == coordinatorRunId && d.Status == SteeringStatus.Queued))
            .Should().Be(0);
    }

    /// <summary>
    /// #226 Q4/N3: a <c>send</c> at the live review gate carries no change request, so it is delivered as
    /// an ADVISORY note (directive settles <c>applied</c>) — NOT left <c>queued</c> forever and NOT turned
    /// into a review decision. The gate stays armed and the coordinator remains awaiting_review.
    /// </summary>
    [Fact]
    public async Task Steer_Send_AtLiveAssemblyReviewGate_DeliveredAsAdvisory_GateStaysArmed()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        var steering = NewSteeringWithReviewGate();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitUntilArmedAsync(coordinatorRunId);

        var view = await steering.SteerAsync(
            coordinatorRunId, "send", null, "Please explain the assembly risk before I approve.", "alice", ct: cts.Token);

        view.Kind.Should().Be("send");
        view.Status.Should().Be(SteeringStatus.Applied,
            "a send at the review gate is an advisory note, not a queued directive and not a review decision");

        // The advisory send does NOT resolve the gate: the loop is still parked awaiting the human decision.
        run.IsCompleted.Should().BeFalse("an advisory send must not wake or resolve the review gate");
        _reviewGate.IsArmed(coordinatorRunId).Should().BeTrue("the gate stays armed after an advisory send");

        using (var scope = _provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.SteeringDirectives.CountAsync(d =>
                    d.CoordinatorRunId == coordinatorRunId && d.Status == SteeringStatus.Queued))
                .Should().Be(0, "an advisory send must not persist a queued directive");
        }

        // Clean up the still-parked loop so the test disposes deterministically.
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;
    }

    /// <summary>
    /// Builds a <see cref="CoordinatorSteeringService"/> wired WITH the shared <see cref="_reviewGate"/>
    /// so the #226 AwaitingReview interception is active (the class-level <c>_steering</c> is intentionally
    /// constructed without it to preserve the pre-#226 unit-test behavior).
    /// </summary>
    private CoordinatorSteeringService NewSteeringWithReviewGate() =>
        new(
            _streamStore,
            new RunWorkflowRegistry(),
            _scopeFactory,
            NullLogger<CoordinatorSteeringService>.Instance,
            waitRegistry: _steeringWaits,
            runStore: _runStore,
            reviewGate: _reviewGate);

    [Fact]
    public async Task RunAssembly_ApprovedReview_EmitsAssemblySequenceInOrder_AndFlipsNodesToLive()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);

        // The pipeline arms the review gate when it reaches the review stage; approve it.
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);

        await run;

        // The assembly_* events were emitted in the documented order with monotonically increasing seq.
        var assemblyEvents = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type.StartsWith("coordinator.assembly_", StringComparison.Ordinal))
            .ToList();
        assemblyEvents.Select(e => e.Type).Should().ContainInOrder(
            EventTypes.CoordinatorAssemblyStarted,
            EventTypes.CoordinatorAssemblyRaiStarted,
            EventTypes.CoordinatorAssemblyRaiCompleted,
            EventTypes.CoordinatorAssemblyReviewRequested,
            EventTypes.CoordinatorAssemblyReviewApproved,
            EventTypes.CoordinatorAssemblyMergeStarted,
            EventTypes.CoordinatorAssemblyMergeCompleted,
            EventTypes.CoordinatorAssemblyScribeStarted,
            EventTypes.CoordinatorAssemblyScribeCompleted,
            EventTypes.CoordinatorAssemblyCompleted);
        assemblyEvents.Select(e => e.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        var approvedPayload = JsonSerializer.SerializeToNode(
            assemblyEvents.Single(e => e.Type == EventTypes.CoordinatorAssemblyReviewApproved).Payload)!.AsObject();
        approvedPayload["reviewer"]!.GetValue<string>().Should().Be("alice");

        var topologyEvents = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorTopology)
            .ToList();
        topologyEvents.Should().NotBeEmpty();
        topologyEvents.Select(e => e.Sequence).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        foreach (var evt in topologyEvents)
        {
            var payload = JsonSerializer.SerializeToNode(evt.Payload)!.AsObject();
            payload["seq"]!.GetValue<int>().Should().Be(evt.Sequence);
        }

        // The pipeline ran exactly one of each collective stage.
        _pipeline.IntegrationBuilds.Should().Be(1);
        _pipeline.Merges.Should().Be(1);
        _pipeline.Scribes.Should().Be(1);
        (await _runStore.GetRunsByParentAsync(coordinatorRunId))
            .Should().ContainSingle(r => r.AgentName == "Scribe" && r.SubtaskId == "assembly-scribe");

        // Node-flip: the FIRST coordinator.graph (stage=null) renders assembly nodes planned; the LAST
        // (stage=done) renders them all live — proving the planned→live transition.
        var graphs = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorGraph)
            .Select(e => (GraphDescriptor)e.Payload)
            .ToList();
        graphs.Should().NotBeEmpty();
        NodeKind(graphs.First(), CoordinatorGraphDescriptor.AssemblyRaiNodeId).Should().Be("planned");
        NodeKind(graphs.Last(), CoordinatorGraphDescriptor.AssemblyRaiNodeId).Should().Be("live");
        NodeKind(graphs.Last(), CoordinatorGraphDescriptor.AssemblyReviewNodeId).Should().Be("live");
        NodeKind(graphs.Last(), CoordinatorGraphDescriptor.AssemblyMergeNodeId).Should().Be("live");
        NodeKind(graphs.Last(), CoordinatorGraphDescriptor.AssemblyScribeNodeId).Should().Be("live");

        var state = await _assemblyStore.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.Complete);
        state.AssemblyStage.Should().Be(AssemblyStage.Done);
    }

    // #236: when the assembly gate runs with a NON-EMPTY integration diff, the coordinator must
    // provision exactly ONE detached reviewer worktree (at the integration branch) and thread its path
    // into the reviewer requests, so RAI + rubber-duck can read the assembled integration files
    // host-side instead of only the aggregate diff text. (The default gate set here is [rai,
    // human-review]; the same reviewerWorktreePath local feeds the rubber-duck request too.)
    [Fact]
    public async Task RunAssembly_WithChanges_PreparesReviewerWorktreeOnce_AndPropagatesPathToReviewers()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (_, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // Default FakePipeline integration result has a non-empty diff ⇒ HasChanges == true.
        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        _pipeline.ReviewerWorktreePreparations.Should().Be(1,
            "a single detached reviewer worktree must be provisioned for a non-empty assembly");
        _pipeline.LastReviewerWorktreeIntegrationBranch.Should().NotBeNullOrEmpty(
            "the reviewer worktree must check out the assembled integration branch");

        var expectedPath = _pipeline.LastReviewerWorktreePath;
        expectedPath.Should().NotBeNullOrEmpty();
        _pipeline.LastRaiRequest.Should().NotBeNull();
        _pipeline.LastRaiRequest!.WorktreePath.Should().Be(expectedPath,
            "the RAI reviewer request must carry the checked-out worktree path, not an empty string");
    }

    // #236: an EMPTY-diff assembly early-returns approved in the reviewers, so no worktree is needed —
    // the coordinator must NOT provision one, and the reviewer requests carry an empty WorktreePath.
    [Fact]
    public async Task RunAssembly_EmptyDiff_DoesNotPrepareReviewerWorktree_AndReviewerWorktreePathIsEmpty()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (_, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        // Empty aggregate diff ⇒ IntegrationBranchResult.HasChanges == false.
        _pipeline.IntegrationResult = IntegrationBranchResult.Success(
            "agentweaver/integration/coord-empty", treeHash: string.Empty, diff: string.Empty);

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        _pipeline.ReviewerWorktreePreparations.Should().Be(0,
            "an empty-diff assembly must not provision a reviewer worktree");
        _pipeline.LastRaiRequest.Should().NotBeNull();
        _pipeline.LastRaiRequest!.WorktreePath.Should().BeEmpty(
            "with no changes the RAI reviewer request carries no worktree (diff-text-only)");
    }

    [Fact]
    public async Task RunAssembly_PreviewRequiredWithoutStartPreview_EmitsFailureBeforeBuildTestApproval()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        await InvokeEnsurePreviewApplicabilityRecordedAsync(coordinatorRunId, workPlanId, "agg-tree", "aggregate diff");
        await InvokeEnsureFinalPreviewOutcomeBeforeApprovalAsync(coordinatorRunId, workPlanId, "agg-tree");
        await InvokeApplyAuthoredGateDecisionAsync(
            Context(coordinatorRunId),
            workPlanId,
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "build-test"));

        var events = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events;
        var failed = events.Single(e => e.Type == EventTypes.SandboxPreviewFailed);
        var failedPayload = JsonSerializer.SerializeToNode(failed.Payload)!.AsObject();
        failedPayload["work_plan_id"]!.GetValue<int>().Should().Be(workPlanId);
        failedPayload["tree_hash"]!.GetValue<string>().Should().Be("agg-tree");
        failedPayload["reason"]!.GetValue<string>().Should().Be("preview_outcome_missing");

        events.Select(e => e.Type).Should().ContainInOrder(
            EventTypes.SandboxPreviewFailed,
            EventTypes.CoordinatorAssemblyReviewApproved);
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyReviewApproved,
            "missing preview is surfaced but does not block Human Review or approval");
    }

    [Fact]
    public async Task RunAssembly_ExistingPreviewReady_DoesNotEmitMissingOutcomeFailure()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.OnBuildTest = request =>
        {
            _streamStore.Get(coordinatorRunId)!.RecordNext(EventTypes.SandboxPreviewReady, new
            {
                run_id = coordinatorRunId,
                work_plan_id = workPlanId,
                tree_hash = request.AggregateTreeHash,
                preview_url = "https://preview.example.test",
                target_port = 5173,
            });
            _streamStore.Get(coordinatorRunId)!.RecordNext(EventTypes.CoordinatorPreviewReady, new
            {
                run_id = coordinatorRunId,
                work_plan_id = workPlanId,
                tree_hash = request.AggregateTreeHash,
                preview_url = "https://preview.example.test",
                target_port = 5173,
            });
        };

        await InvokeEnsurePreviewApplicabilityRecordedAsync(coordinatorRunId, workPlanId, "agg-tree", "aggregate diff");
        _pipeline.OnBuildTest!(new CollectiveBuildTestRequest(
            coordinatorRunId,
            "proj-1",
            ".",
            "integration",
            "agg-tree",
            "aggregate diff",
            "alice"));
        await InvokeEnsureFinalPreviewOutcomeBeforeApprovalAsync(coordinatorRunId, workPlanId, "agg-tree");

        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.SandboxPreviewReady);
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorPreviewReady);
        _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.SandboxPreviewFailed)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject()["reason"]!.GetValue<string>())
            .Should().NotContain("preview_outcome_missing");
    }

    [Fact]
    public async Task RunAssembly_PreviewOnlyFailureFeedback_DoesNotResetOrRedispatchSubtasks()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.BuildTestDecision = new CollectiveGateDecision(
            Approved: false,
            RequestChanges: true,
            Feedback: "Preview unavailable; start_preview did not return a URL.");

        await InvokeEnsurePreviewApplicabilityRecordedAsync(coordinatorRunId, workPlanId, "agg-tree", "aggregate diff");
        await InvokeEnsureFinalPreviewOutcomeBeforeApprovalAsync(coordinatorRunId, workPlanId, "agg-tree");
        await InvokeApplyAuthoredGateDecisionAsync(
            Context(coordinatorRunId),
            workPlanId,
            new AssemblyReviewDecision(
                Approved: true,
                RequestChanges: false,
                Feedback: _pipeline.BuildTestDecision!.Feedback,
                TargetFiles: null,
                Reviewer: "build-test"));

        _dispatch.StartDispatchCalls.Should().BeEmpty("preview failure must not use the reset and redispatch route");
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyReviewApproved);
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyChangesRequested);
    }

    [Fact]
    public async Task PreviewGuard_StalePendingFromPriorTree_DoesNotDelayLaterAssemblyPass()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        var stream = _streamStore.Get(coordinatorRunId)!;
        stream.RecordNext(EventTypes.SandboxPreviewApplicability, new
        {
            run_id = coordinatorRunId,
            work_plan_id = workPlanId,
            tree_hash = "tree-1",
            state = "preview_required",
        });
        stream.RecordNext(EventTypes.SandboxPreviewPending, new
        {
            run_id = coordinatorRunId,
            target_port = 5173,
            approval = "pending",
            request_id = "stale-null-keyed",
        });
        stream.RecordNext(EventTypes.SandboxPreviewPending, new
        {
            run_id = coordinatorRunId,
            work_plan_id = workPlanId,
            tree_hash = "tree-1",
            target_port = 5173,
            approval = "pending",
            request_id = "stale-old-tree",
        });
        stream.RecordNext(EventTypes.SandboxPreviewFailed, new
        {
            run_id = coordinatorRunId,
            work_plan_id = workPlanId,
            tree_hash = "tree-1",
            source = "preview-api",
            reason = "approval_timed_out",
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var started = DateTimeOffset.UtcNow;
        await InvokeEnsurePreviewApplicabilityRecordedAsync(
            coordinatorRunId, workPlanId, "tree-2", "diff --git a/src/server.ts b/src/server.ts", cts.Token);
        await InvokeEnsureFinalPreviewOutcomeBeforeApprovalAsync(
            coordinatorRunId, workPlanId, "tree-2", cts.Token);
        var elapsed = DateTimeOffset.UtcNow - started;

        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "stale pending events from another tree must not trigger the HITL wait window");

        var events = stream.GetSnapshotSince(0).Events;
        events.Where(e => e.Type == EventTypes.SandboxPreviewApplicability)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .Should().Contain(p => p["tree_hash"]!.GetValue<string>() == "tree-2",
                "the second pass must record its own applicability");
        var failures = events.Where(e => e.Type == EventTypes.SandboxPreviewFailed)
            .Select(e => JsonSerializer.SerializeToNode(e.Payload)!.AsObject())
            .ToList();
        failures.Should().Contain(p =>
            p["tree_hash"]!.GetValue<string>() == "tree-2"
            && p["reason"]!.GetValue<string>() == "preview_outcome_missing");
    }

    [Fact]
    public async Task RunAssembly_AutoResolvedIntegrationConflict_EmitsCoordinatorEvent()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.IntegrationResult = IntegrationBranchResult.Success(
            CoordinatorAssemblyService.IntegrationBranchName(coordinatorRunId),
            "agg-tree",
            "aggregate diff",
            [("agentweaver/child-b", new[] { "shared.txt", "docs\\note.md" })]);

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);

        await run;

        var evt = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.CoordinatorIntegrationConflictAutoResolved);
        var payload = JsonSerializer.SerializeToNode(evt.Payload)!.AsObject();
        payload["workPlanId"]!.GetValue<int>().Should().Be(workPlanId);
        payload["conflictingBranch"]!.GetValue<string>().Should().Be("agentweaver/child-b");
        payload["strategy"]!.GetValue<string>().Should().Be("accept_child");
        payload["conflictingFiles"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().ContainInOrder("shared.txt", "docs\\note.md");
    }

    [Fact]
    public async Task RunAssembly_DeferredReviewDecisionFromAnotherReplica_IsConsumedAndApplied()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);

        await SeedDeferredAssemblyDecisionAsync(coordinatorRunId,
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"));

        await run;

        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyReviewApproved,
            "the owner replica should poll and apply the deferred review decision");
        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Complete);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.AssemblyReviews.CountAsync(d => d.CoordinatorRunId == coordinatorRunId)).Should().Be(0,
            "the persisted review state is cleared after merge ownership is durably advanced");
    }

    [Fact]
    public async Task RunAssembly_RecoveredInReview_WithPersistedApproval_AdvancesDirectlyToMerge()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SetPlanReviewStateAsync(workPlanId);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice", "agentweaver/integration/recover", "agg-tree", CancellationToken.None);
        await SeedDeferredAssemblyDecisionAsync(coordinatorRunId,
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null, TargetFiles: null, Reviewer: "alice"));
        _streamStore.Create(coordinatorRunId, "alice");

        await _sut.RunAssemblyAsync(Context(coordinatorRunId), default);

        _pipeline.IntegrationBuilds.Should().Be(0, "recovery should not rebuild assembly after approval was already persisted");
        _pipeline.Merges.Should().Be(1);
        _pipeline.Scribes.Should().Be(1);
        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Complete);
    }

    [Fact]
    public async Task RunAssembly_RecoveredInReview_WithoutPersistedApproval_ReArmsGateWithoutRebuilding()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SetPlanReviewStateAsync(workPlanId);
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice", "agentweaver/integration/recover", "agg-tree", CancellationToken.None);
        _streamStore.Create(coordinatorRunId, "alice");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _pipeline.IntegrationBuilds.Should().Be(0, "recovery should re-arm the review gate from persisted state");
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null, TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        _pipeline.Merges.Should().Be(1);
        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Complete);
    }

    // ── request_changes deterministic (explicit-target) re-dispatch (rev8: no prose inference) ─────

    // rev8 (unified autonomous steering): the OLD assembly-gate REQUEST_CHANGES reflex — auto
    // reset-to-pending + auto re-dispatch driven directly by the gate — has been REMOVED. Gates no
    // longer force a reset+dispatch; ALL correction feedback (human-review, build-test, RAI,
    // rubberduck) now normalizes into a SteeringSignal and routes to the coordinator, which
    // CONSCIOUSLY decides A (in-place resume, context preserved) / B (logged fresh dispatch) / C /
    // D. That coordinator-owned routing + decision transaction + two-phase effect proof is covered
    // end-to-end in UnifiedSteeringTests (real decider + stubs); the in-place resume executor path
    // requires a live RunOrchestrator and is exercised there, not in this orchestration harness.

    [Fact]
    public async Task RunBuildTestAsync_BareLaunchInvalidOperation_MapsToRetryableInfrastructureFailure()
    {
        var repoPath = CreateGitRepository();
        var worktreesBase = Path.Combine(Path.GetTempPath(), $"agentweaver-buildtest-wt-{Guid.NewGuid():N}");

        try
        {
            var worktreeManager = new WorktreeManager(
                new ConfigurationBuilder()
                    .AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Worktrees:BasePath"] = worktreesBase,
                    })
                    .Build(),
                NullLogger<WorktreeManager>.Instance);
            var pipeline = new CollectiveAssemblyPipeline(
                worktreeManager,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                null!,
                NullLoggerFactory.Instance,
                new ThrowingLaunchPodLifecycle(new InvalidOperationException("AgentHost pod did not become ready within 90s.")),
                Options.Create(new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" }));

            var act = () => pipeline.RunBuildTestAsync(
                new CollectiveBuildTestRequest(
                    RunId.New().ToString(),
                    ProjectId: null,
                    repoPath,
                    "main",
                    "tree",
                    "diff",
                    "alice"),
                CancellationToken.None);

            var ex = await act.Should().ThrowAsync<CollectiveBuildTestInfrastructureException>();
            ex.Which.Reason.Should().Be("agenthost_launch_failed");
            ex.Which.Retryable.Should().BeTrue();
        }
        finally
        {
            TryDeleteDirectory(repoPath);
            TryDeleteDirectory(worktreesBase);
        }
    }

    [Fact]
    public async Task BuildTestRetryableInfrastructureFailure_ParksAssemblyBlocked_NotPermanentFailed()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        await InvokeParkBuildTestInfrastructureFailureAsync(
            Context(coordinatorRunId),
            workPlanId,
            new CollectiveBuildTestInfrastructureException(
                "agenthost_launch_failed",
                "AgentHost pod did not become ready within 90s.",
                retryable: true));

        var state = await _assemblyStore.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.AssemblyBlocked);
        state.AssemblyStatusReason.Should().Be("build_test_infra_agenthost_launch_failed");
        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyBlocked);
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyFailed);
        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status
            .Should().Be(RunStatus.InProgress);
    }

    [Fact]
    public async Task BuildTestInfrastructureFailure_PersistsAssemblyEvent_WithInnerExceptionDetail()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");
        _streamStore.Complete(coordinatorRunId);

        var inner = new InvalidOperationException("/configure returned HTTP 500 for workdir");
        var ex = new CollectiveBuildTestInfrastructureException(
            "agenthost_launch_failed",
            "AgentHost pod launch failed for Build & Test: /configure returned HTTP 500 for workdir",
            retryable: false,
            inner);

        await InvokeParkBuildTestInfrastructureFailureAsync(Context(coordinatorRunId), workPlanId, ex);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var persisted = await db.RunEvents
            .Where(e => e.RunId == coordinatorRunId && e.EventType == EventTypes.CoordinatorAssemblyFailed)
            .OrderBy(e => e.Sequence)
            .SingleAsync();
        using var doc = JsonDocument.Parse(persisted.PayloadJson);
        doc.RootElement.GetProperty("reason").GetString().Should().Be("build_test_infra_agenthost_launch_failed");
        doc.RootElement.GetProperty("detail").GetString().Should().Contain("/configure returned HTTP 500");
        doc.RootElement.GetProperty("innerExceptionMessage").GetString().Should().Be(inner.Message);
        doc.RootElement.GetProperty("infrastructureReason").GetString().Should().Be("agenthost_launch_failed");
    }

    [Fact]
    public async Task AutomatedGateRequestChanges_RetainsBuildTestResources_ForNextAssemblyPass()
    {
        var coordinatorRunId = RunId.New().ToString();
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedCoordinatorRunAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        await InvokeRequestChangesAsync(
            Context(coordinatorRunId),
            workPlanId,
            new AssemblyReviewDecision(
                Approved: false,
                RequestChanges: true,
                Feedback: "Please update the generated aggregate.",
                TargetFiles: null,
                Reviewer: "build-test"));

        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Dispatching);
        _dispatch.StartDispatchCalls.Should().ContainSingle();
        _pipeline.CleanupBuildTestResourcesCalls.Should().Be(0,
            "automated Build/Test request-changes should reuse the coordinator pod and detached worktree on the next assembly pass");
    }

    // ── Terminal coordinator-run status + reason (so the UI never shows a bare "Failed") ──────────

    [Fact]
    public async Task RunAssembly_BlockedStop_TerminalizesCoordinatorRun_Failed_WithReason()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);
        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", default);
        await runTask;

        var run = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        run!.Status.Should().Be(RunStatus.Failed);
        run.Result.Should().Be("steering_stop");
    }

    [Fact]
    public async Task RunAssembly_BlockedSend_AcknowledgesWithoutRetryingAssembly()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = _sut.RunAssemblyAsync(Context(coordinatorRunId), cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorAssemblyBlocked, cts.Token);

        await _steering.SteerAsync(coordinatorRunId, "send", null, "please retry", "alice", ct: cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorRecovered, cts.Token);

        var events = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events;
        events.Count(e => e.Type == EventTypes.CoordinatorAssemblyBlocked).Should().Be(1,
            "a send message is not a state change and must not re-enter the blocked assembly path");
        _pipeline.IntegrationBuilds.Should().Be(0);

        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", ct: cts.Token);
        await runTask;
    }

    [Fact]
    public async Task RunAssembly_Declined_EmitsDeclinedEvent_AndTerminalizesCoordinatorRun_Declined()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: false, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyDeclined);
        var persisted = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        persisted!.Status.Should().Be(RunStatus.Declined);
        persisted.Result.Should().Be("assembly_declined");
        (await _runStore.GetRunsByParentAsync(coordinatorRunId))
            .Should().ContainSingle(r => r.AgentName == "Scribe" && r.SubtaskId == "assembly-scribe");
    }

    [Fact]
    public async Task RunAssembly_MergeFailed_TerminalizesCoordinatorRun_MergeFailed_WithReason()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.MergeOverride = CollectiveMergeResult.Failed("merge_error");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyMergeFailed);
        var persisted = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        persisted!.Status.Should().Be(RunStatus.MergeFailed);
        persisted.Result.Should().StartWith("assembly_merge_failed:");
        var state = await _assemblyStore.GetAsync(workPlanId, default);
        state!.AssemblyTerminalStage.Should().Be(AssemblyStage.Merge);
        state.AssemblyStatusReason.Should().Be(persisted.Result);
        state.AssemblyStage.Should().Be(AssemblyStage.Scribe,
            "the terminal failure stage must survive even after the failure scribe advances AssemblyStage");
    }

    [Fact]
    public async Task RunAssembly_UnexpectedFault_FailsRunWithReason_AndEmitsAssemblyFailed()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");
        _pipeline.MergeThrows = true;

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyFailed);
        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.AssemblyFailed);
        var persisted = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        persisted!.Status.Should().Be(RunStatus.Failed);
        persisted.Result.Should().StartWith("assembly_error:");
        _streamStore.Get(coordinatorRunId)!.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task FailAssembly_WithOpenReviewGate_PreservesGate_MarksCoordinatorFailed_AndEmitsReviewPreserved()
    {
        // The review gate must OUTLIVE a failed coordinator run: if the run fails while the human
        // review gate is still open (no decision submitted — e.g. the git integration ref-lock race
        // exhausted the reconciler's re-arm cap), the durable review record is PRESERVED and marked
        // coordinator_failed rather than cleared, and a review_preserved event is emitted so the UI
        // keeps the changes visible instead of kicking the operator out.
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SetPlanReviewStateAsync(workPlanId);
        _streamStore.Create(coordinatorRunId, "alice");
        await CoordinatorAssemblyReviewPersistence.UpsertReviewRequestAsync(
            _scopeFactory, coordinatorRunId, "alice",
            "agentweaver/integration/" + coordinatorRunId, "deadbeef", default);

        const string reason = "assembly_rearm_exhausted after 3 attempts";
        await _sut.FailAssemblyAsync(Context(coordinatorRunId), reason, default);

        // The gate is preserved (not deleted) and stamped coordinator_failed.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var record = await db.AssemblyReviews.AsNoTracking()
            .SingleAsync(r => r.CoordinatorRunId == coordinatorRunId);
        record.CoordinatorFailedAt.Should().NotBeNull("an open gate must be preserved, not cleared, on failure");
        record.CoordinatorFailureReason.Should().Be(reason);
        record.DecisionSubmittedAt.Should().BeNull("the human never acted — the gate is still theirs to complete");

        EventTypes_(coordinatorRunId).Should().Contain(EventTypes.CoordinatorAssemblyReviewPreserved);
        (await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default))!.Status.Should().Be(RunStatus.Failed);
    }

    [Fact]
    public async Task FailAssembly_WithNoOpenReviewGate_ClearsRecord_AndDoesNotEmitReviewPreserved()
    {
        // When there is no OPEN gate (the human already decided — DecisionSubmittedAt set), a failure
        // clears the record as before and never emits the preserved event.
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SetPlanReviewStateAsync(workPlanId);
        _streamStore.Create(coordinatorRunId, "alice");
        await CoordinatorAssemblyReviewPersistence.PersistDecisionAsync(
            _scopeFactory, coordinatorRunId,
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"),
            default);

        await _sut.FailAssemblyAsync(Context(coordinatorRunId), "some_failure", default);

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.AssemblyReviews.CountAsync(r => r.CoordinatorRunId == coordinatorRunId))
            .Should().Be(0, "a decided gate is cleared on failure as before");
        EventTypes_(coordinatorRunId).Should().NotContain(EventTypes.CoordinatorAssemblyReviewPreserved);
    }

    [Fact]
    public async Task FailAssembly_PreGateTerminalFailure_FinalScribeGraphKeepsAssemblyGatesPlanned()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        const string reason = "assembly_rearm_exhausted after 3 attempts";
        await _sut.FailAssemblyAsync(Context(coordinatorRunId), reason, default);

        var state = await _assemblyStore.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.AssemblyFailed);
        state.AssemblyStage.Should().Be(AssemblyStage.Scribe,
            "terminal cleanup may run the scribe after the pre-gate failure");
        state.AssemblyTerminalStage.Should().BeNull(
            "the failure happened before RAI/review/merge/scribe started");
        state.AssemblyStatusReason.Should().Be(reason);

        var graphs = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.CoordinatorGraph)
            .Select(e => (GraphDescriptor)e.Payload)
            .ToList();
        graphs.Should().HaveCountGreaterThanOrEqualTo(2,
            "FailAssembly emits a failure graph and the later scribe cleanup emits another graph");
        var finalGraph = graphs.Last();
        var coordinator = finalGraph.Nodes.Single(n => n.Id == CoordinatorGraphDescriptor.CoordinatorNodeId);
        coordinator.Status.Should().Be(WorkPlanStatus.AssemblyFailed);
        coordinator.StatusReason.Should().Be(reason);
        coordinator.TerminalStage.Should().BeNull();

        foreach (var nodeId in new[]
                 {
                     CoordinatorGraphDescriptor.AssemblyRaiNodeId,
                     CoordinatorGraphDescriptor.AssemblyReviewNodeId,
                     CoordinatorGraphDescriptor.AssemblyMergeNodeId,
                     CoordinatorGraphDescriptor.AssemblyScribeNodeId,
                 })
        {
            var node = finalGraph.Nodes.Single(n => n.Id == nodeId);
            node.Kind.Should().Be("planned", $"{node.Label} never ran before terminal cleanup");
            node.Status.Should().BeNull();
            node.StatusReason.Should().BeNull();
            node.TerminalStage.Should().BeNull();
        }

        _pipeline.Scribes.Should().Be(1);
        var persisted = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        persisted!.Status.Should().Be(RunStatus.Failed);
        persisted.Result.Should().Be(reason);
    }

    [Fact]
    public async Task RunAssembly_Approved_TerminalizesCoordinatorRun_Completed_WithReason()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        _streamStore.Create(coordinatorRunId, "alice");

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        var persisted = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        persisted!.Status.Should().Be(RunStatus.Completed);
        persisted.Result.Should().Be("assembly_complete");
    }

    // ── coordinator decision promotion ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAssembly_Approved_Coordinator_PromotesPendingArchitecturalAndScopeDecisions()
    {
        var coordinatorRunId = RunId.New().ToString();
        var projectId = ProjectId.New();
        var projectKey = projectId.Value.ToString();

        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
        await SeedInboxEntryAsync(projectKey, "use-event-sourcing", "architectural", "Adopt event sourcing");
        await SeedInboxEntryAsync(projectKey, "exclude-billing", "scope", "Billing is out of scope");
        await SeedInboxEntryAsync(projectKey, "cache-gotcha", "learning", "Cache invalidation gotcha");
        _streamStore.Create(coordinatorRunId, "alice");

        var context = new CoordinatorDispatchContext(coordinatorRunId, "repo", "main", "alice", projectId);
        var run = _sut.RunAssemblyAsync(context, default);
        await WaitUntilArmedAsync(coordinatorRunId);
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: true, RequestChanges: false, Feedback: null,
                TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);
        await run;

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var decisions = await db.Decisions
            .Where(d => d.ProjectId == projectKey && d.Status == "active")
            .ToListAsync();
        decisions.Select(d => d.Type).Should().BeEquivalentTo(new[] { "architectural", "scope" });

        var arch = await db.DecisionInbox.SingleAsync(e => e.Slug == "use-event-sourcing");
        arch.Status.Should().Be("merged");
        var boundary = await db.DecisionInbox.SingleAsync(e => e.Slug == "exclude-billing");
        boundary.Status.Should().Be("merged");

        // The learning entry is the per-run Scribe's responsibility, not the coordinator backstop.
        var learning = await db.DecisionInbox.SingleAsync(e => e.Slug == "cache-gotcha");
        learning.Status.Should().Be("pending");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private static CoordinatorDispatchContext Context(string coordinatorRunId) =>
        new(coordinatorRunId, "repo", "main", "alice", null);

    private List<string> EventTypes_(string coordinatorRunId) =>
        _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events.Select(e => e.Type).ToList();

    private async Task WaitForEventAsync(string runId, string eventType, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_streamStore.Get(runId)?.GetSnapshotSince(0).Events.Any(e => e.Type == eventType) == true)
                return;

            await Task.Delay(25, ct);
        }
    }

    private async Task WaitForEventCountAsync(string runId, string eventType, int expectedCount, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var count = _streamStore.Get(runId)?.GetSnapshotSince(0).Events.Count(e => e.Type == eventType) ?? 0;
            if (count >= expectedCount)
                return;

            await Task.Delay(25, ct);
        }
    }

    private static string NodeKind(GraphDescriptor graph, string nodeId) =>
        graph.Nodes.Single(n => n.Id == nodeId).Kind;

    private static string DiffTouching(string path) =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -0,0 +1 @@\n+change\n";

    private async Task InvokePersistAssemblyReviewDecisionAsync(
        string coordinatorRunId,
        AssemblyReviewDecision decision)
    {
        // #226 S2 refactor: the endpoint's private PersistAssemblyReviewDecisionAsync local was folded
        // into the shared CoordinatorAssemblyReviewPersistence.DeliverDecisionAsync helper, which delegates
        // durable persistence to PersistDecisionAsync. Assert against that canonical persistence method.
        await CoordinatorAssemblyReviewPersistence.PersistDecisionAsync(
            _scopeFactory, coordinatorRunId, decision, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task InvokeParkBuildTestInfrastructureFailureAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        CollectiveBuildTestInfrastructureException exception)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "ParkBuildTestInfrastructureFailureAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("Build/Test infra failures must park outside the request-changes path");

        var task = (Task)method!.Invoke(_sut,
        [
            context,
            workPlanId,
            Array.Empty<(int, int)>(),
            exception,
            CancellationToken.None,
        ])!;
        await task.ConfigureAwait(false);
    }

    private async Task InvokeRequestChangesAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        AssemblyReviewDecision decision)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "RequestChangesAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("request-changes owns coordinator build/test resource retention");

        var task = (Task)method!.Invoke(_sut,
        [
            context,
            workPlanId,
            Array.Empty<(int, int)>(),
            decision,
            new Dictionary<int, IReadOnlySet<string>>(),
            CancellationToken.None,
        ])!;
        await task.ConfigureAwait(false);
    }

    private async Task InvokeEnsurePreviewApplicabilityRecordedAsync(
        string coordinatorRunId,
        int workPlanId,
        string treeHash,
        string aggregateDiff,
        CancellationToken ct = default)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "EnsurePreviewApplicabilityRecordedAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("the coordinator owns durable preview applicability");

        var task = (Task)method!.Invoke(_sut,
        [
            coordinatorRunId,
            workPlanId,
            treeHash,
            aggregateDiff,
            ct,
        ])!;
        await task.ConfigureAwait(false);
    }

    private async Task InvokeEnsureFinalPreviewOutcomeBeforeApprovalAsync(
        string coordinatorRunId,
        int workPlanId,
        string treeHash,
        CancellationToken ct = default)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "EnsureFinalPreviewOutcomeBeforeApprovalAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("the coordinator guard owns preview outcome enforcement");

        var task = (Task)method!.Invoke(_sut,
        [
            coordinatorRunId,
            workPlanId,
            treeHash,
            ct,
        ])!;
        await task.ConfigureAwait(false);
    }

    private async Task<bool> InvokeApplyAuthoredGateDecisionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        AssemblyReviewDecision decision)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "ApplyAuthoredGateDecisionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("approval application remains the assembly seam after preview guard");

        var task = (Task<bool>)method!.Invoke(_sut,
        [
            context,
            workPlanId,
            Array.Empty<(int, int)>(),
            new Dictionary<int, IReadOnlySet<string>>(),
            decision,
            SteeringSource.BuildTest,
            string.Empty,
            CancellationToken.None,
        ])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<bool> InvokeRouteAssemblyGateThroughSteeringAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        string source,
        string? feedback,
        IReadOnlyDictionary<int, IReadOnlySet<string>> touchedFilesBySubtask,
        string aggregateTreeHash,
        CancellationToken ct,
        IReadOnlyList<string>? targetFiles = null,
        IReadOnlyCollection<(int, int)>? edges = null)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "RouteAssemblyGateThroughSteeringAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("unified steering routes every gate through the coordinator");
        var task = (Task<bool>)method!.Invoke(_sut,
        [
            context,
            workPlanId,
            edges ?? Array.Empty<(int, int)>(),
            source,
            feedback,
            targetFiles,
            touchedFilesBySubtask,
            aggregateTreeHash,
            ct,
        ])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<bool> InvokeParkAtHumanReviewAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        int directiveId,
        string reason,
        string aggregateTreeHash,
        CancellationToken ct)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "ParkAtHumanReviewAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("escalation parks the plan at the human-review gate durably");
        var task = (Task<bool>)method!.Invoke(_sut,
        [
            context,
            workPlanId,
            directiveId,
            reason,
            aggregateTreeHash,
            ct,
        ])!;
        return await task.ConfigureAwait(false);
    }

    private async Task<bool> InvokeDriveOutstandingSteeringExecutionAsync(
        CoordinatorDispatchContext context,
        int workPlanId,
        CancellationToken ct)
    {
        var method = typeof(CoordinatorAssemblyService).GetMethod(
            "DriveOutstandingSteeringExecutionAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        method.Should().NotBeNull("recovery drives outstanding steering directives to completion");
        var task = (Task<bool>)method!.Invoke(_sut,
        [
            context,
            workPlanId,
            Array.Empty<(int, int)>(),
            ct,
        ])!;
        return await task.ConfigureAwait(false);
    }

    private async Task SetPlanSteeringStateAsync(
        int workPlanId, string? status = null, int? steeringIterations = null, int? humanReviewRoundTrips = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.FirstAsync(p => p.Id == workPlanId);
        if (status is not null) plan.Status = status;
        if (steeringIterations is not null) plan.SteeringIterations = steeringIterations.Value;
        if (humanReviewRoundTrips is not null) plan.HumanReviewRoundTrips = humanReviewRoundTrips.Value;
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<(int SteeringIterations, int HumanReviewRoundTrips, string Status, string? Stage)> GetPlanSteeringStateAsync(int workPlanId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var p = await db.WorkPlans.AsNoTracking().Where(w => w.Id == workPlanId)
            .Select(w => new { w.SteeringIterations, w.HumanReviewRoundTrips, w.Status, w.AssemblyStage })
            .FirstAsync();
        return (p.SteeringIterations, p.HumanReviewRoundTrips, p.Status, p.AssemblyStage);
    }

    private async Task<SteeringDirective?> GetLatestDirectiveAsync(string coordinatorRunId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.SteeringDirectives.AsNoTracking()
            .Where(d => d.CoordinatorRunId == coordinatorRunId)
            .OrderByDescending(d => d.Id)
            .FirstOrDefaultAsync();
    }

    private async Task<int> SeedExecutingProceedDirectiveAsync(
        string coordinatorRunId, IReadOnlyList<int> targetSubtaskIds, string treeHash)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var directive = new SteeringDirective
        {
            CoordinatorRunId = coordinatorRunId,
            Kind = SteeringKind.Redirect,
            Instruction = "Budget exhausted — escalate to human review.",
            Status = SteeringStatus.Executing,
            CreatedBy = "gate:rubberduck",
            CreatedAt = DateTimeOffset.UtcNow,
            Source = SteeringSource.Rubberduck,
            Severity = SteeringSeverity.RequestChanges,
            TargetScopeJson = SteeringTargetScope.ForSubtasks(targetSubtaskIds.ToArray()).ToJson(),
            TreeHash = treeHash,
            DecidedAction = SteeringDirection.Proceed,
            ActionAttempt = 0,
            ExecStartedAt = DateTimeOffset.UtcNow,
        };
        db.SteeringDirectives.Add(directive);
        await db.SaveChangesAsync();
        return directive.Id;
    }

    private static string CreateGitRepository()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-buildtest-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoPath);
        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("initial", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        return repoPath;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for git worktrees that may still have transient handles.
        }
    }

    private async Task WaitUntilArmedAsync(string coordinatorRunId)
    {
        for (var i = 0; i < 200 && !_reviewGate.IsArmed(coordinatorRunId); i++)
            await Task.Delay(25);
        _reviewGate.IsArmed(coordinatorRunId).Should().BeTrue("the pipeline should arm the review gate");
    }

    private async Task SeedInboxEntryAsync(string projectId, string slug, string type, string title)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.DecisionInbox.Add(new DecisionInboxEntry
        {
            ProjectId = projectId,
            AgentName = "coordinator",
            Slug = slug,
            Type = type,
            Title = title,
            Content = $"Content for {slug}",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetPlanReviewStateAsync(int workPlanId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var plan = await db.WorkPlans.FirstAsync(p => p.Id == workPlanId);
        plan.Status = WorkPlanStatus.InReview;
        plan.AssemblyStage = AssemblyStage.Review;
        plan.IntegrationBranch = "agentweaver/integration/recover";
        plan.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SetSubtaskStatusAsync(int subtaskId, string status)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtask = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        subtask.Status = status;
        subtask.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<SteeringDirective?> GetDirectiveAsync(int directiveId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.SteeringDirectives.AsNoTracking().FirstOrDefaultAsync(d => d.Id == directiveId);
    }

    /// <summary>
    /// Forces a subtask UNRESUMABLE in the decider's eyes (lapses <c>SteeringRetentionUntil</c> into the
    /// past) WITHOUT clearing its <c>ChildRunId</c>, so a RequestChanges routes to DispatchFresh → lockout
    /// rotation while the prior child run remains available as the context-carrying handoff source.
    /// </summary>
    private async Task LapseSteeringRetentionAsync(int subtaskId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtask = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        subtask.SteeringRetentionUntil = DateTimeOffset.UtcNow.AddMinutes(-5);
        subtask.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<string> GetSubtaskStatusAsync(int subtaskId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().Where(s => s.Id == subtaskId)
            .Select(s => s.Status).FirstAsync();
    }

    private async Task<(string AssignedAgent, string? LockedOutAgents, string? PriorChildRunId, string? RecoveryGuidance)>
        GetSubtaskFieldsAsync(int subtaskId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var s = await db.Subtasks.AsNoTracking().Where(x => x.Id == subtaskId)
            .Select(x => new { x.AssignedAgent, x.LockedOutAgents, x.PriorChildRunId, x.RecoveryGuidance })
            .FirstAsync();
        return (s.AssignedAgent, s.LockedOutAgents, s.PriorChildRunId, s.RecoveryGuidance);
    }

    private async Task<int> GetSubtaskRecoveryAttemptsAsync(int subtaskId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().Where(x => x.Id == subtaskId)
            .Select(x => x.RecoveryAttempts).FirstAsync();
    }

    /// <summary>
    /// Returns a lockout-rotated subtask to a routable state (as if its rotated child completed and was
    /// re-reviewed/rejected) WITHOUT touching its <c>ChildRunId</c>, so the NEXT rotation resolves the
    /// prior handoff child as its <c>priorChild</c> — exercising the compounding-guidance path.
    /// </summary>
    private async Task ResetSubtaskForNextRejectionKeepingChildAsync(int subtaskId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtask = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        subtask.Status = SubtaskStatus.AssembleReady;
        subtask.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task<string?> GetRunTaskAsync(RunId runId)
        => (await _runStore.GetAsync(runId, default))?.Task;

    private async Task SeedPriorRejectionDirectiveAsync(
        string coordinatorRunId, IReadOnlyList<int> targetSubtaskIds, string source, string createdBy, string feedback)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        db.SteeringDirectives.Add(new SteeringDirective
        {
            CoordinatorRunId = coordinatorRunId,
            Kind = SteeringKind.Redirect,
            Instruction = feedback,
            Status = SteeringStatus.Applied,
            CreatedBy = createdBy,
            CreatedAt = DateTimeOffset.UtcNow,
            Source = source,
            Severity = SteeringSeverity.RequestChanges,
            TargetScopeJson = SteeringTargetScope.ForSubtasks(targetSubtaskIds.ToArray()).ToJson(),
        });
        await db.SaveChangesAsync();
    }

    private async Task<string> InvokeBuildAccumulatedRetryGuidanceAsync(
        string coordinatorRunId, IReadOnlyList<int> targetIds, string latestFeedback,
        string? priorChildRunId, string? integrationBranch)
    {
        // Exercise the IN-PLACE render path exactly as ExecuteInPlaceSteerAsync does: the target+rejection
        // -scoped prior rounds rendered WITHOUT a prior-worktree pointer (the session is preserved).
        var rounds = await _sut.BuildPriorReviewRoundsAsync(
            coordinatorRunId, targetIds, CancellationToken.None).ConfigureAwait(false);
        return ReviewFeedbackRenderer.RenderForRevisionPrompt(latestFeedback, rounds, integrationBranch);
    }


    private async Task<int> SeedExecutingInPlaceDirectiveAsync(
        string coordinatorRunId, IReadOnlyList<int> targetSubtaskIds, int attempt, string instruction)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var directive = new SteeringDirective
        {
            CoordinatorRunId = coordinatorRunId,
            TargetChildRunId = null,
            Kind = SteeringKind.Redirect,
            Instruction = instruction,
            Status = SteeringStatus.Executing,
            CreatedBy = "gate:rubberduck",
            CreatedAt = DateTimeOffset.UtcNow,
            Source = SteeringSource.Rubberduck,
            Severity = SteeringSeverity.RequestChanges,
            TargetScopeJson = SteeringTargetScope.ForSubtasks(targetSubtaskIds.ToArray()).ToJson(),
            DecidedAction = SteeringDirection.InPlaceSteer,
            ActionAttempt = attempt,
            ExecStartedAt = DateTimeOffset.UtcNow,
        };
        db.SteeringDirectives.Add(directive);
        await db.SaveChangesAsync();
        return directive.Id;
    }

    private async Task<int> SeedExecutingDispatchFreshDirectiveAsync(
        string coordinatorRunId, IReadOnlyList<int> targetSubtaskIds, int attempt, string instruction)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var directive = new SteeringDirective
        {
            CoordinatorRunId = coordinatorRunId,
            TargetChildRunId = null,
            Kind = SteeringKind.Redirect,
            Instruction = instruction,
            Status = SteeringStatus.Executing,
            CreatedBy = "gate:rubberduck",
            CreatedAt = DateTimeOffset.UtcNow,
            Source = SteeringSource.Rubberduck,
            Severity = SteeringSeverity.RequestChanges,
            TargetScopeJson = SteeringTargetScope.ForSubtasks(targetSubtaskIds.ToArray()).ToJson(),
            DecidedAction = SteeringDirection.DispatchFresh,
            ActionAttempt = attempt,
            ExecStartedAt = DateTimeOffset.UtcNow,
        };
        db.SteeringDirectives.Add(directive);
        await db.SaveChangesAsync();
        return directive.Id;
    }

    private async Task SeedDeferredAssemblyDecisionAsync(string coordinatorRunId, AssemblyReviewDecision decision)
    {
        await CoordinatorAssemblyReviewPersistence.PersistDecisionAsync(
            _scopeFactory, coordinatorRunId, decision, CancellationToken.None);
    }

    private async Task SeedCoordinatorRunAsync(string coordinatorRunId)
    {
        await _runStore.InsertAsync(new Run
        {
            Id = RunId.Parse(coordinatorRunId),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "goal",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
        });
    }

    private async Task SeedChildRunAsync(RunId runId, string worktreeBranch, string diff)
    {
        await _runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "subtask",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
        });
        await _runStore.SetAssembleReadyAsync(
            runId, treeHash: "tree-" + runId, worktreeBranch, diff, stepCount: 1, DateTimeOffset.UtcNow);
    }

    private async Task<(int WorkPlanId, List<int> SubtaskIds)> SeedPlanAsync(
        string coordinatorRunId, IReadOnlyList<string> subtaskStatuses, IReadOnlyList<string?>? childRunIds = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var spec = new OutcomeSpec
        {
            ProjectId = "proj-1",
            CoordinatorRunId = coordinatorRunId,
            Goal = "g",
            DesiredOutcome = "o",
            Scope = "s",
            Assumptions = "a",
            Status = "confirmed",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = "proj-1",
            CoordinatorRunId = coordinatorRunId,
            Status = WorkPlanStatus.AwaitingAssembly,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        for (var i = 0; i < subtaskStatuses.Count; i++)
        {
            var subtask = new Subtask
            {
                WorkPlanId = plan.Id,
                Title = $"t{i}",
                Scope = "s",
                AssignedAgent = "morpheus",
                SelectedModelId = "gpt",
                Phase = "execution",
                IsolationStrategy = "worktree",
                Status = subtaskStatuses[i],
                ChildRunId = childRunIds?[i],
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();
            ids.Add(subtask.Id);
        }

        return (plan.Id, ids);
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
    }

    // ── fakes ───────────────────────────────────────────────────────────────────────────────────

    private sealed class FakePipeline : ICollectiveAssemblyPipeline
    {
        public int IntegrationBuilds;
        public int IntegrationRetryPreparations;
        public int BuildTests;
        public int CleanupBuildTestResourcesCalls;
        public int Merges;
        public int Scribes;
        public IntegrationBranchResult? IntegrationResult;
        public int IntegrationBuildThrowsRemaining;
        public CollectiveGateDecision? BuildTestDecision;
        public Action<CollectiveBuildTestRequest>? OnBuildTest;

        /// <summary>When set, <see cref="MergeAsync"/> returns this result instead of a clean merge.</summary>
        public CollectiveMergeResult? MergeOverride;

        /// <summary>When true, <see cref="MergeAsync"/> throws to exercise the unexpected-fault path.</summary>
        public bool MergeThrows;

        public IntegrationBranchResult BuildIntegrationBranch(CollectiveIntegrationRequest request)
        {
            IntegrationBuilds++;
            if (IntegrationBuildThrowsRemaining > 0)
            {
                IntegrationBuildThrowsRemaining--;
                throw new InvalidOperationException("boom in integration");
            }
            return IntegrationResult
                ?? IntegrationBranchResult.Success(request.IntegrationBranch, "agg-tree", "aggregate diff");
        }

        public void PrepareIntegrationBranchRetry(CollectiveIntegrationRequest request) =>
            IntegrationRetryPreparations++;

        public CollectiveRaiRequest? LastRaiRequest;
        public CollectiveRubberduckRequest? LastRubberduckRequest;

        public Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct)
        {
            LastRaiRequest = request;
            return Task.FromResult(new CollectiveRaiResult(SafetyFlagged: false));
        }

        public Task<CollectiveGateDecision> RunRubberduckAsync(CollectiveRubberduckRequest request, CancellationToken ct)
        {
            LastRubberduckRequest = request;
            return Task.FromResult(new CollectiveGateDecision(Approved: true, RequestChanges: false, Feedback: null));
        }

        public Task<CollectiveGateDecision> RunBuildTestAsync(CollectiveBuildTestRequest request, CancellationToken ct)
        {
            BuildTests++;
            OnBuildTest?.Invoke(request);
            return Task.FromResult(BuildTestDecision
                ?? new CollectiveGateDecision(Approved: true, RequestChanges: false, Feedback: null));
        }

        public Task CleanupBuildTestResourcesAsync(
            string coordinatorRunId,
            string repositoryPath,
            CancellationToken ct = default)
        {
            CleanupBuildTestResourcesCalls++;
            return Task.CompletedTask;
        }

        public string GetBuildTestWorktreePath(string coordinatorRunId) =>
            $"/workspace/assembly-build-test-{coordinatorRunId}";

        public int ReviewerWorktreePreparations;
        public string? LastReviewerWorktreeIntegrationBranch;
        public string? LastReviewerWorktreePath;

        public string PrepareReviewerWorktree(string coordinatorRunId, string repositoryPath, string integrationBranch)
        {
            ReviewerWorktreePreparations++;
            LastReviewerWorktreeIntegrationBranch = integrationBranch;
            LastReviewerWorktreePath = $"/workspace/assembly-build-test-{coordinatorRunId}";
            return LastReviewerWorktreePath;
        }

        public Task<CollectiveMergeResult> MergeAsync(CollectiveMergeRequest request, CancellationToken ct)
        {
            Merges++;
            if (MergeThrows) throw new InvalidOperationException("boom in merge");
            return Task.FromResult(MergeOverride ?? CollectiveMergeResult.Merged("merge-commit"));
        }

        public Task RunScribeAsync(CollectiveScribeRequest request, CancellationToken ct)
        {
            Scribes++;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingLaunchPodLifecycle(Exception exception) : IAgentHostPodLifecycle
    {
        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.FromException<string>(exception);

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            Task.FromException<string>(exception);

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDispatch : ICoordinatorDispatch
    {
        public List<CoordinatorDispatchContext> StartDispatchCalls { get; } = [];
        public void StartDispatch(CoordinatorDispatchContext context) => StartDispatchCalls.Add(context);
        public bool IsDispatchActive(string coordinatorRunId) => false;
    }

    /// <summary>
    /// Req-2 (Strict Lockout) test double for <see cref="IAssemblyAuthorRotationSelector"/>. Default
    /// rotates to a synthetic different author ("rotated-{current}"); tests override <see cref="Impl"/>
    /// to return null (deadlock / single-eligible-agent domain → escalate) or a specific candidate.
    /// </summary>
    private sealed class ConfigurableRotationSelector : IAssemblyAuthorRotationSelector
    {
        public Func<string, RotationSubtaskContext, IReadOnlySet<string>, RotationChoice?> Impl { get; set; }
            = (_, s, _) => new RotationChoice($"rotated-{s.CurrentAuthor}", "model-rotated", null);

        public RotationChoice? SelectRotationAuthor(
            string repositoryPath, RotationSubtaskContext subtask, IReadOnlySet<string> lockedOut)
            => Impl(repositoryPath, subtask, lockedOut);
    }

    /// <summary>
    /// Fake <see cref="IChildRevisionHandoff"/> — records each context-carrying handoff and inserts the
    /// new child run (mirroring the real orchestrator's InsertAsync), persisting its Task as
    /// <c>base + RenderedGuidance</c> exactly as <c>RunOrchestrator.StartChildRevisionHandoffAsync</c>
    /// does, so tests can assert the new agent's persisted Task carries the guidance without compounding.
    /// Captures the exact contract the coordinator hands to Morpheus.
    /// </summary>
    private sealed class FakeChildRevisionHandoff : IChildRevisionHandoff
    {
        private readonly SqliteRunStore _runStore;
        public FakeChildRevisionHandoff(SqliteRunStore runStore) => _runStore = runStore;

        public readonly List<(Run NewAgentRun, Run PriorChild, AccumulatedReviewFeedback Feedback)> Calls = new();

        public async Task StartChildRevisionHandoffAsync(
            Run newAgentRun, Run priorChild, AccumulatedReviewFeedback feedback, CancellationToken ct)
        {
            Calls.Add((newAgentRun, priorChild, feedback));

            // Mirror RunOrchestrator: the guidance is appended to the base Task ONCE by the handoff.
            var guidance = string.IsNullOrWhiteSpace(feedback.RenderedGuidance)
                ? feedback.RenderForRevisionPrompt()
                : feedback.RenderedGuidance!;
            var handoffTask = string.IsNullOrWhiteSpace(newAgentRun.Task)
                ? guidance
                : newAgentRun.Task + "\n\n" + guidance;

            await _runStore.InsertAsync(
                newAgentRun with
                {
                    Status = RunStatus.InProgress,
                    StartedAt = DateTimeOffset.UtcNow,
                    Task = handoffTask,
                }, ct);
        }
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
