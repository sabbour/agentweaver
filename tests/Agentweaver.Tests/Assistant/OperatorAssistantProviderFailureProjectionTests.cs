using Agentweaver.AgentRuntime;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Assistant;

public sealed class OperatorAssistantProviderFailureProjectionTests
{
    [Fact]
    public async Task RunTurnAsync_WithoutRunBoundSnapshot_ProjectsStructuredFailureBeforeThrowing()
    {
        var sink = new RecordingFailureSink();
        var agent = new OperatorAssistantAgent(
            new GitHubCopilotClientFactory(
                new ConfigurationBuilder().Build(),
                new FixedGitHubCopilotCapabilityCredentialProvider()),
            new UnexpectedMcpToolProvider(),
            NullLogger<OperatorAssistantAgent>.Instance);

        var act = () => agent.RunTurnAsync(
            new OperatorAssistantRequest(
                ConversationId: "conversation-1",
                Message: "hello",
                CallerUser: "user-1",
                GitHubLogin: "octocat",
                ProjectId: "project-1",
                RunId: null,
                ModelId: null,
                AgentDefinition: "You are the operator.",
                McpBrokerToken: "broker-token",
                History: []),
            sink,
            CancellationToken.None);

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_auth_required");
        sink.Failures.Should().ContainSingle();
        sink.Failures[0].ErrorCode.Should().Be("github_copilot_auth_required");
        sink.CallOrder.Should().Equal("run.failed");
    }

    [Fact]
    public async Task InvokeProviderOperationAsync_StartAsyncFailure_ProjectsStructuredFailureBeforeThrowing()
    {
        var sink = new RecordingFailureSink();
        sink.CallOrder.Add("start");

        Func<Task> act = async () =>
        {
            try
            {
                await OperatorAssistantAgent.InvokeProviderOperationAsync(
                    _ => Task.FromException(new InvalidOperationException(
                        "Session was not created with authentication info.")),
                    ModelSource.GitHubCopilot,
                    sink,
                    CancellationToken.None);
            }
            finally
            {
                sink.CallOrder.Add("caught");
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_auth_required");
        sink.Failures.Should().ContainSingle();
        sink.Failures[0].FailureKind.Should().Be(AgentProviderFailureKind.Authorization);
        sink.CallOrder.Should().Equal("start", "run.failed", "caught");
    }

    [Fact]
    public async Task InvokeProviderOperationAsync_CreateClientFailure_ProjectsStructuredFailureBeforeThrowing()
    {
        var sink = new RecordingFailureSink();
        sink.CallOrder.Add("create-client");

        Func<Task> act = async () =>
        {
            try
            {
                await OperatorAssistantAgent.InvokeProviderOperationAsync(
                    _ => Task.FromException<string>(new GitHubCopilotUnauthorizedException(
                        "GitHub Copilot requires a live run-bound capability snapshot.")),
                    ModelSource.GitHubCopilot,
                    sink,
                    CancellationToken.None);
            }
            finally
            {
                sink.CallOrder.Add("caught");
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_auth_required");
        sink.Failures.Should().ContainSingle();
        sink.Failures[0].FailureKind.Should().Be(AgentProviderFailureKind.Authorization);
        sink.CallOrder.Should().Equal("create-client", "run.failed", "caught");
    }

    [Fact]
    public async Task InvokeProviderOperationAsync_CreateSessionFailure_ProjectsStructuredFailureBeforeThrowing()
    {
        var sink = new RecordingFailureSink();
        sink.CallOrder.Add("create-session");

        Func<Task> act = async () =>
        {
            try
            {
                await OperatorAssistantAgent.InvokeProviderOperationAsync(
                    _ => Task.FromException<string>(new InvalidOperationException(
                        "Session error: Execution failed: Error: Failed to list models")),
                    ModelSource.GitHubCopilot,
                    sink,
                    CancellationToken.None);
            }
            finally
            {
                sink.CallOrder.Add("caught");
            }
        };

        var ex = await act.Should().ThrowAsync<AgentProviderException>();
        ex.Which.ErrorCode.Should().Be("github_copilot_models_unavailable");
        sink.Failures.Should().ContainSingle();
        sink.Failures[0].FailureKind.Should().Be(AgentProviderFailureKind.ProviderUnavailable);
        sink.CallOrder.Should().Equal("create-session", "run.failed", "caught");
    }

    private sealed class UnexpectedMcpToolProvider : IAgentweaverMcpToolProvider
    {
        public Task<AgentweaverMcpToolSession> ConnectAsync(string brokerToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("MCP should not be reached when the run-bound snapshot is missing.");
    }

    private sealed class RecordingFailureSink : IOperatorAssistantTurnSink
    {
        public List<string> CallOrder { get; } = [];
        public List<AgentProviderException> Failures { get; } = [];

        public ValueTask OnAssistantTextDeltaAsync(string delta, CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask OnToolCallAsync(string toolName, string? argumentsJson, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask OnToolResultAsync(string toolName, bool success, CancellationToken ct) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> OnApprovalRequiredAsync(
            string requestId,
            string toolName,
            string? argumentsJson,
            CancellationToken ct) =>
            ValueTask.FromResult(true);

        public ValueTask OnMcpBrokerTokenRefreshRequiredAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask OnRunFailedAsync(AgentProviderException providerFailure, CancellationToken ct)
        {
            Failures.Add(providerFailure);
            CallOrder.Add("run.failed");
            return ValueTask.CompletedTask;
        }
    }
}
