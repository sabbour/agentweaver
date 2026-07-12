using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Structural regression for the in-place steering revision wedge (v0.9.12-rc1).
///
/// The coordinator CHILD pipeline (agent -> child-assemble-ready) has no failure->terminal edge, so
/// an executor throw halts the MAF workflow with NO WorkflowOutputEvent. Previously the watch loop
/// only emitted a failed step for an <see cref="Microsoft.Agents.AI.Workflows.ExecutorFailedEvent"/>
/// and let the stream simply END, so the child was failed via the fragile, uninformative
/// `watch_stream_completed_without_terminal_event` fallback (or hung).
///
/// The structural fix terminalizes a CHILD run as a VISIBLE Failure the moment an executor fails, so
/// the watcher ALWAYS produces a terminal (never a hung stream / generic reason) and the subtask is
/// marked Failed — feeding the coordinator's conscious dispatch_fresh fallback.
///
/// This test drives a REAL child workflow (RunWorkflowFactory.StartAsync isChild:true) whose agent
/// turn throws, watches it through the REAL RunWatchLoopService, and asserts the child terminalizes
/// Failed with an informative `child_executor_failed:*` reason — never
/// `watch_stream_completed_without_terminal_event`.
/// </summary>
public sealed class RunWatchLoopChildExecutorFailureTests
{
    [Fact]
    public async Task ChildRun_AgentExecutorThrows_TerminalizesVisibleFailure_NotHungStream()
    {
        await using var factory = new ReviewWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IWorkflowAgentFactory));
                    if (descriptor is not null)
                        services.Remove(descriptor);
                    services.AddSingleton<IWorkflowAgentFactory>(new ThrowingWorkerAgentFactory());
                });
            });

        var services = factory.Services;
        var runStore = services.GetRequiredService<SqliteRunStore>();
        var streamStore = services.GetRequiredService<RunStreamStore>();
        var workflowFactory = services.GetRequiredService<RunWorkflowFactory>();
        var watchLoop = services.GetRequiredService<RunWatchLoopService>();

        var parentRunId = RunId.New().ToString();
        var childRunId = RunId.New();
        var runIdStr = childRunId.ToString();

        // A coordinator CHILD run (ParentRunId set), InProgress so it can transition to a terminal.
        await runStore.InsertAsync(new Run
        {
            Id = childRunId,
            RepositoryPath = Path.GetTempPath(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Address the review feedback.",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            AgentName = "morpheus",
            ParentRunId = parentRunId,
            SubtaskId = "7",
            WorktreePath = Path.GetTempPath(),
            WorktreeBranch = "agentweaver/child-branch",
        }, CancellationToken.None);

        var entry = streamStore.Create(runIdStr, ReviewWebApplicationFactory.OwnerUser);

        var input = new AgentTurnInput(
            RunId: runIdStr,
            Task: "Address the review feedback.",
            WorktreePath: Path.GetTempPath(),
            WorktreeBranch: "agentweaver/child-branch",
            RepositoryPath: Path.GetTempPath(),
            OriginatingBranch: "main",
            ModelSource: ModelSource.GitHubCopilot.ToApiString(),
            ModelId: "claude-sonnet-5",
            SubmittingUser: ReviewWebApplicationFactory.OwnerUser,
            IsRevision: true);

        var streamingRun = await workflowFactory.StartAsync(input, runIdStr, CancellationToken.None, isChild: true);
        watchLoop.StartWatching(runIdStr, streamingRun, entry, ReviewWebApplicationFactory.OwnerUser, CancellationToken.None);

        // Poll until the child reaches a terminal state (the throw should fail it within ~1s).
        Run? run = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            run = await runStore.GetAsync(childRunId, CancellationToken.None);
            if (run is not null && run.Status == RunStatus.Failed)
                break;
            await Task.Delay(100);
        }

        run.Should().NotBeNull();
        run!.Status.Should().Be(RunStatus.Failed,
            "an executor failure on the trimmed child pipeline must terminalize the run as a visible Failure");
        run.Result.Should().NotBeNull();
        run.Result.Should().StartWith("child_executor_failed:",
            "the visible terminal reason must name the child executor failure, not the fragile stream-end fallback");
        run.Result.Should().NotBe("watch_stream_completed_without_terminal_event",
            "a child executor failure must NEVER surface as the uninformative hung-stream fallback");
    }

    private sealed class ThrowingWorkerAgentFactory : IWorkflowAgentFactory
    {
        public IWorkflowTurnAgent CreateWorkerAgent() => new ThrowingTurnAgent();
        public IWorkflowTurnAgent CreateRaiAgent() => new ThrowingTurnAgent();
        public IWorkflowTurnAgent CreateRubberduckAgent() => new ThrowingTurnAgent();
        public IWorkflowTurnAgent CreateBuildTestAgent() => new ThrowingTurnAgent();
        public IWorkflowTurnAgent CreateScribeAgent() => new ThrowingTurnAgent();
    }

    private sealed class ThrowingTurnAgent : IWorkflowTurnAgent
    {
        public Task SetupAsync(
            string workingDirectory,
            string repositoryPath,
            string runId,
            string? modelId,
            string? systemPromptContext,
            ChannelWriter<RunEvent>? streamWriter,
            string? projectId,
            string? agentName,
            string? apiBaseUrl,
            string? apiKey,
            CancellationToken ct,
            string? userId = null) => Task.CompletedTask;

        // A non-content-safety throw: AgentTurnExecutor emits a "failed" step and rethrows, which
        // MAF surfaces as ExecutorFailedEvent (the structural failure this test exercises).
        public Task<string> RunTurnAsync(string task, bool isRevision, CancellationToken ct) =>
            throw new InvalidOperationException("simulated agent turn failure (transient runtime error)");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
