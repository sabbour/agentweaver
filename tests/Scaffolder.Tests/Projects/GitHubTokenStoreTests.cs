using FluentAssertions;
using Scaffolder.Api.Auth;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Projects;

/// <summary>
/// Tests for IGitHubTokenStore implementations:
/// InMemoryGitHubTokenStore tri-state lifecycle and per-scope isolation.
/// OsCredentialStoreGitHubTokenStore roundtrip (skipped on non-Windows/CI).
/// </summary>
public sealed class GitHubTokenStoreTests
{
    // =========================================================================
    // GTS-01: InMemory — initial state is NeverSignedIn
    // =========================================================================
    [Fact]
    public async Task InMemory_InitialState_IsNeverSignedIn()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;

        var entry = await store.GetAsync(scope);

        entry.Status.Should().Be(GitHubTokenStatus.NeverSignedIn);
        entry.AccessToken.Should().BeNull();
    }

    // =========================================================================
    // GTS-02: InMemory — SetAsync transitions to SignedIn
    // =========================================================================
    [Fact]
    public async Task InMemory_SetAsync_TransitionsToSignedIn()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        var token = new GitHubToken("ghp_test_access_token", null, null, "testuser", null, ["repo"]);

        await store.SetAsync(scope, token);
        var entry = await store.GetAsync(scope);

        entry.Status.Should().Be(GitHubTokenStatus.SignedIn);
        entry.AccessToken.Should().Be("ghp_test_access_token");
    }

    // =========================================================================
    // GTS-03: InMemory — SignOutAsync writes SignedOut tombstone
    // =========================================================================
    [Fact]
    public async Task InMemory_SignOutAsync_WritesTombstone()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;
        var token = new GitHubToken("ghp_test_access_token", null, null, "testuser", null, ["repo"]);

        await store.SetAsync(scope, token);
        await store.SignOutAsync(scope);
        var entry = await store.GetAsync(scope);

        entry.Status.Should().Be(GitHubTokenStatus.SignedOut,
            "explicit sign-out must write a SignedOut tombstone");
        entry.AccessToken.Should().BeNull();
    }

    // =========================================================================
    // GTS-04: InMemory — full lifecycle NeverSignedIn -> SignedIn -> SignedOut
    // =========================================================================
    [Fact]
    public async Task InMemory_FullLifecycle()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;

        // NeverSignedIn
        (await store.GetAsync(scope)).Status.Should().Be(GitHubTokenStatus.NeverSignedIn);

        // SignedIn
        await store.SetAsync(scope, new GitHubToken("tok", null, null, "user", null, []));
        (await store.GetAsync(scope)).Status.Should().Be(GitHubTokenStatus.SignedIn);

        // SignedOut
        await store.SignOutAsync(scope);
        (await store.GetAsync(scope)).Status.Should().Be(GitHubTokenStatus.SignedOut);
    }

    // =========================================================================
    // GTS-05: InMemory — per-scope isolation: two scopes don't clobber each other
    // =========================================================================
    [Fact]
    public async Task InMemory_PerScopeIsolation()
    {
        var store  = new InMemoryGitHubTokenStore();
        var scope1 = GitHubTokenScope.ForUser("user-alice");
        var scope2 = GitHubTokenScope.ForUser("user-bob");

        await store.SetAsync(scope1, new GitHubToken("token-alice", null, null, "alice", null, []));
        // scope2 never set

        var entry1 = await store.GetAsync(scope1);
        var entry2 = await store.GetAsync(scope2);

        entry1.Status.Should().Be(GitHubTokenStatus.SignedIn);
        entry1.AccessToken.Should().Be("token-alice");
        entry2.Status.Should().Be(GitHubTokenStatus.NeverSignedIn,
            "scope2 must be completely independent of scope1");
    }

    // =========================================================================
    // GTS-06: InMemory — signing out scope1 does not affect scope2
    // =========================================================================
    [Fact]
    public async Task InMemory_SignOut_DoesNotAffectOtherScope()
    {
        var store  = new InMemoryGitHubTokenStore();
        var scope1 = GitHubTokenScope.ForUser("user-a");
        var scope2 = GitHubTokenScope.ForUser("user-b");

        await store.SetAsync(scope1, new GitHubToken("tok-a", null, null, "a", null, []));
        await store.SetAsync(scope2, new GitHubToken("tok-b", null, null, "b", null, []));

        await store.SignOutAsync(scope1);

        (await store.GetAsync(scope1)).Status.Should().Be(GitHubTokenStatus.SignedOut);
        (await store.GetAsync(scope2)).Status.Should().Be(GitHubTokenStatus.SignedIn,
            "signing out scope1 must not affect scope2");
    }

    // =========================================================================
    // GTS-07: InMemory — GetIdentityAsync returns login when signed in
    // =========================================================================
    [Fact]
    public async Task InMemory_GetIdentityAsync_ReturnsLogin_WhenSignedIn()
    {
        var store = new InMemoryGitHubTokenStore();
        var scope = GitHubTokenScope.Installation;

        await store.SetAsync(scope, new GitHubToken("tok", null, null, "mylogin", null, []));
        var identity = await store.GetIdentityAsync(scope);

        identity.Should().NotBeNull();
        identity!.Login.Should().Be("mylogin");
    }

    // =========================================================================
    // GTS-08: OsCredentialStore — roundtrip (Windows only; skip on CI/non-Windows)
    // =========================================================================
    [Fact]
    public async Task OsCredentialStore_Roundtrip_OnWindows()
    {
        if (!System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(
                System.Runtime.InteropServices.OSPlatform.Windows)
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            // Skip: Windows Credential Manager not available in this environment.
            return;
        }

        var store = new OsCredentialStoreGitHubTokenStore();
        var scope = GitHubTokenScope.ForUser($"test-roundtrip-{Guid.NewGuid():N}");

        try
        {
            // Initial state
            var initial = await store.GetAsync(scope);
            initial.Status.Should().Be(GitHubTokenStatus.NeverSignedIn);

            // Set
            await store.SetAsync(scope, new GitHubToken("ghp_os_test", null, null, "osuser", null, []));
            var after = await store.GetAsync(scope);
            after.Status.Should().Be(GitHubTokenStatus.SignedIn);
            after.AccessToken.Should().Be("ghp_os_test");

            // Sign out
            await store.SignOutAsync(scope);
            var siggedOut = await store.GetAsync(scope);
            siggedOut.Status.Should().Be(GitHubTokenStatus.SignedOut);
        }
        finally
        {
            // Best-effort cleanup: sign out to leave a tombstone (no delete API exposed).
            try { await store.SignOutAsync(scope); } catch { /* ignored */ }
        }
    }
}
