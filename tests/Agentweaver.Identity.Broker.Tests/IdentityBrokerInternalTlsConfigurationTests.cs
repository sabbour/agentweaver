using Agentweaver.Identity.Broker;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class IdentityBrokerInternalTlsConfigurationTests
{
    [Fact]
    public void ProductionHttpsEndpointUsesInternalCertificateAndPublicFallback()
    {
        var builder = CreateBuilder(Environments.Production, httpsUrl: "https://+:8443");

        Assert.True(IdentityBrokerInternalTlsConfiguration.Configure(builder));
        Assert.Equal(
            "/var/run/identity-broker-internal-tls/tls.crt",
            builder.Configuration["Kestrel:Endpoints:Https:Sni:identity-broker.agentweaver.svc.cluster.local:Certificate:Path"]);
        Assert.Equal(
            "/var/run/identity-broker-internal-tls/tls.key",
            builder.Configuration["Kestrel:Endpoints:Https:Sni:identity-broker.agentweaver.svc.cluster.local:Certificate:KeyPath"]);
        Assert.Equal(
            "/operator/public/tls.crt",
            builder.Configuration["Kestrel:Endpoints:Https:Sni:*:Certificate:Path"]);
        Assert.Equal(
            "/operator/public/tls.key",
            builder.Configuration["Kestrel:Endpoints:Https:Sni:*:Certificate:KeyPath"]);
    }

    [Fact]
    public void ProductionHttpsEndpointRequiresDefaultCertificatePaths()
    {
        var builder = CreateBuilder(Environments.Production, httpsUrl: "https://+:8443");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Certificates:Default:Path"] = null,
            ["Kestrel:Certificates:Default:KeyPath"] = null,
        });

        Assert.Throws<InvalidOperationException>(
            () => IdentityBrokerInternalTlsConfiguration.Configure(builder));
    }

    [Theory]
    [InlineData("Development", "https://+:8443")]
    [InlineData("Production", null)]
    public void DoesNotConfigureSniOutsideTheProductionHttpsEndpoint(string environment, string? httpsUrl)
    {
        var builder = CreateBuilder(environment, httpsUrl);

        Assert.False(IdentityBrokerInternalTlsConfiguration.Configure(builder));
        Assert.Null(builder.Configuration["Kestrel:Endpoints:Https:Sni:*:Certificate:Path"]);
    }

    private static WebApplicationBuilder CreateBuilder(string environment, string? httpsUrl)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            Args = [],
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Kestrel:Endpoints:Https:Url"] = httpsUrl,
            ["Kestrel:Certificates:Default:Path"] = "/operator/public/tls.crt",
            ["Kestrel:Certificates:Default:KeyPath"] = "/operator/public/tls.key",
        });
        return builder;
    }
}
