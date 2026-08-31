using Agentweaver.Api.Auth;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class GitHubConnectionsCredentialVaultTests
{
    [Fact]
    public async Task TombstoneAndDelete_MakesReservedCredentialUnavailableToCurrentReads()
    {
        var store = new InMemorySecretStore();
        var vault = new GitHubConnectionsCredentialVault(store);
        var locator = GitHubConnectionsCredentialLocator.ForRepoAppUser("repo-app-user-credential-grant");
        await vault.WriteAsync(locator, """{"access_token":"ghu_secret"}""");

        (await vault.ReadCurrentAsync(locator)).Found.Should().BeTrue();
        await vault.TombstoneAndDeleteAsync(locator);

        (await vault.ReadCurrentAsync(locator)).Should().Be(SecretGetResult.NotFound);
    }

    [Fact]
    public void Locator_RejectsCallerSuppliedPrefixesOutsideItsTypedPurpose()
    {
        var action = () => GitHubConnectionsCredentialLocator.ForCopilotProject("repo-app-user-credential-grant");

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ReadCurrent_TreatsMalformedJsonShapesAsMissingInsteadOfThrowing()
    {
        var store = new InMemorySecretStore();
        var vault = new GitHubConnectionsCredentialVault(store);
        var locator = GitHubConnectionsCredentialLocator.ForCopilotBinding("copilot-app-platform-default-bad-shape");

        await store.SetSecretAsync(locator.Key, "\"signed-in\"");
        (await vault.ReadCurrentAsync(locator)).Found.Should().BeTrue();

        await store.SetSecretAsync(locator.Key, """{"status":{}}""");
        (await vault.ReadCurrentAsync(locator)).Found.Should().BeTrue();
    }
}
