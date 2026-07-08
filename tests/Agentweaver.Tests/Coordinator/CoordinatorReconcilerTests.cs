using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the coordinator watchdog/reconciler (Feature 008 recovery). A coordinator run can get
/// permanently stuck when the in-memory dispatch loop dies (or the process restarts) between dispatch
/// and child completion: nothing re-observes the in-flight subtasks, the persisted terminal child
/// status is never reconciled, and the frontier never advances.
///
/// Two seams are exercised against REAL components (EF <see cref="MemoryDbContext"/> on in-memory
/// SQLite, a real <see cref="SqliteRunStore"/>, a real <see cref="RunStreamStore"/>; Constitution VII,
/// no mocks):
/// <list type="bullet">
/// <item>The RECOVERY-AWARE re-arm inside <see cref="CoordinatorDispatchService.RunDispatchLoopAsync"/>:
/// re-observing an orphaned dispatched/running subtask store-resolves its terminal child and advances
/// the plan; a genuinely stalled child is failed with guidance.</item>
/// <item><see cref="CoordinatorReconciler.SweepAsync"/>: detecting an orphan (work plan still
/// dispatching, no active loop) and re-arming it idempotently (observed via a recording dispatch).</item>
/// </list>
/// </summary>
public sealed class CoordinatorReconcilerTests : IAsyncDisposable
{
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingAssembly _assembly = new();

    public CoordinatorReconcilerTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    // -----------------------------------------------------------------------
    // Recovery-aware re-arm (the core fix): the dispatch loop reconciles orphaned in-flight subtasks.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReArm_OrphanedRunningSubtask_ReconcilesTerminalChild_DispatchesDependent_AdvancesPlan()
    {
        const string coord = "coord-rearm-1";
        // s0: running, but its child already reached assemble_ready in the store (the orphaned case).
        // s1: pending, depends on s0 — blocked forever until s0 is reconciled.
        var child0 = await SeedChildRunAsync(RunStatus.AssembleReady);
        var (planId, ids) = await SeedPlanAsync(coord, new[]
        {
            (SubtaskStatus.Running, (string?)child0),
            (SubtaskStatus.Pending, null),
        }, dependency: (1, 0));
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch();
        await sut.RunDispatchLoopAsync(Context(coord), default);

        // The orphaned running subtask was reconciled from its terminal child run.
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "the re-armed loop store-resolves the orphaned child and applies the result");

