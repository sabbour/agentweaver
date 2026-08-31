using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Denies the retired ambient GitHub-token path. GitHub authority must be acquired through a
/// GitHub connections capability or an immutable run snapshot, neither of which exposes raw user tokens.
/// </summary>
public sealed class EntraOnlyGitHubCredentialBoundary :
    IGitHubTokenStore,
    IGitHubTokenScopeProvider,
    IGitHubAccessTokenProvider
{
    public GitHubTokenScope Resolve(string? userId) => GitHubTokenScope.Installation;

    public Task<GitHubTokenScope> ResolveAsync(
        string? userId,
        string? projectId,
        CancellationToken ct = default) =>
        Task.FromResult(Resolve(userId));

    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubToken?>(null);

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "Ambient GitHub token storage was retired. Use the GitHub connections credential vault.");

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubIdentity?>(null);

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<string?> GetValidAccessTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
