extern alias agenthost;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using AgentHostRuntimeServiceCollectionExtensions = agenthost::Agentweaver.AgentHost.AgentHostRuntimeServiceCollectionExtensions;
using AgentHostDurableToolApprovalGate = agenthost::Agentweaver.AgentHost.AgentHostDurableToolApprovalGate;
using IAgentHostToolApprovalPolicyClient = agenthost::Agentweaver.AgentHost.IAgentHostToolApprovalPolicyClient;
using AgentHostToolApprovalPolicyClient = agenthost::Agentweaver.AgentHost.AgentHostToolApprovalPolicyClient;
using AgentHostToolApprovalRequest = agenthost::AgentHostToolApprovalRequest;
using ToolApprovalEndpointHandlers = agenthost::ToolApprovalEndpointHandlers;

namespace Agentweaver.Tests;

public sealed class AgentHostToolApprovalEndpointTests
{
    [Fact]
    public async Task ProductionRuntimeWiring_RunGrant_ResolvesOnlyTheConfiguredRequestUntilApiPersistsScope()
    {
        var services = new ServiceCollection();
        AgentHostRuntimeServiceCollectionExtensions.AddAgentHostRuntime(services);
        await using var provider = services.BuildServiceProvider();

        var state = provider.GetRequiredService<AgentHostRuntimeState>();
        var gate = provider.GetRequiredService<IToolApprovalGate>();
        var ownerResolver = provider.GetRequiredService<IToolApprovalOwnerResolver>();

        ownerResolver.GetCanonicalOwner("run-1").Should().BeNull();
        gate.IsAutoApproved("run-1", "web_fetch", "https://before-configure.test")
            .Should().BeFalse();

        state.TryConfigure("run-1", "user-1", "", null, null, "pod-credential")
            .Should().BeTrue();
        ownerResolver.GetCanonicalOwner("run-1").Should().Be("user-1");
        ownerResolver.GetCanonicalOwner("different-run").Should().BeNull();
        var firstFetch = gate.WaitForApprovalAsync(
            "run-1",
            "req-run",
            "web_fetch",
            "https://first.test",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest
            {
                RunId = "run-1",
                RequestId = "req-run",
                Scope = "run",
            },
            gate,
            state);

        Status(result).Should().Be(StatusCodes.Status200OK);
        (await firstFetch).Should().BeTrue();
        gate.IsAutoApproved("run-1", "web_fetch", "https://second.test")
            .Should().BeFalse("the API persists durable scopes only after this local approval succeeds");
        gate.IsAutoApproved("different-run", "web_fetch", "https://second.test")
            .Should().BeFalse();
    }

    [Fact]
    public async Task Grant_ResolvesPendingRequest()
    {
        var gate = new InMemoryToolApprovalGate();
        var state = ConfiguredState();
        var wait = gate.WaitForApprovalAsync(
            "run-1", "req-1", "web_fetch", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForStateAsync(gate, "req-1", ToolApprovalRequestState.Pending);

        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest { RunId = "run-1", RequestId = "req-1" },
            gate,
            state);

        Status(result).Should().Be(StatusCodes.Status200OK);
        Json(result).GetProperty("state").GetString().Should().Be("approved");
        Json(result).GetProperty("applied").GetBoolean().Should().BeTrue();
        Json(result).GetProperty("toolName").GetString().Should().Be("web_fetch");
        (await wait).Should().BeTrue();
    }

    [Fact]
    public void FreshPod_UsesApiBackedPolicyForItsConfiguredRun()
    {
        var state = ConfiguredState();
        var policyClient = new RecordingPolicyClient(autoApproved: true);
        var gate = new AgentHostDurableToolApprovalGate(state, policyClient);

        gate.IsAutoApproved("run-1", "web_fetch", "https://future-run.test").Should().BeTrue();
        policyClient.Requests.Should().ContainSingle()
            .Which.Should().Be(("run-1", "web_fetch"));
        gate.IsAutoApproved("other-run", "web_fetch", "https://other.test").Should().BeFalse();
    }

