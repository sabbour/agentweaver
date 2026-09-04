using Agentweaver.Api.Auth.OAuth;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Agentweaver.Tests.Auth;

public sealed class OAuthServerConfigurationTests
{
    [Fact]
    public void Resolve_NormalizesCanonicalOriginAndResource()
    {
        var configuration = Configuration(("Auth:OAuth:PublicOrigin", "https://agentweaver.example"));

        var result = OAuthServerConfiguration.Resolve(configuration, Environment("Production"));

        result.PublicOrigin.AbsoluteUri.Should().Be("https://agentweaver.example/");
        result.Resource.AbsoluteUri.Should().Be("https://agentweaver.example/mcp");
    }

    [Theory]
    [InlineData("http://agentweaver.example")]
    [InlineData("https://user@agentweaver.example")]
    [InlineData("https://agentweaver.example/path")]
    [InlineData("https://agentweaver.example?host=evil")]
    [InlineData("https://agentweaver.example/#fragment")]
    public void Resolve_RejectsUnsafeProductionOrigins(string origin)
    {
        var action = () => OAuthServerConfiguration.Resolve(
            Configuration(("Auth:OAuth:PublicOrigin", origin)), Environment("Production"));

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolve_RequiresConfiguredProductionOrigin()
    {
        var action = () => OAuthServerConfiguration.Resolve(Configuration(), Environment("Production"));
        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Resolve_AcceptsOnlyExactTrustedCallbackForKnownClaudeClient()
    {
        var configuration = Configuration(
            ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"));

        var result = OAuthServerConfiguration.Resolve(configuration, Environment("Production"));

        result.EnableClaudeHostedClient.Should().BeTrue();
        var client = result.StaticClients.Should().ContainSingle().Subject;
        client.ClientId.Should().Be(OAuthKnownClients.ClaudeHostedClientId);
        client.RedirectUris.Should().Equal(OAuthKnownClients.ClaudeHostedRedirectUri);
        client.Scopes.Should().Equal(OAuthServerConfiguration.McpScope);
    }

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback/")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?next=%2Fmcp")]
    [InlineData("https://claude.ai.evil.example/api/mcp/auth_callback")]
    public void Resolve_RejectsClaudeClientCallbackLookalikes(string redirectUri)
    {
        var action = () => OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:EnableClaudeHostedClient", "false"),
                ("Auth:OAuth:Clients:0:ClientId", OAuthKnownClients.ClaudeHostedClientId),
                ("Auth:OAuth:Clients:0:DisplayName", "Claude hosted connectors"),
                ("Auth:OAuth:Clients:0:RedirectUris:0", redirectUri)),
            Environment("Production"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*exact trusted Claude callback*");
    }

    [Fact]
    public void Resolve_RejectsDuplicateStaticClientIds()
    {
        var action = () => OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:EnableClaudeHostedClient", "false"),
                ("Auth:OAuth:Clients:0:ClientId", "duplicate"),
                ("Auth:OAuth:Clients:0:DisplayName", "First"),
                ("Auth:OAuth:Clients:0:RedirectUris:0", "com.example.first:/callback"),
                ("Auth:OAuth:Clients:1:ClientId", "duplicate"),
                ("Auth:OAuth:Clients:1:DisplayName", "Second"),
                ("Auth:OAuth:Clients:1:RedirectUris:0", "com.example.second:/callback")),
            Environment("Production"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*client IDs must be unique*");
    }

    [Fact]
    public void Resolve_AllowsDifferentStaticClientsToShareRedirectUri()
    {
        const string sharedRedirectUri = "com.example.shared:/oauth/callback";
        var result = OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:Clients:0:ClientId", "first"),
                ("Auth:OAuth:Clients:0:DisplayName", "First"),
                ("Auth:OAuth:Clients:0:RedirectUris:0", sharedRedirectUri),
                ("Auth:OAuth:Clients:1:ClientId", "second"),
                ("Auth:OAuth:Clients:1:DisplayName", "Second"),
                ("Auth:OAuth:Clients:1:RedirectUris:0", sharedRedirectUri)),
            Environment("Production"));

