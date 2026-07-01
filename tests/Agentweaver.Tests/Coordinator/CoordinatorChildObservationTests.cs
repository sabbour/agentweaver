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
/// Tests for push-based child observation via <see cref="IRunEventStream.SubscribeAsync"/>
/// (016-US2). Verifies that the coordinator's dispatch loop subscribes to child event streams
/// rather than polling with Task.Delay, that replay survives a simulated process restart, and
/// that the TTL-based stall signal fires when no events arrive within the configured window.
/// </summary>
public sealed class CoordinatorChildObservationTests : IAsyncDisposable
{
    private readonly string _tempDir;
    private readonly IConfiguration _streamConfig;
    private readonly SqliteConnection _memoryConn;
    private readonly ServiceProvider _provider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;
    private readonly RunStreamStore _streamStore = new();
    private readonly RecordingAssembly _assembly = new();

    public CoordinatorChildObservationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aw-obs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        CreateRunEventsTable(Path.Combine(_tempDir, "memory.db"));

        _streamConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_tempDir, "agentweaver.db"),
            })
            .Build();

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
    // US2-AC1: coordinator subscribes via await foreach — no Task.Delay
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_ChildEventsPreAppended_AreDeliveredViaReplay_NoPolling()
    {
        // Pre-append events to the child's durable log (simulates the child running and
        // completing before the coordinator's observation starts — covered by replay).
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "doing work" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        const string coord = "obs-replay-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "replay delivers the terminal event so the subtask resolves to assemble_ready");
    }

    // -----------------------------------------------------------------------
    // US2-AC2: child process restart — replay resumes from persisted events
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_PersistedEventsPresent_AfterSimulatedRestart_ReplayedCorrectly()
    {
        // Phase 1: a "prior process" appends events durably (including a terminal).
        var priorStream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        await priorStream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "step 1" }));
        await priorStream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "step 2" }));
        await priorStream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        // Phase 1 ends without completing the channel (process crashed)

        // Phase 2: a "new process" creates a fresh SqliteRunEventStream (no in-memory channel)
        // and the coordinator loop re-observes the child by replaying from the durable log.
        var newStream = new SqliteRunEventStream(_streamConfig);
        const string coord = "obs-restart-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(newStream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "replay on the new process instance delivers the persisted terminal event");
    }

    // -----------------------------------------------------------------------
    // US2-AC3: stall TTL — no events within timeout → stall signal emitted
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_NoEventsWithinStallTtl_SubtaskFailedAsStalled()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        // Child is InProgress but emits no events — the stall TTL fires.
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress, startedAt: DateTimeOffset.UtcNow.AddHours(-1));
        const string coord = "obs-stall-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        // Configure an extremely short stall timeout (0.001 min ≈ 60 ms).
        var sut = BuildDispatch(stream, stallTimeoutMinutes: 0.001);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await sut.RunDispatchLoopAsync(Context(coord), cts.Token);

        var subtask = await GetSubtaskAsync(ids[0]);
        subtask.Status.Should().Be(SubtaskStatus.Failed,
            "a child that emits no events within the stall TTL is failed by the dispatch loop");
        subtask.RecoveryGuidance.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // US2-AC4: terminal event → subscription ends cleanly
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_TerminalEventViaLiveChannel_SubscriptionEndsCleanly()
    {
        // Use a live channel: start the dispatch loop in the background, then inject events
        // concurrently so they arrive via the Channel path (not replay).
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);
        const string coord = "obs-live-coord";
        var (_, ids) = await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        using var loopCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Start the dispatch loop in the background so it begins subscribing to the child.
        var loopTask = Task.Run(() => sut.RunDispatchLoopAsync(Context(coord), loopCts.Token), loopCts.Token);

        // Give the loop a moment to start subscribing, then inject live events.
        await Task.Delay(150);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentMessage, new { content = "live work" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady, new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        await loopTask;

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.AssembleReady,
            "the terminal event delivered via the live channel resolves the subtask cleanly");
    }

    // -----------------------------------------------------------------------
    // US2-AC4b: interaction bubbling — question event reaches coordinator stream
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ObserveChild_ChildQuestionEvent_IsBubbledOntoCoordinatorStream()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var childRunId = await SeedChildRunAsync(RunStatus.InProgress);

        // Pre-append a question followed by a terminal event.
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.AgentQuestionAsked,
            new { requestId = "req-42", question = "Shall I proceed?" }));
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunAssembleReady,
            new { raiSafetyFlagged = false }));
        await stream.CompleteAsync(childRunId);

        const string coord = "obs-bubble-coord";
        await SeedPlanAsync(coord, [(SubtaskStatus.Running, childRunId)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        var coordEvents = _streamStore.Get(coord)!.GetSnapshotSince(0).Events;
        coordEvents.Should().Contain(e => e.Type == EventTypes.CoordinatorChildQuestion,
            "a child AgentQuestionAsked event must be bubbled onto the coordinator stream");
    }

    [Fact]
    public async Task RunDispatchLoop_CoordinatorStoppedAfterActiveChild_DoesNotDispatchRemainingPendingSubtasks()
    {
        var stream = new SqliteRunEventStream(_streamConfig);
        var coord = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coord, RunStatus.Failed);
        var childRunId = await SeedChildRunAsync(RunStatus.Failed);
        await stream.AppendAsync(childRunId, new RunEvent(0, EventTypes.RunCancelled, new { reason = "steering_stop" }));
        await stream.CompleteAsync(childRunId);

        var (_, ids) = await SeedPlanAsync(coord,
            [(SubtaskStatus.Running, childRunId), (SubtaskStatus.Pending, null)]);
        _streamStore.Create(coord, "owner");

        var sut = BuildDispatch(stream);
        await sut.RunDispatchLoopAsync(Context(coord), default);

        (await GetSubtaskAsync(ids[0])).Status.Should().Be(SubtaskStatus.Failed);
        var pending = await GetSubtaskAsync(ids[1]);
        pending.Status.Should().Be(SubtaskStatus.Pending,
            "a stopped coordinator must not launch new children after active stop cancellation is observed");
        pending.ChildRunId.Should().BeNull();
        _assembly.Started.Should().Be(0, "stopped dispatch must not hand off to assembly");
    }

    // -----------------------------------------------------------------------
    // Harness
    // -----------------------------------------------------------------------

    private CoordinatorDispatchService BuildDispatch(
        IRunEventStream eventStream,
        double stallTimeoutMinutes = 5)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:SubtaskStallTimeoutMinutes"] =
                    stallTimeoutMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
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
            runOptions: null, autopilot: null, configuration: config, eventStream: eventStream);
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

    private async Task<(int PlanId, List<int> SubtaskIds)> SeedPlanAsync(
        string coordinatorRunId,
        (string Status, string? ChildRunId)[] subtasks)
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

        return (plan.Id, ids);
    }

    private async Task<Subtask> GetSubtaskAsync(int id)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        return await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private async Task SeedCoordinatorRunAsync(string coordinatorRunId, RunStatus status)
    {
        var run = new Run
        {
            Id = RunId.Parse(coordinatorRunId),
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "coordinate",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Coordinator",
        };
        await _runStore.InsertAsync(run);
        if (status != RunStatus.InProgress)
            await _runStore.UpdateStatusAsync(run.Id, status, DateTimeOffset.UtcNow);
    }

    private static void CreateRunEventsTable(string memoryDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={memoryDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "RunEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RunEvents_RunId_Sequence" ON "RunEvents" ("RunId", "Sequence");
            """;
        cmd.ExecuteNonQuery();
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        _memoryConn.Dispose();
        await _runDb.DisposeAsync();

        await Task.Delay(50);
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class RecordingAssembly : ICoordinatorAssembly
    {
        public int Started { get; private set; }
        public void StartAssembly(CoordinatorDispatchContext context) => Started++;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
