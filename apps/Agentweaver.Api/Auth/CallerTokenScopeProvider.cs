using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Resolves per-user GitHub token scopes from an explicit authenticated subject.
/// Missing caller identity is rejected so background/system work cannot silently
/// fall back to a shared GitHub credential.
/// </summary>
public sealed class CallerTokenScopeProvider : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId) =>
        string.IsNullOrWhiteSpace(userId)
            ? throw new InvalidOperationException(
                "GitHub operations require an explicit user identity. Shared installation-scope fallback is not supported.")
            : GitHubTokenScope.ForUser(userId);
}
