using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Threading.Channels;

namespace Agentweaver.Tests.Assistant;

public sealed class RemoteOperatorAssistantAgentTests
{
    [Fact]
    public async Task RunTurn_LaunchesAgentHostWithCurrentMcpBrokerToken()
    {
        var lifecycle = new RecordingPodLifecycle();
        var agent = NewAgent(lifecycle);

        await RunUntilEndpointFailureAsync(agent, Request(
            "conversation-1", "broker-token-v1", _ => Task.FromResult("broker-token-v1-renewed")));
        await RunUntilEndpointFailureAsync(agent, Request(
            "conversation-2", "broker-token-v2", _ => Task.FromResult("broker-token-v2-renewed")));

        lifecycle.Launches.Select(launch => launch.Context.McpBrokerToken).Should().Equal(
            ["broker-token-v1", "broker-token-v2"],
            "each turn must propagate only its short-lived MCP broker token");
        lifecycle.Launches.Should().OnlyContain(launch =>
            launch.Context.Purpose == Agentweaver.Domain.AgentHostPurpose.OperatorAssistant);
        lifecycle.Refreshes.Should().Equal(
            [("conversation-1", "broker-token-v1-renewed"), ("conversation-2", "broker-token-v2-renewed")],
            "each token must be renewed after pod launch and immediately before model/MCP use");
    }

    [Fact]
    public async Task RunTurn_ReleasesAgentHostPod_WhenTheTurnFails()
    {
        // A conversation HOLDS its pod between successful turns (that is what removes the per-turn
        // cold start), but a turn that FAILED may have left the pod half-configured or wedged, so it
        // must never be carried into the next message.
        var lifecycle = new RecordingPodLifecycle();
        var agent = NewAgent(lifecycle);

        await RunUntilEndpointFailureAsync(agent, Request("conversation-failed", "broker-token-v1"));

        lifecycle.Releases.Should().Equal(["conversation-failed"],
            "a failed operator turn must give its AgentHost pod back exactly once");
    }

    [Fact]
    public async Task RunTurn_DoesNotReleaseAgentHostPod_WhenTheLaunchItselfFails()
    {
        // Launch failures are cleaned up by LaunchAgentHostPodAsync itself (it deletes any claim it
        // created), so this agent must not issue a second, redundant release for a pod it never got.
        var lifecycle = new RecordingPodLifecycle { FailLaunch = true };
        var agent = NewAgent(lifecycle);

        var act = () => agent.RunTurnAsync(Request("conversation-launch-failed", "t"), sink: null, CancellationToken.None);
        await act.Should().ThrowAsync<AgentProviderException>();

        lifecycle.Releases.Should().BeEmpty();
    }

    [Fact]
    public void ClassifyOrWrap_WhenProxyReportsCopilotAuthFailure_PreservesProviderDetails()
    {
        var classified = RemoteOperatorAssistantAgent.ClassifyOrWrap(
            new WorkflowAgentInfrastructureException(
                "github_copilot_auth_required",
                "Run run-auth-1: GitHub Copilot is not authorized for this user. Sign in with a Copilot-entitled GitHub account and retry.",
                isRetryable: false),
            "run-auth-1",
            "Operator assistant turn failed on the AgentHost pod");

        classified.Should().BeOfType<AgentProviderException>();
        var provider = (AgentProviderException)classified;
        provider.ErrorCode.Should().Be("github_copilot_auth_required");
        provider.FailureKind.Should().Be(AgentProviderFailureKind.Authorization);
        provider.IsRetryable.Should().BeFalse();
        provider.UserMessage.Should().Contain("not authorized");
    }

    [Fact]
    public void ClassifyOrWrap_WhenProxyReportsCopilotRateLimit_PreservesProviderDetails()
    {
        var classified = RemoteOperatorAssistantAgent.ClassifyOrWrap(
            new WorkflowAgentInfrastructureException(
                "github_copilot_rate_limited",
                "Run run-rate-1: GitHub Copilot rate-limited the model request. Retry after the provider limit resets.",
                isRetryable: true),
            "run-rate-1",
            "Operator assistant turn failed on the AgentHost pod");

        classified.Should().BeOfType<AgentProviderException>();
        var provider = (AgentProviderException)classified;
        provider.ErrorCode.Should().Be("github_copilot_rate_limited");
        provider.FailureKind.Should().Be(AgentProviderFailureKind.RateLimited);
        provider.IsRetryable.Should().BeTrue();
    }

