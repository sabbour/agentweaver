using System.Security.Authentication;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpCredentialUseCompositionTests
{
    [Fact]
    public void MissingConfigurationDoesNotRegisterARecipientOrLoadCertificates()
    {
        var builder = Builder();

        Assert.False(builder.AddRemoteMcpCredentialUseTls());
        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceKey is RemoteMcpCredentialUseComposition.RecipientTlsKey);
    }

    [Fact]
    public void RecipientProfileIsKeyedAndDoesNotReplaceAnotherServiceProfile()
    {
        var builder = Builder(Profile());
        var existing = new RuntimeServiceTlsOptions { LocalServiceName = "Unrelated" };
        builder.Services.AddSingleton(existing);

        Assert.True(builder.AddRemoteMcpCredentialUseTls());
        using var services = builder.Services.BuildServiceProvider();
        Assert.Same(existing, services.GetRequiredService<RuntimeServiceTlsOptions>());
        var recipient = services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(
            RemoteMcpCredentialUseComposition.RecipientTlsKey);
        Assert.Equal("IdentityBroker", recipient.LocalServiceName);
        Assert.Equal("Environment", recipient.PeerServiceName);
        Assert.Single(builder.Services, service =>
            service.ServiceType == typeof(RuntimeServiceTlsIdentity) &&
            service.ServiceKey is RemoteMcpCredentialUseComposition.RecipientTlsKey);
    }

    [Theory]
    [InlineData("LocalServiceName", "Environment")]
    [InlineData("PeerServiceName", "Orchestrator")]
    [InlineData("PeerServicePrincipalId", "")]
    [InlineData("PeerCertificateSha256Pins:0", "")]
    [InlineData("local-pin", "")]
    [InlineData("ListenerPort", "9446")]
    [InlineData("endpoint", "http://identity.example:9445/")]
    [InlineData("endpoint", "https://identity.example:9445/credential-use")]
    [InlineData("shared-port", "https://identity.example:9445/")]
    public void InvalidOrSharedRecipientConfigurationIsRejectedBeforeCertificateLoading(
        string setting, string value)
    {
        var configuration = Profile();
        var key = setting switch
        {
            "endpoint" => "Kestrel:Endpoints:RemoteMcpCredentialUse:Url",
            "shared-port" => "Kestrel:Endpoints:Public:Url",
            "local-pin" => RemoteMcpCredentialUseComposition.SectionName + ":Tls:PeerCertificateSha256Pins:0",
            _ => RemoteMcpCredentialUseComposition.SectionName + ":Tls:" + setting
        };
        configuration[key] = setting == "local-pin"
            ? configuration[RemoteMcpCredentialUseComposition.SectionName + ":Tls:LocalCertificateSha256"]
            : value;
        var builder = Builder(configuration);

        Assert.Throws<InvalidOperationException>(() => builder.AddRemoteMcpCredentialUseTls());
        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceType == typeof(RuntimeServiceTlsIdentity));
    }

    [Fact]
    public void IncompleteRecipientSectionIsNotTreatedAsDisabled()
    {
        var builder = Builder(new()
        {
            [RemoteMcpCredentialUseComposition.SectionName + ":Tls:PeerServiceName"] = "Environment"
        });

        Assert.Throws<InvalidOperationException>(() => builder.AddRemoteMcpCredentialUseTls());
    }

    [Theory]
    [InlineData(false, null, 9445, "Environment", "runtime_remote_tool_result_https_required")]
    [InlineData(true, null, 9445, "Environment", "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.None, 9445, "Environment", "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.Tls13, 443, "Environment", "runtime_remote_tool_result_service_listener_denied")]
    [InlineData(true, SslProtocols.Tls13, 9445, "Orchestrator", "remote_mcp_credential_use_recipient_profile_invalid")]
    public void RecipientGateRejectsWrongTransportListenerOrRoleBeforeResolvingCertificate(
        bool https, SslProtocols? protocol, int port, string peer, string code)
    {
        var services = new ServiceCollection();
        services.AddKeyedSingleton(
            RemoteMcpCredentialUseComposition.RecipientTlsKey,
            new RuntimeServiceTlsOptions
            {
                LocalServiceName = "IdentityBroker",
                PeerServiceName = peer,
                ListenerPort = 9445
            });
        var certificateResolved = false;
        services.AddKeyedSingleton<RuntimeServiceTlsIdentity>(
            RemoteMcpCredentialUseComposition.RecipientTlsKey, (_, _) =>
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
            () => RemoteMcpCredentialUseComposition.RequireRecipient(context));

        Assert.Equal(code, failure.Code);
        Assert.False(certificateResolved);
    }

    [Fact]
    public void OrdinaryUnkeyedTlsProfileDoesNotAdmitCredentialUse()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new RuntimeServiceTlsOptions
        {
            LocalServiceName = "Orchestrator",
            PeerServiceName = "EventsAndSessions"
        });
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };

        var failure = Assert.Throws<RuntimeAuthorizationException>(
            () => RemoteMcpCredentialUseComposition.RequireRecipient(context));

        Assert.Equal("remote_mcp_credential_use_route_unavailable", failure.Code);
    }

    private static WebApplicationBuilder Builder(Dictionary<string, string?>? values = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(values ?? []);
        builder.Services.AddSingleton(TimeProvider.System);
        return builder;
    }

    private static Dictionary<string, string?> Profile()
    {
        var prefix = RemoteMcpCredentialUseComposition.SectionName + ":Tls:";
        return new()
        {
            [prefix + "LocalServiceName"] = "IdentityBroker",
            [prefix + "LocalServicePrincipalId"] = "11111111-1111-1111-1111-111111111111",
            [prefix + "LocalCertificatePemPath"] = Path.Combine(AppContext.BaseDirectory, "unused-identity.pem"),
            [prefix + "LocalPrivateKeyPemPath"] = Path.Combine(AppContext.BaseDirectory, "unused-identity-key.pem"),
            [prefix + "LocalCertificateSha256"] = new string('a', 64),
            [prefix + "ListenerPort"] = "9445",
            [prefix + "PeerServiceName"] = "Environment",
            [prefix + "PeerServicePrincipalId"] = "22222222-2222-2222-2222-222222222222",
            [prefix + "PeerOwnerAddress"] = "https://environment.example:9446/",
            [prefix + "PeerCertificateSha256Pins:0"] = new string('b', 64),
            ["Kestrel:Endpoints:RemoteMcpCredentialUse:Url"] = "https://identity.example:9445/"
        };
    }

    private sealed class Handshake(SslProtocols protocol) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => protocol;
        public string HostName => string.Empty;
#pragma warning disable SYSLIB0058 // ITlsHandshakeFeature still requires the obsolete algorithm properties.
        public CipherAlgorithmType CipherAlgorithm => default;
        public int CipherStrength => 0;
        public HashAlgorithmType HashAlgorithm => default;
        public int HashStrength => 0;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => default;
#pragma warning restore SYSLIB0058
        public int KeyExchangeStrength => 0;
        public System.Net.Security.TlsCipherSuite? NegotiatedCipherSuite => null;
    }
}
