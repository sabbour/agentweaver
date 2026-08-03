using System.Collections.Concurrent;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// Outermost <see cref="IGitHubTokenStore"/> decorator that makes the caller's ACTIVE linked GitHub
/// account the token the rest of the platform sees.
///
/// <para>
/// With Entra sign-in the authenticated caller id is the Entra object id, so every consumer resolves
/// the legacy <see cref="GitHubTokenScope.ForUser(string)"/> scope (<c>user:{oid}</c>) — a scope that
/// is never written in Entra mode, because linked GitHub credentials are persisted per identity under
/// <c>user-link:{oid}:{login}</c>. The result was that a signed-in user with linked GitHub accounts
/// behaved as if they had no GitHub token at all: no Copilot entitlement, no run/session starts, no
/// generation.
/// </para>
///
/// <para>
/// This decorator closes that gap centrally instead of threading async identity resolution through
/// every <see cref="IGitHubTokenScopeProvider"/> call site: a <c>user:{id}</c> scope is transparently
/// rewritten to the default (active) linked identity's scope whenever <c>{id}</c> has linked GitHub
/// accounts. Reads AND writes are rewritten consistently, so refresh-token rotation
/// (<see cref="GitHubTokenRefreshService"/>) persists back onto the linked identity rather than
/// stamping a divergent copy onto the unused legacy scope. Deployments without linked identities
/// (GitHubLegacy mode) are unaffected — the link index is empty, so the scope passes through.
/// </para>
///
/// <para>
/// The link-index lookup is memoized for a short TTL and evicted on link/unlink/set-default so the
/// hot path does not issue an extra Key Vault read per request while account switches still take
/// effect immediately on the replica that served the switch (and within the TTL elsewhere).
/// </para>
/// </summary>
public sealed class LinkedIdentityGitHubTokenStore
    : IMultiIdentityGitHubTokenStore, IDistributedGitHubTokenRefreshLeaseStore, IEffectiveGitHubTokenScopeResolver
{
    private const string UserScopePrefix = "user:";

    private readonly IGitHubTokenStore _inner;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<string, CachedResolution> _resolved = new(StringComparer.Ordinal);

    private sealed record CachedResolution(string? LinkedLogin, DateTimeOffset ExpiresAt);

    public LinkedIdentityGitHubTokenStore(IGitHubTokenStore inner, TimeSpan? ttl = null)
    {
        _inner = inner;
        _ttl = ttl ?? TimeSpan.FromSeconds(30);
    }

    // ── IGitHubTokenStore ─────────────────────────────────────────────────────

    public async Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        await _inner.GetAsync(await ResolveAsync(scope, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        await _inner.GetTokenAsync(await ResolveAsync(scope, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) =>
        await _inner.SetAsync(await ResolveAsync(scope, ct).ConfigureAwait(false), token, ct).ConfigureAwait(false);

    public async Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        await _inner.GetIdentityAsync(await ResolveAsync(scope, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        await _inner.SignOutAsync(await ResolveAsync(scope, ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    // ── IEffectiveGitHubTokenScopeResolver ────────────────────────────────────

    public Task<GitHubTokenScope> ResolveEffectiveScopeAsync(string userId, CancellationToken ct = default) =>
        ResolveAsync(GitHubTokenScope.ForUser(userId), ct);

    // ── IDistributedGitHubTokenRefreshLeaseStore ─────────────────────────────

    public async Task<IDistributedGitHubTokenRefreshLease?> TryAcquireRefreshLeaseAsync(
        GitHubTokenScope scope,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        if (_inner is not IDistributedGitHubTokenRefreshLeaseStore leaseStore)
            return null;

        var effective = await ResolveAsync(scope, ct).ConfigureAwait(false);
        return await leaseStore.TryAcquireRefreshLeaseAsync(effective, owner, ttl, ct).ConfigureAwait(false);
    }

    // ── IMultiIdentityGitHubTokenStore ───────────────────────────────────────

    public Task<IReadOnlyList<GitHubIdentityLink>> ListLinkedIdentitiesAsync(
        string entraUserId,
        CancellationToken ct = default)
        => RequireMultiIdentity().ListLinkedIdentitiesAsync(entraUserId, ct);

    public Task<GitHubIdentityLink?> GetLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
        => RequireMultiIdentity().GetLinkedIdentityAsync(entraUserId, githubLogin, ct);

    public Task<GitHubIdentityLink?> GetDefaultLinkedIdentityAsync(
        string entraUserId,
        CancellationToken ct = default)
        => RequireMultiIdentity().GetDefaultLinkedIdentityAsync(entraUserId, ct);

    public async Task LinkIdentityAsync(
        string entraUserId,
        GitHubToken token,
        bool isDefault = false,
        bool? copilotEntitled = null,
        DateTimeOffset? copilotEntitledCheckedAt = null,
        CancellationToken ct = default)
    {
        await RequireMultiIdentity()
            .LinkIdentityAsync(entraUserId, token, isDefault, copilotEntitled, copilotEntitledCheckedAt, ct)
            .ConfigureAwait(false);
        Invalidate(entraUserId);
    }

    public async Task<bool> SetDefaultLinkedIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var changed = await RequireMultiIdentity()
            .SetDefaultLinkedIdentityAsync(entraUserId, githubLogin, ct)
            .ConfigureAwait(false);
        Invalidate(entraUserId);
        return changed;
    }

    public async Task<bool> UnlinkIdentityAsync(
        string entraUserId,
        string githubLogin,
        CancellationToken ct = default)
    {
        var changed = await RequireMultiIdentity()
            .UnlinkIdentityAsync(entraUserId, githubLogin, ct)
            .ConfigureAwait(false);
        Invalidate(entraUserId);
        return changed;
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    private async Task<GitHubTokenScope> ResolveAsync(GitHubTokenScope scope, CancellationToken ct)
    {
        if (!scope.Key.StartsWith(UserScopePrefix, StringComparison.Ordinal))
            return scope;

        var userId = scope.Key[UserScopePrefix.Length..];
        if (string.IsNullOrWhiteSpace(userId))
            return scope;

        var login = await ResolveActiveLoginAsync(userId, ct).ConfigureAwait(false);
        return login is null ? scope : GitHubTokenScope.ForLinkedIdentity(userId, login);
    }

    private async Task<string?> ResolveActiveLoginAsync(string userId, CancellationToken ct)
    {
        if (_resolved.TryGetValue(userId, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.LinkedLogin;

        string? login = null;
        if (_inner is IMultiIdentityGitHubTokenStore multi)
        {
            var links = await multi.ListLinkedIdentitiesAsync(userId, ct).ConfigureAwait(false);
            // Prefer the explicit default (the account the user marked active); fall back to the only
            // / first linked account so a user whose index somehow lost its default flag is not left
            // without a usable token.
            login = (links.FirstOrDefault(x => x.IsDefault) ?? links.FirstOrDefault())?.GitHubLogin;
        }

        _resolved[userId] = new CachedResolution(login, DateTimeOffset.UtcNow + _ttl);
        return login;
    }

    private void Invalidate(string userId) => _resolved.TryRemove(userId, out _);

    private IMultiIdentityGitHubTokenStore RequireMultiIdentity() =>
        _inner as IMultiIdentityGitHubTokenStore
        ?? throw new NotSupportedException("The wrapped GitHub token store does not support multi-identity operations.");
}
