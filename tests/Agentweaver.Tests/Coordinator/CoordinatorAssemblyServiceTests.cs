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

        await _steering.SteerAsync(coordinatorRunId, "send", null, "please retry", "alice", cts.Token);
        await WaitForEventAsync(coordinatorRunId, EventTypes.CoordinatorRecovered, cts.Token);

        var events = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events;
        events.Count(e => e.Type == EventTypes.CoordinatorAssemblyBlocked).Should().Be(1,
            "a send message is not a state change and must not re-enter the blocked assembly path");
        _pipeline.IntegrationBuilds.Should().Be(0);

        await _steering.SteerAsync(coordinatorRunId, "stop", null, "", "alice", cts.Token);
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
        var method = typeof(CoordinatorEndpoints).GetMethod(
            "PersistAssemblyReviewDecisionAsync",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("the endpoint helper owns durable assembly review persistence");

        var task = (Task)method!.Invoke(null,
        [
            coordinatorRunId,
            decision,
            _scopeFactory,
            CancellationToken.None,
        ])!;
        await task.ConfigureAwait(false);
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

        public Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct) =>
            Task.FromResult(new CollectiveRaiResult(SafetyFlagged: false));

        public Task<CollectiveGateDecision> RunRubberduckAsync(CollectiveRubberduckRequest request, CancellationToken ct) =>
            Task.FromResult(new CollectiveGateDecision(Approved: true, RequestChanges: false, Feedback: null));

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

        public Task CheckAgentHostCapacityAsync(CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDispatch : ICoordinatorDispatch
    {
        public List<CoordinatorDispatchContext> StartDispatchCalls { get; } = [];
        public void StartDispatch(CoordinatorDispatchContext context) => StartDispatchCalls.Add(context);
        public bool IsDispatchActive(string coordinatorRunId) => false;
    }

    private sealed class TestHostApplicationLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }
}
