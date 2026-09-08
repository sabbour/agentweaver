using System.Text.Json;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class McpWorkspaceConfigurationTests
{
    [Fact]
    public void AgentweaverEntry_UsesCanonicalPublishedHttpEndpoint()
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, ".mcp.json")));
        var server = config.RootElement
            .GetProperty("mcpServers")
            .GetProperty("agentweaver");

        server.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo(["type", "url", "tools"]);
        server.GetProperty("type").GetString().Should().Be("http");
        server.GetProperty("url").GetString().Should().Be(
            "https://agentweaver.6a6f0602b81a5700010708e7.eastus2euap.aksapp.io/mcp");
        server.GetProperty("tools").EnumerateArray().Select(tool => tool.GetString())
            .Should().Equal("*");
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, ".mcp.json")))
            directory = directory.Parent;

        directory.Should().NotBeNull("the test must run under an Agentweaver repository checkout");
        return directory!.FullName;
    }
}
