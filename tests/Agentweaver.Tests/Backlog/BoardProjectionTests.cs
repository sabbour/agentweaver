using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Board-projection tests for <see cref="BoardProjectionService"/> (FR-015 / FR-016 / FR-019,
/// SC-007). Runs against the REAL store stack: <see cref="SqliteBacklogTaskStore"/> +
/// <see cref="SqliteRunStore"/> over a temp SQLite file, a real EF <see cref="MemoryDbContext"/>
/// (in-memory SQLite) for the work-plan stage projection, and the real
/// <see cref="WorkflowStageProjector"/>. No store or projection logic is mocked (Principle VII).
/// </summary>
public sealed class BoardProjectionTests : IAsyncDisposable
{
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;

    public BoardProjectionTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    private async Task SeedWorkPlanAsync(ProjectId projectId, RunId coordinatorRunId, string status, string? assemblyStage)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var spec = new OutcomeSpec
        {
            ProjectId        = projectId.ToString(),
            CoordinatorRunId = coordinatorRunId.ToString(),
            Goal             = "g",
            DesiredOutcome   = "o",
            Scope            = "s",
            Assumptions      = "a",
            Status           = "confirmed",
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        db.WorkPlans.Add(new WorkPlan
        {
            OutcomeSpecId    = spec.Id,
            ProjectId        = projectId.ToString(),
            CoordinatorRunId = coordinatorRunId.ToString(),
            Status           = status,
            AssemblyStage    = assemblyStage,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Seeds a work plan for a coordinator run plus its subtasks (persona, status, title). Used by the
    /// Phase 2 per-agent work-queue rollup tests to populate <c>agent_queues</c> across runs.
    /// </summary>
    private Task SeedWorkPlanWithSubtasksAsync(
        ProjectId projectId, RunId coordinatorRunId, string status,
        params (string Agent, string Status, string Title)[] subtasks) =>
        SeedWorkPlanWithSubtasksAsync(projectId, coordinatorRunId, status, "g", subtasks);

    private async Task SeedWorkPlanWithSubtasksAsync(
        ProjectId projectId, RunId coordinatorRunId, string status, string goal,
        params (string Agent, string Status, string Title)[] subtasks)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var spec = new OutcomeSpec
        {
            ProjectId        = projectId.ToString(),
            CoordinatorRunId = coordinatorRunId.ToString(),
            Goal             = goal,
            DesiredOutcome   = "o",
            Scope            = "s",
            Assumptions      = "a",
            Status           = "confirmed",
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();

        var plan = new WorkPlan
        {
            OutcomeSpecId    = spec.Id,
            ProjectId        = projectId.ToString(),
            CoordinatorRunId = coordinatorRunId.ToString(),
            Status           = status,
            AssemblyStage    = null,
            CreatedAt        = DateTimeOffset.UtcNow,
            UpdatedAt        = DateTimeOffset.UtcNow,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();

        foreach (var (agent, subtaskStatus, title) in subtasks)
        {
            db.Subtasks.Add(new Subtask
            {
                WorkPlanId        = plan.Id,
                Title             = title,
                Scope             = "scope",
                AssignedAgent     = agent,
                SelectedModelId   = "gpt-4o",
                Phase             = "execution",
                IsolationStrategy = "worktree",
                Status            = subtaskStatus,
                CreatedAt         = DateTimeOffset.UtcNow,
                UpdatedAt         = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
    }

    private static List<object> CardsIn(BoardDto board, string columnId) =>
        board.Columns.Single(c => c.Id == columnId).Cards.ToList();

    [Fact]
    public async Task GetBoard_RendersFixedBuckets_AndMapsRunCardToCanonicalBucket()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var projector = new WorkflowStageProjector();
        var service = new BoardProjectionService(backlogStore, runStore, projector, _scopeFactory);

        var projectA = MakeProject();
        var projectB = MakeProject();
        await projects.InsertAsync(projectA);
        await projects.InsertAsync(projectB);

        // Project A intake: one Backlog, one Ready task.
        var backlogTask = MakeBacklogTask(projectA.Id, "n");
        var readyTask = MakeReadyTask(projectA.Id, "g");
        await backlogStore.InsertAsync(backlogTask);
        await backlogStore.InsertAsync(readyTask);

        // Project A claimed task -> coordinator run, with a work plan mid-assembly (RAI).
        var claimTask = MakeReadyTask(projectA.Id, "a");
        await backlogStore.InsertAsync(claimTask);
        var runId = RunId.New();
        (await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            projectA.Id, claimTask.Id,
            MakeCoordinatorRun(projectA.Id, runId), DateTimeOffset.UtcNow))
            .Should().Be(ClaimReserveResult.Won);
        await SeedWorkPlanAsync(projectA.Id, runId, WorkPlanStatus.Assembling, AssemblyStage.Rai);

        // Project B: an unrelated task + claimed run that must NEVER appear on project A's board.
        var bTask = MakeReadyTask(projectB.Id, "a");
        await backlogStore.InsertAsync(bTask);
        var bRunId = RunId.New();
        await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            projectB.Id, bTask.Id,
            MakeCoordinatorRun(projectB.Id, bRunId), DateTimeOffset.UtcNow);

        var board = await service.GetBoardAsync(projectA.Id, includeTerminalHistory: false, default);

        // Fixed bucket model: intake columns first, then canonical run buckets.
        board.WorkflowStagesAvailable.Should().BeTrue();
        board.Columns.Select(c => c.Label).Should().Equal(
            "Backlog", "Ready", "Problems", "Human Review", "Active", "Done");
        board.Columns[0].Kind.Should().Be("intake");
        board.Columns[1].Kind.Should().Be("intake");
        board.Columns.Skip(2).Should().OnlyContain(c => c.Kind == "workflow");

        // Intake cards land in their buckets.
        CardsIn(board, "backlog").Cast<TaskCardDto>().Single().TaskId.Should().Be(backlogTask.Id.ToString());
        CardsIn(board, "ready").Cast<TaskCardDto>().Single().TaskId.Should().Be(readyTask.Id.ToString());

        // FR-016: the claimed task's coordinator run card sits in its canonical bucket, linked
        // back to the originating backlog task.
        var activeCards = CardsIn(board, WorkflowStageProjector.ActiveStageId).Cast<RunCardDto>().ToList();
        var card = activeCards.Single();
        card.RunId.Should().Be(runId.ToString());
        card.BacklogTaskId.Should().Be(claimTask.Id.ToString());
        card.StageId.Should().Be(WorkflowStageProjector.ActiveStageId);
        card.WorkPlanStatus.Should().Be(WorkPlanStatus.Assembling);
        card.AssemblyStage.Should().Be(AssemblyStage.Rai);

        // The claimed task is represented ONLY by its run card, not duplicated as an intake card.
        board.Columns.Where(c => c.Kind == "intake")
            .SelectMany(c => c.Cards).Cast<TaskCardDto>()
            .Should().NotContain(t => t.TaskId == claimTask.Id.ToString());

        // SC-007: project B's task and run never leak into project A's board.
        var allTaskIds = board.Columns.SelectMany(c => c.Cards).OfType<TaskCardDto>().Select(t => t.TaskId);
        var allRunIds = board.Columns.SelectMany(c => c.Cards).OfType<RunCardDto>().Select(r => r.RunId);
        allTaskIds.Should().NotContain(bTask.Id.ToString());
        allRunIds.Should().NotContain(bRunId.ToString());
    }

    [Fact]
    public async Task GetBoard_MapsProblemAndReviewRuns_ToCanonicalBuckets()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var service = new BoardProjectionService(backlogStore, runStore, new WorkflowStageProjector(), _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);

        var problemRun = await ClaimRunAsync(backlogStore, project.Id, "a");
        await SeedWorkPlanAsync(project.Id, problemRun, WorkPlanStatus.AssemblyBlocked, null);

        var reviewRun = await ClaimRunAsync(backlogStore, project.Id, "b");
        await SeedWorkPlanAsync(project.Id, reviewRun, WorkPlanStatus.InReview, AssemblyStage.Review);

        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        board.Columns.Single(c => c.Label == "Problems").Cards.Cast<RunCardDto>()
            .Single().RunId.Should().Be(problemRun.ToString());
        board.Columns.Single(c => c.Label == "Human Review").Cards.Cast<RunCardDto>()
            .Single().RunId.Should().Be(reviewRun.ToString());
        CardsIn(board, WorkflowStageProjector.ActiveStageId).Should().BeEmpty();
    }

    [Fact]
    public async Task GetBoard_TerminalRun_CollapsesToDoneColumn()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var projector = new WorkflowStageProjector();
        var service = new BoardProjectionService(backlogStore, runStore, projector, _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);

        var task = MakeReadyTask(project.Id, "a");
        await backlogStore.InsertAsync(task);
        var runId = RunId.New();
        await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id,
            MakeCoordinatorRun(project.Id, runId), DateTimeOffset.UtcNow);
        // Drive the run terminal; a completed work plan collapses the card to the Done column.
        await runStore.UpdateStatusAsync(runId, RunStatus.Merged, DateTimeOffset.UtcNow);
        await SeedWorkPlanAsync(project.Id, runId, WorkPlanStatus.Complete, null);

        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        CardsIn(board, WorkflowStageProjector.DoneStageId).Cast<RunCardDto>()
            .Single().RunId.Should().Be(runId.ToString());
        CardsIn(board, WorkflowStageProjector.ActiveStageId).Should().BeEmpty();
    }

    [Fact]
    public async Task GetBoard_ArchivedTaskAndRun_DoNotAppearInAnyColumn()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var service = new BoardProjectionService(backlogStore, runStore, new WorkflowStageProjector(), _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);

        var backlogTask = MakeBacklogTask(project.Id, "a");
        await backlogStore.InsertAsync(backlogTask);
        (await backlogStore.TryArchiveAsync(project.Id, backlogTask.Id, DateTimeOffset.UtcNow))
            .Should().BeTrue();

        var claimedTask = MakeReadyTask(project.Id, "b");
        await backlogStore.InsertAsync(claimedTask);
        var runId = RunId.New();
        (await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, claimedTask.Id, MakeCoordinatorRun(project.Id, runId), DateTimeOffset.UtcNow))
            .Should().Be(ClaimReserveResult.Won);
        (await backlogStore.TryArchiveAsync(project.Id, claimedTask.Id, DateTimeOffset.UtcNow))
            .Should().BeTrue();

        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        board.Columns.SelectMany(c => c.Cards).OfType<TaskCardDto>()
            .Should().NotContain(t => t.TaskId == backlogTask.Id.ToString());
        board.Columns.SelectMany(c => c.Cards).OfType<RunCardDto>()
            .Should().NotContain(r => r.RunId == runId.ToString());
    }

    [Fact]
    public async Task GetBoard_WhenStageProjectorReturnsEmpty_FR019FallbackExposesOnlyIntakeColumns()
    {
        // Arrange: a projector that simulates an unresolvable topology (returns empty stages).
        var emptyProjector = new EmptyStageProjector();

        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var service = new BoardProjectionService(backlogStore, runStore, emptyProjector, _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);
        await backlogStore.InsertAsync(MakeBacklogTask(project.Id, "n"));
        await backlogStore.InsertAsync(MakeReadyTask(project.Id, "g"));

        // Act
        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        // Assert FR-019: unavailable flag set, exactly 2 intake columns, no workflow columns.
        board.WorkflowStagesAvailable.Should().BeFalse();
        board.Columns.Should().HaveCount(2);
        board.Columns[0].Id.Should().Be("backlog");
        board.Columns[0].Kind.Should().Be("intake");
        board.Columns[1].Id.Should().Be("ready");
        board.Columns[1].Kind.Should().Be("intake");
        board.Columns.Should().NotContain(c => c.Kind == "workflow");
    }

    // =========================================================================
    // Phase 2: project-aggregate per-agent work-queue rollup (agent_queues).
    // =========================================================================
    [Fact]
    public async Task GetBoard_AgentQueues_AggregatePerPersona_AcrossActiveRuns_WithCorrectBuckets()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var projector = new WorkflowStageProjector();
        var service = new BoardProjectionService(backlogStore, runStore, projector, _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);

        // Two ACTIVE coordinator runs (InProgress + a non-terminal "planned" work plan).
        var run1 = await ClaimRunAsync(backlogStore, project.Id, "a");
        var run2 = await ClaimRunAsync(backlogStore, project.Id, "b");

        await SeedWorkPlanWithSubtasksAsync(project.Id, run1, WorkPlanStatus.Planned,
            ("Tank", "dispatched", "Tank wires the endpoint"),   // active
            ("Tank", "pending", "Tank writes the migration"),    // queued
            ("Tank", "failed", "Tank's flaky attempt"),          // blocked
            ("Trinity", "completed", "Trinity ships the rail")); // done

        await SeedWorkPlanWithSubtasksAsync(project.Id, run2, WorkPlanStatus.Planned,
            ("Tank", "running", "Tank streams the board"),       // active
            ("Trinity", "assemble_ready", "Trinity polishes UX"),// done
            ("Trinity", "pending", "Trinity drafts the modal")); // queued

        // A TERMINAL run whose subtasks must NOT leak into the rollup (historical, merged-away).
        var run3 = await ClaimRunAsync(backlogStore, project.Id, "c");
        await runStore.UpdateStatusAsync(run3, RunStatus.Merged, DateTimeOffset.UtcNow);
        await SeedWorkPlanWithSubtasksAsync(project.Id, run3, WorkPlanStatus.Complete,
            ("Tank", "completed", "Tank's old merged work"));

        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        board.AgentQueues.Should().NotBeNull();
        board.AgentQueues.Select(q => q.AgentName).Should().BeEquivalentTo(new[] { "Tank", "Trinity" });

        var tank = board.AgentQueues.Single(q => q.AgentName == "Tank");
        tank.Active.Should().Be(2);   // dispatched (run1) + running (run2)
        tank.Queued.Should().Be(1);   // pending (run1)
        tank.Blocked.Should().Be(1);  // failed (run1)
        tank.Done.Should().Be(0);     // run3's completed subtask is terminal -> excluded
        tank.RunIds.Should().BeEquivalentTo(new[] { run1.ToString(), run2.ToString() });
        tank.RunIds.Should().NotContain(run3.ToString());
        tank.SampleTitles.Should().NotBeEmpty();
        tank.SampleTitles.Count.Should().BeLessThanOrEqualTo(3);

        var trinity = board.AgentQueues.Single(q => q.AgentName == "Trinity");
        trinity.Active.Should().Be(0);
        trinity.Queued.Should().Be(1);   // pending (run2)
        trinity.Blocked.Should().Be(0);
        trinity.Done.Should().Be(2);     // completed (run1) + assemble_ready (run2)
        trinity.RunIds.Should().BeEquivalentTo(new[] { run1.ToString(), run2.ToString() });

        // Ordering: Tank (more load) sorts before Trinity.
        board.AgentQueues[0].AgentName.Should().Be("Tank");
    }

    [Fact]
    public async Task GetBoard_AgentQueues_GroupedByOrchestration_PerRunBuckets_TitleAndOrdering()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var service = new BoardProjectionService(backlogStore, runStore, new WorkflowStageProjector(), _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);

        var run1 = await ClaimRunAsync(backlogStore, project.Id, "a");
        var run2 = await ClaimRunAsync(backlogStore, project.Id, "b");
        var run3 = await ClaimRunAsync(backlogStore, project.Id, "c");

        // run1: heavier load for Tank (active + queued + blocked) and a named objective.
        await SeedWorkPlanWithSubtasksAsync(project.Id, run1, WorkPlanStatus.Planned, "Build the inbox",
            ("Tank", "dispatched", "Tank wires the endpoint"),   // active
            ("Tank", "pending", "Tank writes the migration"),    // queued
            ("Tank", "failed", "Tank's flaky attempt"),          // blocked
            ("Tank", "pending", "Tank writes the migration"));   // duplicate queued title (dedupe check)

        // run2: lighter load for Tank, distinct objective.
        await SeedWorkPlanWithSubtasksAsync(project.Id, run2, WorkPlanStatus.Planned, "Polish the board",
            ("Tank", "running", "Tank streams the board"));      // active

        // run3: an orchestration with no human objective (blank goal) -> title must be null.
        await SeedWorkPlanWithSubtasksAsync(project.Id, run3, WorkPlanStatus.Planned, "   ",
            ("Tank", "pending", "Tank triages the queue"));      // queued

        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        var tank = board.AgentQueues.Single(q => q.AgentName == "Tank");
        tank.Orchestrations.Should().HaveCount(3);

        // Per-orchestration buckets sum to the agent-level totals.
        tank.Orchestrations.Sum(o => o.Active).Should().Be(tank.Active);
        tank.Orchestrations.Sum(o => o.Queued).Should().Be(tank.Queued);
        tank.Orchestrations.Sum(o => o.Blocked).Should().Be(tank.Blocked);
        tank.Orchestrations.Sum(o => o.Done).Should().Be(tank.Done);

        var o1 = tank.Orchestrations.Single(o => o.RunId == run1.ToString());
        o1.Active.Should().Be(1);
        o1.Queued.Should().Be(2);
        o1.Blocked.Should().Be(1);
        o1.Done.Should().Be(0);
        o1.Title.Should().Be("Build the inbox");
        // sample_titles scoped to this run, in-flight only, deduped (the repeated queued title appears once).
        o1.SampleTitles.Should().BeEquivalentTo(new[] { "Tank wires the endpoint", "Tank writes the migration" });
        o1.SampleTitles.Should().NotContain("Tank's flaky attempt"); // blocked is not in-flight

        var o2 = tank.Orchestrations.Single(o => o.RunId == run2.ToString());
        o2.Active.Should().Be(1);
        o2.Queued.Should().Be(0);
        o2.Title.Should().Be("Polish the board");
        o2.SampleTitles.Should().Equal("Tank streams the board");

        var o3 = tank.Orchestrations.Single(o => o.RunId == run3.ToString());
        o3.Queued.Should().Be(1);
        o3.Title.Should().BeNull(); // no objective available -> null, web falls back to a short run id

        // Ordering: most-loaded orchestration first. run1 (load 4+4+1=9) > run2 (load 4) > run3 (load 2).
        tank.Orchestrations[0].RunId.Should().Be(run1.ToString());
        tank.Orchestrations[1].RunId.Should().Be(run2.ToString());
        tank.Orchestrations[2].RunId.Should().Be(run3.ToString());

        // Back-compat: top-level run_ids stays the sorted distinct set of the orchestrations' run ids.
        tank.RunIds.Should().BeEquivalentTo(new[] { run1.ToString(), run2.ToString(), run3.ToString() });
    }

    [Fact]
    public async Task GetBoard_AgentQueues_EmptyWhenNoActiveRuns_NeverNull()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var backlogStore = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);
        var service = new BoardProjectionService(backlogStore, runStore, new WorkflowStageProjector(), _scopeFactory);

        var project = MakeProject();
        await projects.InsertAsync(project);
        await backlogStore.InsertAsync(MakeReadyTask(project.Id, "g"));

        var board = await service.GetBoardAsync(project.Id, includeTerminalHistory: false, default);

        board.AgentQueues.Should().NotBeNull();
        board.AgentQueues.Should().BeEmpty();
    }

    private static async Task<RunId> ClaimRunAsync(
        SqliteBacklogTaskStore backlogStore, ProjectId projectId, string orderKey)
    {
        var task = MakeReadyTask(projectId, orderKey);
        await backlogStore.InsertAsync(task);
        var runId = RunId.New();
        (await backlogStore.TryClaimAndReserveCoordinatorRunAsync(
            projectId, task.Id, MakeCoordinatorRun(projectId, runId), DateTimeOffset.UtcNow))
            .Should().Be(ClaimReserveResult.Won);
        return runId;
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        _memoryConn.Dispose();
    }

    /// <summary>
    /// Test double that simulates an unresolvable coordinator topology (FR-019 fallback path).
    /// Only used in test code — never registered in production DI.
    /// </summary>
    private sealed class EmptyStageProjector : IWorkflowStageProjector
    {
        public IReadOnlyList<WorkflowStage> GetStages() => Array.Empty<WorkflowStage>();
        public string CoordinatorRunToStageId(Run _, CoordinatorWorkPlanStage? __) =>
            WorkflowStageProjector.TerminalStageId;
    }
}
