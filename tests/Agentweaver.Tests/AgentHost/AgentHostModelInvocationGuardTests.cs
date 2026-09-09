extern alias agenthost;

using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using AgentHostModelInvocationGuard = agenthost::Agentweaver.AgentHost.AgentHostModelInvocationGuard;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using AgentHostRunConfiguration = agenthost::Agentweaver.AgentHost.AgentHostRunConfiguration;

namespace Agentweaver.Tests.AgentHost;

public sealed class AgentHostModelInvocationGuardTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Host_retry_and_reviewer_reask_revalidate_before_a_second_model_call(bool rateLimitRetry)
    {
        var configuration = new ByokProviderConfiguration(
            "byok", "Provider", "openai", "https://provider.test", "model", "private-key");
        var state = new AgentHostRuntimeState();
        state.TryConfigure(new AgentHostRunConfiguration(
            "run-host", "owner", "run-capability", null, null, null,
            ToolApprovalApiBaseUrl: "https://api.internal",
            ByokProviderConfiguration: configuration,
            ModelProviderKey: "accepted-fingerprint")).Should().BeTrue();
        var handler = new ValidationHandler();
        using var client = new HttpClient(handler);
        var guard = new AgentHostModelInvocationGuard(state, new ClientFactory(client));
        var policyStore = new StubPolicyStore();
        var executor = SandboxExecutorFactory.CreatePassthrough();
        await using var agent = new CopilotAIAgent(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NoCopilotCredential()),
            executor, policyStore, new InMemoryShellApprovalStore(), new InMemoryToolApprovalGate(),
            NullLogger<CopilotAIAgent>.Instance,
            byokProviderConfiguration: new ByokConfiguration(configuration),
            modelInvocationGuard: guard);
        agent.ConfigureProviderBoundary(ModelSource.Byok, configuration.ExecutionFingerprint());
        var model = new ModelAgent(rateLimitRetry);
        SetField(agent, "_runId", "run-host");
        SetField(agent, "_inner", model);
        SetField(agent, "_activeExecutor", executor);
        SetField(agent, "_sandboxPolicy", await policyStore.GetPolicyAsync(".", CancellationToken.None));

        if (!rateLimitRetry)
            await agent.ExecuteStreamingLoopAsync("review", null!, CancellationToken.None);
        var secondCall = () => agent.ExecuteStreamingLoopAsync("review or re-ask", null!, CancellationToken.None);
        (await secondCall.Should().ThrowAsync<AgentProviderException>())
            .Which.ErrorCode.Should().Be("model_provider_changed");
        model.Calls.Should().Be(1, "a rejected retry or re-ask must not reach the model");
        handler.Calls.Should().Be(2, "validation must not be cached across attempts");
    }

    [Fact]
    public async Task Unconfigured_host_refuses_model_validation_without_network_access()
    {
        var handler = new ValidationHandler();
        using var client = new HttpClient(handler);
        var guard = new AgentHostModelInvocationGuard(new AgentHostRuntimeState(), new ClientFactory(client));
        var invoke = () => guard.ValidateAsync("another-run", CancellationToken.None);
        (await invoke.Should().ThrowAsync<AgentProviderException>()).Which.ErrorCode
            .Should().Be("model_provider_changed");
        handler.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Operator_host_rejects_unvalidated_execution_before_MCP_or_model_access()
    {
        var handler = new ValidationHandler();
        using var client = new HttpClient(handler);
        var guard = new AgentHostModelInvocationGuard(new AgentHostRuntimeState(), new ClientFactory(client));
        var agent = new OperatorAssistantAgent(
            new GitHubCopilotClientFactory(new ConfigurationBuilder().Build(), new NoCopilotCredential()),
            mcpToolProvider: null!, NullLogger<OperatorAssistantAgent>.Instance,
            modelInvocationGuard: guard);
        var request = new OperatorAssistantRequest(
            "conversation", "message", "owner", null, null, "run-host", null, "definition", "broker", []);
        var invoke = () => agent.RunTurnAsync(request, null, CancellationToken.None);
        (await invoke.Should().ThrowAsync<AgentProviderException>()).Which.ErrorCode
            .Should().Be("model_provider_changed");
        handler.Calls.Should().Be(0);
    }

    private static void SetField(object target, string name, object value) =>
        typeof(CopilotAIAgent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ValidationHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/runs/run-host/model-provider/validate");
            request.Headers.Authorization!.Parameter.Should().Be("run-capability");
            request.Headers.GetValues(RunAuthorshipHeaders.RunId).Should().ContainSingle().Which.Should().Be("run-host");
            request.Headers.GetValues(RunAuthorshipHeaders.RunToken).Should().ContainSingle().Which.Should().Be("run-capability");
            var body = await request.Content!.ReadAsStringAsync(ct);
            body.Should().Contain("accepted-fingerprint").And.NotContain("private-key");
            return new HttpResponseMessage(++Calls == 1 ? HttpStatusCode.NoContent : HttpStatusCode.Conflict);
        }
    }

    private sealed class ByokConfiguration(ByokProviderConfiguration configuration) : IByokProviderConfigurationProvider
    {
        public Task<ByokProviderConfiguration?> GetAsync(CancellationToken ct) => Task.FromResult<ByokProviderConfiguration?>(configuration);
    }

    private sealed class NoCopilotCredential : IGitHubCopilotCapabilityCredentialProvider
    {
        public Task<GitHubCapabilitySnapshotCredential?> GetCredentialAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<GitHubCapabilitySnapshotCredential?>(null);
    }

    private sealed class ModelAgent(bool rateLimit) : AIAgent
    {
        public int Calls { get; private set; }
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            await Task.Yield();
            if (rateLimit)
                throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests);
            yield return new AgentResponseUpdate { Role = ChatRole.Assistant, Contents = [new TextContent("VERDICT: GREEN")] };
        }
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession? session, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
