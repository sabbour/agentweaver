using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Assistant;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentweaver.Tests.Assistant;

public sealed class RemoteOperatorAssistantAgentTests
{
    [Fact]
    public async Task RunTurn_LaunchesAgentHostWithCurrentMcpBrokerToken()
    {
        var lifecycle = new RecordingPodLifecycle();
        var agent = new RemoteOperatorAssistantAgent(
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

        await RunUntilEndpointFailureAsync(agent, Request("conversation-1", "broker-token-v1"));
        await RunUntilEndpointFailureAsync(agent, Request("conversation-2", "broker-token-v2"));

        lifecycle.Launches.Select(launch => launch.Context.McpBrokerToken).Should().Equal(
            ["broker-token-v1", "broker-token-v2"],
            "each turn must propagate only its short-lived MCP broker token");
        lifecycle.Launches.Should().OnlyContain(launch =>
            launch.Context.Purpose == Agentweaver.Domain.AgentHostPurpose.OperatorAssistant);
    }

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

    private static OperatorAssistantRequest Request(string conversationId, string mcpBrokerToken) =>
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
            History: []);

    private sealed class MissingEndpointResolver : ISandboxAgentEndpointResolver
    {
        public Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct) =>
            Task.FromResult<Uri?>(null);
    }

    private sealed class RecordingPodLifecycle : IAgentHostPodLifecycle
    {
        public List<(string RunId, AgentHostLaunchContext Context)> Launches { get; } = [];

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
            return Task.FromResult("http://agenthost/a2a/agent");
        }

        public Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("HTTP should not be reached when endpoint resolution fails.");
    }
}