    private static RemoteOperatorAssistantAgent NewAgent(RecordingPodLifecycle lifecycle) =>
        new(
            new MissingEndpointResolver(),
            new PodNameRegistry(),
            new ThrowingHttpClientFactory(),
            NullLoggerFactory.Instance,
            Options.Create(new RemoteAgentProxyOptions()),
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Agentweaver:RemoteApiBaseUrl"] = "http://agentweaver-api:8080",
                })
                .Build(),
            null!,
            NullLogger<RemoteOperatorAssistantAgent>.Instance,
            lifecycle);

    [Fact]
    public async Task RunTurn_WithoutMcpBrokerToken_FailsBeforeAgentHostLaunch()
    {
        var lifecycle = new RecordingPodLifecycle();
        var agent = new RemoteOperatorAssistantAgent(
            new MissingEndpointResolver(),
            new PodNameRegistry(),
            new ThrowingHttpClientFactory(),
            NullLoggerFactory.Instance,
            Options.Create(new RemoteAgentProxyOptions()),
            new ConfigurationBuilder().Build(),
            null!,
            NullLogger<RemoteOperatorAssistantAgent>.Instance,
            lifecycle);

        var act = () => agent.RunTurnAsync(Request("conversation-missing-token", ""), null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MCP broker token*");
        lifecycle.Launches.Should().BeEmpty();
    }

    private static async Task RunUntilEndpointFailureAsync(
        RemoteOperatorAssistantAgent agent,
        OperatorAssistantRequest request)
    {
        var act = () => agent.RunTurnAsync(request, sink: null, CancellationToken.None);
        await act.Should().ThrowAsync<AgentProviderException>()
            .Where(ex => ex.ErrorCode == "agenthost_unavailable");
    }

    [Fact]
    public async Task RunTurn_WhenPostLaunchRenewalFails_FailsExplicitly()
    {
        var lifecycle = new RecordingPodLifecycle();
        var agent = new RemoteOperatorAssistantAgent(
            new MissingEndpointResolver(),
            new PodNameRegistry(),
            new ThrowingHttpClientFactory(),
            NullLoggerFactory.Instance,
            Options.Create(new RemoteAgentProxyOptions()),
            new ConfigurationBuilder().Build(),
            null!,
            NullLogger<RemoteOperatorAssistantAgent>.Instance,
            lifecycle);

        var act = () => agent.RunTurnAsync(
            Request(
                "conversation-renewal-failure",
                "initial-token",
                _ => Task.FromException<string>(new InvalidOperationException("issuer unavailable"))),
            null,
            CancellationToken.None);

        await act.Should().ThrowAsync<AgentProviderException>()
            .WithMessage("*issuer unavailable*");
        lifecycle.Refreshes.Should().BeEmpty();
    }

    [Fact]
    public async Task DrainAsync_ForwardsAgentMessageDeltasToTheRunSink()
    {
        var lifecycle = new RecordingPodLifecycle();
        var agent = NewAgent(lifecycle);
        var channel = Channel.CreateUnbounded<RunEvent>();
        await channel.Writer.WriteAsync(new RunEvent(
            1,
            EventTypes.AgentMessageDelta,
            JsonSerializer.SerializeToElement(new { delta = "partial reply" })));
        channel.Writer.Complete();
        var sink = new RecordingSink();

        await agent.DrainAsync(
            "conversation-streaming",
            channel.Reader,
            sink,
            [],
            Request("conversation-streaming", "broker-token"),
            lifecycle,
            CancellationToken.None);

        sink.Deltas.Should().Equal("partial reply");
    }

    private static OperatorAssistantRequest Request(
        string conversationId,
        string mcpBrokerToken,
        Func<CancellationToken, Task<string>>? renew = null) =>
        new(
            ConversationId: conversationId,
            Message: "test",
            CallerUser: "entra-object-id",
            GitHubLogin: "octocat",
            ProjectId: "project-1",
            RunId: null,
            ModelId: null,
            AgentDefinition: "You are the operator.",
            McpBrokerToken: mcpBrokerToken,
            History: [],
            RenewMcpBrokerTokenAsync: renew ?? (_ => Task.FromResult(mcpBrokerToken)));

    private sealed class MissingEndpointResolver : ISandboxAgentEndpointResolver
    {
        public Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct) =>
            Task.FromResult<Uri?>(null);
    }

    private sealed class RecordingPodLifecycle : IAgentHostPodLifecycle
    {
        public List<(string RunId, AgentHostLaunchContext Context)> Launches { get; } = [];
        public List<(string RunId, string Token)> Refreshes { get; } = [];
        public List<string> Releases { get; } = [];
        public bool FailLaunch { get; init; }

        public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            string? workingDirectoryOverride,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<string> LaunchAgentHostPodAsync(
            string runId,
            AgentHostLaunchContext context,
            CancellationToken ct = default)
        {
            Launches.Add((runId, context));
            if (FailLaunch)
                throw new InvalidOperationException("claim bind failed");
            return Task.FromResult("http://agenthost/a2a/agent");
        }

        public Task RefreshAgentHostMcpBrokerTokenAsync(
            string runId,
            string mcpBrokerToken,
            CancellationToken ct = default)
        {
            Refreshes.Add((runId, mcpBrokerToken));
            return Task.CompletedTask;
        }

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
        {
            Releases.Add(runId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP should not be reached when endpoint resolution fails.");
    }

    private sealed class RecordingSink : IOperatorAssistantTurnSink
    {
        public List<string> Deltas { get; } = [];

        public ValueTask OnAssistantTextDeltaAsync(string delta, CancellationToken ct)
        {
            Deltas.Add(delta);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnToolCallAsync(string toolName, string? argumentsJson, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnToolResultAsync(string toolName, bool success, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnMcpBrokerTokenRefreshRequiredAsync(CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> OnApprovalRequiredAsync(
            string requestId,
            string toolName,
            string? argumentsJson,
            CancellationToken ct) =>
            ValueTask.FromResult(true);
    }
}
