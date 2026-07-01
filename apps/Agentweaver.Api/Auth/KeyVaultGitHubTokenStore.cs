using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Domain;

namespace Agentweaver.Api.Auth;

/// <summary>
/// <see cref="IGitHubTokenStore"/> backed by an <see cref="ISecretStore"/> (e.g. Azure Key Vault).
/// Keeps the same <see cref="StoredCredential"/> JSON shape as
/// <see cref="FileSystemGitHubTokenStore"/> so values round-trip disk ↔ KV identically.
///
/// Status semantics
///   absent                          → NeverSignedIn
///   present + status="signed-out"   → SignedOut  (tombstone; not deleted to avoid KV purge conflicts)
///   present + status="signed-in"    → SignedIn
///   malformed                       → NeverSignedIn (defensive)
///
/// Concurrency: refresh-token rotation is serialized through the backing store's
/// <see cref="IAtomicSecretLeaseStore"/> implementation. Value-write ETags remain
/// a best-effort stale-write guard for stores that support atomic conditional writes.
///
/// Migration/back-compat: when the KV secret is absent and a <paramref name="diskFallback"/>
/// is provided, the on-disk token is lazily read and written through to KV.
/// A KV tombstone (signed-out) always wins over any disk value.
///
/// Disk mirror: after every successful SetAsync, the token is also written to
/// <paramref name="diskMirror"/> so pods that read the shared filesystem file remain
/// functional in phase-1 (before they are updated to call the API).
/// </summary>
public sealed class KeyVaultGitHubTokenStore : IGitHubTokenStore, IDistributedGitHubTokenRefreshLeaseStore
{
    private readonly ISecretStore _secretStore;
    private readonly FileSystemGitHubTokenStore? _diskFallback; // lazy migration source
    private readonly FileSystemGitHubTokenStore? _diskMirror;   // post-write mirror

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    public KeyVaultGitHubTokenStore(
        ISecretStore secretStore,
        FileSystemGitHubTokenStore? diskFallback = null,
        FileSystemGitHubTokenStore? diskMirror = null)
    {
        _secretStore = secretStore;
        _diskFallback = diskFallback;
        _diskMirror = diskMirror;
    }

    // ── IGitHubTokenStore ────────────────────────────────────────────────────

    public async Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var result = await _secretStore.GetSecretAsync(scope.Key, ct).ConfigureAwait(false);
        if (result.Found)
            return ParseEntry(result.Value);

        // KV secret absent — attempt lazy migration from disk.
        if (_diskFallback is not null)
        {
            var diskEntry = await _diskFallback.GetAsync(scope, ct).ConfigureAwait(false);
            if (diskEntry.Status == GitHubTokenStatus.SignedIn)
            {
                var full = await _diskFallback.GetTokenAsync(scope, ct).ConfigureAwait(false);
                if (full is not null)
                    await SetAsync(scope, full, ct).ConfigureAwait(false); // write-through
                return diskEntry;
            }
        }

