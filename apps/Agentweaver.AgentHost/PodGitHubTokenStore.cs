using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// In-pod GitHub token store that serves a single pre-configured installation token.
/// The token is sourced from <c>Providers:GitHubCopilot:GitHubToken</c> config or the
/// <c>GITHUB_TOKEN</c> environment variable and written into this store at startup.
/// </summary>
internal sealed class PodGitHubTokenStore : IGitHubTokenStore
{
    private readonly ConcurrentDictionary<string, (GitHubTokenStatus status, GitHubToken? token)>
        _map = new(StringComparer.Ordinal);

    public void Seed(GitHubTokenScope scope, string accessToken) =>
        _map[scope.Key] = (GitHubTokenStatus.SignedIn, new GitHubToken(accessToken, null, null, "", null, Array.Empty<string>()));

    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (_map.TryGetValue(scope.Key, out var e))
            return Task.FromResult(new GitHubTokenEntry(e.status, e.token?.AccessToken));
        return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));
    }

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (_map.TryGetValue(scope.Key, out var e) && e.status == GitHubTokenStatus.SignedIn)
            return Task.FromResult(e.token);
        return Task.FromResult<GitHubToken?>(null);
    }

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        _map[scope.Key] = (GitHubTokenStatus.SignedIn, token);
        return Task.CompletedTask;
    }

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (_map.TryGetValue(scope.Key, out var e) && e.token is not null)
            return Task.FromResult<GitHubIdentity?>(new GitHubIdentity(e.token.Login, e.token.AvatarUrl));
        return Task.FromResult<GitHubIdentity?>(null);
    }

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        _map[scope.Key] = (GitHubTokenStatus.SignedOut, null);
        return Task.CompletedTask;
    }
}
