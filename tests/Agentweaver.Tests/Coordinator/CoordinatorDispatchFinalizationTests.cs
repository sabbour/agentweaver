using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for the Feature 008 dispatch finalization
/// (<see cref="CoordinatorDispatchService.FinalizeDispatchAsync"/>). After every child subtask is
/// terminal, finalization must emit <see cref="EventTypes.CoordinatorChildrenComplete"/>, move the
/// work plan to <see cref="WorkPlanStatus.AwaitingAssembly"/>, publish a final topology snapshot, and
/// HAND OFF to Phase 3 collective assembly (the stream is left open for the assembly pipeline). Real
/// service + real EF <see cref="MemoryDbContext"/> (no mocks); a fake <see cref="ICoordinatorAssembly"/>
/// records the hand-off without launching the real pipeline.
/// </summary>
public sealed class CoordinatorDispatchFinalizationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingAssembly _assembly = new();
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
            worktreeManager: null!,
            steering: null!,
            _assembly,
            _scopeFactory,
            new TestHostApplicationLifetime(),
            NullLogger<CoordinatorDispatchService>.Instance);
    }

    [Fact]
    public async Task FinalizeDispatch_AllChildrenTerminal_EmitsChildrenComplete_AndHandsOffToAssembly()
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

        // The work plan reached the AwaitingAssembly hand-off status.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var plan = await db.WorkPlans.AsNoTracking().FirstAsync(w => w.Id == workPlanId);
            plan.Status.Should().Be(WorkPlanStatus.AwaitingAssembly);
        }

        // The coordinator stream carries the explicit children-complete signal and a final snapshot.
        var entry = _streamStore.Get(coordinatorRunId)!;
        var events = entry.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorChildrenComplete);
        events.Should().Contain(e => e.Type == EventTypes.CoordinatorTopology,
            "a final topology snapshot must reflect the hand-off status");

        // Phase 3 hand-off: assembly was triggered for this run (stream stays open for it).
        _assembly.Started.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coordinatorRunId);
        entry.IsCompleted.Should().BeFalse("the assembly pipeline now owns closing the coordinator stream");
    }

    private sealed class RecordingAssembly : ICoordinatorAssembly
    {
        public List<CoordinatorDispatchContext> Started { get; } = [];
        public void StartAssembly(CoordinatorDispatchContext context) => Started.Add(context);
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
