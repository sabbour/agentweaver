using Agentweaver.Abstractions;
using Agentweaver.Identity.Broker;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class ModelProviderCredentialContractTests
{
    private const string ExistingVault = "https://agentweaver-kv.vault.azure.net/";
    private const string ByokVault = "https://agentweaver-byok.vault.azure.net/";

    [Fact]
    public void IdentityRequiresProjectIdOnlyForProjectScope()
    {
        ModelProviderCredentialStore.ValidateIdentity(new(
            Guid.NewGuid(), ModelProviderCredentialScope.Platform, null, "provider"));
        ModelProviderCredentialStore.ValidateIdentity(new(
            Guid.NewGuid(), ModelProviderCredentialScope.Project, "project-1", "provider"));

        AssertInvalidIdentity(ModelProviderCredentialScope.Platform, "project-1");
        AssertInvalidIdentity(ModelProviderCredentialScope.Project, null);
    }

    [Fact]
    public void ReferenceUsesNativeKeyVaultNameAndExactVersionFormats()
    {
        ModelProviderCredentialStore.ValidateReference(new SecretRef("provider-key-1", new string('a', 32)));

        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialStore.ValidateReference(new SecretRef("bad_name", new string('a', 32))));
        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialStore.ValidateReference(new SecretRef("provider-key", new string('g', 32))));
        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialStore.ValidateReference(new SecretRef("provider-key", new string('a', 31))));
    }

    [Fact]
    public void DerivedByokNameRejectsInvalidProviderCharactersAndOverflow()
    {
        ModelProviderCredentialStore.ValidateByokName(
            ModelProviderCredentialScope.Project, "provider-1", ModelProviderCredentialVaultSource.Byok,
            "byok-project-provider-1");

        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialStore.ValidateByokName(
                ModelProviderCredentialScope.Project, "provider.name", ModelProviderCredentialVaultSource.Byok,
                "byok-project-provider.name"));
        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialStore.ValidateByokName(
                ModelProviderCredentialScope.Project, new string('a', 115), ModelProviderCredentialVaultSource.Byok,
                "byok-project-" + new string('a', 115)));
    }

    [Fact]
    public void OptionalByokVaultDistinguishesOmittedEmptyMalformedAndSameRoot()
    {
        Assert.Null(ModelProviderCredentialVaults.Create(ExistingVault, null).Byok);
        AssertInvalidVaultConfiguration(string.Empty);
        AssertInvalidVaultConfiguration("not a vault URI");
        AssertInvalidVaultConfiguration(ExistingVault.ToUpperInvariant());

        var configured = ModelProviderCredentialVaults.Create(ExistingVault, ByokVault);
        Assert.Equal(ByokVault, configured.Resolve(ModelProviderCredentialVaultSource.Byok));
    }

    [Fact]
    public void ByokReferenceFailsClosedWhenItsVaultIsNotConfigured()
    {
        var vaults = ModelProviderCredentialVaults.Create(ExistingVault, null);

        var exception = Assert.Throws<ModelProviderCredentialException>(() =>
            vaults.Resolve(ModelProviderCredentialVaultSource.Byok));

        Assert.Equal("credential_vault_unavailable", exception.Code);
    }

    [Fact]
    public void EgressHostMatchesTheConfiguredVaultAndDefaultsToTheExistingVault()
    {
        var existingOnly = ModelProviderCredentialVaults.Create(ExistingVault, null);
        Assert.Equal("agentweaver-kv.vault.azure.net", existingOnly.ResolveEgressHost(null));
        Assert.Throws<ModelProviderCredentialException>(() =>
            existingOnly.ResolveEgressHost("other.vault.azure.net"));

        var withByok = ModelProviderCredentialVaults.Create(ExistingVault, ByokVault);
        Assert.Equal("agentweaver-byok.vault.azure.net",
            withByok.ResolveEgressHost("agentweaver-byok.vault.azure.net"));
        Assert.Throws<ModelProviderCredentialException>(() => withByok.ResolveEgressHost(null));
        Assert.Throws<ModelProviderCredentialException>(() =>
            withByok.ResolveEgressHost("other.vault.azure.net"));
    }

    private static void AssertInvalidIdentity(ModelProviderCredentialScope scope, string? projectId) =>
        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialStore.ValidateIdentity(new(Guid.NewGuid(), scope, projectId, "provider")));

    private static void AssertInvalidVaultConfiguration(string byokUri) =>
        Assert.Throws<ModelProviderCredentialException>(() =>
            ModelProviderCredentialVaults.Create(ExistingVault, byokUri));
}
