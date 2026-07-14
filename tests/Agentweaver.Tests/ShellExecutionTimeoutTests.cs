using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentTools;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class ShellExecutionTimeoutTests
{
    [Fact]
    public void Tracker_ObservesApprovedShell_UntilMatchingCompletion()
    {
        using var tracker = new ShellExecutionTracker();

        tracker.TryStartObservedExecution(
            "shell-call-254",
            "command-hash-254",
            TimeSpan.FromMinutes(30)).Should().BeTrue();
        tracker.ActiveExecution!.ToolCallId.Should().Be("shell-call-254");
        tracker.CompleteObservedExecution("different-call").Should().BeFalse();
        tracker.ActiveExecution.Should().NotBeNull();
        tracker.CompleteObservedExecution("shell-call-254").Should().BeTrue();
        tracker.ActiveExecution.Should().BeNull();
    }

    [Fact]
    public void Tracker_FencingTimeout_ReleasesMatchingSlot_AndRejectsStaleCallbacks()
    {
        using var tracker = new ShellExecutionTracker();
        var firstGeneration = tracker.BeginObservedTurn();
        tracker.TryStartObservedExecution("shell-one", "hash-one", TimeSpan.FromMinutes(30), firstGeneration)
            .Should().BeTrue();
        var timedOut = tracker.ActiveExecution!;

        tracker.TryBeginObservedTermination(timedOut).Should().BeTrue();
        tracker.TryStartObservedExecution("shell-two", "hash-two", TimeSpan.FromMinutes(30), firstGeneration)
            .Should().BeFalse("Terminating must retain the single active slot");
        tracker.FenceObservedExecution(timedOut).Should().BeTrue();
        tracker.ActiveExecution.Should().BeNull();
        tracker.TryStartObservedExecution("late-shell", "hash-late", TimeSpan.FromMinutes(30), firstGeneration)
            .Should().BeFalse("the timed-out generation is fenced");

        var secondGeneration = tracker.BeginObservedTurn();
        tracker.TryStartObservedExecution("shell-two", "hash-two", TimeSpan.FromMinutes(30), secondGeneration)
            .Should().BeTrue();
        tracker.CompleteObservedExecution("shell-one", firstGeneration).Should().BeFalse(
            "an old completion must not clear a new shell");
        tracker.ActiveExecution!.ToolCallId.Should().Be("shell-two");
    }

    [Fact]
    public async Task HardTimeout_EmitsStructuredRunFailed_AndTerminatesProcessTree()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(),
            new NullGitHubTokenStore(),
            new FixedInstallationScopeStub());
        await using var agent = new CopilotAIAgent(
            factory,
            new FixedInstallationScopeStub(),
            executor,
            new StubPolicyStore(),
            new InMemoryShellApprovalStore(),
            new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance);
        var events = Channel.CreateUnbounded<RunEvent>();
        var terminated = false;
        agent.SetTurnStreamWriter(events.Writer);
        agent.ShellTimeoutTerminator = () =>
        {
            terminated = true;
            return Task.CompletedTask;
        };
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-31);
        var snapshot = new ShellExecutionSnapshot(
            "shell-call-254",
            "command-hash-254",
            startedAt,
            startedAt.AddMinutes(30));

        await agent.HandleShellExecutionTimeoutAsync(snapshot);

        terminated.Should().BeTrue(
            "the production terminator force-stops the Copilot CLI process and its shell children");
        var failed = await events.Reader.ReadAsync();
        failed.Type.Should().Be(EventTypes.RunFailed);
        var payload = JsonSerializer.SerializeToElement(failed.Payload);
        payload.GetProperty("errorCode").GetString().Should().Be("shell_execution_timeout");
        payload.GetProperty("retryable").GetBoolean().Should().BeTrue();
        payload.GetProperty("message").GetString().Should().Contain("terminated");
    }

    [Fact]
    public async Task HardTimeout_TerminationFailure_StillFencesAndReleasesMatchingShell()
    {
        var executor = SandboxExecutorFactory.CreatePassthrough();
        var factory = new GitHubCopilotClientFactory(
            new ConfigurationBuilder().Build(),
            new NullGitHubTokenStore(),
            new FixedInstallationScopeStub());
        await using var agent = new CopilotAIAgent(
            factory, new FixedInstallationScopeStub(), executor, new StubPolicyStore(),
            new InMemoryShellApprovalStore(), new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance);
        using var tracker = new ShellExecutionTracker();
        agent.ShellExecutionTrackerForTesting = tracker;
        var generation = tracker.BeginObservedTurn();
        tracker.TryStartObservedExecution("shell-failure", "hash", TimeSpan.FromMinutes(30), generation)
            .Should().BeTrue();
        var snapshot = tracker.ActiveExecution!;
        agent.ShellTimeoutTerminator = () => Task.FromException(new InvalidOperationException("force-stop failed"));

        await agent.HandleShellExecutionTimeoutAsync(snapshot);

        tracker.ActiveExecution.Should().BeNull();
        tracker.TryStartObservedExecution("late", "late-hash", TimeSpan.FromMinutes(30), generation)
            .Should().BeFalse("the generation remains fenced after a failed termination");
    }
}