    [Fact]
    public async Task ApiBackedPolicyReader_UsesBoundCapabilityAndParsesSnakeCaseResponse()
    {
        var state = ConfiguredState();
        state.SetToolApprovalApiAccess("https://agentweaver-api.example.test", "internal-api-key");
        var handler = new PolicyResponseHandler("""{"auto_approved":true}""");
        var client = new AgentHostToolApprovalPolicyClient(
            state,
            new StubHttpClientFactory(handler),
            NullLogger<AgentHostToolApprovalPolicyClient>.Instance);

        (await client.IsAutoApprovedAsync("run-1", "web_fetch", null, CancellationToken.None))
            .Should().BeTrue();
        handler.Request!.RequestUri!.ToString()
            .Should().Be("https://agentweaver-api.example.test/api/runs/run-1/tool-approval-policies/web_fetch");
        handler.Request.Headers.Authorization.Should().Be(new AuthenticationHeaderValue("Bearer", "internal-api-key"));
        handler.Request.Headers.GetValues(RunAuthorshipHeaders.RunId).Should().ContainSingle("run-1");
        handler.Request.Headers.GetValues(RunAuthorshipHeaders.RunToken).Should().ContainSingle("turn-capability");
    }

    [Fact]
    public async Task Deny_ResolvesPendingRequest()
    {
        var gate = new InMemoryToolApprovalGate();
        var state = ConfiguredState();
        var wait = gate.WaitForApprovalAsync(
            "run-1", "req-2", "web_fetch", null, TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForStateAsync(gate, "req-2", ToolApprovalRequestState.Pending);

        var result = await ToolApprovalEndpointHandlers.DenyAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest { RunId = "run-1", RequestId = "req-2" },
            gate,
            state);

        Status(result).Should().Be(StatusCodes.Status200OK);
        Json(result).GetProperty("state").GetString().Should().Be("denied");
        (await wait).Should().BeFalse();
    }

    [Fact]
    public async Task UnknownRequest_ReturnsNotFound()
    {
        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest { RunId = "run-1", RequestId = "missing" },
            new InMemoryToolApprovalGate(),
            ConfiguredState());

        Status(result).Should().Be(StatusCodes.Status404NotFound);
        Json(result).GetProperty("state").GetString().Should().Be("unknown");
    }

    [Fact]
    public async Task RunMismatch_ReturnsConflict()
    {
        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest { RunId = "stale-run", RequestId = "req-3" },
            new InMemoryToolApprovalGate(),
            ConfiguredState());

        Status(result).Should().Be(StatusCodes.Status409Conflict);
        Json(result).GetProperty("state").GetString().Should().Be("run_mismatch");
    }

    [Fact]
    public async Task ConfiguredCredential_WithMissingBearer_ReturnsUnauthorized()
    {
        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context(null),
            new AgentHostToolApprovalRequest { RunId = "run-1", RequestId = "req-4" },
            new InMemoryToolApprovalGate(),
            ConfiguredState());

        Status(result).Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task BlankRequestId_ReturnsBadRequest()
    {
        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest { RunId = "run-1", RequestId = " " },
            new InMemoryToolApprovalGate(),
            ConfiguredState());

        Status(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    private static AgentHostRuntimeState ConfiguredState()
    {
        var state = new AgentHostRuntimeState();
        state.TryConfigure("run-1", "user-1", "turn-capability", null, null, "pod-credential").Should().BeTrue();
        return state;
    }

    private static HttpContext Context(string? bearer)
    {
        var context = new DefaultHttpContext();
        if (bearer is not null)
            context.Request.Headers.Authorization = "Bearer " + bearer;
        return context;
    }

    private static int? Status(IResult result) =>
        ((IStatusCodeHttpResult)result).StatusCode;

    private static JsonElement Json(IResult result) =>
        JsonSerializer.SerializeToElement(((IValueHttpResult)result).Value);

    private static async Task WaitForStateAsync(
        IToolApprovalGate gate,
        string requestId,
        ToolApprovalRequestState expected)
    {
        for (var i = 0; i < 100; i++)
        {
            if (gate.GetRequestState("run-1", requestId) == expected)
                return;
            await Task.Delay(10);
        }

        throw new TimeoutException($"Request {requestId} did not reach {expected}.");
    }

    private sealed class RecordingPolicyClient(bool autoApproved) : IAgentHostToolApprovalPolicyClient
    {
        public List<(string RunId, string ToolName)> Requests { get; } = [];

        public Task<bool> IsAutoApprovedAsync(
            string runId,
            string toolName,
            string? url,
            CancellationToken ct)
        {
            Requests.Add((runId, toolName));
            return Task.FromResult(autoApproved);
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class PolicyResponseHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
