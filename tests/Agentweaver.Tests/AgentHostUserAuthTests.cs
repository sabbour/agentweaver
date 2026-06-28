extern alias agenthost;

using Agentweaver.AgentRuntime;
using Agentweaver.Domain;
using FluentAssertions;
using Xunit;
using SharedUserScopeProvider = agenthost::Agentweaver.AgentHost.SharedUserScopeProvider;

namespace Agentweaver.Tests;

/// <summary>
/// Verifies the in-pod GitHub Copilot auth resolution path that the AgentHost__UserId injection
/// (KubernetesSandboxExecutor) drives: when the run's submitting user id is configured the pod
/// resolves the per-user token scope (user_&lt;id&gt;.json); when it is absent it degrades. Also
/// verifies the clear-error detection that replaces the opaque SDK auth failure.
/// </summary>
public sealed class AgentHostUserAuthTests
{
    [Fact]
    public void Resolve_uses_configured_user_id_when_set()
    {
        // Configured user id (AgentHost:UserId / the run's submitting user) wins → per-user scope,
        // so SharedHomeGitHubTokenStore reads user_<id>.json (the Copilot-entitled token).
        var provider = new SharedUserScopeProvider(authDir: "/nonexistent", configuredUserId: "sabbour");

        var scope = provider.Resolve(userId: null);

        scope.Key.Should().Be("user:sabbour");
    }

    [Fact]
    public void Resolve_falls_back_to_installation_when_no_user_and_no_signed_in_file()
    {
        // No configured user id and no discoverable signed-in user_*.json → installation scope
        // (the non-Copilot fallback the fix is designed to avoid by injecting AgentHost__UserId).
        var emptyDir = Path.Combine(Path.GetTempPath(), "agentweaver-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var provider = new SharedUserScopeProvider(authDir: emptyDir, configuredUserId: null);

            var scope = provider.Resolve(userId: null);

            scope.Key.Should().Be("installation");
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
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
}
