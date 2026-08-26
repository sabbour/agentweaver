using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Resolves per-user GitHub token scopes from an explicit authenticated subject.
/// Missing caller identity is rejected so background/system work cannot silently
/// fall back to a shared GitHub credential.
/// </summary>
public sealed class CallerTokenScopeProvider : IGitHubTokenScopeProvider
{
    public GitHubTokenScope Resolve(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException(
                "GitHub operations require an explicit user identity. Shared installation-scope fallback is not supported.");

        return GitHubTokenScope.ForUser(userId);
    }

    public Task<GitHubTokenScope> ResolveAsync(
        string? userId,
        string? projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Task.FromResult(Resolve(userId));

        if (!string.IsNullOrWhiteSpace(projectId) && !ProjectId.TryParse(projectId, out _))
            throw new InvalidOperationException($"GitHub token scope project id '{projectId}' is invalid.");

        return Task.FromResult(GitHubTokenScope.ForUser(userId));
    }
}
