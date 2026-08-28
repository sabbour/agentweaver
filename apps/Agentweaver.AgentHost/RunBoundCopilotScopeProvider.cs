using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Provides the host's already-bound user identity for legacy runtime call sites. The credential
/// factory ignores this scope when its run-bound Copilot provider is present, so model input cannot
/// select a GitHub credential scope.
/// </summary>
internal sealed class RunBoundCopilotScopeProvider(AgentHostRuntimeState runtimeState)
    : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId)
    {
        if (string.IsNullOrWhiteSpace(runtimeState.UserId))
            throw new InvalidOperationException("AgentHost has no configured run-bound Copilot identity.");

        return GitHubTokenScope.ForUser(runtimeState.UserId);
    }
}
