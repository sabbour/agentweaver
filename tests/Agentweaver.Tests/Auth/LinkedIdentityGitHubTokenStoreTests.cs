using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// Regression tests for the Entra multi-account defect where a signed-in user with linked GitHub
/// accounts resolved the legacy <c>user:{oid}</c> scope (never written in Entra mode) and therefore
/// behaved as if no GitHub token existed at all.
/// </summary>
public sealed class LinkedIdentityGitHubTokenStoreTests
{
    private const string EntraUserId = "00000000-0000-0000-0000-0000000009a1";

    [Fact]
    public async Task GetToken_ForLegacyUserScope_ResolvesActiveLinkedIdentityToken()
    {
        var inner = new InMemoryGitHubTokenStore();
        var store = new LinkedIdentityGitHubTokenStore(inner);

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);
        await store.LinkIdentityAsync(EntraUserId, Token("tok-bob", "bob"));

        var entry = await store.GetAsync(GitHubTokenScope.ForUser(EntraUserId));
        entry.Status.Should().Be(GitHubTokenStatus.SignedIn);
        entry.AccessToken.Should().Be("tok-alice");

        (await store.GetTokenAsync(GitHubTokenScope.ForUser(EntraUserId)))!
            .AccessToken.Should().Be("tok-alice");
        (await store.GetIdentityAsync(GitHubTokenScope.ForUser(EntraUserId)))!
            .Login.Should().Be("alice");
    }

    [Fact]
    public async Task SwitchingActiveAccount_ChangesTheTokenSeenByLegacyUserScope()
    {
        var store = new LinkedIdentityGitHubTokenStore(new InMemoryGitHubTokenStore());

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);
        await store.LinkIdentityAsync(EntraUserId, Token("tok-bob", "bob"));

        (await store.SetDefaultLinkedIdentityAsync(EntraUserId, "bob")).Should().BeTrue();

        (await store.GetTokenAsync(GitHubTokenScope.ForUser(EntraUserId)))!
            .AccessToken.Should().Be("tok-bob");
        (await store.ResolveEffectiveScopeAsync(EntraUserId)).Key
            .Should().Be(GitHubTokenScope.ForLinkedIdentity(EntraUserId, "bob").Key);
    }

    [Fact]
    public async Task Set_ForLegacyUserScope_WritesBackOntoTheActiveLinkedIdentity()
    {
        var inner = new InMemoryGitHubTokenStore();
        var store = new LinkedIdentityGitHubTokenStore(inner);

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);

        // Simulates GitHubTokenRefreshService persisting a rotated token through the caller's scope.
        await store.SetAsync(GitHubTokenScope.ForUser(EntraUserId), Token("tok-alice-rotated", "alice"));

        (await inner.GetTokenAsync(GitHubTokenScope.ForLinkedIdentity(EntraUserId, "alice")))!
            .AccessToken.Should().Be("tok-alice-rotated");
        (await inner.GetAsync(GitHubTokenScope.ForUser(EntraUserId))).Status
            .Should().Be(GitHubTokenStatus.NeverSignedIn);
    }

    [Fact]
    public async Task Unlinking_TheOnlyAccount_FallsBackToTheLegacyScope()
    {
        var store = new LinkedIdentityGitHubTokenStore(new InMemoryGitHubTokenStore());

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);
        (await store.UnlinkIdentityAsync(EntraUserId, "alice")).Should().BeTrue();

        (await store.ResolveEffectiveScopeAsync(EntraUserId)).Key
            .Should().Be(GitHubTokenScope.ForUser(EntraUserId).Key);
        (await store.GetTokenAsync(GitHubTokenScope.ForUser(EntraUserId))).Should().BeNull();
    }

    [Fact]
    public async Task LegacyUsersWithoutLinkedAccounts_AreUnaffected()
    {
        var inner = new InMemoryGitHubTokenStore();
        var store = new LinkedIdentityGitHubTokenStore(inner);

        await store.SetAsync(GitHubTokenScope.ForUser("octocat"), Token("tok-legacy", "octocat"));

        (await store.ResolveEffectiveScopeAsync("octocat")).Key
            .Should().Be(GitHubTokenScope.ForUser("octocat").Key);
        (await inner.GetTokenAsync(GitHubTokenScope.ForUser("octocat")))!
            .AccessToken.Should().Be("tok-legacy");
        (await store.GetTokenAsync(GitHubTokenScope.ForUser("octocat")))!
            .AccessToken.Should().Be("tok-legacy");
    }

    [Fact]
    public async Task LinkedIdentityScopes_PassThroughUnchanged()
    {
        var store = new LinkedIdentityGitHubTokenStore(new InMemoryGitHubTokenStore());

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);
        await store.LinkIdentityAsync(EntraUserId, Token("tok-bob", "bob"));

        (await store.GetTokenAsync(GitHubTokenScope.ForLinkedIdentity(EntraUserId, "bob")))!
            .AccessToken.Should().Be("tok-bob");
        (await store.GetTokenAsync(GitHubTokenScope.Installation)).Should().BeNull();
    }

    [Fact]
    public async Task SignOut_ForLegacyUserScope_TombstonesTheActiveLinkedIdentity()
    {
        var inner = new InMemoryGitHubTokenStore();
        var store = new LinkedIdentityGitHubTokenStore(inner);

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);
        await store.SignOutAsync(GitHubTokenScope.ForUser(EntraUserId));

        (await inner.GetAsync(GitHubTokenScope.ForLinkedIdentity(EntraUserId, "alice"))).Status
            .Should().Be(GitHubTokenStatus.SignedOut);
    }

    [Fact]
    public async Task ResolveEffectiveScope_IsPerUser()
    {
        var store = new LinkedIdentityGitHubTokenStore(new InMemoryGitHubTokenStore());
        const string otherEntraUserId = "00000000-0000-0000-0000-0000000009a2";

        await store.LinkIdentityAsync(EntraUserId, Token("tok-alice", "alice"), isDefault: true);
        await store.LinkIdentityAsync(otherEntraUserId, Token("tok-carol", "carol"), isDefault: true);

        (await store.ResolveEffectiveScopeAsync(EntraUserId)).Key
            .Should().Be(GitHubTokenScope.ForLinkedIdentity(EntraUserId, "alice").Key);
        (await store.ResolveEffectiveScopeAsync(otherEntraUserId)).Key
            .Should().Be(GitHubTokenScope.ForLinkedIdentity(otherEntraUserId, "carol").Key);
    }

    private static GitHubToken Token(string access, string login) =>
        new(access, RefreshToken: null, ExpiresAt: null, Login: login, AvatarUrl: $"https://avatars.example/{login}", Scopes: []);
}
