using System.Collections.Concurrent;
using Scaffolder.Domain;

namespace Scaffolder.Api.Auth;

/// <summary>
/// In-memory GitHub token store. Used for development/testing where the OS credential
/// manager is not available. Tokens are stored in-process and lost on restart.
/// NOT suitable for production — use OsCredentialStoreGitHubTokenStore or
/// EncryptedSecretStoreGitHubTokenStore for production deployments.
/// </summary>
public sealed class InMemoryGitHubTokenStore : IGitHubTokenStore
{
    private readonly ConcurrentDictionary<string, StoreEntry> _entries = new(StringComparer.Ordinal);

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
}
