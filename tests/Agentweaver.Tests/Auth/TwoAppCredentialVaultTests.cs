using Agentweaver.Api.Auth;
using FluentAssertions;

namespace Agentweaver.Tests.Auth;

public sealed class TwoAppCredentialVaultTests
{
    [Fact]
    public async Task TombstoneAndDelete_MakesReservedCredentialUnavailableToCurrentReads()
    {
        var store = new InMemorySecretStore();
        var vault = new TwoAppCredentialVault(store);
        var locator = TwoAppCredentialLocator.ForRepoAppUser("repo-app-user-credential-grant");
        await vault.WriteAsync(locator, """{"access_token":"ghu_secret"}""");

        (await vault.ReadCurrentAsync(locator)).Found.Should().BeTrue();
        await vault.TombstoneAndDeleteAsync(locator);

        (await vault.ReadCurrentAsync(locator)).Should().Be(SecretGetResult.NotFound);
    }

    [Fact]
    public void Locator_RejectsCallerSuppliedPrefixesOutsideItsTypedPurpose()
    {
        var action = () => TwoAppCredentialLocator.ForCopilotProject("repo-app-user-credential-grant");

        action.Should().Throw<ArgumentException>();
    }
}
