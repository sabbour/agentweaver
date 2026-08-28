extern alias agenthost;

using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using FluentAssertions;
using Xunit;
using AgentHostRuntimeState = agenthost::Agentweaver.AgentHost.AgentHostRuntimeState;
using AgentHostRunConfiguration = agenthost::Agentweaver.AgentHost.AgentHostRunConfiguration;
using RunBoundCopilotScopeProvider = agenthost::Agentweaver.AgentHost.RunBoundCopilotScopeProvider;

namespace Agentweaver.Tests;

/// <summary>
/// Verifies that AgentHost uses only its server-bound run identity for Copilot initialization.
/// </summary>
public sealed class AgentHostUserAuthTests
{
    [Fact]
    public void Resolve_uses_configured_user_id_when_set()
    {
        var state = ConfiguredState("sabbour");
        var provider = new RunBoundCopilotScopeProvider(state);

        var scope = provider.Resolve(userId: null);

        scope.Key.Should().Be("user:sabbour");
    }

    [Fact]
    public void Resolve_fails_closed_when_host_has_no_run_identity()
    {
        var provider = new RunBoundCopilotScopeProvider(new AgentHostRuntimeState());

        var act = () => provider.Resolve(userId: "attacker-selected-user");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*run-bound Copilot identity*");
    }

    [Fact]
    public void IsMissingCopilotAuth_detects_sdk_session_auth_error()
    {
        var sdkError = new InvalidOperationException(
            "Session error: Execution failed: Error: Session was not created with authentication info or custom provider");

        CopilotAIAgent.IsMissingCopilotAuth(sdkError).Should().BeTrue();
    }

    [Fact]
    public void IsMissingCopilotAuth_detects_nested_sdk_session_auth_error()
    {
        var sdkError = new InvalidOperationException(
            "outer wrapper",
            new InvalidOperationException("Session was not created with authentication info or custom provider"));

        CopilotAIAgent.IsMissingCopilotAuth(sdkError).Should().BeTrue();
    }

    [Fact]
    public void IsMissingCopilotAuth_ignores_unrelated_errors()
    {
        CopilotAIAgent.IsMissingCopilotAuth(new InvalidOperationException("Connection refused"))
            .Should().BeFalse();
    }

    private static AgentHostRuntimeState ConfiguredState(string userId)
    {
        var state = new AgentHostRuntimeState();
        state.TryConfigure(new AgentHostRunConfiguration(
            RunId: "run-auth",
            UserId: userId,
            TurnBearerToken: "turn",
            CopilotAccessToken: "copilot-sign-in",
            PreviewRunnerCredential: null,
            SharedWorkingDirectory: "/workspace/run-auth"));
        return state;
    }
}
