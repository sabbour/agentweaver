using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Caching decorator for <see cref="IGitHubTokenStore"/>.
/// Caches signed-in tokens, signed-out tombstones, and NeverSignedIn negatives so
/// hot-path reads (every API request) don't hit the backing store repeatedly.
///
/// TTL: 30 s (configurable).
/// Eviction: on <see cref="SetAsync"/> or <see cref="SignOutAsync"/> for the affected scope.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class CachingGitHubTokenStore : IGitHubTokenStore, IDistributedGitHubTokenRefreshLeaseStore
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly IGitHubTokenStore _inner;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _localLeaseGates = new(StringComparer.Ordinal);

    private sealed record CachedEntry(
        GitHubTokenStatus Status,
        GitHubToken? Token,
        DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new(StringComparer.Ordinal);

    public CachingGitHubTokenStore(IGitHubTokenStore inner, TimeSpan? ttl = null)
    {
        _inner = inner;
        _ttl = ttl ?? TimeSpan.FromSeconds(30);
    }

    // ── IGitHubTokenStore ─────────────────────────────────────────────────────

    public async Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var cached = TryGetCached(scope);
        if (cached is not null)
            return new GitHubTokenEntry(cached.Status, cached.Token?.AccessToken);

        var entry = await _inner.GetAsync(scope, ct).ConfigureAwait(false);

        // We have the entry status but may need the full token for the cache.
        // If signed-in, also fetch the full token to populate the cache completely.
        GitHubToken? full = null;
        if (entry.Status == GitHubTokenStatus.SignedIn)
            full = await _inner.GetTokenAsync(scope, ct).ConfigureAwait(false);

        SetCache(scope, entry.Status, full);
        return entry;
    }

    public async Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var cached = TryGetCached(scope);
        if (cached is not null)
            return cached.Status == GitHubTokenStatus.SignedIn ? cached.Token : null;

        var token = await _inner.GetTokenAsync(scope, ct).ConfigureAwait(false);

        var status = token is not null
            ? GitHubTokenStatus.SignedIn
            : GitHubTokenStatus.NeverSignedIn; // treat as not-signed-in for cache; GetAsync provides authoritative status
        SetCache(scope, status, token);
        return token;
    }

    public async Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        Evict(scope);
        await _inner.SetAsync(scope, token, ct).ConfigureAwait(false);
        SetCache(scope, GitHubTokenStatus.SignedIn, token);
    }

    public async Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var cached = TryGetCached(scope);
        if (cached?.Token is not null)
            return new GitHubIdentity(cached.Token.Login, cached.Token.AvatarUrl);

        return await _inner.GetIdentityAsync(scope, ct).ConfigureAwait(false);
    }

    public async Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        Evict(scope);
        await _inner.SignOutAsync(scope, ct).ConfigureAwait(false);
        SetCache(scope, GitHubTokenStatus.SignedOut, null);
    }

    public Task<IDistributedGitHubTokenRefreshLease?> TryAcquireRefreshLeaseAsync(
        GitHubTokenScope scope,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        if (_inner is IDistributedGitHubTokenRefreshLeaseStore leaseStore)
            return leaseStore.TryAcquireRefreshLeaseAsync(scope, owner, ttl, ct);

        return AcquireLocalRefreshLeaseAsync(scope, ct);
    }

    // ── Cache helpers ─────────────────────────────────────────────────────────

    private CachedEntry? TryGetCached(GitHubTokenScope scope)
    {
        if (_cache.TryGetValue(scope.Key, out var e) && e.ExpiresAt > DateTimeOffset.UtcNow)
            return e;
        return null;
    }

    private void SetCache(GitHubTokenScope scope, GitHubTokenStatus status, GitHubToken? token)
    {
        var expiresAt = DateTimeOffset.UtcNow + _ttl;
        if (token?.ExpiresAt is DateTimeOffset tokenExpiresAt)
        {
            var refreshBoundary = tokenExpiresAt - RefreshSkew;
            if (refreshBoundary < expiresAt)
                expiresAt = refreshBoundary;
        }

        _cache[scope.Key] = new CachedEntry(status, token, expiresAt);
    }

    private void Evict(GitHubTokenScope scope) => _cache.TryRemove(scope.Key, out _);

    private async Task<IDistributedGitHubTokenRefreshLease?> AcquireLocalRefreshLeaseAsync(
        GitHubTokenScope scope,
        CancellationToken ct)
    {
        var gate = _localLeaseGates.GetOrAdd(scope.Key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        return new LocalRefreshLease(gate);
    }

    private sealed class LocalRefreshLease : IDistributedGitHubTokenRefreshLease
    {
        private readonly SemaphoreSlim _gate;

        public LocalRefreshLease(SemaphoreSlim gate) => _gate = gate;

        public ValueTask DisposeAsync()
        {
            _gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
