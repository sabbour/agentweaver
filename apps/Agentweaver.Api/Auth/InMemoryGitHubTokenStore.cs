using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// In-memory GitHub token store. Used for development/testing where the OS credential
/// manager is not available. Tokens are stored in-process and lost on restart.
/// NOT suitable for production — use OsCredentialStoreGitHubTokenStore or
/// EncryptedSecretStoreGitHubTokenStore for production deployments.
/// </summary>
public sealed class InMemoryGitHubTokenStore : IMultiIdentityGitHubTokenStore
{
    private readonly ConcurrentDictionary<string, StoreEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, GitHubIdentityLink>> _links =
        new(StringComparer.Ordinal);

    private sealed record StoreEntry(GitHubTokenStatus Status, GitHubToken? Token);

    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(scope.Key, out var entry))
        {
            return Task.FromResult(new GitHubTokenEntry(entry.Status, entry.Token?.AccessToken));
        }
        return Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null));
    }

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(scope.Key, out var entry) && entry.Status == GitHubTokenStatus.SignedIn)
            return Task.FromResult(entry.Token);
        return Task.FromResult<GitHubToken?>(null);
    }

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        _entries[scope.Key] = new StoreEntry(GitHubTokenStatus.SignedIn, token);
        return Task.CompletedTask;
    }

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        if (_entries.TryGetValue(scope.Key, out var entry) && entry.Token is not null)
            return Task.FromResult<GitHubIdentity?>(new GitHubIdentity(entry.Token.Login, entry.Token.AvatarUrl));
        return Task.FromResult<GitHubIdentity?>(null);
    }

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        // Write SignedOut tombstone — explicit sign-out; config fallback suppressed afterward
        _entries[scope.Key] = new StoreEntry(GitHubTokenStatus.SignedOut, null);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GitHubIdentityLink>> ListLinkedIdentitiesAsync(
        string entraUserId,
        CancellationToken ct = default)
    {
        if (!_links.TryGetValue(entraUserId, out var links))
            return Task.FromResult<IReadOnlyList<GitHubIdentityLink>>([]);

        var ordered = links.Values
            .OrderByDescending(x => x.IsDefault)
            .ThenBy(x => x.LinkedAt)
            .ToArray();
        return Task.FromResult<IReadOnlyList<GitHubIdentityLink>>(ordered);
    }

    public Task<GitHubIdentityLink?> GetLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        if (_links.TryGetValue(entraUserId, out var links)
            && links.TryGetValue(githubLogin, out var link))
            return Task.FromResult<GitHubIdentityLink?>(link);

        return Task.FromResult<GitHubIdentityLink?>(null);
    }

    public async Task<GitHubIdentityLink?> GetDefaultLinkedIdentityAsync(
        string entraUserId,
        CancellationToken ct = default)
        => (await ListLinkedIdentitiesAsync(entraUserId, ct).ConfigureAwait(false))
            .FirstOrDefault(x => x.IsDefault);

    public Task LinkIdentityAsync(
        string entraUserId,
        GitHubToken token,
        bool isDefault = false,
        bool? copilotEntitled = null,
        DateTimeOffset? copilotEntitledCheckedAt = null,
        CancellationToken ct = default)
    {
        var scope = GitHubTokenScope.ForLinkedIdentity(entraUserId, token.Login);
        _entries[scope.Key] = new StoreEntry(GitHubTokenStatus.SignedIn, token);

        var links = _links.GetOrAdd(entraUserId, _ => new ConcurrentDictionary<string, GitHubIdentityLink>(StringComparer.Ordinal));
        var existing = links.TryGetValue(token.Login, out var current) ? current : null;
        var makeDefault = isDefault || links.Count == 0 || (existing?.IsDefault ?? false);

        if (makeDefault)
        {
            foreach (var pair in links.ToArray())
            {
                if (pair.Value.IsDefault)
                    links[pair.Key] = pair.Value with { IsDefault = false };
            }
        }

        links[token.Login] = new GitHubIdentityLink(
            entraUserId,
            token.Login,
            scope.Key,
            makeDefault,
            existing?.LinkedAt ?? DateTimeOffset.UtcNow,
            copilotEntitled ?? existing?.CopilotEntitled,
            copilotEntitledCheckedAt ?? existing?.CopilotEntitledCheckedAt,
            token.AvatarUrl);
        return Task.CompletedTask;
    }

    public Task<bool> SetDefaultLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        if (!_links.TryGetValue(entraUserId, out var links)
            || !links.TryGetValue(githubLogin, out var selected))
            return Task.FromResult(false);

        foreach (var pair in links.ToArray())
            links[pair.Key] = pair.Value with { IsDefault = string.Equals(pair.Key, githubLogin, StringComparison.Ordinal) };

        return Task.FromResult(true);
    }

    public Task<bool> UnlinkIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        if (!_links.TryGetValue(entraUserId, out var links)
            || !links.TryRemove(githubLogin, out var removed))
            return Task.FromResult(false);

        _entries[removed.TokenScopeKey] = new StoreEntry(GitHubTokenStatus.SignedOut, null);

        if (removed.IsDefault && links.Count > 0)
        {
            var replacement = links.Values.OrderBy(x => x.LinkedAt).First();
            links[replacement.GitHubLogin] = replacement with { IsDefault = true };
        }

        if (links.IsEmpty)
            _links.TryRemove(entraUserId, out _);

        return Task.FromResult(true);
    }
}
