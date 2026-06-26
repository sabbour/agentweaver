using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Tests.Helpers;
using Agentweaver.Domain;
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
    private readonly CoordinatorAssemblyService _sut;

    public CoordinatorAssemblyServiceTests()
    {
        _memoryConn = new SqliteConnection("DataSource=:memory:");
        _memoryConn.Open();

        var services = new ServiceCollection();
        services.AddDbContext<MemoryDbContext>(o => o.UseSqlite(_memoryConn));
        services.AddSingleton<ICoordinatorDispatch>(_dispatch);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
            scope.ServiceProvider.GetRequiredService<MemoryDbContext>().Database.EnsureCreated();

        _scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
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
            NullLogger<CoordinatorAssemblyService>.Instance);
    }

    // ── D2 eligibility gate ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAssembly_BlocksAndStops_WhenASubtaskIsIneligible()
    {
        const string coordinatorRunId = "coord-block-1";
        var (workPlanId, subtaskIds) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        await _sut.RunAssemblyAsync(Context(coordinatorRunId), default);

        var types = EventTypes_(coordinatorRunId);
        types.Should().Contain(EventTypes.CoordinatorAssemblyBlocked);
        types.Should().NotContain(EventTypes.CoordinatorAssemblyRaiStarted,
            "an ineligible plan must not proceed to collective RAI");
        _pipeline.IntegrationBuilds.Should().Be(0, "no integration branch is built when blocked");

        var state = await _assemblyStore.GetAsync(workPlanId, default);
        state!.Status.Should().Be(WorkPlanStatus.AssemblyBlocked);
        _streamStore.Get(coordinatorRunId)!.IsCompleted.Should().BeTrue("a blocked assembly stream is terminal");

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

        // The block event is PERSISTED to RunEvents so a page reload replays the same detail.
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

    // ── Happy path: event sequence + node-flip ──────────────────────────────────────────────────

    [Fact]
    public async Task RunAssembly_ApprovedReview_EmitsAssemblySequenceInOrder_AndFlipsNodesToLive()
    {
        const string coordinatorRunId = "coord-happy-1";
        var (workPlanId, _) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
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

        // The pipeline ran exactly one of each collective stage.
        _pipeline.IntegrationBuilds.Should().Be(1);
        _pipeline.Merges.Should().Be(1);
        _pipeline.Scribes.Should().Be(1);

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

    // ── D6 request_changes inference + re-dispatch ──────────────────────────────────────────────

    [Fact]
    public async Task RunAssembly_RequestChanges_InfersAffectedChild_ResetsItToPending_AndRedispatches()
    {
        const string coordinatorRunId = "coord-reject-1";

        // Two independent eligible children with known, distinct touched-files.
        var childA = RunId.New();
        var childB = RunId.New();
        await SeedChildRunAsync(childA, "agentweaver/child-a", DiffTouching("src/a.txt"));
        await SeedChildRunAsync(childB, "agentweaver/child-b", DiffTouching("src/b.txt"));

        var (workPlanId, subtaskIds) = await SeedPlanAsync(coordinatorRunId,
            new[] { SubtaskStatus.AssembleReady, SubtaskStatus.AssembleReady },
            childRunIds: new[] { childA.ToString(), childB.ToString() });
        _streamStore.Create(coordinatorRunId, "alice");
        var subtaskA = subtaskIds[0];
        var subtaskB = subtaskIds[1];

        var run = _sut.RunAssemblyAsync(Context(coordinatorRunId), default);

        await WaitUntilArmedAsync(coordinatorRunId);
        // Feedback references ONLY child A's file → only subtask A (+ dependents) should be re-dispatched.
        _reviewGate.TrySubmit(coordinatorRunId, "alice",
            new AssemblyReviewDecision(Approved: false, RequestChanges: true,
                Feedback: "Please fix the bug in src/a.txt", TargetFiles: null, Reviewer: "alice"))
            .Should().Be(AssemblyReviewSubmitResult.Accepted);

        await run;

        var changes = _streamStore.Get(coordinatorRunId)!.GetSnapshotSince(0).Events
            .Single(e => e.Type == EventTypes.CoordinatorAssemblyChangesRequested);
        // Assembly event payloads are stamped with timestamp_utc and stored as a JsonObject.
        var changesPayload = (System.Text.Json.Nodes.JsonObject)changes.Payload;
        var redispatch = changesPayload["redispatchSubtaskIds"]!.AsArray()
            .Select(n => (int)n!).ToList();
        redispatch.Should().BeEquivalentTo(new[] { subtaskA });

        // Subtask A reset to pending; B's prior result is left intact.
        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == subtaskA)).Status
                .Should().Be(SubtaskStatus.Pending);
            (await db.Subtasks.AsNoTracking().FirstAsync(s => s.Id == subtaskB)).Status
                .Should().Be(SubtaskStatus.AssembleReady);
        }

        // The plan returned to dispatching and re-dispatch was triggered.
        (await _assemblyStore.GetAsync(workPlanId, default))!.Status.Should().Be(WorkPlanStatus.Dispatching);
        _dispatch.StartDispatchCalls.Should().ContainSingle().Which.CoordinatorRunId.Should().Be(coordinatorRunId);
    }

    // ── Terminal coordinator-run status + reason (so the UI never shows a bare "Failed") ──────────

    [Fact]
    public async Task RunAssembly_Blocked_TerminalizesCoordinatorRun_Failed_WithReason()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.Failed });
        _streamStore.Create(coordinatorRunId, "alice");

        await _sut.RunAssemblyAsync(Context(coordinatorRunId), default);

        var run = await _runStore.GetAsync(RunId.Parse(coordinatorRunId), default);
        run!.Status.Should().Be(RunStatus.Failed);
        run.Result.Should().StartWith("assembly_blocked:");
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
    }

    [Fact]
    public async Task RunAssembly_MergeFailed_TerminalizesCoordinatorRun_MergeFailed_WithReason()
    {
        var coordinatorRunId = RunId.New().ToString();
        await SeedCoordinatorRunAsync(coordinatorRunId);
        await SeedPlanAsync(coordinatorRunId, new[] { SubtaskStatus.Completed, SubtaskStatus.AssembleReady });
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

    private static string NodeKind(GraphDescriptor graph, string nodeId) =>
        graph.Nodes.Single(n => n.Id == nodeId).Kind;

    private static string DiffTouching(string path) =>
        $"diff --git a/{path} b/{path}\n--- a/{path}\n+++ b/{path}\n@@ -0,0 +1 @@\n+change\n";

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
        string coordinatorRunId, IReadOnlyList<string> subtaskStatuses, IReadOnlyList<string>? childRunIds = null)
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
        public int Merges;
        public int Scribes;

        /// <summary>When set, <see cref="MergeAsync"/> returns this result instead of a clean merge.</summary>
        public CollectiveMergeResult? MergeOverride;

        /// <summary>When true, <see cref="MergeAsync"/> throws to exercise the unexpected-fault path.</summary>
        public bool MergeThrows;

        public IntegrationBranchResult BuildIntegrationBranch(CollectiveIntegrationRequest request)
        {
            IntegrationBuilds++;
            return IntegrationBranchResult.Success(request.IntegrationBranch, "agg-tree", "aggregate diff");
        }

        public Task<CollectiveRaiResult> RunRaiAsync(CollectiveRaiRequest request, CancellationToken ct) =>
            Task.FromResult(new CollectiveRaiResult(SafetyFlagged: false));

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
