using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Legacy fallback scope provider. AgentHost executes Copilot model turns, so installation
/// tokens must never be used as model credentials.
/// </summary>
internal sealed class PodInstallationScopeProvider : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId) =>
        throw new InvalidOperationException(
            "AgentHost is configured with an installation-token scope provider, but Copilot model " +
            "turns require a submitting user token. Configure Key Vault or shared user-token storage.");
}
