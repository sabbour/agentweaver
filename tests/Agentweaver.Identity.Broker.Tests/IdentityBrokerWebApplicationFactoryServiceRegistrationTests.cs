using System.Security.Cryptography;
using System.Text;
using Agentweaver.Identity.Broker;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class BrokerRunBindingTlsRegistrationTests
{
    [Fact]
    public void SharedRegistrationUsesRawTestCaInsteadOfTheFixedFileLoader()
    {
        using var authority = BrokerRunBindingTlsIdentityTests.TestCertificateAuthority.Create();
        var options = authority.CreateOptions();
        options.ProjectsOwnerAddress = "https://projects.test/";
        options.ProjectsAudience = "https://projects-private.test/";
        var productionOptions = BrokerRunBindingAuthorityTests.CreateOptions();
        productionOptions.RunBinding = options;
        var services = new ServiceCollection();
        var fixedFileLoaderInvoked = false;
        services.AddSingleton<BrokerRunBindingTlsIdentity>(_ =>
        {
            fixedFileLoaderInvoked = true;
            return BrokerRunBindingAuthority.LoadTlsConfiguration(productionOptions)!;
        });
        ProjectsConfigBrokerAuthorizationTests.ConfigureRunBindingTlsIdentity(
            services, options, authority.TrustBundleBytes);
        using var provider = services.BuildServiceProvider();

        _ = provider.GetRequiredService<BrokerRunBindingTlsIdentity>();

        Assert.False(fixedFileLoaderInvoked);
    }

    [Fact]
    public void SharedRegistrationRejectsChangedTrustBundleBytes()
    {
        using var authority = BrokerRunBindingTlsIdentityTests.TestCertificateAuthority.Create();
        var changedBytes = authority.TrustBundleBytes.Concat(Encoding.UTF8.GetBytes("\n")).ToArray();

        AssertInvalidRegistration(authority.CreateOptions(), changedBytes);
    }

    [Fact]
    public void SharedRegistrationRejectsInvalidTrustBundle()
    {
        using var authority = BrokerRunBindingTlsIdentityTests.TestCertificateAuthority.Create();
        var invalidBytes = Encoding.UTF8.GetBytes("not a PEM certificate");
        var options = authority.CreateOptions();
        options.ProjectsTrustBundleSha256 =
            Convert.ToHexString(SHA256.HashData(invalidBytes));

        AssertInvalidRegistration(options, invalidBytes);
    }

    private static void AssertInvalidRegistration(BrokerRunBindingOptions options, byte[] trustBundleBytes)
    {
        var services = new ServiceCollection();
        ProjectsConfigBrokerAuthorizationTests.ConfigureRunBindingTlsIdentity(
            services, options, trustBundleBytes);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<BrokerRunBindingTlsIdentity>());
    }
}