        return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
    }

    public async Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var result = await _secretStore.GetSecretAsync(scope.Key, ct).ConfigureAwait(false);
        if (result.Found)
            return ParseToken(result.Value);

        // KV absent — lazy migration.
        if (_diskFallback is not null)
        {
            var full = await _diskFallback.GetTokenAsync(scope, ct).ConfigureAwait(false);
            if (full is not null)
                await SetAsync(scope, full, ct).ConfigureAwait(false); // write-through
            return full;
        }

        return null;
    }

    public async Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default)
    {
        var stored = new StoredCredential
        {
            Status = "signed-in",
            AccessToken = token.AccessToken,
            RefreshToken = token.RefreshToken,
            ExpiresAt = token.ExpiresAt,
            Login = token.Login,
            AvatarUrl = token.AvatarUrl,
            Scopes = token.Scopes,
        };
        var json = JsonSerializer.Serialize(stored, _json);

        // Best-effort stale-write guard; refresh-token rotation is protected by the
        // atomic lease acquired before the GitHub refresh call.
        var current = await _secretStore.GetSecretAsync(scope.Key, ct).ConfigureAwait(false);
        var currentETag = current.Found ? current.ETag : null;

        try
        {
            await _secretStore.SetSecretAsync(scope.Key, json, currentETag, ct).ConfigureAwait(false);
        }
        catch (SecretPreconditionFailedException)
        {
            // Another writer got there first — adopt the winner; don't overwrite with our stale token.
            return;
        }

        // Mirror to disk so pods reading the shared filesystem file still work.
        if (_diskMirror is not null)
        {
            try { await _diskMirror.SetAsync(scope, token, ct).ConfigureAwait(false); }
            catch (Exception) { /* best effort */ }
        }
    }

    public async Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        var result = await _secretStore.GetSecretAsync(scope.Key, ct).ConfigureAwait(false);
        if (!result.Found)
            return null;

        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(result.Value!, _json);
            if (stored?.Login is not null)
                return new GitHubIdentity(stored.Login, stored.AvatarUrl);
        }
        catch (Exception) { }

        return null;
    }

    public async Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default)
    {
        // Write a tombstone — do NOT delete, to avoid KV soft-delete/purge conflicts on re-login.
        var tombstone = new StoredCredential { Status = "signed-out" };
        var json = JsonSerializer.Serialize(tombstone, _json);
        // No ETag check for sign-out: the tombstone must always win.
        await _secretStore.SetSecretAsync(scope.Key, json, etag: null, ct).ConfigureAwait(false);

        if (_diskMirror is not null)
        {
            try { await _diskMirror.SignOutAsync(scope, ct).ConfigureAwait(false); }
            catch (Exception) { /* best effort */ }
        }
    }

    public async Task<IDistributedGitHubTokenRefreshLease?> TryAcquireRefreshLeaseAsync(
        GitHubTokenScope scope,
        string owner,
        TimeSpan ttl,
        CancellationToken ct = default)
    {
        if (_secretStore is not IAtomicSecretLeaseStore leaseStore)
            return null;

        var lease = await leaseStore.TryAcquireLeaseAsync(scope.Key, owner, ttl, ct).ConfigureAwait(false);
        return lease is null ? null : new SecretStoreRefreshLease(lease);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static GitHubTokenEntry ParseEntry(string? json)
    {
        if (json is null)
            return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(json, _json);
            if (stored?.Status == "signed-out")
                return new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null);
            if (stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken))
                return new GitHubTokenEntry(GitHubTokenStatus.SignedIn, stored.AccessToken);
        }
        catch (Exception) { /* malformed */ }
        return new GitHubTokenEntry(GitHubTokenStatus.NeverSignedIn, null);
    }

    private static GitHubToken? ParseToken(string? json)
    {
        if (json is null) return null;
        try
        {
            var stored = JsonSerializer.Deserialize<StoredCredential>(json, _json);
            if (stored?.Status == "signed-in" && !string.IsNullOrEmpty(stored.AccessToken))
                return new GitHubToken(
                    stored.AccessToken!,
                    stored.RefreshToken,
                    stored.ExpiresAt,
                    stored.Login ?? "unknown",
                    stored.AvatarUrl,
                    stored.Scopes ?? []);
        }
        catch (Exception) { /* malformed */ }
        return null;
    }

    // ── JSON shape ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shared JSON shape used by FileSystemGitHubTokenStore, OsCredentialStoreGitHubTokenStore,
    /// and KeyVaultGitHubTokenStore so values round-trip across backends identically.
    /// </summary>
    internal sealed record StoredCredential
    {
        [JsonPropertyName("Status")]   public string? Status { get; init; }
        [JsonPropertyName("AccessToken")]  public string? AccessToken { get; init; }
        [JsonPropertyName("RefreshToken")] public string? RefreshToken { get; init; }
        [JsonPropertyName("ExpiresAt")]    public DateTimeOffset? ExpiresAt { get; init; }
        [JsonPropertyName("Login")]        public string? Login { get; init; }
        [JsonPropertyName("AvatarUrl")]    public string? AvatarUrl { get; init; }
        [JsonPropertyName("Scopes")]       public string[]? Scopes { get; init; }
    }

    private sealed class SecretStoreRefreshLease(ISecretStoreLease inner) : IDistributedGitHubTokenRefreshLease
    {
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
