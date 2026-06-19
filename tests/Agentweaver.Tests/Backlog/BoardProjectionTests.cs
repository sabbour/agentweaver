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

    private static List<object> CardsIn(BoardDto board, string columnId) =>
        board.Columns.Single(c => c.Id == columnId).Cards.ToList();

    [Fact]
    public async Task GetBoard_RendersIntakeFirst_DescriptorColumns_AndMapsRunCardToItsStage()
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

        // FR-019 available; intake columns first, then descriptor-driven workflow columns.
        board.WorkflowStagesAvailable.Should().BeTrue();
        board.Columns[0].Id.Should().Be("backlog");
        board.Columns[0].Kind.Should().Be("intake");
        board.Columns[1].Id.Should().Be("ready");
        board.Columns[1].Kind.Should().Be("intake");
        board.Columns.Skip(2).Select(c => c.Id).Should().Equal(projector.GetStages().Select(s => s.Id));
        board.Columns.Skip(2).Should().OnlyContain(c => c.Kind == "workflow");

        // Intake cards land in their buckets.
        CardsIn(board, "backlog").Cast<TaskCardDto>().Single().TaskId.Should().Be(backlogTask.Id.ToString());
        CardsIn(board, "ready").Cast<TaskCardDto>().Single().TaskId.Should().Be(readyTask.Id.ToString());

        // FR-016: the claimed task's coordinator run card sits in its current stage column, linked
        // back to the originating backlog task.
        var raiCards = CardsIn(board, CoordinatorGraphDescriptor.AssemblyRaiNodeId).Cast<RunCardDto>().ToList();
        var card = raiCards.Single();
        card.RunId.Should().Be(runId.ToString());
        card.BacklogTaskId.Should().Be(claimTask.Id.ToString());
        card.StageId.Should().Be(CoordinatorGraphDescriptor.AssemblyRaiNodeId);
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

        CardsIn(board, WorkflowStageProjector.TerminalStageId).Cast<RunCardDto>()
            .Single().RunId.Should().Be(runId.ToString());
        CardsIn(board, CoordinatorGraphDescriptor.CoordinatorNodeId).Should().BeEmpty();
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
