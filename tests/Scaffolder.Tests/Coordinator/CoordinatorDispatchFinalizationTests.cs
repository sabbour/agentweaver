using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Scaffolder.Api.Coordinator;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Memory;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Coordinator;

/// <summary>
/// Unit tests for the Feature 008 Defect D dispatch finalization
/// (<see cref="CoordinatorDispatchService.FinalizeDispatchAsync"/>). After every child subtask is
/// terminal, the coordinator run must NOT silently stay in-flight (it looked hung). Finalization must
/// emit <see cref="EventTypes.CoordinatorChildrenComplete"/>, move the work plan to the terminal-ish
/// <see cref="WorkPlanStatus.AwaitingAssembly"/> status, publish a final topology snapshot, and close
/// the coordinator stream. Real service + real EF <see cref="MemoryDbContext"/> (no mocks).
/// </summary>
public sealed class CoordinatorDispatchFinalizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunStreamStore _streamStore = new();
    private readonly CoordinatorDispatchService _sut;

    public CoordinatorDispatchFinalizationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _sut = new CoordinatorDispatchService(
            runStore: null!,
            _streamStore,
            orchestrator: null!,
            steering: null!,
            _scopeFactory,
            new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance);
    }

    [Fact]
    public async Task FinalizeDispatch_AllChildrenTerminal_EmitsChildrenComplete_AndAwaitingAssembly()
    {
        const string coordinatorRunId = "coord-final-1";
        var (workPlanId, subtaskIds) = await SeedPlanAsync(coordinatorRunId);
        _streamStore.Create(coordinatorRunId, "alice");

        var statusById = new Dictionary<int, string>
        {
            [subtaskIds[0]] = SubtaskStatus.Completed,
            [subtaskIds[1]] = SubtaskStatus.AssembleReady,
            [subtaskIds[2]] = SubtaskStatus.Failed,
        };

        var context = new CoordinatorDispatchContext(coordinatorRunId, "repo", "main", "alice", null);

        await _sut.FinalizeDispatchAsync(
            context, workPlanId, statusById, edges: [], new CoordinatorDispatchService.SeqCounter(), default);

        // The work plan reached the terminal-ish AwaitingAssembly status.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == workPlanId);
            plan.Status.Should().Be(WorkPlanStatus.AwaitingAssembly);
        }

        // The coordinator stream carries the explicit children-complete signal and a final snapshot,
        // and is now completed so SSE clients stop polling (no longer looks hung).
        var entry = _streamStore.Get(coordinatorRunId)!;
        var events = entry.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorChildrenComplete);
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorTopology,
            "a final topology snapshot must reflect the terminal status");
        entry.IsCompleted.Should().BeTrue("the coordinator stream must close so the run is not hung");
    }

    private async Task<(int WorkPlanId, List<int> SubtaskIds)> SeedPlanAsync(string coordinatorRunId)
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
        foreach (var i in Enumerable.Range(0, 3))
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
                Status = SubtaskStatus.Completed,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Subtasks.Add(subtask);
            await db.SaveChangesAsync();
            ids.Add(subtask.Id);
        }

        return (plan.Id, ids);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
