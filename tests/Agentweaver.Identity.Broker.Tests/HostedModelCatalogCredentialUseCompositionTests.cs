using System.Security.Authentication;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
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

    [Fact]
    public async Task IssueSyncHostedCatalogAndRemoteMcpListenersAllKeepTheirTlsConfiguration()
    {
        using var tls = new HostedModelCatalogCredentialTlsTestSupport();
        var workerPort = FreePort();
        var hostedPort = FreePort();
        var remotePort = FreePort();
        var settings = new Dictionary<string, string?>();
        tls.Configure(settings, "IdentityBroker:IssueSync:WorkerTls:", "IssueSyncWorker",
            workerPort, "Orchestrator");
        tls.Configure(settings, HostedModelCatalogCredentialUseComposition.SectionName + ":Tls:",
            HostedModelCatalogCredentialUseComposition.ListenerName, hostedPort, "AgentHost");
        tls.Configure(settings, RemoteMcpCredentialUseComposition.SectionName + ":Tls:",
            RemoteMcpCredentialUseComposition.ListenerName, remotePort, "Environment");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton(TimeProvider.System);
        Assert.True(IssueSyncServiceTransport.AddChannel(
            builder, "IssueSyncWorker", "IdentityBroker:IssueSync:WorkerTls",
            "IdentityBroker", "Orchestrator", true));
        Assert.True(builder.AddHostedModelCatalogCredentialUseTls());
        Assert.True(builder.AddRemoteMcpCredentialUseTls());
        await using var app = builder.Build();
        app.Run(context => context.Response.WriteAsync("ready"));
        await app.StartAsync();

        foreach (var port in new[] { workerPort, hostedPort, remotePort })
        {
            X509Certificate2? serverCertificate = null;
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
                {
                    serverCertificate = certificate is null ? null : new X509Certificate2(certificate);
                    return true;
                }
            };
            using var client = new HttpClient(handler);
            await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
                client.GetAsync($"https://127.0.0.1:{port}/"));
            Assert.NotNull(serverCertificate);
            Assert.Equal(tls.Options.LocalCertificateSha256,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(serverCertificate.RawData)));
            serverCertificate.Dispose();
        }
        await app.StopAsync();
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

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

}
