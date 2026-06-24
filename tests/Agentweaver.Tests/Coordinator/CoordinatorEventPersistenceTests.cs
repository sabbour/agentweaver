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
/// Verifies that the coordinator event persist contract (the logic shared between
/// RunWorkflowFactory.PersistRunEventsAsync and CoordinatorAssemblyService) works correctly:
/// all accumulated stream events reach the RunEvents table in order, persist is idempotent,
/// and stopped child run streams contain the terminal event that lets the dispatch observer
/// resolve the child. Tests run against real SQLite — no mocks (Constitution VII).
/// </summary>
public sealed class CoordinatorEventPersistenceTests : IAsyncDisposable
{
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly RunStreamStore _streamStore = new();

    public CoordinatorEventPersistenceTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        _provider = services.BuildServiceProvider();
        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();
        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
    }

    // -----------------------------------------------------------------------
    // Core persist contract
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Persist_StreamEvents_ReachRunEventsTable()
    {
        var runId = RunId.New().ToString();
        var entry = _streamStore.Create(runId, "alice");

        entry.RecordNext(EventTypes.CoordinatorStarted, new { });
        entry.RecordNext(EventTypes.SubtaskDispatched, new { subtaskId = 1 });
        entry.RecordNext(EventTypes.RunCompleted, new { result = "complete" });
        _streamStore.Complete(runId);

        await PersistStreamEventsAsync(runId);

        var persisted = await GetRunEventsAsync(runId);
        persisted.Should().HaveCount(3, "all events in the stream must be persisted");
        persisted.Select(e => e.EventType).Should().ContainInOrder(
            EventTypes.CoordinatorStarted,
            EventTypes.SubtaskDispatched,
            EventTypes.RunCompleted);
    }

    [Fact]
    public async Task Persist_IsIdempotent_NoDuplicateSequences()
    {
        var runId = RunId.New().ToString();
        var entry = _streamStore.Create(runId, "alice");
        entry.RecordNext(EventTypes.CoordinatorStarted, new { });
        entry.RecordNext(EventTypes.RunCompleted, new { result = "settled" });
        _streamStore.Complete(runId);

        await PersistStreamEventsAsync(runId);
        await PersistStreamEventsAsync(runId); // second call must not duplicate rows

        var persisted = await GetRunEventsAsync(runId);
        persisted.Should().HaveCount(2, "idempotent persist must not insert duplicate sequences");
        persisted.Select(e => e.Sequence).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Persist_FullTimeline_PreservesSequenceOrder()
    {
        var runId = RunId.New().ToString();
        var entry = _streamStore.Create(runId, "alice");

        entry.RecordNext(EventTypes.CoordinatorStarted, new { });
        entry.RecordNext(EventTypes.CoordinatorOutcomeSpec, new { specId = 1 });
        entry.RecordNext(EventTypes.SubtaskDispatched, new { subtaskId = 1 });
        entry.RecordNext(EventTypes.SubtaskRunning, new { subtaskId = 1 });
        entry.RecordNext(EventTypes.SubtaskAssembleReady, new { subtaskId = 1 });
        entry.RecordNext(EventTypes.CoordinatorAssemblyStarted, new { });
        entry.RecordNext(EventTypes.CoordinatorAssemblyCompleted, new { });
        entry.RecordNext(EventTypes.RunCompleted, new { result = "confirmed" });
        _streamStore.Complete(runId);

        await PersistStreamEventsAsync(runId);

        var persisted = await GetRunEventsAsync(runId);
        persisted.Should().HaveCount(8);
        persisted.Select(e => e.Sequence).Should().BeInAscendingOrder(
            "events must be persisted in monotonic sequence order");
    }

    // -----------------------------------------------------------------------
    // Stop directive stream contract: terminal RunCancelled event present and persistable
    // -----------------------------------------------------------------------

    [Fact]
    public async Task StopDirective_ChildStream_ContainsRunCancelled_AndIsCompletedForPersist()
    {
        const string coord = "coord-stop-struct";
        const string child = "child-stop-struct";

        _streamStore.Create(coord, "alice");
        var childEntry = _streamStore.Create(child, "alice");
        childEntry.RecordNext(EventTypes.SubtaskRunning, new { });
        childEntry.RecordNext(EventTypes.AgentMessage, new { text = "working" });

        await SeedPlanWithChildAsync(coord, child, SubtaskStatus.Running);

        var registry = new RunWorkflowRegistry();
        var cts = new CancellationTokenSource();
        registry.Register(child, null!, cts);

        var sut = new CoordinatorSteeringService(
            _streamStore, registry, new CoordinatorSteeringQueue(), _scopeFactory,
            NullLogger<CoordinatorSteeringService>.Instance);

        await sut.SteerAsync(coord, "stop", child, "halt", "alice", default);

        var stream = _streamStore.Get(child)!;
        stream.IsCompleted.Should().BeTrue("stop must complete the child stream");
        stream.GetSnapshotSince(0).Events
            .Should().Contain(e => e.Type == EventTypes.RunCancelled,
                "the terminal run.cancelled event must be present for the dispatch observer and persist path");
        cts.IsCancellationRequested.Should().BeTrue("stop must cancel the workflow token");

        // Verify the completed stream events can be persisted correctly (production path: fire-and-forget).
        await PersistStreamEventsAsync(child);
        var persisted = await GetRunEventsAsync(child);
        persisted.Should().Contain(e => e.EventType == EventTypes.RunCancelled,
            "run.cancelled must survive persist to RunEvents for timeline replay");
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    /// <summary>
    /// Inlines the same persist logic as <c>RunWorkflowFactory.PersistRunEventsAsync</c> and
    /// <c>CoordinatorAssemblyService.PersistAndCompleteStreamAsync</c> so the test exercises the
    /// persistence contract without constructing the heavyweight RunWorkflowFactory.
    /// </summary>
    private async Task PersistStreamEventsAsync(string runId)
    {
        var entry = _streamStore.Get(runId);
        if (entry is null) return;
        var events = entry.GetSnapshotSince(0).Events;
        if (events.Count == 0) return;

        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

        var existingSeqs = db.RunEvents
            .Where(e => e.RunId == runId)
            .Select(e => e.Sequence)
            .ToHashSet();

        var toInsert = events
            .Where(e => !existingSeqs.Contains(e.Sequence))
            .Select(e => new RunEventRecord
            {
                RunId = runId,
                Sequence = e.Sequence,
                EventType = e.Type,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(e.Payload),
                CreatedAt = DateTime.UtcNow,
            })
            .ToList();

        if (toInsert.Count > 0)
        {
            db.RunEvents.AddRange(toInsert);
            await db.SaveChangesAsync();
        }
    }

    private async Task SeedPlanWithChildAsync(string coordinatorRunId, string childRunId, string status)
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

        db.Subtasks.Add(new Subtask
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
        });
        await db.SaveChangesAsync();
    }

    private async Task<List<RunEventRecord>> GetRunEventsAsync(string runId)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Sequence)
            .ToListAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();
    }
}