        result.StaticClients
            .Where(client => client.ClientId is "first" or "second")
            .Should().HaveCount(2)
            .And.OnlyContain(client =>
                client.RedirectUris.Length == 1
                && client.RedirectUris[0] == sharedRedirectUri);
    }

    [Fact]
    public void Resolve_RejectsReservedClaudeCallbackForDifferentStaticClient()
    {
        var action = () => OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:EnableClaudeHostedClient", "false"),
                ("Auth:OAuth:Clients:0:ClientId", "not-claude"),
                ("Auth:OAuth:Clients:0:DisplayName", "Unsafe reassignment"),
                ("Auth:OAuth:Clients:0:RedirectUris:0", OAuthKnownClients.ClaudeHostedRedirectUri)),
            Environment("Production"));

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"*reserved for static OAuth client '{OAuthKnownClients.ClaudeHostedClientId}'*");
    }

    [Fact]
    public void Resolve_AllowsExplicitClaudeHostedClientOptOut()
    {
        var result = OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:EnableClaudeHostedClient", "false")),
            Environment("Production"));

        result.EnableClaudeHostedClient.Should().BeFalse();
        result.StaticClients.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_RequiresConfiguredProductionTrustedProxyNetworks()
    {
        var action = () => OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:ForwardedHeaders:TrustedNetworks", "")),
            Environment("Production"));

        action.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("203.0.113.0/24")]
    [InlineData("::/0")]
    public void Resolve_RejectsUnboundedOrPublicTrustedProxyNetworks(string network)
    {
        var action = () => OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:ForwardedHeaders:TrustedNetworks", network)),
            Environment("Production"));

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ProductionHttpsBehindProxy_TrustsConfiguredGatewayButIgnoresInternetSpoofing()
    {
        var configuration = OAuthServerConfiguration.Resolve(
            Configuration(
                ("Auth:OAuth:PublicOrigin", "https://agentweaver.example"),
                ("Auth:OAuth:ForwardedHeaders:TrustedNetworks", "10.244.0.0/16")),
            Environment("Production"));
        var options = new ForwardedHeadersOptions();
        OAuthForwardedHeaders.Configure(options, configuration);

        static DefaultHttpContext RequestFrom(string address, string host, bool includeForwardedHost)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            context.Request.Scheme = "http";
            context.Request.Host = new HostString(host);
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.10";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            if (includeForwardedHost)
                context.Request.Headers["X-Forwarded-Host"] = "agentweaver.example";
            return context;
        }

        var trusted = RequestFrom("10.244.3.9", "agentweaver.example", includeForwardedHost: false);
        var trustedMiddleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(options));
        await trustedMiddleware.Invoke(trusted);
        trusted.Request.IsHttps.Should().BeTrue();
        trusted.Request.Host.Value.Should().Be("agentweaver.example");

        var spoofed = RequestFrom("203.0.113.9", "agentweaver-api:8080", includeForwardedHost: true);
        await trustedMiddleware.Invoke(spoofed);
        spoofed.Request.IsHttps.Should().BeFalse();
        spoofed.Request.Host.Value.Should().Be("agentweaver-api:8080");
    }

    [Theory]
    [InlineData("https://app.example/callback", true)]
    [InlineData("com.github.copilot:/oauth/callback", true)]
    [InlineData("http://127.0.0.1:49731/callback", true)]
    [InlineData("http://[::1]:49731/callback", true)]
    [InlineData("http://localhost:49731/callback", false)]
    [InlineData("http://10.0.0.4:49731/callback", false)]
    [InlineData("https://app.example/*", false)]
    [InlineData("https://user@app.example/callback", false)]
    [InlineData("https://app.example/callback#fragment", false)]
    public void RedirectPolicy_AdmitsOnlyExactNativeCallbacks(string redirect, bool expected) =>
        OAuthRedirectUriValidator.IsValid(redirect, allowDynamicLoopbackPort: true).Should().Be(expected);

    [Fact]
    public async Task CertificateLoader_AllowsEphemeralKeysOnlyInDevelopment()
    {
        var development = await OAuthCertificateLoader.LoadAsync(
            Configuration(), Environment(Environments.Development), client: null);
        development.SigningKeys.Should().ContainSingle();
        development.EncryptionKeys.Should().ContainSingle();
        development.SigningKeys[0].KeyId.Should().NotBeNullOrWhiteSpace();

        var action = () => OAuthCertificateLoader.LoadAsync(
            Configuration(), Environment("Production"), client: null);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public void CertificateKeys_HaveDeterministicDistinctKidsForOverlapPublication()
    {
        using var first = Certificate("first");
        using var second = Certificate("second");

        var active = OAuthCertificateLoader.CreateSecurityKey(first);
        var previous = OAuthCertificateLoader.CreateSecurityKey(second);

        active.KeyId.Should().Be(OAuthCertificateLoader.CreateSecurityKey(first).KeyId);
        active.KeyId.Should().NotBe(previous.KeyId);
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:OAuth:ForwardedHeaders:TrustedNetworks"] = "10.244.0.0/16",
        };
        foreach (var (key, value) in values)
            settings[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static IHostEnvironment Environment(string name) => new TestEnvironment { EnvironmentName = name };

    private static X509Certificate2 Certificate(string name)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Agentweaver.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
