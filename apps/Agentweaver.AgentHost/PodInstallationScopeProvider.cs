using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// Token-scope provider for the in-pod AgentHost: always returns
/// <see cref="GitHubTokenScope.Installation"/> since the pod runs under a single
/// workload identity / claim-time token (§5 credential model).
/// </summary>
internal sealed class PodInstallationScopeProvider : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId) => GitHubTokenScope.Installation;
}
