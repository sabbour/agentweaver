using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Auth;

public sealed class MultiIdentityGitHubTokenStoreExtendedTests
{
    [Fact]
    public async Task InMemory_SetDefaultLinkedIdentityAsync_SwitchesDefaultWithoutRemovingTokens()
    {
        var store = new InMemoryGitHubTokenStore();
        const string entraUserId = "00000000-0000-0000-0000-000000000101";

        await store.LinkIdentityAsync(entraUserId, Token("tok-a", "alice"), isDefault: true, copilotEntitled: true);
        await store.LinkIdentityAsync(entraUserId, Token("tok-b", "bob"), copilotEntitled: false);

        (await store.SetDefaultLinkedIdentityAsync(entraUserId, "bob")).Should().BeTrue();

        var links = await store.ListLinkedIdentitiesAsync(entraUserId);
        links.Should().ContainSingle(x => x.GitHubLogin == "bob" && x.IsDefault);
        links.Should().ContainSingle(x => x.GitHubLogin == "alice" && !x.IsDefault && x.CopilotEntitled == true);

        (await store.GetTokenAsync(GitHubTokenScope.ForLinkedIdentity(entraUserId, "alice")))!
            .AccessToken.Should().Be("tok-a");
        (await store.GetTokenAsync(GitHubTokenScope.ForLinkedIdentity(entraUserId, "bob")))!
            .AccessToken.Should().Be("tok-b");
    }

    [Fact]
    public async Task InMemory_LinkedIdentities_AreIsolatedPerEntraUser()
    {
        var store = new InMemoryGitHubTokenStore();
        const string aliceEntra = "00000000-0000-0000-0000-000000000102";
        const string bobEntra = "00000000-0000-0000-0000-000000000103";

        await store.LinkIdentityAsync(aliceEntra, Token("tok-a", "shared-login"), isDefault: true);
        await store.LinkIdentityAsync(bobEntra, Token("tok-b", "shared-login"), isDefault: true);

        var aliceLinks = await store.ListLinkedIdentitiesAsync(aliceEntra);
        var bobLinks = await store.ListLinkedIdentitiesAsync(bobEntra);

        aliceLinks.Should().ContainSingle(x => x.GitHubLogin == "shared-login" && x.EntraUserId == aliceEntra);
        bobLinks.Should().ContainSingle(x => x.GitHubLogin == "shared-login" && x.EntraUserId == bobEntra);

        (await store.GetTokenAsync(GitHubTokenScope.ForLinkedIdentity(aliceEntra, "shared-login")))!
            .AccessToken.Should().Be("tok-a");
        (await store.GetTokenAsync(GitHubTokenScope.ForLinkedIdentity(bobEntra, "shared-login")))!
            .AccessToken.Should().Be("tok-b");
    }

    [Fact]
    public async Task KeyVault_SetDefaultLinkedIdentityAsync_PersistsRequestedDefault()
    {
        var secrets = new InMemorySecretStore();
        var store = new KeyVaultGitHubTokenStore(secrets);
        const string entraUserId = "00000000-0000-0000-0000-000000000104";

        await store.LinkIdentityAsync(entraUserId, Token("tok-a", "alice"), isDefault: true, copilotEntitled: true);
        await store.LinkIdentityAsync(entraUserId, Token("tok-b", "bob"), copilotEntitled: false);

        (await store.SetDefaultLinkedIdentityAsync(entraUserId, "bob")).Should().BeTrue();

        var defaultLink = await store.GetDefaultLinkedIdentityAsync(entraUserId);
        defaultLink.Should().NotBeNull();
        defaultLink!.GitHubLogin.Should().Be("bob");

        var rawIndex = await secrets.GetSecretAsync(GitHubTokenScope.ForLinkedIdentityIndex(entraUserId).Key);
        rawIndex.Found.Should().BeTrue();
        JsonDocument.Parse(rawIndex.Value!).RootElement.GetProperty("Links")
            .EnumerateArray()
            .Count(x => x.GetProperty("IsDefault").GetBoolean())
            .Should().Be(1);
    }

    [Fact]
    public async Task KeyVault_UnlinkDefault_ReassignsDefaultWithinSameEntraUserOnly()
    {
        var store = new KeyVaultGitHubTokenStore(new InMemorySecretStore());
        const string firstUser = "00000000-0000-0000-0000-000000000105";
        const string secondUser = "00000000-0000-0000-0000-000000000106";

        await store.LinkIdentityAsync(firstUser, Token("tok-a", "alice"), isDefault: true);
        await store.LinkIdentityAsync(firstUser, Token("tok-b", "bob"));
        await store.LinkIdentityAsync(secondUser, Token("tok-c", "carol"), isDefault: true);

        (await store.UnlinkIdentityAsync(firstUser, "alice")).Should().BeTrue();

        (await store.GetDefaultLinkedIdentityAsync(firstUser))!.GitHubLogin.Should().Be("bob");
        (await store.GetDefaultLinkedIdentityAsync(secondUser))!.GitHubLogin.Should().Be("carol");
    }

    [Fact(Skip = "pending Tank's per-project GitHub-identity override service or API endpoint")]
    public async Task ProjectOverride_WinsOverUserDefault_WhenResolvingGitHubIdentity()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "pending Tank's cross-user linked-login uniqueness enforcement")]
    public async Task LinkingGitHubLogin_AlreadyLinkedToDifferentEntraUser_IsRejected()
    {
        await Task.CompletedTask;
    }

    [Fact(Skip = "pending Tank's Copilot entitlement probe wiring")]
    public async Task CopilotEntitlement_IsRecordedPerLinkedAccount_FromProbeResult()
    {
        await Task.CompletedTask;
    }

    private static GitHubToken Token(string access, string login) =>
        new(access, RefreshToken: null, ExpiresAt: null, Login: login, AvatarUrl: $"https://avatars.example/{login}", Scopes: []);
}
