using System.Security.Authentication;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class HostedModelCatalogCredentialUseCompositionTests
{
    [Fact]
    public void MissingConfigurationDoesNotRegisterAnAgentHostRecipient()
    {
        var builder = Builder();

        Assert.False(builder.AddHostedModelCatalogCredentialUseTls());
        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceKey is HostedModelCatalogCredentialUseComposition.RecipientTlsKey);
    }

    [Fact]
    public void IncompleteProfileIsRejectedBeforeCertificateLoading()
    {
        var builder = Builder(new()
        {
            [HostedModelCatalogCredentialUseComposition.SectionName + ":Tls:PeerServiceName"] = "AgentHost"
        });

        Assert.Throws<InvalidOperationException>(() => builder.AddHostedModelCatalogCredentialUseTls());
        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceType == typeof(RuntimeServiceTlsIdentity));
    }

    [Theory]
    [InlineData(false, null, 9445, "AgentHost", "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.Tls13, 443, "AgentHost", "runtime_remote_tool_result_service_listener_denied")]
    [InlineData(true, SslProtocols.Tls13, 9445, "Environment", "hosted_model_catalog_credential_use_recipient_profile_invalid")]
    public void RecipientGateRejectsWrongTransportPortOrPeerRole(
        bool https, SslProtocols? protocol, int port, string peer, string code)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(
            HostedModelCatalogCredentialUseComposition.RecipientTlsKey,
            new RuntimeServiceTlsOptions
            {
                LocalServiceName = "IdentityBroker",
                PeerServiceName = peer,
                ListenerPort = 9445
            });
        var certificateResolved = false;
        services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(
            HostedModelCatalogCredentialUseComposition.RecipientTlsKey, (_, _) =>
            {
                certificateResolved = true;
                throw new InvalidOperationException("Denied requests must not resolve the certificate.");
            });
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = https ? "https" : "http";
        context.Connection.LocalPort = port;
        if (protocol is { } value)
            context.Features.Set<ITlsHandshakeFeature>(new Handshake(value));

        var failure = Assert.Throws<RuntimeAuthorizationException>(
            () => HostedModelCatalogCredentialUseComposition.RequireAgentHost(context));

        Assert.Equal(code, failure.Code);
        Assert.False(certificateResolved);
    }

    [Fact]
    public void RecipientGateAcceptsOnlyThePinnedAgentHostCertificate()
    {
        using var tls = new HostedModelCatalogCredentialTlsTestSupport();
        using var identity = new RuntimeServiceTlsIdentity(tls.Options, "IdentityBroker", "AgentHost");
        var services = new ServiceCollection();
        services.AddKeyedSingleton(
            HostedModelCatalogCredentialUseComposition.RecipientTlsKey, tls.Options);
        services.AddKeyedSingleton(
            HostedModelCatalogCredentialUseComposition.RecipientTlsKey, identity);
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        tls.ConfigureContext(context);

        HostedModelCatalogCredentialUseComposition.RequireAgentHost(context);
    }

    private sealed class Handshake(SslProtocols protocol) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => protocol;
        public string HostName => string.Empty;
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public System.Net.Security.TlsCipherSuite? NegotiatedCipherSuite => null;
    }

    private static WebApplicationBuilder Builder(Dictionary<string, string?>? values = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values ?? []);
        builder.Services.AddSingleton(TimeProvider.System);
        return builder;
    }

}
