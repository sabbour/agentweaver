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
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the unified STEERING / RESUME recovery
/// (<see cref="CoordinatorSteeringService.SteerAsync"/> redirect/amend on a parked coordinator).
///
/// When a coordinator dead-ends — a <c>rai_flagged</c> subtask blocked assembly, or a collective
/// assembly conflict parked the run — the one-shot dispatch loop has already exited, so a queued
/// redirect/amend would never drain. The service must instead RESUME: reset the affected subtasks to
/// pending with guidance, bump their attempt counter (capped), un-terminalize the coordinator run,
/// re-open its stream, and re-arm dispatch.
///
/// Exercised against REAL components (EF <see cref="MemoryDbContext"/> on in-memory SQLite, a real
/// <see cref="SqliteRunStore"/>, a real <see cref="RunStreamStore"/>, the real
/// <see cref="CoordinatorSteeringService"/>). The dispatch RE-ARM boundary is observed via a recording
/// <see cref="ICoordinatorDispatch"/> — the same collaborator pattern used by
/// <c>CoordinatorAssemblyServiceTests</c>/<c>CoordinatorDispatchFinalizationTests</c> (record the
/// hand-off, never fake the SUT). "Progress is now possible" is proven deterministically by asserting
/// the reset subtask re-enters <see cref="SubtaskFrontier.ReadyPending"/>.
///
/// Owner-only enforcement (e) lives at the endpoint and is already covered by
/// <c>CoordinatorPhase2EndpointsTests.Steer_NonOwner_Returns403</c>.
/// </summary>
public sealed class CoordinatorSteeringRecoveryTests : IAsyncDisposable
{
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingDispatch _dispatch = new();
    private readonly CoordinatorSteeringService _sut;

    public CoordinatorSteeringRecoveryTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        services.AddSingleton(_runStore);
        services.AddSingleton<IRunStore>(_runStore);
        services.AddSingleton<ICoordinatorDispatch>(_dispatch);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _sut = new CoordinatorSteeringService(
            _streamStore, new RunWorkflowRegistry(),
            _scopeFactory, NullLogger<CoordinatorSteeringService>.Instance,
            runStore: _runStore);
    }

    [Fact]
    public async Task Redirect_OnRaiFlaggedDeadEnd_ResetsSubtask_UnTerminalizes_AndReArms()
    {
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.Failed, "assembly_blocked: ineligible_subtasks");
        var (planId, ids) = await SeedPlanAsync(coord, WorkPlanStatus.AssemblyBlocked, new[]
        {
            SubtaskStatus.RaiFlagged, // s0 — the flagged subtask that dead-ended the run
            SubtaskStatus.Pending,    // s1 — dependent, blocked forever today
        }, dependency: (1, 0));

        var view = await _sut.SteerAsync(coord, "redirect", null, "Remove the leaked credential and re-run.", "owner", default);

        view.Status.Should().Be(SteeringStatus.Applied, "a parked coordinator resumes immediately");
        view.RelayedAt.Should().NotBeNull();

        // The flagged subtask is reset to pending with guidance + a bumped attempt counter.
        var s0 = await GetSubtaskAsync(ids[0]);
        s0.Status.Should().Be(SubtaskStatus.Pending);
        s0.ChildRunId.Should().BeNull("a reset subtask drops its dead child run id");
        s0.RecoveryAttempts.Should().Be(1);
        s0.RecoveryGuidance.Should().NotBeNull();
        s0.RecoveryGuidance.Should().Contain("Remove the leaked credential");
        s0.RecoveryGuidance.Should().Contain("Responsible AI", "the failure context tells the worker why");

        // The coordinator run is un-terminalized.
        var run = await _runStore.GetAsync(RunId.Parse(coord));
        run!.Status.Should().Be(RunStatus.InProgress, "the failed coordinator run goes live again");
        run.EndedAt.Should().BeNull();

        // The plan returns to dispatching and the stream re-opens with a recovery signal.
        (await GetPlanStatusAsync(planId)).Should().Be(WorkPlanStatus.Dispatching);
        var events = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorRecovered);
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorSteering);

        // Dispatch is re-armed for this coordinator.
        _dispatch.StartDispatchCalls.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord);

        // Progress is now possible: the reset subtask re-enters the dispatch frontier.
        var frontier = await CurrentFrontierAsync(planId);
        frontier.Should().Contain(ids[0], "the reset subtask is dispatchable again");
    }

    [Fact]
    public async Task Redirect_OnAssemblyConflict_ResetsAssembleReadySubtasks_AndReArms()
    {
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.Failed, "assembly_blocked: integration_conflict");
        var (planId, ids) = await SeedPlanAsync(coord, WorkPlanStatus.AssemblyBlocked, new[]
        {
            SubtaskStatus.AssembleReady, // both produced changes that conflicted on merge
            SubtaskStatus.AssembleReady,
            SubtaskStatus.Completed,     // no-change subtask — left intact
        });

        await _sut.SteerAsync(coord, "redirect", null, "Rebase onto the latest integration branch to avoid the conflict.", "owner", default);

        var s0 = await GetSubtaskAsync(ids[0]);
        var s1 = await GetSubtaskAsync(ids[1]);
        var s2 = await GetSubtaskAsync(ids[2]);
        s0.Status.Should().Be(SubtaskStatus.Pending);
        s1.Status.Should().Be(SubtaskStatus.Pending);
        s2.Status.Should().Be(SubtaskStatus.Completed, "a completed no-change subtask is not re-run");
        s0.RecoveryGuidance.Should().Contain("conflicted during collective assembly");

        _dispatch.StartDispatchCalls.Should().ContainSingle();
    }

    [Fact]
    public async Task Redirect_WhenEveryAffectedSubtaskOverAttemptCap_Throws_AndDoesNotReArm()
    {
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.Failed, "assembly_blocked: ineligible_subtasks");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.AssemblyBlocked, new[] { SubtaskStatus.RaiFlagged });
        await SetRecoveryAttemptsAsync(ids[0], 3); // == cap

        var act = async () => await _sut.SteerAsync(coord, "redirect", null, "try again", "owner", default);

        (await act.Should().ThrowAsync<SteeringRecoveryExhaustedException>())
            .Which.Message.Should().Contain("attempt cap");

        // The subtask stays flagged, the run stays failed, and dispatch is NOT re-armed.
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.RaiFlagged);
        (await _runStore.GetAsync(RunId.Parse(coord)))!.Status.Should().Be(RunStatus.Failed);
        _dispatch.StartDispatchCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Redirect_OnOrphanedDispatch_ReArms_AndQueues_WithoutResetting()
    {
        var coord = RunId.New().ToString();
        // An ORPHANED orchestration: run InProgress, plan dispatching, a running child, but NO active
        // dispatch loop (the in-memory loop died / never re-armed after a restart). A queued directive
        // would drain into the void, so steering must re-arm dispatch.
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.InProgress, result: "");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.Dispatching, new[] { SubtaskStatus.Running });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = false; // no live loop — orphaned

        var view = await _sut.SteerAsync(coord, "redirect", "child-1", "use the v2 API", "owner", default);

        view.Status.Should().Be(SteeringStatus.Queued, "the directive queues to drain at the re-armed loop's next boundary");
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.Running,
            "an orphan's in-flight subtask is re-observed by the re-armed loop, not reset");
        _dispatch.StartDispatchCalls.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coord,
            "an orphaned coordinator is re-armed so the queued directive can drain");
    }

    [Fact]
    public async Task Redirect_WhenDispatchAlreadyActive_DoesNotRecover_Queues()
    {
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.Failed, "assembly_blocked: ineligible_subtasks");
        await SeedPlanAsync(coord, WorkPlanStatus.AssemblyBlocked, new[] { SubtaskStatus.RaiFlagged });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = true; // single-writer guard: a loop is (claimed) running

        var view = await _sut.SteerAsync(coord, "redirect", null, "fix it", "owner", default);

        view.Status.Should().Be(SteeringStatus.Queued, "recovery must not mutate subtasks while a loop owns them");
        _dispatch.StartDispatchCalls.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Redirect vs Amend: distinct behavior on parked (AssemblyFailed) coordinators.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Redirect_OnParkedCoordinator_WithFailedSubtasks_ResetsAndReArms()
    {
        // AssemblyFailed (not AssemblyBlocked) — the plan dead-ended without rai_flagged.
        // Redirect must treat failed subtasks the same as rai_flagged: reset to pending and re-arm.
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.Failed, "failed: subtask_failed");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.AssemblyFailed, new[] { SubtaskStatus.Failed });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = false;

        var view = await _sut.SteerAsync(coord, "redirect", null, "try a different approach", "owner", default);

        view.Status.Should().Be(SteeringStatus.Applied, "redirect on a parked coordinator resumes immediately");
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.Pending,
            "redirect resets failed subtasks to pending for re-dispatch");
        _dispatch.StartDispatchCalls.Should().ContainSingle("redirect re-arms the dispatch loop");
    }

    [Fact]
    public async Task Amend_OnParkedCoordinator_WithOnlyFailedSubtasks_DoesNotResetSubtasks()
    {
        // AssemblyFailed, only Failed subtasks (no rai_flagged). Amend is additive: it must NOT reset
        // failed subtasks. With nothing to unblock it falls through to queue.
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.Failed, "failed: subtask_failed");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.AssemblyFailed, new[] { SubtaskStatus.Failed });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = false;

        var view = await _sut.SteerAsync(coord, "amend", null, "extend the scope", "owner", default);

        view.Status.Should().Be(SteeringStatus.Queued,
            "amend does not reset failed subtasks — no rai_flagged gate to unblock, falls through to queue");
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.Failed,
            "amend must not reset failed subtasks");
        _dispatch.StartDispatchCalls.Should().BeEmpty("amend does not re-arm without an unblockable gate");
    }

    [Fact]
    public async Task Amend_OnLiveCoordinator_DoesNotCancelInFlightWork()
    {
        // Live coordinator (Dispatching + active loop). Amend must queue for the next boundary;
        // no subtask reset or stream cancellation must occur.
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.InProgress, "");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.Dispatching, new[] { SubtaskStatus.Running });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = true;

        var view = await _sut.SteerAsync(coord, "amend", null, "add extra criteria", "owner", default);

        view.Status.Should().Be(SteeringStatus.Queued, "amend queues for the next turn boundary");
        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.Running,
            "amend must not cancel or reset in-flight work");
        _dispatch.StartDispatchCalls.Should().BeEmpty("amend on a live coordinator does not re-arm");
    }

    [Fact]
    public async Task Stop_OnNonOwnerReplica_TerminalizesTargetChildDurably()
    {
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.InProgress, "");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.Dispatching, new[] { SubtaskStatus.Running });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = true;

        var childRunId = (await GetSubtaskAsync(ids[0])).ChildRunId!;
        await SeedChildRunAsync(childRunId, coord, ids[0]);

        var view = await _sut.SteerAsync(coord, "stop", childRunId, "stop the stuck child", "owner", default);

        view.Status.Should().Be(SteeringStatus.Applied);
        var child = await _runStore.GetAsync(RunId.Parse(childRunId));
        child!.Status.Should().Be(RunStatus.Failed,
            "a non-owner replica must persist terminal state instead of relying on local RunWorkflowRegistry");
        child.Result.Should().Be("steering_stop");

        var repeated = await _sut.SteerAsync(coord, "stop", childRunId, "stop the stuck child", "owner", default);
        repeated.Status.Should().Be(SteeringStatus.Applied, "repeated stops are idempotent from the caller perspective");
        (await _runStore.GetAsync(RunId.Parse(childRunId)))!.Status.Should().Be(RunStatus.Failed);
    }

    [Fact]
    public async Task Stop_Broadcast_OnNonOwnerReplica_TerminalizesAllActiveChildrenDurably()
    {
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.InProgress, "");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.Dispatching, new[]
        {
            SubtaskStatus.Running,
            SubtaskStatus.Running,
        });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = true;

        var childRunIds = new[]
        {
            (await GetSubtaskAsync(ids[0])).ChildRunId!,
            (await GetSubtaskAsync(ids[1])).ChildRunId!,
        };
        await SeedChildRunAsync(childRunIds[0], coord, ids[0]);
        await SeedChildRunAsync(childRunIds[1], coord, ids[1]);

        var view = await _sut.SteerAsync(coord, "stop", targetChildRunId: null, "stop all", "owner", default);

        view.Status.Should().Be(SteeringStatus.Applied);
        foreach (var childRunId in childRunIds)
        {
            var child = await _runStore.GetAsync(RunId.Parse(childRunId));
            child!.Status.Should().Be(RunStatus.Failed);
            child.Result.Should().Be("steering_stop");
        }

        var coordinator = await _runStore.GetAsync(RunId.Parse(coord));
        coordinator!.Status.Should().Be(RunStatus.Failed, "broadcast stop also terminalizes the coordinator run");
        coordinator.Result.Should().Be("steering_stop");
    }

    [Fact]
    public async Task Redirect_WithSpecificChildTarget_ForceCompletesChildStream()
    {
        // Redirect targeting a specific in-progress child must force-complete that child's stream
        // so the dispatch observer resolves it as failed/cancelled and drains the queued redirect.
        var coord = RunId.New().ToString();
        await SeedTerminalCoordinatorRunAsync(coord, RunStatus.InProgress, "");
        var (_, ids) = await SeedPlanAsync(coord, WorkPlanStatus.Dispatching, new[] { SubtaskStatus.Running });
        _streamStore.Create(coord, "owner");
        _dispatch.Active = true;

        var childRunId = (await GetSubtaskAsync(ids[0])).ChildRunId!;
        childRunId.Should().NotBeNull("SeedPlanAsync assigns a ChildRunId for Running subtasks");
        _streamStore.Create(childRunId, "owner");

        var view = await _sut.SteerAsync(coord, "redirect", childRunId, "take a new approach", "owner", default);

        view.Status.Should().Be(SteeringStatus.Queued, "redirect queues to drain at the dispatch loop boundary");
        _streamStore.Get(childRunId)!.IsCompleted.Should().BeTrue(
            "redirect force-completes the targeted child stream so the dispatch loop resolves it");
        _streamStore.Get(childRunId)!.GetSnapshotSince(0).Events
            .Should().Contain(e => e.Type == EventTypes.RunCancelled,
                "run.cancelled is emitted to the child stream so the dispatch loop resolves the child as failed");
    }

    // -----------------------------------------------------------------------
    // Seed + read helpers (real EF + real run store).
    // -----------------------------------------------------------------------

    private async Task SeedTerminalCoordinatorRunAsync(string coordinatorRunId, RunStatus status, string result)
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
        };
        await _runStore.InsertAsync(run);
        if (status != RunStatus.InProgress)
            await _runStore.UpdateResultAsync(run.Id, status, result, DateTimeOffset.UtcNow);
    }

    private async Task SeedChildRunAsync(string childRunId, string coordinatorRunId, int subtaskId)
    {
        await _runStore.InsertAsync(new Run
        {
            Id = RunId.Parse(childRunId),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "child task",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = coordinatorRunId,
            SubtaskId = subtaskId.ToString(),
        });
    }

    private async Task<(int PlanId, List<int> SubtaskIds)> SeedPlanAsync(
        string coordinatorRunId, string planStatus, string[] subtaskStatuses, (int Dependent, int DependsOn)? dependency = null)
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
            Status = planStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        var ids = new List<int>();
        foreach (var status in subtaskStatuses)
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
                ChildRunId = status is SubtaskStatus.RaiFlagged or SubtaskStatus.Failed or SubtaskStatus.Running
                    ? RunId.New().ToString()
                    : null,
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

    private async Task<IReadOnlyList<int>> CurrentFrontierAsync(int planId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var subtasks = await db.Subtasks.AsNoTracking().Where(s => s.WorkPlanId == planId).ToListAsync();
        var ids = subtasks.Select(s => s.Id).ToHashSet();
        var statusById = subtasks.ToDictionary(s => s.Id, s => s.Status);
        var edges = (await db.SubtaskDependencies.AsNoTracking().Where(d => ids.Contains(d.SubtaskId)).ToListAsync())
            .Select(d => (d.SubtaskId, d.DependsOnSubtaskId))
            .ToList();
        return SubtaskFrontier.ReadyPending(statusById, edges);
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
    }

    /// <summary>
    /// Records <see cref="ICoordinatorDispatch.StartDispatch"/> hand-offs (the re-arm boundary) and
    /// reports a configurable <see cref="IsDispatchActive"/> so the single-writer guard can be exercised.
    /// Mirrors the recording dispatch already used by <c>CoordinatorAssemblyServiceTests</c>.
    /// </summary>
    private sealed class RecordingDispatch : ICoordinatorDispatch
    {
        public List<CoordinatorDispatchContext> StartDispatchCalls { get; } = [];
        public bool Active { get; set; }
        public void StartDispatch(CoordinatorDispatchContext context) => StartDispatchCalls.Add(context);
        public bool IsDispatchActive(string coordinatorRunId) => Active;
    }
}
