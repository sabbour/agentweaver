using Agentweaver.Mcp;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Tests.Mcp;

public sealed class McpOAuthConfigurationTests
{
    [Theory]
    [InlineData("https://agentweaver.example/path")]
    [InlineData("https://user@agentweaver.example")]
    [InlineData("http://agentweaver.example")]
    public void ProductionOAuthOrigin_RejectsNonOriginOrInsecureValues(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:OAuth:PublicOrigin"] = value,
            })
            .Build();

        var action = () => McpOAuthConfiguration.Resolve(
            configuration,
            new TestEnvironment(),
            "http://agentweaver-api:8080");

        action.Should().Throw<InvalidOperationException>();
    }

    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
