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
using AgentHostToolApprovalScopeRequest = agenthost::AgentHostToolApprovalScopeRequest;
using ToolApprovalEndpointHandlers = agenthost::ToolApprovalEndpointHandlers;

namespace Agentweaver.Tests;

public sealed class AgentHostToolApprovalEndpointTests
{
    [Theory]
    [InlineData("run")]
    [InlineData("tool")]
    [InlineData("always")]
    public async Task ProductionRuntimeWiring_ScopedGrant_AutoApprovesFollowingToolCallForConfiguredRun(
        string scope)
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
                Scope = scope,
            },
            gate,
            state);

        Status(result).Should().Be(StatusCodes.Status200OK);
        (await firstFetch).Should().BeTrue();
        gate.IsAutoApproved("run-1", "web_fetch", "https://second.test")
            .Should().BeTrue($"a successful {scope}-scoped approval must cover following local tool calls");
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

    [Theory]
    [InlineData("run")]
    [InlineData("tool")]
    [InlineData("always")]
    public async Task Grant_LateScopedRetry_DoesNotCreateCurrentPodBridge(string scope)
    {
        var state = ConfiguredState();
        var gate = new AgentHostDurableToolApprovalGate(
            state,
            new RecordingPolicyClient(autoApproved: false));
        var wait = gate.WaitForApprovalAsync(
            "run-1", "req-late", "web_fetch", "https://first.test",
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForStateAsync(gate, "req-late", ToolApprovalRequestState.Pending);

        var winner = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest
            {
                RunId = "run-1",
                RequestId = "req-late",
                Scope = "once",
            },
            gate,
            state);
        Status(winner).Should().Be(StatusCodes.Status200OK);
        (await wait).Should().BeTrue();

        var late = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest
            {
                RunId = "run-1",
                RequestId = "req-late",
                Scope = scope,
            },
            gate,
            state);

        Status(late).Should().Be(StatusCodes.Status200OK);
        Json(late).GetProperty("state").GetString().Should().Be("approved");
        Json(late).GetProperty("applied").GetBoolean().Should().BeFalse();
        gate.IsAutoApproved("run-1", "web_fetch", "https://following.test")
            .Should().BeFalse("a late scoped forward did not win the local approval");
    }

    [Fact]
    public async Task GetPendingContext_DoesNotResolveTheLocalApproval()
    {
        var gate = new InMemoryToolApprovalGate();
        var state = ConfiguredState();
        var wait = gate.WaitForApprovalAsync(
            "run-1", "req-context", "web_fetch", "https://context.test",
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForStateAsync(gate, "req-context", ToolApprovalRequestState.Pending);

        var result = await ToolApprovalEndpointHandlers.GetPendingContextAsync(
            Context("pod-credential"),
            "req-context",
            gate,
            state);

        Status(result).Should().Be(StatusCodes.Status200OK);
        Json(result).GetProperty("state").GetString().Should().Be("pending");
        Json(result).GetProperty("toolName").GetString().Should().Be("web_fetch");
        gate.GetRequestState("run-1", "req-context").Should().Be(ToolApprovalRequestState.Pending);
        gate.Deny("run-1", "req-context").Should().BeTrue();
        (await wait).Should().BeFalse();
    }

    [Fact]
    public async Task RollbackScope_RemovesOnlyTheExactProvisionalScope()
    {
        var state = ConfiguredState();
        var gate = new AgentHostDurableToolApprovalGate(
            state,
            new RecordingPolicyClient(autoApproved: false));
        var first = gate.WaitForApprovalAsync(
            "run-1", "req-first", "web_fetch", "https://first.test",
            TimeSpan.FromSeconds(5), CancellationToken.None);
        var second = gate.WaitForApprovalAsync(
            "run-1", "req-second", "web_fetch", "https://second.test",
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForStateAsync(gate, "req-first", ToolApprovalRequestState.Pending);
        await WaitForStateAsync(gate, "req-second", ToolApprovalRequestState.Pending);

        var firstGrant = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest
            {
                RunId = "run-1",
                RequestId = "req-first",
                Scope = "run",
                ScopeGrantId = "scope-first",
                ScopeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            gate,
            state);
        var secondGrant = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest
            {
                RunId = "run-1",
                RequestId = "req-second",
                Scope = "run",
                ScopeGrantId = "scope-second",
                ScopeExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            },
            gate,
            state);
        (await first).Should().BeTrue();
        (await second).Should().BeTrue();

        var firstScopeGrantId = Json(firstGrant).GetProperty("scopeGrantId").GetString();
        var secondScopeGrantId = Json(secondGrant).GetProperty("scopeGrantId").GetString();
        firstScopeGrantId.Should().Be("scope-first");
        secondScopeGrantId.Should().Be("scope-second");
        gate.IsAutoApproved("run-1", "web_fetch", "https://following.test").Should().BeTrue();

        var rollback = await ToolApprovalEndpointHandlers.RollbackScopeAsync(
            Context("pod-credential"),
            "req-first",
            new AgentHostToolApprovalScopeRequest
            {
                RunId = "run-1",
                ScopeGrantId = firstScopeGrantId,
            },
            gate,
            state);

        Status(rollback).Should().Be(StatusCodes.Status200OK);
        gate.IsAutoApproved("run-1", "web_fetch", "https://following.test").Should().BeTrue(
            "a rollback must not revoke an equivalent scope granted by another approval");

        var secondRollback = await ToolApprovalEndpointHandlers.RollbackScopeAsync(
            Context("pod-credential"),
            "req-second",
            new AgentHostToolApprovalScopeRequest
            {
                RunId = "run-1",
                ScopeGrantId = secondScopeGrantId,
            },
            gate,
            state);

        Status(secondRollback).Should().Be(StatusCodes.Status200OK);
        gate.IsAutoApproved("run-1", "web_fetch", "https://following.test").Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionalScopedGrant_ExpiresWithoutApiFinalization()
    {
        var state = ConfiguredState();
        var gate = new AgentHostDurableToolApprovalGate(
            state,
            new RecordingPolicyClient(autoApproved: false));
        var wait = gate.WaitForApprovalAsync(
            "run-1", "req-expiring", "web_fetch", "https://first.test",
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await WaitForStateAsync(gate, "req-expiring", ToolApprovalRequestState.Pending);

        var result = await ToolApprovalEndpointHandlers.GrantAsync(
            Context("pod-credential"),
            new AgentHostToolApprovalRequest
            {
                RunId = "run-1",
                RequestId = "req-expiring",
                Scope = "run",
                ScopeGrantId = "expiring-scope",
                ScopeExpiresAt = DateTimeOffset.UtcNow.AddMilliseconds(250),
            },
            gate,
            state);

        Status(result).Should().Be(StatusCodes.Status200OK);
        (await wait).Should().BeTrue();
        gate.IsAutoApproved("run-1", "web_fetch", "https://following.test").Should().BeTrue();
        await Task.Delay(400);
        gate.IsAutoApproved("run-1", "web_fetch", "https://following.test").Should().BeFalse(
            "a response that cannot be finalized by the API must not leave a usable local scope");
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
