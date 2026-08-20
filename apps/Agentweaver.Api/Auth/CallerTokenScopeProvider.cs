using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Resolves per-user GitHub token scopes from an explicit authenticated subject.
/// Missing caller identity is rejected so background/system work cannot silently
/// fall back to a shared GitHub credential.
/// </summary>
public sealed class CallerTokenScopeProvider(
    IHttpContextAccessor? httpContextAccessor = null,
    IServiceScopeFactory? serviceScopeFactory = null) : IGitHubTokenScopeProvider
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

    public async Task<GitHubTokenScope> ResolveAsync(
        string? userId,
        string? projectId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Resolve(userId);

        if (string.IsNullOrWhiteSpace(projectId))
            return Resolve(userId);

        if (!ProjectId.TryParse(projectId, out var parsedProjectId))
            throw new InvalidOperationException($"GitHub token scope project id '{projectId}' is invalid.");

        if (httpContextAccessor?.HttpContext?.Items.TryGetValue(ProjectScopeItemKey, out var selected) == true
            && selected is ProjectGitHubTokenScope requestScope
            && requestScope.ProjectId == parsedProjectId
            && string.Equals(requestScope.EntraUserId, userId, StringComparison.Ordinal))
        {
            return GitHubTokenScope.ForLinkedIdentity(requestScope.EntraUserId, requestScope.GitHubLogin);
        }

        if (serviceScopeFactory is null)
            return GitHubTokenScope.ForUser(userId);

        using var scope = serviceScopeFactory.CreateScope();
        var identityService = scope.ServiceProvider.GetRequiredService<ProjectGitHubIdentityService>();
        var effective = await identityService
            .GetEffectiveIdentityAsync(parsedProjectId, userId, ct)
            .ConfigureAwait(false);

        return string.Equals(effective.ResolutionSource, "project_override", StringComparison.Ordinal)
               && effective.EffectiveLink is not null
            ? GitHubTokenScope.ForLinkedIdentity(userId, effective.EffectiveLink.GitHubLogin)
            : GitHubTokenScope.ForUser(userId);
    }

    internal static void SelectProjectIdentity(
        HttpContext httpContext,
        ProjectId projectId,
        string entraUserId,
        string githubLogin) =>
        httpContext.Items[ProjectScopeItemKey] = new ProjectGitHubTokenScope(projectId, entraUserId, githubLogin);

    private sealed record ProjectGitHubTokenScope(ProjectId ProjectId, string EntraUserId, string GitHubLogin);
}
