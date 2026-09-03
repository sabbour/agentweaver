using Agentweaver.Api.Auth.OAuth;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
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

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(x => x.Key, x => (string?)x.Value)).Build();

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
