using Agentweaver.Api.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Tests.OAuth;

/// <summary>
/// Tank-owned unit tests for <see cref="OAuthConfigGuard"/> (Seraph T4–T7 review, Fix 1).
/// Verifies the startup fail-fast that pins the OAuth issuer/audience to the public host in
/// Production so MCP→API JWT validation cannot silently break on host-derived audience mismatch.
/// </summary>
public sealed class OAuthConfigGuardTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "Agentweaver.Api";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Production_WithBothPinned_DoesNotThrow()
    {
        var env = new FakeEnvironment { EnvironmentName = Environments.Production };
        var config = Config(
            ("Auth:OAuth:Issuer", "https://host.example/"),
            ("Auth:OAuth:Audience", "https://host.example/mcp"));

        var act = () => OAuthConfigGuard.EnsureProductionIssuerAudiencePinned(env, config);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null, "https://host.example/mcp")]
    [InlineData("https://host.example/", null)]
    [InlineData("", "")]
    [InlineData("   ", "https://host.example/mcp")]
    public void Production_WithMissingOrEmptyValue_FailsFast(string? issuer, string? audience)
    {
        var env = new FakeEnvironment { EnvironmentName = Environments.Production };
        var config = Config(
            ("Auth:OAuth:Issuer", issuer),
            ("Auth:OAuth:Audience", audience));

        var act = () => OAuthConfigGuard.EnsureProductionIssuerAudiencePinned(env, config);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must be pinned to the public host in Production*");
    }

    [Fact]
    public void NonProduction_WithNothingConfigured_DoesNotThrow()
    {
        var env = new FakeEnvironment { EnvironmentName = Environments.Development };
        var config = Config();

        var act = () => OAuthConfigGuard.EnsureProductionIssuerAudiencePinned(env, config);

        act.Should().NotThrow("host-derived issuer/audience is permitted outside Production");
    }
}
