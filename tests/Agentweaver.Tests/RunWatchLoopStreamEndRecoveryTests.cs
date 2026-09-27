using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Workflows;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Run = Agentweaver.Domain.Run;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Regression for issue #331: a coordinator CHILD subtask (e.g. a "build" persona that runs
/// npm install/build, starts a dev server, and health-checks a forwarded port) can complete its
/// agent turn successfully — <c>agent.turn.end</c> observed (#242's guard satisfied), post-turn
/// commit succeeds, real files land in the worktree — yet the MAF workflow stream can still end
/// before the trimmed child graph's conditional edge (agent -&gt; child-assemble-ready) produces the
/// terminal <see cref="WorkflowOutputEvent"/>. Previously this collapsed the run to the fragile,
/// uninformative <c>watch_stream_completed_without_terminal_event</c> fallback, discarding VERIFIED
/// real work and cascading into the parent coordinator's `assembly_blocked: ineligible_subtasks`.
///
/// <see cref="RunWatchLoopService.TryRecoverChildAssembleReadyOnStreamEndAsync"/> is the targeted fix:
/// when the watch loop observed a successful <see cref="AgentTurnOutput"/> (TerminalFailureReason
/// null) for the "agent" node on a coordinator CHILD run, and the stream then ends without ever
/// emitting a terminal WorkflowOutputEvent, the watcher now recovers the run as assemble-ready
/// instead of failing it. Root/non-child runs (which have additional RAI/review/merge/scribe stages
/// after the agent turn) are deliberately NOT covered — a successful agent turn there is not
/// sufficient evidence the run is actually done, so they still fall through to the generic fallback.
/// </summary>
[Trait("Category", "ProcessEnvironment")]
public sealed class RunWatchLoopStreamEndRecoveryTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;

    public RunWatchLoopStreamEndRecoveryTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuspendedFanParent_WatchClosesDuringRollout_PreservesPlanAndChildren(bool gracefulShutdown)
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runStore = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();
        var registry = scope.ServiceProvider.GetRequiredService<RunWorkflowRegistry>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var parentId = RunId.New();
        var completedId = RunId.New();
        var runningId = RunId.New();
        var now = DateTimeOffset.UtcNow;
        var projectId = Guid.NewGuid().ToString();

        async Task InsertRunAsync(RunId id, RunStatus status, string? parent = null) =>
            await runStore.InsertAsync(new Run
            {
                Id = id,
                RepositoryPath = Path.GetTempPath(),
                OriginatingBranch = "main",
                ModelSource = ModelSource.GitHubCopilot,
                Task = "Durable fan branch",
                SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
                Status = status,
                StartedAt = now,
                ParentRunId = parent,
            });

        await InsertRunAsync(parentId, RunStatus.AwaitingReview);
        await InsertRunAsync(completedId, RunStatus.AssembleReady, parentId.ToString());
        await InsertRunAsync(runningId, RunStatus.InProgress, parentId.ToString());
        var spec = new OutcomeSpec
        {
            ProjectId = projectId,
            CoordinatorRunId = parentId.ToString(),
            Goal = "Join two branches",
            DesiredOutcome = "Preserve in-flight work through an API rollout",
            Scope = "Fan",
            Assumptions = "No user cancellation",
            Status = "confirmed",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OutcomeSpecs.Add(spec);
        await db.SaveChangesAsync();
        var plan = new WorkPlan
        {
            OutcomeSpecId = spec.Id,
            ProjectId = projectId,
            CoordinatorRunId = parentId.ToString(),
            ParentRunId = parentId.ToString(),
            ParentWorkflowNodeId = "discovery-fan-out",
            ParentResumeState = WorkflowChildWorkResumeStates.Committed,
            Status = "dispatching",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.WorkPlans.Add(plan);
        await db.SaveChangesAsync();
        db.Subtasks.AddRange(
            new Subtask
            {
                WorkPlanId = plan.Id, Title = "Completed", Scope = "Completed branch",
                AssignedAgent = "tank", SelectedModelId = "test", Phase = "execution",
                IsolationStrategy = "worktree", Status = "assemble_ready",
                ChildRunId = completedId.ToString(), CreatedAt = now, UpdatedAt = now,
            },
            new Subtask
            {
                WorkPlanId = plan.Id, Title = "Running", Scope = "Healthy branch",
                AssignedAgent = "tank", SelectedModelId = "test", Phase = "execution",
                IsolationStrategy = "worktree", Status = "running",
                ChildRunId = runningId.ToString(), CreatedAt = now, UpdatedAt = now,
            });
        await db.SaveChangesAsync();

        var checkpointPath = Path.Combine(Path.GetTempPath(), $"watch-rollout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(checkpointPath);
        try
        {
            using var checkpointStore = new FileSystemJsonCheckpointStore(new DirectoryInfo(checkpointPath));
            ExecutorBinding draft = new FunctionExecutor<string, string>("draft", (input, _, _) => input);
            ExecutorBinding gate = RequestPort.Create<string, string>("fan-join");
            var workflow = new WorkflowBuilder(draft).AddEdge(draft, gate).Build()!;
            await using var streamingRun = await InProcessExecution.RunStreamingAsync(
                workflow, "waiting for healthy branch", CheckpointManager.CreateJson(checkpointStore),
                parentId.ToString(), CancellationToken.None);
            await foreach (var evt in streamingRun.WatchStreamAsync(CancellationToken.None))
                if (evt is RequestInfoEvent)
                    break;
            streamingRun.LastCheckpoint.Should().NotBeNull();

            var entry = streamStore.Create(parentId.ToString(), ReviewWebApplicationFactory.OwnerUser);
            using var runCts = new CancellationTokenSource();
            registry.Register(parentId.ToString(), streamingRun, runCts);
            using var rollout = new CancellationTokenSource();
            if (gracefulShutdown)
            {
                using var lifetime = new RolloutLifetime();
                var shutdownWatcher = ActivatorUtilities.CreateInstance<RunWatchLoopService>(
                    scope.ServiceProvider, lifetime);
                shutdownWatcher.StartWatching(
                    parentId.ToString(), streamingRun, entry,
                    ReviewWebApplicationFactory.OwnerUser, runCts.Token);
                await Task.Delay(100);
                lifetime.StopApplication();
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (registry.Get(parentId.ToString()) is not null && DateTimeOffset.UtcNow < deadline)
                    await Task.Delay(20);
                registry.Get(parentId.ToString()).Should().BeNull(
                    "the API watcher must release the local slot during graceful shutdown");
                rollout.Cancel();
            }

            await svc.HandleStreamEndAsync(parentId.ToString(), streamingRun, entry, null, rollout.Token);
            await svc.HandleStreamEndAsync(parentId.ToString(), streamingRun, entry, null, rollout.Token);
            await scope.ServiceProvider.GetRequiredService<WorkflowChildWorkService>()
                .PrepareRestartRecoveryAsync();

            (await runStore.GetAsync(parentId))!.Status.Should().Be(RunStatus.AwaitingReview);
            (await runStore.GetAsync(completedId))!.Status.Should().Be(RunStatus.AssembleReady);
            (await runStore.GetAsync(runningId))!.Status.Should().Be(RunStatus.InProgress);
            var persisted = await db.WorkPlans.AsNoTracking().SingleAsync(p => p.Id == plan.Id);
            persisted.Status.Should().Be("dispatching");
            persisted.ParentResumeState.Should().Be(WorkflowChildWorkResumeStates.Committed);
            (await db.Subtasks.AsNoTracking().Where(s => s.WorkPlanId == plan.Id)
                .OrderBy(s => s.Id).Select(s => new { s.Status, s.ChildRunId }).ToListAsync())
                .Select(s => (s.Status, s.ChildRunId))
                .Should().Equal(
                    ("assemble_ready", completedId.ToString()),
                    ("running", runningId.ToString()));
            entry.IsCompleted.Should().BeFalse();
            entry.HasEventType(EventTypes.RunFailed).Should().BeFalse();
            entry.HasEventType(EventTypes.RunCancelled).Should().BeFalse();
            registry.Get(parentId.ToString()).Should().BeNull();
        }
        finally
        {
            Directory.Delete(checkpointPath, recursive: true);
        }
    }

    private sealed class RolloutLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopping.Token;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    [Fact]
    public async Task ChildStreamEnd_OldLeaseCannotTerminalizeAfterRecoveryTakesOwnership()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var store = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var leases = scope.ServiceProvider.GetRequiredService<IRunLeaseStore>();
        var childId = RunId.New();
        var id = childId.ToString();
        var entry = scope.ServiceProvider.GetRequiredService<RunStreamStore>()
            .Create(id, ReviewWebApplicationFactory.OwnerUser);
        await store.InsertAsync(new Run
        {
            Id = childId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Verified child output",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = RunId.New().ToString(),
            WorktreeBranch = "agentweaver/child",
        });
        var old = await leases.TryClaimAsync(id, "rollout-old", TimeSpan.FromMinutes(5));
        old.Claimed.Should().BeTrue();
        await leases.ReleaseAsync(id, "rollout-old", old.FencingToken);
        var successor = await leases.TryClaimAsync(id, "recovery-new", TimeSpan.FromMinutes(5));
        successor.Claimed.Should().BeTrue();

        var output = new AgentTurnOutput(
            RunId: id, TreeHash: "verified-tree", Diff: "verified diff", StepCount: 1,
            WorktreePath: Path.GetTempPath(), WorktreeBranch: "agentweaver/child",
            RepositoryPath: Path.GetTempPath(), OriginatingBranch: "main",
            ContentSafetyFlagged: false, SubmittingUser: ReviewWebApplicationFactory.OwnerUser,
            AgentName: "tank");
        (await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            id, entry, output, CancellationToken.None,
            new RunLeaseClaim("rollout-old", old.FencingToken))).Should().BeFalse();
        (await store.GetAsync(childId))!.Status.Should().Be(RunStatus.InProgress);
        entry.HasEventType(EventTypes.RunAssembleReady).Should().BeFalse();
        entry.IsCompleted.Should().BeFalse();

        (await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            id, entry, output, CancellationToken.None,
            new RunLeaseClaim("recovery-new", successor.FencingToken))).Should().BeTrue();
        (await store.GetAsync(childId))!.Status.Should().Be(RunStatus.AssembleReady);
        entry.GetSnapshotSince(0).Events.Count(e => e.Type == EventTypes.RunAssembleReady)
            .Should().Be(1);
        (await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            id, entry, output, CancellationToken.None,
            new RunLeaseClaim("rollout-old", old.FencingToken))).Should().BeFalse();
        entry.GetSnapshotSince(0).Events.Count(e => e.Type == EventTypes.RunAssembleReady)
            .Should().Be(1);
        await leases.ReleaseAsync(id, "recovery-new", successor.FencingToken);
    }

    [Fact]
    public async Task ChildRun_SuccessfulAgentTurn_StreamEndsWithoutTerminal_RecoversAsAssembleReady()
    {
        var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runStore = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();

        var runId = RunId.New();
        var runIdText = runId.ToString();

        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Build the SplitTab web prototype.",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Hicks",
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "10",
            WorktreePath = Path.GetTempPath(),
            WorktreeBranch = "agentweaver/child-branch",
        }, CancellationToken.None);

        var entry = streamStore.Create(runIdText, ReviewWebApplicationFactory.OwnerUser);

        // Real, verified work: a non-empty diff and a produced tree hash, TerminalFailureReason
        // null (the agent turn — and the post-turn commit — genuinely succeeded).
        var successfulAgentTurnOutput = new AgentTurnOutput(
            RunId: runIdText,
            TreeHash: "treehash-verified-abc123",
            Diff: "diff --git a/prototype/src/App.jsx b/prototype/src/App.jsx\n+// SplitTab prototype",
            StepCount: 25,
            WorktreePath: Path.GetTempPath(),
            WorktreeBranch: "agentweaver/child-branch",
            RepositoryPath: Path.GetTempPath(),
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            SubmittingUser: ReviewWebApplicationFactory.OwnerUser,
            AgentName: "Hicks");

        var recovered = await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            runIdText, entry, successfulAgentTurnOutput, CancellationToken.None);

        recovered.Should().BeTrue(
            "a successful agent turn on a coordinator child run must recover as assemble-ready " +
            "instead of falling through to the generic stream-end fallback");

        var run = await runStore.GetAsync(runId, CancellationToken.None);
        run.Should().NotBeNull();
        run!.Status.Should().Be(RunStatus.AssembleReady,
            "verified real work must never be discarded as watch_stream_completed_without_terminal_event");
        run.Result.Should().NotBe("watch_stream_completed_without_terminal_event");
        run.TreeHash.Should().Be("treehash-verified-abc123");

        entry.HasEventType(EventTypes.RunAssembleReady).Should().BeTrue(
            "the coordinator's assembly wave reads run.assemble_ready to collect this child's output");
        entry.IsCompleted.Should().BeTrue("the recovered terminal completes the stream");
    }

    [Fact]
    public async Task ChildRun_DuplicateProviderAssembleReady_LinksCanonicalSequenceForRestart()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runStore = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();
        var eventStream = scope.ServiceProvider.GetRequiredService<IRunEventStream>();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

        var runId = RunId.New();
        var runIdText = runId.ToString();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "recover duplicate provider terminal",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Hicks",
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "10",
            WorktreePath = Path.GetTempPath(),
            WorktreeBranch = "agentweaver/child-branch",
        });
        var entry = streamStore.Create(runIdText, ReviewWebApplicationFactory.OwnerUser);
        var canonicalPayload = new
        {
            treeHash = "provider-tree",
            worktreeBranch = "agentweaver/child-branch",
            diff = "provider diff",
            stepCount = 3,
        };
        var canonical = await eventStream.AppendTerminalOutcomeAsync(
            runIdText,
            TerminalRunOutcome.Create(
                RunStatus.AssembleReady,
                EventTypes.RunAssembleReady,
                canonicalPayload,
                DateTimeOffset.UtcNow,
                (await runStore.GetAsync(runId, CancellationToken.None))!.LifecycleGeneration));
        entry.RecordDurable(canonical);

        var successfulAgentTurnOutput = new AgentTurnOutput(
            RunId: runIdText,
            TreeHash: "provider-tree",
            Diff: "provider diff",
            StepCount: 3,
            WorktreePath: Path.GetTempPath(),
            WorktreeBranch: "agentweaver/child-branch",
            RepositoryPath: Path.GetTempPath(),
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            SubmittingUser: ReviewWebApplicationFactory.OwnerUser,
            AgentName: "Hicks");

        (await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            runIdText, entry, successfulAgentTurnOutput, CancellationToken.None)).Should().BeTrue();
        (await runStore.GetUnprojectedTerminalOutcomesAsync()).Should().BeEmpty();

        var persisted = await eventStream.GetPersistedEventsAsync(runIdText);
        persisted.Where(evt => evt.Type == EventTypes.RunAssembleReady).Should().ContainSingle()
            .Which.Sequence.Should().Be(canonical.Sequence);

        var replayed = new List<RunEvent>();
        await foreach (var evt in new SqliteRunEventStream(config).SubscribeAsync(runIdText))
            replayed.Add(evt);
        replayed.Where(evt => evt.Type == EventTypes.RunAssembleReady).Should().ContainSingle()
            .Which.Sequence.Should().Be(canonical.Sequence);
    }

    [Fact]
    public async Task ChildRun_NoSuccessfulAgentTurnObserved_DoesNotRecover()
    {
        var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runStore = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();

        var runId = RunId.New();
        var runIdText = runId.ToString();

        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Build the SplitTab web prototype.",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Hicks",
            ParentRunId = RunId.New().ToString(),
            SubtaskId = "10",
            WorktreePath = Path.GetTempPath(),
            WorktreeBranch = "agentweaver/child-branch",
        }, CancellationToken.None);

        var entry = streamStore.Create(runIdText, ReviewWebApplicationFactory.OwnerUser);

        // No successful AgentTurnOutput was ever observed for the "agent" node (e.g. the stream
        // truly ended before the turn made progress) — there is nothing safe to recover.
        var recovered = await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            runIdText, entry, lastSuccessfulAgentTurnOutput: null, CancellationToken.None);

        recovered.Should().BeFalse(
            "with no observed successful agent turn output, the watcher must fall through to the " +
            "generic watch_stream_completed_without_terminal_event fallback rather than fabricate success");
    }

    [Fact]
    public async Task RootRun_SuccessfulAgentTurn_StreamEndsWithoutTerminal_DoesNotRecover()
    {
        var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runStore = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();

        var runId = RunId.New();
        var runIdText = runId.ToString();

        // A ROOT run (no ParentRunId) — the full pipeline has RAI/review/merge/scribe stages after
        // the agent turn, so a successful agent turn alone is NOT sufficient evidence the run is done.
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Build the SplitTab web prototype.",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "Hicks",
            WorktreePath = Path.GetTempPath(),
            WorktreeBranch = "agentweaver/root-branch",
        }, CancellationToken.None);

        var entry = streamStore.Create(runIdText, ReviewWebApplicationFactory.OwnerUser);

        var successfulAgentTurnOutput = new AgentTurnOutput(
            RunId: runIdText,
            TreeHash: "treehash-verified-root",
            Diff: "diff --git a/prototype/src/App.jsx b/prototype/src/App.jsx\n+// SplitTab prototype",
            StepCount: 25,
            WorktreePath: Path.GetTempPath(),
            WorktreeBranch: "agentweaver/root-branch",
            RepositoryPath: Path.GetTempPath(),
            OriginatingBranch: "main",
            ContentSafetyFlagged: false,
            SubmittingUser: ReviewWebApplicationFactory.OwnerUser,
            AgentName: "Hicks");

        var recovered = await svc.TryRecoverChildAssembleReadyOnStreamEndAsync(
            runIdText, entry, successfulAgentTurnOutput, CancellationToken.None);

        recovered.Should().BeFalse(
            "root/full-pipeline runs have stages after the agent turn (RAI/review/merge/scribe); " +
            "recovery is scoped to coordinator CHILD runs whose graph ends at child-assemble-ready");
    }
}
