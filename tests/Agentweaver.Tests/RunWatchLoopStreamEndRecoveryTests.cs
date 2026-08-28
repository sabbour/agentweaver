using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

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
