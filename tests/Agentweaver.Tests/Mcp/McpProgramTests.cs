using Agentweaver.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Tests.Mcp;

[Collection("McpRealProcess")]
public sealed class McpProgramTests
{
    [Fact]
    public async Task Main_Stdio_WithoutBrokerToken_RefusesToStart()
    {
        var prior = Environment.GetEnvironmentVariable("AGENTWEAVER_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_TOKEN", null);
            var result = await McpProgram.Main(["--stdio"]);
            result.Should().Be(1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTWEAVER_TOKEN", prior);
        }
    }

    [Fact]
    public void OAuthConfiguration_DerivesCanonicalResourceAndMetadata()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:OAuth:PublicOrigin"] = "https://agentweaver.example",
            })
            .Build();

        var resolved = McpOAuthConfiguration.Resolve(
            configuration,
            new TestEnvironment("Production"),
            "http://agentweaver-api:8080");

        resolved.Issuer.AbsoluteUri.Should().Be("https://agentweaver.example/");
        resolved.Resource.AbsoluteUri.Should().Be("https://agentweaver.example/mcp");
        resolved.ResourceMetadata.AbsoluteUri.Should().Be(
            "https://agentweaver.example/.well-known/oauth-protected-resource/mcp");
    }

    [Fact]
    public void OAuthConfiguration_ProductionRequiresConfiguredHttpsOrigin()
    {
        var configuration = new ConfigurationBuilder().Build();
        var action = () => McpOAuthConfiguration.Resolve(
            configuration,
            new TestEnvironment("Production"),
            "http://agentweaver-api:8080");

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*PublicOrigin is required*");
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
