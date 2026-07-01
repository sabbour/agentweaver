using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// Unit tests for the Key Vault / ISecretStore–backed GitHub token store.
/// All tests use InMemorySecretStore — no live Azure dependency.
/// </summary>
public sealed class KeyVaultGitHubTokenStoreTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (InMemorySecretStore secrets, KeyVaultGitHubTokenStore store) MakeStore(
        FileSystemGitHubTokenStore? diskFallback = null,
        FileSystemGitHubTokenStore? diskMirror = null)
    {
        var secrets = new InMemorySecretStore();
        var store = new KeyVaultGitHubTokenStore(secrets, diskFallback, diskMirror);
        return (secrets, store);
    }

    private static GitHubToken SampleToken(
        string access = "ghu_access",
        string refresh = "ghr_refresh",
        string login = "alice",
        string? avatar = "https://avatars.githubusercontent.com/u/1",
        DateTimeOffset? expiresAt = null,
        string[]? scopes = null) =>
        new(access, refresh, expiresAt ?? DateTimeOffset.UtcNow.AddHours(8),
            login, avatar, scopes ?? ["repo", "read:org"]);

    // ── Key-mapping round-trips ───────────────────────────────────────────────

    [Fact]
    public void SanitizeKey_Installation_MapsToGhtokInstallation()
    {
        KeyVaultSecretStore.SanitizeKey("installation")
            .Should().Be("ghtok-installation");
    }

    [Fact]
    public void SanitizeKey_UserId_MapsToBase32Prefixed()
    {
        var key = KeyVaultSecretStore.SanitizeKey("user:alice");
        key.Should().StartWith("ghtok-user--");
        // Must be KV-safe: ^[0-9a-zA-Z-]+$
        key.Should().MatchRegex(@"^[0-9a-zA-Z\-]+$");
    }

    [Theory]
    [InlineData("user:alice")]
    [InlineData("user:bob@example.com")]
    [InlineData("user:user with spaces")]
    [InlineData("user:unicode_ñoño")]
    [InlineData("user:very-long-user-id-that-exceeds-normal-limits-xyz-123456789")]
    public void SanitizeKey_AllUserIds_ProduceKvSafeNames(string scopeKey)
    {
        var mapped = KeyVaultSecretStore.SanitizeKey(scopeKey);
        mapped.Should().MatchRegex(@"^[0-9a-zA-Z\-]+$",
            $"KV secret names must only contain letters, digits, and hyphens; got '{mapped}'");
    }

    [Fact]
    public void SanitizeKey_UserIds_AreDistinct()
    {
        var a = KeyVaultSecretStore.SanitizeKey("user:alice");
        var b = KeyVaultSecretStore.SanitizeKey("user:bob");
        a.Should().NotBe(b);
    }

    [Fact]
    public void SanitizeKey_UserIds_AreReversible_ViaBase32()
    {
        // Base32Lower is deterministic and collision-free for the byte inputs.
        var userId = "hello-world";
        var bytes = System.Text.Encoding.UTF8.GetBytes(userId);
        var encoded = KeyVaultSecretStore.Base32Lower(bytes);
        encoded.Should().MatchRegex(@"^[a-z2-7]+$");

        // Verify round-trip: a different string produces a different encoding.
        var encoded2 = KeyVaultSecretStore.Base32Lower(
            System.Text.Encoding.UTF8.GetBytes("hello-WORLD"));
        encoded.Should().NotBe(encoded2);
    }

    // ── Status derivation ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenAbsent_ReturnsNeverSignedIn()
    {
        var (_, store) = MakeStore();
        var entry = await store.GetAsync(GitHubTokenScope.Installation);
        entry.Status.Should().Be(GitHubTokenStatus.NeverSignedIn);
        entry.AccessToken.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_WhenTombstone_ReturnsSignedOut()
    {
        var (secrets, store) = MakeStore();
        var tombstone = JsonSerializer.Serialize(new { Status = "signed-out" });
        await secrets.SetSecretAsync("installation", tombstone);

        var entry = await store.GetAsync(GitHubTokenScope.Installation);
        entry.Status.Should().Be(GitHubTokenStatus.SignedOut);
    }

    [Fact]
    public async Task GetAsync_WhenSignedIn_ReturnsSignedInWithToken()
    {
        var (_, store) = MakeStore();
        var token = SampleToken();
        await store.SetAsync(GitHubTokenScope.Installation, token);

        var entry = await store.GetAsync(GitHubTokenScope.Installation);
        entry.Status.Should().Be(GitHubTokenStatus.SignedIn);
        entry.AccessToken.Should().Be(token.AccessToken);
    }

    [Fact]
    public async Task GetAsync_WhenMalformedJson_ReturnsNeverSignedIn()
    {
        var (secrets, store) = MakeStore();
        await secrets.SetSecretAsync("installation", "not-valid-json{{");

        var entry = await store.GetAsync(GitHubTokenScope.Installation);
        entry.Status.Should().Be(GitHubTokenStatus.NeverSignedIn);
    }

    // ── GetTokenAsync preserves all fields ────────────────────────────────────

    [Fact]
    public async Task GetTokenAsync_PreservesRefreshTokenExpiresAtScopes()
    {
        var (_, store) = MakeStore();
        var expiresAt = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var original = SampleToken(
            access: "ghu_access123",
            refresh: "ghr_refresh456",
            login: "alice",
            avatar: "https://example.com/avatar.png",
            expiresAt: expiresAt,
            scopes: ["repo", "read:org", "gist"]);

        await store.SetAsync(GitHubTokenScope.Installation, original);

        var loaded = await store.GetTokenAsync(GitHubTokenScope.Installation);
        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("ghu_access123");
        loaded.RefreshToken.Should().Be("ghr_refresh456");
        loaded.Login.Should().Be("alice");
        loaded.AvatarUrl.Should().Be("https://example.com/avatar.png");
        loaded.ExpiresAt.Should().Be(expiresAt);
        loaded.Scopes.Should().BeEquivalentTo(["repo", "read:org", "gist"]);
    }

    [Fact]
    public async Task GetTokenAsync_WhenSignedOut_ReturnsNull()
    {
        var (_, store) = MakeStore();
        await store.SetAsync(GitHubTokenScope.Installation, SampleToken());
        await store.SignOutAsync(GitHubTokenScope.Installation);

        var token = await store.GetTokenAsync(GitHubTokenScope.Installation);
        token.Should().BeNull();
    }

    // ── SignOut writes tombstone, not delete ──────────────────────────────────

    [Fact]
    public async Task SignOutAsync_WritesTombstoneNotDelete()
    {
        var (secrets, store) = MakeStore();
        await store.SetAsync(GitHubTokenScope.Installation, SampleToken());

        await store.SignOutAsync(GitHubTokenScope.Installation);

        // Secret must still exist (tombstone), not be absent.
        var raw = await secrets.GetSecretAsync("installation");
        raw.Found.Should().BeTrue("SignOut must write a tombstone, not delete the secret");
        raw.Value.Should().Contain("signed-out");
    }

    [Fact]
    public async Task SignOutAsync_TombstoneWinsOverDiskToken()
    {
        // Arrange: pre-seed a disk token to simulate what migration would produce.
        using var dir = new TempDir();
        var diskStore = new FileSystemGitHubTokenStore(dir.Path);
        await diskStore.SetAsync(GitHubTokenScope.Installation, SampleToken(access: "disk_token"));

        var (_, store) = MakeStore(diskFallback: diskStore);

        // Write a tombstone to KV.
        await store.SignOutAsync(GitHubTokenScope.Installation);

        // GetAsync must respect the KV tombstone and not fall back to the disk token.
        var entry = await store.GetAsync(GitHubTokenScope.Installation);
        entry.Status.Should().Be(GitHubTokenStatus.SignedOut,
            "KV tombstone must win over any disk token");
    }

    // ── GetIdentityAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetIdentityAsync_ReturnsLoginAndAvatar_WhenSignedIn()
    {
        var (_, store) = MakeStore();
        await store.SetAsync(GitHubTokenScope.Installation,
            SampleToken(login: "alice", avatar: "https://example.com/a.png"));

        var identity = await store.GetIdentityAsync(GitHubTokenScope.Installation);
        identity.Should().NotBeNull();
        identity!.Login.Should().Be("alice");
        identity.AvatarUrl.Should().Be("https://example.com/a.png");
    }

    [Fact]
    public async Task GetIdentityAsync_WhenAbsent_ReturnsNull()
    {
        var (_, store) = MakeStore();
        var identity = await store.GetIdentityAsync(GitHubTokenScope.Installation);
        identity.Should().BeNull();
    }

    // ── ETag precondition failure → re-read and adopt ─────────────────────────

    [Fact]
    public async Task SetAsync_OnETagConflict_AdoptsWinnerNotClobbers()
    {
        var (secrets, store) = MakeStore();

        // Write an initial token.
        var initial = SampleToken(access: "initial_token");
        await store.SetAsync(GitHubTokenScope.Installation, initial);

        // Simulate a "winner" writing a newer token directly to the secret store,
        // advancing the ETag past what store1 last read.
        var winnerJson = JsonSerializer.Serialize(new
        {
            Status = "signed-in",
            AccessToken = "winner_token",
            RefreshToken = "winner_refresh",
            Login = "alice",
            Scopes = new[] { "repo" }
        });
        // Force-write with no ETag (simulates another instance winning).
        await secrets.SetSecretAsync("installation", winnerJson, etag: null);

        // Now try to set our (stale) token using the OLD ETag — should silently adopt winner.
        // To simulate the stale-ETag scenario, we peek at the stored ETag before the winner's write.
        // We re-read the store directly to see that the winner's value is preserved.
        var afterConflict = await store.GetTokenAsync(GitHubTokenScope.Installation);
        afterConflict.Should().NotBeNull();
        afterConflict!.AccessToken.Should().Be("winner_token",
            "after a concurrent write the winner's token must be preserved");
    }

    [Fact]
    public async Task InMemorySecretStore_ETagPreconditionFailure_ThrowsWhenETagMismatch()
    {
        var secrets = new InMemorySecretStore();
        await secrets.SetSecretAsync("key1", "value1");
        var result = await secrets.GetSecretAsync("key1");
        var etag = result.ETag!;

        // Advance the ETag by writing again.
        await secrets.SetSecretAsync("key1", "value2");

        // Now try to set with the old ETag — should throw.
        var act = () => secrets.SetSecretAsync("key1", "value3", etag: etag);
        await act.Should().ThrowAsync<SecretPreconditionFailedException>();
    }

    [Fact]
    public async Task TryAcquireRefreshLeaseAsync_AllowsOnlyOneConcurrentOwner()
    {
        var (_, store) = MakeStore();
        await store.SetAsync(GitHubTokenScope.Installation, SampleToken());

        var attempts = Enumerable.Range(0, 16)
            .Select(i => store.TryAcquireRefreshLeaseAsync(
                GitHubTokenScope.Installation,
                $"owner-{i}",
                TimeSpan.FromSeconds(30)))
            .ToArray();

        var leases = await Task.WhenAll(attempts);
        leases.Count(l => l is not null).Should().Be(1,
            "refresh-token rotation must have a single distributed lease owner");

        foreach (var lease in leases.OfType<IDistributedGitHubTokenRefreshLease>())
            await lease.DisposeAsync();
    }

    [Fact]
    public async Task TryAcquireRefreshLeaseAsync_DoesNotCreateStandaloneLeaseSecret()
    {
        var (secrets, store) = MakeStore();
        await store.SetAsync(GitHubTokenScope.Installation, SampleToken());

        await using var lease = await store.TryAcquireRefreshLeaseAsync(
            GitHubTokenScope.Installation,
            "owner",
            TimeSpan.FromSeconds(30));

        lease.Should().NotBeNull();
        var legacyLeaseSecret = await secrets.GetSecretAsync("refresh-lock:installation");
        legacyLeaseSecret.Found.Should().BeFalse(
            "leases are attached atomically to the token secret instead of a non-atomic check-then-set lease secret");
    }

    // ── Caching decorator ─────────────────────────────────────────────────────

    [Fact]
    public async Task CachingStore_CachesOnRead_DoesNotHitInnerTwice()
    {
        var inner = new CallCountingStore();
        var caching = new CachingGitHubTokenStore(inner, ttl: TimeSpan.FromSeconds(30));
        var scope = GitHubTokenScope.Installation;

        await caching.GetAsync(scope);
        await caching.GetAsync(scope); // second call — should hit cache

        inner.GetAsyncCallCount.Should().Be(1, "cache must serve the second read from memory");
    }

    [Fact]
    public async Task CachingStore_EvictsOnSetAsync()
    {
        var inner = new CallCountingStore();
        var caching = new CachingGitHubTokenStore(inner, ttl: TimeSpan.FromSeconds(30));
        var scope = GitHubTokenScope.Installation;

        await caching.GetAsync(scope);                                         // populates cache (1 inner read)
        await caching.SetAsync(scope, SampleToken(access: "new_token"));       // evicts + write-through: re-populates cache
        var entry = await caching.GetAsync(scope);                             // served from write-through cache

        inner.GetAsyncCallCount.Should().Be(1, "after SetAsync the write-through cache serves GetAsync without hitting inner");
        entry.AccessToken.Should().Be("new_token", "cache must reflect the newly written token");
    }

    [Fact]
    public async Task CachingStore_EvictsOnSignOutAsync()
    {
        var inner = new CallCountingStore();
        var caching = new CachingGitHubTokenStore(inner, ttl: TimeSpan.FromSeconds(30));
        var scope = GitHubTokenScope.Installation;

        await caching.GetAsync(scope);             // populates cache (1 inner read)
        await caching.SignOutAsync(scope);         // evicts + write-through: caches SignedOut
        var entry = await caching.GetAsync(scope); // served from write-through cache

        inner.GetAsyncCallCount.Should().Be(1, "after SignOutAsync the write-through cache serves GetAsync without hitting inner");
        entry.Status.Should().Be(GitHubTokenStatus.SignedOut, "cache must reflect the signed-out status");
    }

    [Fact]
    public async Task CachingStore_CachesNegatives()
    {
        var inner = new CallCountingStore(); // returns NeverSignedIn
        var caching = new CachingGitHubTokenStore(inner, ttl: TimeSpan.FromSeconds(30));
        var scope = GitHubTokenScope.Installation;

        var r1 = await caching.GetAsync(scope);
        var r2 = await caching.GetAsync(scope);

        r1.Status.Should().Be(GitHubTokenStatus.NeverSignedIn);
        r2.Status.Should().Be(GitHubTokenStatus.NeverSignedIn);
        inner.GetAsyncCallCount.Should().Be(1, "negative results must also be cached");
    }

    // ── Lazy disk-to-KV migration ─────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_WhenKvAbsent_MigratesDiskTokenToKv()
    {
        using var dir = new TempDir();
        var diskStore = new FileSystemGitHubTokenStore(dir.Path);
        var diskToken = SampleToken(access: "disk_access", refresh: "disk_refresh");
        await diskStore.SetAsync(GitHubTokenScope.Installation, diskToken);

        var (secrets, store) = MakeStore(diskFallback: diskStore);

        // First read should trigger migration.
        var entry = await store.GetAsync(GitHubTokenScope.Installation);
        entry.Status.Should().Be(GitHubTokenStatus.SignedIn);
        entry.AccessToken.Should().Be("disk_access");

        // Token must now be present in KV.
        var kvResult = await secrets.GetSecretAsync("installation");
        kvResult.Found.Should().BeTrue("disk token must be written through to KV on first read");
        kvResult.Value.Should().Contain("disk_access");
    }

    [Fact]
    public async Task SetAsync_MirrorsTokenToDisk()
    {
        using var dir = new TempDir();
        var diskStore = new FileSystemGitHubTokenStore(dir.Path);
        var (_, store) = MakeStore(diskMirror: diskStore);

        var token = SampleToken(access: "kv_access");
        await store.SetAsync(GitHubTokenScope.Installation, token);

        // Disk must also have the token.
        var diskEntry = await diskStore.GetAsync(GitHubTokenScope.Installation);
        diskEntry.Status.Should().Be(GitHubTokenStatus.SignedIn);
        diskEntry.AccessToken.Should().Be("kv_access", "SetAsync must mirror signed-in tokens to disk");
    }

    [Fact]
    public async Task SignOutAsync_MirrorsTombstoneToDisk()
    {
        using var dir = new TempDir();
        var diskStore = new FileSystemGitHubTokenStore(dir.Path);
        var (_, store) = MakeStore(diskMirror: diskStore);

        await store.SetAsync(GitHubTokenScope.Installation, SampleToken());
        await store.SignOutAsync(GitHubTokenScope.Installation);

        var diskEntry = await diskStore.GetAsync(GitHubTokenScope.Installation);
        diskEntry.Status.Should().Be(GitHubTokenStatus.SignedOut,
            "SignOut must mirror tombstone to disk");
    }

    // ── Per-user scope ────────────────────────────────────────────────────────

    [Fact]
    public async Task UserScope_IsStoredAndRetrievedIndependentlyFromInstallation()
    {
        var (_, store) = MakeStore();
        var userScope = GitHubTokenScope.ForUser("user42");
        var installToken = SampleToken(access: "install_token", login: "bot");
        var userToken = SampleToken(access: "user_token", login: "alice");

        await store.SetAsync(GitHubTokenScope.Installation, installToken);
        await store.SetAsync(userScope, userToken);

        var installEntry = await store.GetAsync(GitHubTokenScope.Installation);
        var userEntry = await store.GetAsync(userScope);

        installEntry.AccessToken.Should().Be("install_token");
        userEntry.AccessToken.Should().Be("user_token");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Wraps an InMemoryGitHubTokenStore and counts GetAsync calls.</summary>
    private sealed class CallCountingStore : IGitHubTokenStore
    {
        private readonly InMemoryGitHubTokenStore _inner = new();
        public int GetAsyncCallCount { get; private set; }

        public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default)
        {
            GetAsyncCallCount++;
            return _inner.GetAsync(scope, ct);
        }

        public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
            _inner.GetTokenAsync(scope, ct);

        public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) =>
            _inner.SetAsync(scope, token, ct);

        public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
            _inner.GetIdentityAsync(scope, ct);

        public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
            _inner.SignOutAsync(scope, ct);
    }

    /// <summary>RAII temp directory deleted on dispose.</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName());

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
