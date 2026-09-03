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
    public async Task RunTurn_LaunchesAgentHostWithCurrentCallerBearerToken()
    {
        var lifecycle = new RecordingPodLifecycle();
        var agent = NewAgent(lifecycle);

        await RunUntilEndpointFailureAsync(agent, Request("conversation-1", "entra-token-v1"));
        await RunUntilEndpointFailureAsync(agent, Request("conversation-2", "entra-token-v2"));

        lifecycle.Launches.Select(launch => launch.Context.CallerBearerToken).Should().Equal(
            ["entra-token-v1", "entra-token-v2"],
            "a later message must propagate its refreshed platform credential instead of reusing an earlier turn's token");
        lifecycle.Launches.Should().OnlyContain(launch =>
            launch.Context.Purpose == Agentweaver.Domain.AgentHostPurpose.OperatorAssistant);
    }

    [Fact]
    public async Task RunTurn_ReleasesAgentHostPod_WhenTheTurnFails()
    {
        // A conversation HOLDS its pod between successful turns (that is what removes the per-turn
        // cold start), but a turn that FAILED may have left the pod half-configured or wedged, so it
        // must never be carried into the next message.
        var lifecycle = new RecordingPodLifecycle();
        var agent = NewAgent(lifecycle);

        await RunUntilEndpointFailureAsync(agent, Request("conversation-failed", "entra-token-v1"));

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

    private static async Task RunUntilEndpointFailureAsync(
        RemoteOperatorAssistantAgent agent,
        OperatorAssistantRequest request)
    {
        var act = () => agent.RunTurnAsync(request, sink: null, CancellationToken.None);
        await act.Should().ThrowAsync<AgentProviderException>()
            .Where(ex => ex.ErrorCode == "agenthost_unavailable");
    }

    private static OperatorAssistantRequest Request(string conversationId, string callerBearerToken) =>
        new(
            ConversationId: conversationId,
            Message: "test",
            CallerUser: "entra-object-id",
            GitHubLogin: "octocat",
            ProjectId: "project-1",
            RunId: null,
            ModelId: null,
            AgentDefinition: "You are the operator.",
            CallerBearerToken: callerBearerToken,
            History: []);

    private sealed class MissingEndpointResolver : ISandboxAgentEndpointResolver
    {
        public Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct) =>
            Task.FromResult<Uri?>(null);
    }

    private sealed class RecordingPodLifecycle : IAgentHostPodLifecycle
    {
        public List<(string RunId, AgentHostLaunchContext Context)> Launches { get; } = [];
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
}
