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
}
