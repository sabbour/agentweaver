extern alias EnvironmentService;
extern alias OrchestratorHost;

using System.Security.Authentication;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Xunit;
using EnvironmentTls = EnvironmentService::Agentweaver.Environment.RemoteMcpTrustedServiceTlsComposition;
using OrchestratorTls = OrchestratorHost::Agentweaver.Orchestrator.RemoteMcpCallVerificationComposition;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class RemoteMcpTrustedServiceTlsCompositionTests
{
    [Fact]
    public void MissingProfilesDoNotRegisterTrustedOwnerClientsOrReplaceExistingTls()
    {
        var builder = Builder();
        var existing = new RuntimeServiceTlsOptions { LocalServiceName = "Unrelated" };
        builder.Services.AddSingleton(existing);

        EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder);
        Assert.False(OrchestratorTls.AddRemoteMcpCallVerificationTls(builder, false));

        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceType == typeof(RuntimeServiceTlsIdentity));
        using var services = builder.Services.BuildServiceProvider();
        Assert.Same(existing, services.GetRequiredService<RuntimeServiceTlsOptions>());
    }

    [Fact]
    public void OrchestratorEnvironmentProfileDoesNotReplaceEventsOrUseItsPeerPins()
    {
        var builder = Builder(OrchestratorProfiles());
        var events = new RuntimeServiceTlsOptions { LocalServiceName = "Orchestrator", PeerServiceName = "EventsAndSessions" };
        builder.Services.AddSingleton(events);

        Assert.True(OrchestratorTls.AddRemoteMcpCallVerificationTls(builder, true));
        using var services = builder.Services.BuildServiceProvider();
        Assert.Same(events, services.GetRequiredService<RuntimeServiceTlsOptions>());
        var environment = services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(OrchestratorTls.TlsKey);
        Assert.Equal("Environment", environment.PeerServiceName);
        Assert.Equal(9446, environment.ListenerPort);
        Assert.NotNull(environment.PeerCertificateSha256Pins);
        Assert.Equal([new string('b', 64)], environment.PeerCertificateSha256Pins);
    }

    [Theory]
    [InlineData("LocalServiceName", "Environment")]
    [InlineData("PeerServiceName", "EventsAndSessions")]
    [InlineData("LocalServicePrincipalId", "44444444-4444-4444-4444-444444444444")]
    [InlineData("PeerServicePrincipalId", "33333333-3333-3333-3333-333333333333")]
    [InlineData("PeerCertificateSha256Pins:0", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("PeerCertificateSha256Pins:0", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("endpoint", "http://orchestrator.example:9446/")]
    [InlineData("shared-port", "https://orchestrator.example:9446/")]
    public void OrchestratorRejectsRolePrincipalPinAndListenerConflictsBeforeCertificateLoading(
        string setting, string value)
    {
        var configuration = OrchestratorProfiles();
        configuration[setting switch
        {
            "endpoint" => "Kestrel:Endpoints:RemoteMcpCallVerification:Url",
            "shared-port" => "Kestrel:Endpoints:Public:Url",
            _ => OrchestratorTls.SectionName + ":Tls:" + setting
        }] = value;
        var builder = Builder(configuration);

        Assert.Throws<InvalidOperationException>(
            () => OrchestratorTls.AddRemoteMcpCallVerificationTls(builder, true));
        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceType == typeof(RuntimeServiceTlsIdentity));
    }

    [Fact]
    public void OrchestratorProfileRequiresTheCurrentRuntimeOwner()
    {
        var builder = Builder(OrchestratorProfiles());

        Assert.Throws<InvalidOperationException>(
            () => OrchestratorTls.AddRemoteMcpCallVerificationTls(builder, false));
    }

    [Fact]
    public void EnvironmentOwnerClientsUseSeparateProfilesFixedAddressesAndFiniteTimeouts()
    {
        var builder = Builder(EnvironmentProfiles());

        EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder);
        using var services = builder.Services.BuildServiceProvider();
        var identity = services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(EnvironmentTls.IdentityTlsKey);
        var orchestrator = services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(EnvironmentTls.OrchestratorTlsKey);
        Assert.Equal("IdentityBroker", identity.PeerServiceName);
        Assert.Equal("Orchestrator", orchestrator.PeerServiceName);
        Assert.Null(services.GetService<RuntimeServiceTlsOptions>());
        var clients = services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>();
        AssertClient(clients.Get(EnvironmentTls.IdentityClientName), "https://identity.example:9445/");
        AssertClient(clients.Get(EnvironmentTls.OrchestratorClientName), "https://orchestrator.example:9446/");
    }

    [Theory]
    [InlineData("LocalServicePrincipalId", "44444444-4444-4444-4444-444444444444")]
    [InlineData("PeerServicePrincipalId", "33333333-3333-3333-3333-333333333333")]
    [InlineData("PeerCertificateSha256Pins:0", "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("PeerServiceName", "Orchestrator")]
    [InlineData("endpoint", "http://environment.example:9447/")]
    [InlineData("shared-port", "https://environment.example:9447/")]
    public void EnvironmentRejectsConflictingOwnerProfilesAndListenerBeforeRegistration(
        string setting, string value)
    {
        var configuration = EnvironmentProfiles();
        configuration[setting switch
        {
            "endpoint" => "Kestrel:Endpoints:RemoteMcpCredentialUse:Url",
            "shared-port" => "Kestrel:Endpoints:Public:Url",
            _ => EnvironmentTls.IdentitySectionName + ":" + setting
        }] = value;
        var builder = Builder(configuration);

        Assert.Throws<InvalidOperationException>(() => EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder));
        Assert.DoesNotContain(builder.Services, service =>
            service.ServiceType == typeof(RuntimeServiceTlsIdentity));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncompleteProfilesDoNotBecomeOrdinaryHttpsClients(bool identity)
    {
        var builder = Builder(new()
        {
            [(identity ? EnvironmentTls.IdentitySectionName : EnvironmentTls.OrchestratorSectionName) +
                ":LocalServiceName"] = "Environment"
        });

        Assert.Throws<InvalidOperationException>(() => EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewedToolRecipientRemainsUnavailableWithoutExplicitOptIn(bool profiles)
    {
        var builder = Builder(profiles ? EnvironmentProfiles() : null);
        EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder);
        using var services = builder.Services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };

        var failure = Assert.Throws<RuntimeAuthorizationException>(() => EnvironmentTls.RequireOrchestratorPeer(context));

        Assert.Equal("remote_mcp_reviewed_tool_recipient_unavailable", failure.Code);
        Assert.Null(services.GetKeyedService<RuntimeServiceTlsOptions>(EnvironmentTls.OrchestratorRecipientTlsKey));
    }

    [Fact]
    public void ReviewedToolRecipientUsesExistingOrchestratorPinsOnItsSeparateListener()
    {
        var builder = Builder(RecipientProfiles());
        EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder);
        using var services = builder.Services.BuildServiceProvider();
        var recipient = services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(EnvironmentTls.OrchestratorRecipientTlsKey);

        Assert.Same(services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(EnvironmentTls.OrchestratorTlsKey), recipient);
        Assert.NotSame(services.GetRequiredKeyedService<RuntimeServiceTlsOptions>(EnvironmentTls.IdentityTlsKey), recipient);
        Assert.Equal("Orchestrator", recipient.PeerServiceName);
        Assert.Equal(9448, recipient.ListenerPort);
        Assert.NotNull(recipient.PeerCertificateSha256Pins);
        Assert.Equal([new string('c', 64)], recipient.PeerCertificateSha256Pins);
    }

    [Theory]
    [InlineData("missing-profile")]
    [InlineData("missing-listener")]
    [InlineData("http")]
    [InlineData("path")]
    [InlineData("wrong-port")]
    [InlineData("shared-port")]
    [InlineData("identity-port")]
    public void InvalidReviewedToolRecipientIsRejectedBeforeAnyTlsRegistration(string changed)
    {
        var values = RecipientProfiles();
        var endpoint = "Kestrel:Endpoints:" + EnvironmentTls.OrchestratorRecipientListenerName + ":Url";
        switch (changed)
        {
            case "missing-profile":
                foreach (var key in values.Keys.Where(key => key.StartsWith(
                             EnvironmentTls.OrchestratorSectionName + ":", StringComparison.Ordinal)).ToArray())
                    values.Remove(key);
                break;
            case "missing-listener": values.Remove(endpoint); break;
            case "http": values[endpoint] = "http://environment.example:9448/"; break;
            case "path": values[endpoint] = "https://environment.example:9448/native"; break;
            case "wrong-port": values[endpoint] = "https://environment.example:9449/"; break;
            case "shared-port": values["Kestrel:Endpoints:Public:Url"] = values[endpoint]; break;
            case "identity-port":
                values[endpoint] = values["Kestrel:Endpoints:RemoteMcpCredentialUse:Url"];
                values[EnvironmentTls.OrchestratorSectionName + ":ListenerPort"] = "9447";
                break;
        }
        var builder = Builder(values);

        Assert.Throws<InvalidOperationException>(() => EnvironmentTls.AddRemoteMcpTrustedServiceTls(builder));
        Assert.DoesNotContain(builder.Services, service => service.ServiceType == typeof(RuntimeServiceTlsIdentity));
    }

    [Theory]
    [InlineData(false, null, 9448, "Orchestrator", "runtime_remote_tool_result_https_required")]
    [InlineData(true, null, 9448, "Orchestrator", "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.None, 9448, "Orchestrator", "runtime_remote_tool_result_https_required")]
    [InlineData(true, SslProtocols.Tls13, 9447, "Orchestrator", "runtime_remote_tool_result_service_listener_denied")]
    [InlineData(true, SslProtocols.Tls13, 9448, "IdentityBroker", "remote_mcp_reviewed_tool_recipient_profile_invalid")]
    public void ReviewedToolRecipientRejectsWrongTransportListenerAndRoleBeforeCertificateLoading(
        bool https, SslProtocols? protocol, int port, string peer, string code)
    {
        var collection = new ServiceCollection();
        collection.AddKeyedSingleton(EnvironmentTls.OrchestratorRecipientTlsKey, new RuntimeServiceTlsOptions
        {
            LocalServiceName = "Environment", PeerServiceName = peer, ListenerPort = 9448
        });
        var certificateResolved = false;
        collection.AddKeyedSingleton<RuntimeServiceTlsIdentity>(EnvironmentTls.OrchestratorRecipientTlsKey, (_, _) =>
        {
            certificateResolved = true;
            throw new InvalidOperationException("Denied requests must not load a certificate.");
        });
        using var services = collection.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = https ? "https" : "http";
        context.Connection.LocalPort = port;
        if (protocol is { } value)
            context.Features.Set<ITlsHandshakeFeature>(new Handshake(value));

        var failure = Assert.Throws<RuntimeAuthorizationException>(() => EnvironmentTls.RequireOrchestratorPeer(context));

        Assert.Equal(code, failure.Code);
        Assert.False(certificateResolved);
    }

    private static void AssertClient(HttpClientFactoryOptions options, string address)
    {
        using var client = new HttpClient();
        foreach (var configure in options.HttpClientActions)
            configure(client);
        Assert.Equal(new Uri(address), client.BaseAddress);
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.NotEmpty(options.HttpMessageHandlerBuilderActions);
    }

    private static WebApplicationBuilder Builder(Dictionary<string, string?>? configuration = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration ?? []);
        builder.Services.AddSingleton(TimeProvider.System);
        return builder;
    }

    private static Dictionary<string, string?> OrchestratorProfiles()
    {
        var values = new Dictionary<string, string?>();
        AddProfile(values, OrchestratorTls.SectionName + ":Tls", "Orchestrator", "Environment",
            "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
            9446, "https://environment.example:9447/", 'b');
        AddProfile(values, "Orchestrator:RemoteToolResults:Tls", "Orchestrator", "EventsAndSessions",
            "11111111-1111-1111-1111-111111111111", "33333333-3333-3333-3333-333333333333",
            9443, "https://events.example:9443/", 'c');
        values["Kestrel:Endpoints:RemoteMcpCallVerification:Url"] = "https://orchestrator.example:9446/";
        values["Kestrel:Endpoints:RemoteToolResults:Url"] = "https://orchestrator.example:9443/";
        return values;
    }

    private static Dictionary<string, string?> EnvironmentProfiles()
    {
        var values = new Dictionary<string, string?>();
        AddProfile(values, EnvironmentTls.IdentitySectionName, "Environment", "IdentityBroker",
            "22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111",
            9447, "https://identity.example:9445/", 'b');
        AddProfile(values, EnvironmentTls.OrchestratorSectionName, "Environment", "Orchestrator",
            "22222222-2222-2222-2222-222222222222", "33333333-3333-3333-3333-333333333333",
            9447, "https://orchestrator.example:9446/", 'c');
        values["Kestrel:Endpoints:RemoteMcpCredentialUse:Url"] = "https://environment.example:9447/";
        return values;
    }

    private static Dictionary<string, string?> RecipientProfiles()
    {
        var values = EnvironmentProfiles();
        values[EnvironmentTls.OrchestratorRecipientEnabledSetting] = "true";
        values[EnvironmentTls.OrchestratorSectionName + ":ListenerPort"] = "9448";
        values["Kestrel:Endpoints:" + EnvironmentTls.OrchestratorRecipientListenerName + ":Url"] =
            "https://environment.example:9448/";
        return values;
    }

    private static void AddProfile(
        Dictionary<string, string?> values, string section, string local, string peer,
        string localId, string peerId, int port, string owner, char pin)
    {
        values[section + ":LocalServiceName"] = local;
        values[section + ":PeerServiceName"] = peer;
        values[section + ":LocalServicePrincipalId"] = localId;
        values[section + ":PeerServicePrincipalId"] = peerId;
        values[section + ":ListenerPort"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        values[section + ":LocalCertificatePemPath"] = Path.Combine(AppContext.BaseDirectory, "unused-service.pem");
        values[section + ":LocalPrivateKeyPemPath"] = Path.Combine(AppContext.BaseDirectory, "unused-service-key.pem");
        values[section + ":LocalCertificateSha256"] = new string('a', 64);
        values[section + ":PeerOwnerAddress"] = owner;
        values[section + ":PeerCertificateSha256Pins:0"] = new string(pin, 64);
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
}
