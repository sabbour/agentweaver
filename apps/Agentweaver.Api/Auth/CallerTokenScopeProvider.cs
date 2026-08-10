using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Resolves per-user GitHub token scopes from an explicit authenticated subject.
/// Missing caller identity is rejected so background/system work cannot silently
/// fall back to a shared GitHub credential.
/// </summary>
public sealed class CallerTokenScopeProvider(IHttpContextAccessor? httpContextAccessor = null) : IGitHubTokenScopeProvider
{
    internal const string ProjectScopeItemKey = "agentweaver.project-github-token-scope";

    public GitHubTokenScope Resolve(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new InvalidOperationException(
                "GitHub operations require an explicit user identity. Shared installation-scope fallback is not supported.");

        if (httpContextAccessor?.HttpContext?.Items.TryGetValue(ProjectScopeItemKey, out var selected) == true
            && selected is ProjectGitHubTokenScope projectScope
            && string.Equals(projectScope.EntraUserId, userId, StringComparison.Ordinal))
        {
            return GitHubTokenScope.ForLinkedIdentity(projectScope.EntraUserId, projectScope.GitHubLogin);
        }

        return GitHubTokenScope.ForUser(userId);
    }

    internal static void SelectProjectIdentity(HttpContext httpContext, string entraUserId, string githubLogin) =>
        httpContext.Items[ProjectScopeItemKey] = new ProjectGitHubTokenScope(entraUserId, githubLogin);

    private sealed record ProjectGitHubTokenScope(string EntraUserId, string GitHubLogin);
}
