using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using RunStatus = Agentweaver.Domain.RunStatus;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Focused unit tests for RunWatchLoopService.HandleTerminalOutputAsync return values.
/// Verifies that genuinely terminal outputs return true (triggering cleanup) while
/// a leaked "blocked" output returns false (preserving registry + checkpoints).
/// </summary>
[Trait("Category", "ProcessEnvironment")]
public sealed class RunWatchLoopTerminalOutputTests : IClassFixture<ReviewWebApplicationFactory>
{
    private readonly ReviewWebApplicationFactory _factory;

    public RunWatchLoopTerminalOutputTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HandleTerminalOutput_MergedStatus_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "merged", "merged:abc123"), "terminal-merge");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a merged output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_MergeFailedStatus_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "merge_failed", "conflict"), "terminal-merge");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a merge_failed output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_BlockedStatus_ReturnsFalse()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "blocked", "dirty_working_tree"), "terminal-merge");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeFalse("a blocked output is non-terminal; run must remain recoverable");
    }

    [Fact]
    public async Task HandleTerminalOutput_BlockedStatus_DoesNotCompleteStream()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new MergeOutput(runId, "blocked", "dirty_working_tree"), "terminal-merge");

        await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        entry.IsCompleted.Should().BeFalse(
            "a blocked output must not mark the stream as completed so clients stay connected");
    }

    [Fact]
    public async Task HandleTerminalOutput_NoChanges_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new NoChangesOutput(runId), "terminal-no-op");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a no_changes output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_Declined_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new DeclinedOutput(runId), "terminal-declined");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a declined output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_ContentSafetyFailed_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new ContentSafetyFailedOutput(runId), "terminal-safety-failed");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a content_safety output is genuinely terminal");
    }

    [Fact]
    public async Task HandleTerminalOutput_AssembleReady_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new AssembleReadyOutput(runId, "agentweaver/child-branch", "treehash123", "diff --git a b", HasChanges: true, StepCount: 4),
            "child-assemble-ready");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a child assemble_ready output is genuinely terminal (no per-child review/merge/scribe)");
    }

    [Fact]
    public async Task HandleTerminalOutput_AssembleReady_EmptyDiff_ReturnsTrue()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new AssembleReadyOutput(runId, "agentweaver/child-branch", "treehash456", "", HasChanges: false, StepCount: 1),
            "child-assemble-ready");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("an empty-diff child still terminalizes cleanly as assemble_ready");
    }

    [Fact]
    public async Task HandleTerminalOutput_ChildTurnFailed_ReturnsTrue_AndCompletesStream()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new ChildTurnFailedOutput(runId, "commit_failed_persistent", "exception=InvalidOperationException: index.lock held | attempt1: cleared=false"),
            "child-turn-failed");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue("a child-turn-failed output is a genuinely terminal VISIBLE failure");
        entry.HasEventType(EventTypes.RunFailed).Should().BeTrue(
            "the persistent commit fault must surface as a visible run.failed event (never a hung stream)");
        entry.IsCompleted.Should().BeTrue("the failure terminal completes the stream (single terminal emission)");
    }

    [Fact]
    public async Task HandleTerminalOutput_StructuredShellTimeout_PersistsRealFailureReason()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new ChildTurnFailedOutput(
                runId,
                "shell_execution_timeout",
                Evidence: "exception=WorkflowAgentInfrastructureException",
                Message: "Shell execution exceeded its hard deadline and was terminated.",
                Retryable: true),
            "child-turn-failed");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue();
        var failed = entry.GetSnapshotSince(0).Events.Single(e => e.Type == EventTypes.RunFailed);
        var payload = JsonSerializer.SerializeToElement(failed.Payload);
        payload.GetProperty("errorCode").GetString().Should().Be("shell_execution_timeout",
            "the watcher must not flatten the typed failure to child_executor_failed:agent-turn");
    }

    [Fact]
    public async Task HandleTerminalOutput_RootStructuredShellTimeout_PersistsRealFailureReason()
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        var woe = new WorkflowOutputEvent(
            new AgentTurnFailedOutput(
                runId,
                "shell_execution_timeout",
                Evidence: "exception=WorkflowAgentInfrastructureException",
                Message: "Shell execution exceeded its hard deadline and was terminated.",
                Retryable: true),
            "terminal-turn-failed");

        var result = await svc.HandleTerminalOutputAsync(runId, woe, entry, CancellationToken.None);

        result.Should().BeTrue();
        var failed = entry.GetSnapshotSince(0).Events.Single(e => e.Type == EventTypes.RunFailed);
        var payload = JsonSerializer.SerializeToElement(failed.Payload);
        payload.GetProperty("errorCode").GetString().Should().Be("shell_execution_timeout",
            "root failures must not flatten to watch_stream_completed_without_terminal_event");
        entry.IsCompleted.Should().BeTrue();
    }

    [Theory]
    [InlineData("merged")]
    [InlineData("completed")]
    [InlineData("merge_failed")]
    [InlineData("no_changes")]
    [InlineData("declined")]
    [InlineData("content_safety")]
    [InlineData("failed")]
    public async Task TerminalOutput_LeaseTransfersAfterPrecheck_CannotCommitOrPublish(string outcome)
    {
        var (_, entry, runId) = CreateServiceAndEntry();
        using var scope = _factory.Services.CreateScope();
        var svc = ActivatorUtilities.CreateInstance<RunWatchLoopService>(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<IRunStore>();
        var leases = scope.ServiceProvider.GetRequiredService<IRunLeaseStore>();
        var old = await leases.TryClaimAsync(runId, "old-watcher", TimeSpan.FromMinutes(5));
        old.Claimed.Should().BeTrue();
        var successorToken = 0L;
        svc.BeforeTerminalMutationOverride = async () =>
        {
            await leases.ReleaseAsync(runId, "old-watcher", old.FencingToken);
            await Task.Delay(5);
            var successor = await leases.TryClaimAsync(runId, "successor", TimeSpan.FromMinutes(5));
            successor.Claimed.Should().BeTrue();
            successorToken = successor.FencingToken;
        };

        var output = outcome switch
        {
            "merged" => (object)new MergeOutput(runId, "merged", "sha"),
            "completed" => new MergeOutput(runId, "completed", "done"),
            "merge_failed" => new MergeOutput(runId, "merge_failed", "conflict"),
            "no_changes" => new NoChangesOutput(runId),
            "declined" => new DeclinedOutput(runId),
            "content_safety" => new ContentSafetyFailedOutput(runId),
            _ => new AgentTurnFailedOutput(runId, "agent_error"),
        };
        var changed = await svc.HandleTerminalOutputAsync(
            runId, new WorkflowOutputEvent(output, "terminal"), entry, CancellationToken.None,
            new RunLeaseClaim("old-watcher", old.FencingToken));

        changed.Should().BeFalse();
        (await store.GetAsync(RunId.Parse(runId)))!.Status.Should().Be(RunStatus.InProgress);
        entry.IsCompleted.Should().BeFalse();
        entry.GetSnapshotSince(0).Events.Should().NotContain(e =>
            e.Type == EventTypes.RunFailed || e.Type == EventTypes.RunCompleted
            || e.Type == EventTypes.MergeCompleted || e.Type == EventTypes.MergeFailed
            || e.Type == EventTypes.ReviewDeclined || e.Type == EventTypes.ReviewApproved
            || e.Type == EventTypes.WorkflowStep);
        await leases.ReleaseAsync(runId, "successor", successorToken);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    public async Task TerminalOutput_CurrentLeaseCommitsAndCompletes(string outcome)
    {
        var (svc, entry, runId) = CreateServiceAndEntry();
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRunStore>();
        var leases = scope.ServiceProvider.GetRequiredService<IRunLeaseStore>();
        var owner = await leases.TryClaimAsync(runId, "current-watcher", TimeSpan.FromMinutes(5));
        owner.Claimed.Should().BeTrue();
        var output = outcome == "completed"
            ? (object)new MergeOutput(runId, "completed", "done")
            : new AgentTurnFailedOutput(runId, "agent_error");
        (await svc.HandleTerminalOutputAsync(runId, new WorkflowOutputEvent(output, "terminal"),
            entry, CancellationToken.None, new RunLeaseClaim("current-watcher", owner.FencingToken)))
            .Should().BeTrue();
        (await store.GetAsync(RunId.Parse(runId)))!.Status.Should().Be(
            outcome == "completed" ? RunStatus.Completed : RunStatus.Failed);
        entry.IsCompleted.Should().BeTrue();
        await leases.ReleaseAsync(runId, "current-watcher", owner.FencingToken);
    }

    [Fact]
    public async Task GenericFailure_HandoffDuringMutation_CannotFailOrCloseSuccessorStream()
    {
        var (_, entry, runId) = CreateServiceAndEntry();
        using var scope = _factory.Services.CreateScope();
        var svc = ActivatorUtilities.CreateInstance<RunWatchLoopService>(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<IRunStore>();
        var leases = scope.ServiceProvider.GetRequiredService<IRunLeaseStore>();
        var old = await leases.TryClaimAsync(runId, "old-watcher", TimeSpan.FromMinutes(5));
        var successorToken = 0L;
        svc.BeforeTerminalMutationOverride = async () =>
        {
            await leases.ReleaseAsync(runId, "old-watcher", old.FencingToken);
            await Task.Delay(5);
            var successor = await leases.TryClaimAsync(runId, "new-watcher", TimeSpan.FromMinutes(5));
            successor.Claimed.Should().BeTrue();
            successorToken = successor.FencingToken;
        };

        (await svc.FailRunSafeAsync(runId, entry, "watch_loop_error",
            new RunLeaseClaim("old-watcher", old.FencingToken))).Should().BeFalse();
        (await store.GetAsync(RunId.Parse(runId)))!.Status.Should().Be(RunStatus.InProgress);
        entry.IsCompleted.Should().BeFalse();
        entry.HasEventType(EventTypes.RunFailed).Should().BeFalse();
        await leases.ReleaseAsync(runId, "new-watcher", successorToken);
    }

    private (RunWatchLoopService Service, RunStreamEntry Entry, string RunId) CreateServiceAndEntry()
    {
        var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<RunWatchLoopService>();
        var runId = RunId.New().ToString();
        var runStore = scope.ServiceProvider.GetRequiredService<IRunStore>();
        runStore.InsertAsync(new Agentweaver.Domain.Run
        {
            Id = RunId.Parse(runId),
            RepositoryPath = "test",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "terminal output test",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = Agentweaver.Domain.RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        }).GetAwaiter().GetResult();
        var streamStore = scope.ServiceProvider.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Create(runId, ReviewWebApplicationFactory.OwnerUser);
        return (svc, entry, runId);
    }
}
