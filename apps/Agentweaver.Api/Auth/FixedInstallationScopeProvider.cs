using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Local scope provider: always returns GitHubTokenScope.Installation.
/// Used on developer machines where a single OS user is the only identity.
/// Selected when Auth:GitHub:ScopeProvider is "installation" (default).
/// </summary>
public sealed class FixedInstallationScopeProvider : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId) => GitHubTokenScope.Installation;
}
