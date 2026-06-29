using Agentweaver.Domain;

namespace Agentweaver.AgentHost;

/// <summary>
/// <see cref="IGitHubTokenStore"/> that reads tokens from CSI-mounted Key Vault files (Option B).
/// The CSI secrets-store driver writes token files as <c>{mountPath}/user_{userId}.json</c>,
/// refreshing every 2 minutes from Key Vault.
///
/// <para>
/// Cold-start polling: on pod startup the CSI driver may not have written the file yet.
/// <see cref="GetAsync"/> and <see cref="GetTokenAsync"/> retry up to <see cref="DefaultMaxAttempts"/>
/// times with <see cref="DefaultDelayMs"/> between attempts when the token is absent.
/// </para>
///
/// <para>
/// Token refresh: each call re-reads from disk, so a CSI rotation is picked up automatically
/// on the next call without any in-memory staleness.
/// </para>
/// </summary>
internal sealed class CsiMountedGitHubTokenStore : IGitHubTokenStore
{
    private readonly SharedHomeGitHubTokenStore _inner;
    private readonly int _maxAttempts;
    private readonly int _delayMs;

    internal const int DefaultMaxAttempts = 6;   // 6 × 5s = 30s cold-start window
    internal const int DefaultDelayMs = 5_000;

    public CsiMountedGitHubTokenStore(string mountPath, int maxAttempts = DefaultMaxAttempts, int delayMs = DefaultDelayMs)
    {
        _inner = new SharedHomeGitHubTokenStore(mountPath);
        _maxAttempts = maxAttempts;
        _delayMs = delayMs;
    }

    public async Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            var entry = await _inner.GetAsync(scope, ct).ConfigureAwait(false);
            if (entry.Status != GitHubTokenStatus.NeverSignedIn)
                return entry;
            if (attempt < _maxAttempts - 1)
                await Task.Delay(_delayMs, ct).ConfigureAwait(false);
        }
        return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
    }

    public async Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < _maxAttempts; attempt++)
        {
            var token = await _inner.GetTokenAsync(scope, ct).ConfigureAwait(false);
            if (token is not null)
                return token;
            if (attempt < _maxAttempts - 1)
                await Task.Delay(_delayMs, ct).ConfigureAwait(false);
        }
        return null;
    }

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
        => _inner.GetIdentityAsync(scope, ct);

    // Mutations are no-ops — pods are read-only consumers of the CSI-mounted token.
    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
        => Task.CompletedTask;
}
