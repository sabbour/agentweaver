using Agentweaver.Identity.Broker;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class IdentityBrokerWebApplicationFactoryTests
{
    [Fact]
    public void TestServiceOverrideRunsAfterProductionTlsRegistration()
    {
        using var testAuthority = BrokerRunBindingTlsIdentityTests.TestCertificateAuthority.Create();
        using var testTlsIdentity = BrokerRunBindingTlsIdentity.Load(
            testAuthority.CreateOptions(), testAuthority.TrustBundleBytes);
        var identityOptions = BrokerRunBindingAuthorityTests.CreateOptions();
        var runBindingOptions = testAuthority.CreateOptions();
        runBindingOptions.ProjectsOwnerAddress = identityOptions.RunBinding!.ProjectsOwnerAddress;
        runBindingOptions.ProjectsAudience = identityOptions.RunBinding.ProjectsAudience;
        identityOptions.RunBinding = runBindingOptions;
        var productionLoaderInvoked = false;
        var builder = new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
                services.AddSingleton<BrokerRunBindingTlsIdentity>(_ =>
                {
                    productionLoaderInvoked = true;
                    return BrokerRunBindingAuthority.LoadTlsConfiguration(identityOptions)!;
                }));
            webHost.Configure(_ => { });
            IdentityBrokerWebApplicationFactory.ConfigureTestServiceOverrides(
                webHost, services => services.AddSingleton(testTlsIdentity));
        });
        using var host = builder.Start();
        var resolvedTlsIdentity = host.GetTestServer()
            .Services.GetRequiredService<BrokerRunBindingTlsIdentity>();

        Assert.Same(testTlsIdentity, resolvedTlsIdentity);
        Assert.False(productionLoaderInvoked);
    }
}
