using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests;

public sealed class AgentHostApprovalRoutingTests
{
    [Fact]
    public async Task PodPerRun_InvokesClient_WithStoredBearer_AndEmitsResolutionOnce()
    {
        var client = new RecordingClient(new AgentHostApprovalOutcome(true, "approved", false, 200));
        var secrets = new InMemorySecretStore();
        await secrets.SetSecretAsync(PreviewRunnerCredential.SecretKey("child-run"), "pod-credential");
        var streams = new RunStreamStore();
        var entry = streams.Create("child-run", "owner");
        var options = new SandboxRuntimeOptions { AgentExecutionMode = "pod-per-run" };

        await RunEndpoints.TryResolveAgentHostApprovalAsync(
            true, "child-run", "request-1", "once", options, client, secrets, streams, CancellationToken.None);
        await RunEndpoints.TryResolveAgentHostApprovalAsync(
            true, "child-run", "request-1", "once", options, client, secrets, streams, CancellationToken.None);

        client.GrantCalls.Should().Be(2);
        client.LastBearer.Should().Be("pod-credential");
        var resolved = entry.GetSnapshotSince(0).Events
            .Where(e => e.Type == EventTypes.ToolApprovalResolved)
            .ToList();
        resolved.Should().ContainSingle();
        JsonSerializer.SerializeToElement(resolved[0].Payload)
            .GetProperty("requestId").GetString().Should().Be("request-1");
    }

    [Fact]
    public async Task InApi_DoesNotInvokePodClient()
    {
        var client = new RecordingClient(new AgentHostApprovalOutcome(true, "approved", false, 200));

        var outcome = await RunEndpoints.TryResolveAgentHostApprovalAsync(
            true,
            "child-run",
            "request-2",
            "once",
            new SandboxRuntimeOptions { AgentExecutionMode = "in-api" },
            client,
            secretStore: null,
            new RunStreamStore(),
            CancellationToken.None);

        outcome.Should().BeNull();
        client.GrantCalls.Should().Be(0);
        client.DenyCalls.Should().Be(0);
    }

    private sealed class RecordingClient(AgentHostApprovalOutcome outcome) : IAgentHostApprovalHttpClient
    {
        public int GrantCalls { get; private set; }
        public int DenyCalls { get; private set; }
        public string? LastBearer { get; private set; }

        public Task<AgentHostApprovalOutcome> GrantAsync(
            string childRunId,
            string requestId,
            string scope,
            string? bearer,
            CancellationToken ct)
        {
            GrantCalls++;
            LastBearer = bearer;
            return Task.FromResult(outcome);
        }

        public Task<AgentHostApprovalOutcome> DenyAsync(
            string childRunId,
            string requestId,
            string? bearer,
            CancellationToken ct)
        {
            DenyCalls++;
            LastBearer = bearer;
            return Task.FromResult(outcome);
        }
    }
}