        // The dependent sibling became dispatchable once s0 reconciled; in this minimal harness it
        // then fails before a child stream is started because no worktree manager is wired. The point
        // is it left pending and reached the dispatch attempt.
        var s1 = await GetSubtaskAsync(ids[1]);
        s1.ChildRunId.Should().NotBeNull("the unblocked dependent sibling was dispatched after recovery");
        var events = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.SubtaskFailed,
            "the minimal harness records the attempted dependent dispatch as failed");

        // The plan advanced past dispatching to the assembly hand-off.
        (await GetPlanStatusAsync(planId)).Should().Be(WorkPlanStatus.AwaitingAssembly);
        _assembly.Started.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
    }

    [Fact]
    public async Task ReArm_EmitsCoordinatorRecoveredAuditEvent()
    {
        const string coord = "coord-rearm-audit";
        var child0 = await SeedChildRunAsync(RunStatus.AssembleReady);
        await SeedPlanAsync(coord, new[] { (SubtaskStatus.Running, (string?)child0) });
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch();
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var events = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorRecovered,
            "re-observing orphaned in-flight subtasks emits a recovery audit event for the timeline");
    }

    // -----------------------------------------------------------------------
    // Stall handling: a non-terminal orphaned child that has made no progress is failed.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ReArm_StalledOrphanedChild_FailsSubtask_WithGuidance_AndBumpsRecoveryAttempts()
    {
        const string coord = "coord-stall-1";
        // Child run is still in-progress in the store (no terminal status) and has no live stream/watch
        // loop, with a start time well in the past — a genuinely stalled/orphaned child.
        var child0 = await SeedChildRunAsync(RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var (_, ids) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.Running, (string?)child0) });
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stallTimeoutMinutes: 0);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var s0 = await GetSubtaskAsync(ids[0]);
        s0.Status.Should().Be(SubtaskStatus.Failed, "a stalled orphaned child fails its subtask");
        s0.RecoveryGuidance.Should().NotBeNull();
        s0.RecoveryGuidance.Should().Contain("stalled");
        s0.RecoveryAttempts.Should().Be(1, "the stall-fail bumps the recovery-attempt counter");
    }

    [Fact]
    public async Task ReArm_StalledChildAtAttemptCap_DoesNotExceedCap()
    {
        const string coord = "coord-stall-cap";
        var child0 = await SeedChildRunAsync(RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var (_, ids) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.Running, (string?)child0) });
        await SetRecoveryAttemptsAsync(ids[0], 3); // already at the cap
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stallTimeoutMinutes: 0);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var s0 = await GetSubtaskAsync(ids[0]);
        s0.Status.Should().Be(SubtaskStatus.Failed);
        s0.RecoveryAttempts.Should().Be(3, "the recovery-attempt counter is capped (never exceeds the cap)");
    }

    // -----------------------------------------------------------------------
    // Reconciler sweep: orphan detection + idempotent re-arm.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sweep_OrphanedDispatchingPlan_ReArmsDispatch()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        await SeedPlanAsync(coord, new[] { (SubtaskStatus.Running, (string?)RunId.New().ToString()) });

        var dispatch = new RecordingDispatch { Active = false };
        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, dispatch, NullLogger<CoordinatorReconciler>.Instance);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(1);
        dispatch.StartDispatchCalls.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
    }

    [Fact]
    public async Task Sweep_ActiveDispatch_DoesNotDoubleArm()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        await SeedPlanAsync(coord, new[] { (SubtaskStatus.Running, (string?)RunId.New().ToString()) });

        var dispatch = new RecordingDispatch { Active = true }; // a loop already owns this run
        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, dispatch, NullLogger<CoordinatorReconciler>.Instance);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(0);
        dispatch.StartDispatchCalls.Should().BeEmpty("an already-active coordinator is never re-armed");
    }

    // -----------------------------------------------------------------------
    // in_review handling: legitimate-vs-orphaned + auto-abandon escape hatch.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Sweep_InReviewPlan_WithActiveAssembly_DoesNotReArm()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.InReview);
        _assembly.MarkActive(coord); // an assembly loop already owns the open review gate

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(0);
        _assembly.Started.Should().BeEmpty("an in_review run with an active assembly is legitimately in review, not orphaned");
        _assembly.Abandoned.Should().BeEmpty("it has not exceeded the review timeout");
    }

    [Fact]
    public async Task Sweep_InReviewPlan_WithoutActiveAssembly_ReArmsAssembly()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.InReview);
        // assembly NOT marked active AND no review record → genuinely orphaned

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(1);
        _assembly.Started.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
        _assembly.Abandoned.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_InReviewPlan_WithPendingReviewGate_DoesNotReArm_EvenWithoutActiveLoop()
    {
        // The correction: in_review with a pending HUMAN review gate is INTENTIONAL — the operator
        // simply hasn't reviewed yet. This must hold even when no assembly loop is active IN THIS POD
        // (e.g. the awaiting loop is on another replica, or this pod just restarted). The durable
        // AssemblyReviews record — NOT the in-memory IsAssemblyActive guard — is the authoritative
        // signal, so the reconciler must NOT re-arm and must NOT loop.
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.InReview);
        await SeedReviewRecordAsync(coord, decisionSubmitted: false); // pending human decision
        // deliberately do NOT MarkActive — simulate cross-pod / post-restart

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        // Multiple sweeps must remain a silent no-op (no infinite re-arm loop).
        (await reconciler.SweepAsync(default)).Should().Be(0);
        (await reconciler.SweepAsync(default)).Should().Be(0);

        _assembly.Started.Should().BeEmpty("a run with a pending human review gate is not orphaned");
        _assembly.Abandoned.Should().BeEmpty();
        _assembly.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_InReviewPlan_WithSubmittedDecision_ButNoActiveLoop_ReArms()
    {
        // A decision WAS submitted (DecisionSubmittedAt set) but the run is still in_review with no
        // active loop → the loop that should apply the decision died. This is a true orphan, so the
        // reconciler re-arms so the submitted decision gets processed.
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.InReview);
        await SeedReviewRecordAsync(coord, decisionSubmitted: true);

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(1);
        _assembly.Started.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
    }

    [Fact]
    public async Task Sweep_InReviewPlan_StaleBeyondTimeout_Abandons_WithoutReArming()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.InReview, updatedAt: DateTimeOffset.UtcNow.AddHours(-48));
        _assembly.MarkActive(coord); // even an "active" loop that is stuck must be abandoned past the timeout

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Runs:ReviewTimeoutHours"] = "24" })
            .Build();
        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: config, assembly: _assembly);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(1);
        _assembly.Abandoned.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
        _assembly.Started.Should().BeEmpty("an abandoned stale review is never re-armed");
    }

    [Fact]
    public async Task Sweep_InReviewPlan_StaleBeyondTimeout_WithOpenGate_DoesNotAbandon()
    {
        // The review gate is genuinely OPEN (a pending review record, no decision submitted) and the
        // human simply hasn't acted — even for two days. The idle timeout must NOT fire: an open gate
        // waits INDEFINITELY for the human. Abandoning here is exactly what produced the unwanted
        // "the review gate is no longer open" message, so it must never happen while the gate is open.
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.InReview, updatedAt: DateTimeOffset.UtcNow.AddHours(-48));
        await SeedReviewRecordAsync(coord, decisionSubmitted: false); // pending human decision

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Runs:ReviewTimeoutHours"] = "24" })
            .Build();
        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: config, assembly: _assembly);

        // Repeated sweeps remain a silent no-op — never abandon, never re-arm.
        (await reconciler.SweepAsync(default)).Should().Be(0);
        (await reconciler.SweepAsync(default)).Should().Be(0);

        _assembly.Abandoned.Should().BeEmpty("an open review gate is never timed out — it waits for the human");
        _assembly.Started.Should().BeEmpty();
        _assembly.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task Sweep_AssemblingPlan_FreshLease_DoesNotReArm()
    {
        // A fresh `assembling` plan is being actively built by a live loop (possibly on another
        // replica). Re-arming it would drive a second pod into the git integration merge (the ref-lock
        // race). It must be skipped while the lease is fresh — and NOT counted toward the re-arm cap.
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.Assembling, updatedAt: DateTimeOffset.UtcNow);
        // assembly NOT marked active in THIS pod (simulate cross-pod) but the lease is fresh

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        for (var i = 0; i < CoordinatorReconciler.MaxAssemblyReArmAttempts + 2; i++)
            (await reconciler.SweepAsync(default)).Should().Be(0);

        _assembly.Started.Should().BeEmpty("a fresh assembling plan is owned by a live loop, not orphaned");
        _assembly.Failed.Should().BeEmpty("a healthy live assembly must never be failed by the reconciler");
    }

    [Fact]
    public async Task Sweep_AssemblingPlan_StaleLease_ReArms()
    {
        // A stale `assembling` plan (owner silent well past the lease TTL) is a genuine orphan — the
        // owning pod likely died mid-assembly. Re-arm so recovery can resume.
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.Assembling, updatedAt: DateTimeOffset.UtcNow.AddMinutes(-10));

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        var reArmed = await reconciler.SweepAsync(default);

        reArmed.Should().Be(1);
        _assembly.Started.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
    }

    [Fact]
    public async Task Sweep_OrphanedAwaitingAssembly_ReArmsUpToCap_ThenFailsRun()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(planId, WorkPlanStatus.AwaitingAssembly);

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        // The orphaned assembly keeps coming back awaiting_assembly (each re-arm fails). It is re-armed
        // up to the cap, then the run is terminalized instead of looping forever.
        for (var i = 0; i < CoordinatorReconciler.MaxAssemblyReArmAttempts; i++)
            (await reconciler.SweepAsync(default)).Should().Be(1);

        _assembly.Started.Should().HaveCount(CoordinatorReconciler.MaxAssemblyReArmAttempts,
            "the assembly is re-armed up to the cap");
        _assembly.Failed.Should().BeEmpty("the run is not failed until the cap is exceeded");

        // The next sweep exceeds the cap → terminal fail, no further re-arm.
        (await reconciler.SweepAsync(default)).Should().Be(1);
        _assembly.Started.Should().HaveCount(CoordinatorReconciler.MaxAssemblyReArmAttempts,
            "no re-arm happens once the cap is exceeded");
        _assembly.Failed.Should().ContainSingle().Which.Context.CoordinatorRunId.Should().Be(coord);
        _assembly.Failed[0].Reason.Should().Contain("exhausted");
    }

    [Fact]
    public async Task Sweep_AssemblyBlockedIntegrationBuildError_DoesNotReArmOrExhaust()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, _) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.AssembleReady, (string?)null) });
        await SetPlanStatusAsync(
            planId,
            WorkPlanStatus.AssemblyBlocked,
            assemblyStatusReason: "assembly_blocked: integration_build_error");

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        for (var i = 0; i < CoordinatorReconciler.MaxAssemblyReArmAttempts + 2; i++)
            (await reconciler.SweepAsync(default)).Should().Be(0);

        _assembly.Started.Should().BeEmpty("integration_build_error is parked until explicit state-changing input");
        _assembly.Failed.Should().BeEmpty("a parked block is not counted toward re-arm exhaustion");
        (await GetPlanStatusAsync(planId)).Should().Be(WorkPlanStatus.AssemblyBlocked);
    }

    [Fact]
    public async Task Sweep_AssemblyBlockedIneligibleSubtasks_ReArmsOnlyAfterEligibilityChanges()
    {
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord);
        var (planId, ids) = await SeedPlanAsync(coord, new[] { (SubtaskStatus.Failed, (string?)null) });
        await SetPlanStatusAsync(
            planId,
            WorkPlanStatus.AssemblyBlocked,
            assemblyStatusReason: "assembly_blocked: ineligible_subtasks [1]");

        var reconciler = new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, new RecordingDispatch(),
            NullLogger<CoordinatorReconciler>.Instance, configuration: null, assembly: _assembly);

        (await reconciler.SweepAsync(default)).Should().Be(0);
        _assembly.Started.Should().BeEmpty();

        await SetSubtaskStatusAsync(ids[0], SubtaskStatus.AssembleReady);

        (await reconciler.SweepAsync(default)).Should().Be(1);
        _assembly.Started.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private CoordinatorDispatchService BuildDispatch(double stallTimeoutMinutes = 15)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:SubtaskStallTimeoutMinutes"] = stallTimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        var orchestrator = new RunOrchestrator(
            _runStore, _streamStore,
            worktreeManager: null!, workflowFactory: null!, registry: null!, watchLoop: null!,
            _scopeFactory, configuration: null!, NullLogger<RunOrchestrator>.Instance);

        return new CoordinatorDispatchService(
            _runStore, _streamStore, orchestrator, null!, new CoordinatorSteeringQueue(_scopeFactory), _assembly,
            _scopeFactory, new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance,
            runOptions: null, autopilot: null, configuration: config);
    }

    private CoordinatorReconciler BuildReconciler(RecordingDispatch dispatch)
    {
        return new CoordinatorReconciler(
            _scopeFactory, _runStore, _streamStore, dispatch, NullLogger<CoordinatorReconciler>.Instance);
    }

    private static CoordinatorDispatchContext Context(string coord) =>
        new(coord, "repo", "main", "owner", null);

    private async Task<string> SeedChildRunAsync(RunStatus status, DateTimeOffset? startedAt = null)
    {
        var id = RunId.New();
        var run = new Run
        {
            Id = id,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "child subtask",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = startedAt ?? DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "0",
        };
        await _runStore.InsertAsync(run);
        if (status != RunStatus.InProgress)
            await _runStore.UpdateStatusAsync(id, status, DateTimeOffset.UtcNow);
        return id.ToString();
    }

    private async Task SeedCoordinatorRunAsync(string coordinatorRunId)
    {
        var run = new Run
        {
            Id = RunId.Parse(coordinatorRunId),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "coordinate the work",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
        };
        await _runStore.InsertAsync(run);
    }

    private async Task<(int PlanId, List<int> SubtaskIds)> SeedPlanAsync(
        string coordinatorRunId,
        (string Status, string? ChildRunId)[] subtasks,
        (int Dependent, int DependsOn)? dependency = null)
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
            Status = WorkPlanStatus.Dispatching,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        foreach (var (status, childRunId) in subtasks)
        {
            var subtask = new Subtask
            {
                WorkPlanId = plan.Id,
                Title = "t",
                Scope = "s",
                AssignedAgent = "morpheus",
                SelectedModelId = "gpt",
                Phase = "execution",
                IsolationStrategy = "worktree",
                Status = status,
                ChildRunId = childRunId,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();
            ids.Add(subtask.Id);
        }

        if (dependency is { } dep)
        {
            db.SubtaskDependencies.Add(new SubtaskDependency
            {
                SubtaskId = ids[dep.Dependent],
                DependsOnSubtaskId = ids[dep.DependsOn],
            });
            await db.SaveChangesAsync();
        }

        return (plan.Id, ids);
    }

    private async Task<Subtask> GetSubtaskAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private async Task SetRecoveryAttemptsAsync(int id, int attempts)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstAsync(s => s.Id == id);
        row.RecoveryAttempts = attempts;
        await db.SaveChangesAsync();
    }

    private async Task<string> GetPlanStatusAsync(int planId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return (await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == planId)).Status;
    }

    private async Task SetPlanStatusAsync(
        int planId,
        string status,
        DateTimeOffset? updatedAt = null,
        string? assemblyStatusReason = null)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.WorkPlans.FirstAsync(w => w.Id == planId);
        row.Status = status;
        row.AssemblyStatusReason = assemblyStatusReason;
        row.UpdatedAt = updatedAt ?? DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SetSubtaskStatusAsync(int subtaskId, string status)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await db.Subtasks.FirstAsync(s => s.Id == subtaskId);
        row.Status = status;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private async Task SeedReviewRecordAsync(string coordinatorRunId, bool decisionSubmitted)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.AssemblyReviews.Add(new CoordinatorAssemblyReviewRecord
        {
            CoordinatorRunId = coordinatorRunId,
            OwnerUser = "tester",
            IntegrationBranch = $"agentweaver/integration/{coordinatorRunId}",
            AggregateTreeHash = "deadbeef",
            DecisionJson = decisionSubmitted ? "{\"Approved\":true}" : null,
            Reviewer = decisionSubmitted ? "tester" : null,
            DecisionSubmittedAt = decisionSubmitted ? now : null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
    }

    private sealed class RecordingAssembly : ICoordinatorAssembly
    {
        public List<CoordinatorDispatchContext> Started { get; } = [];
        public List<CoordinatorDispatchContext> Abandoned { get; } = [];
        public List<(CoordinatorDispatchContext Context, string Reason)> Failed { get; } = [];
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);

        public void MarkActive(string coordinatorRunId) => _active.Add(coordinatorRunId);
        public void StartAssembly(CoordinatorDispatchContext context) => Started.Add(context);
        public void EnsureFinalScribe(Run coordinatorRun) { }
        public bool IsAssemblyActive(string coordinatorRunId) => _active.Contains(coordinatorRunId);
        public void AbandonStaleReview(CoordinatorDispatchContext context) => Abandoned.Add(context);
        public void FailAssembly(CoordinatorDispatchContext context, string reason) => Failed.Add((context, reason));
    }

    private sealed class RecordingDispatch : ICoordinatorDispatch
    {
        public List<CoordinatorDispatchContext> StartDispatchCalls { get; } = [];
        public bool Active { get; set; }
        public void StartDispatch(CoordinatorDispatchContext context) => StartDispatchCalls.Add(context);
        public bool IsDispatchActive(string coordinatorRunId) => Active;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
