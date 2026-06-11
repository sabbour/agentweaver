using Scaffolder.Domain;

namespace Scaffolder.Api.Auth;

/// <summary>
/// Cloud scope provider: resolves per-user scopes so each caller's credentials
/// are isolated. Used in multi-tenant hosted-cloud deployments.
/// Selected when Auth:GitHub:ScopeProvider is "caller".
/// </summary>
public sealed class CallerTokenScopeProvider : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? GitHubTokenScope.Installation   // fallback for background tasks with no caller context
            : GitHubTokenScope.ForUser(userId);
}
