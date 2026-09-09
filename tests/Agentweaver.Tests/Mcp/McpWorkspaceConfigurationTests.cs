using System.Text.Json;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class McpWorkspaceConfigurationTests
{
    [Fact]
    public void WorkspaceConfig_DoesNotDefineAgentweaverEntry()
    {
        // The published hosted endpoint is configured per-user in each contributor's
        // personal `~/.copilot/mcp-config.json`, not in this repo. Defining an
        // `agentweaver` entry here would shadow that personal connection whenever the
        // CLI runs from this repo root (workspace config wins over user config for
        // same-named servers).
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, ".mcp.json")));
        var servers = config.RootElement.GetProperty("mcpServers");

        servers.TryGetProperty("agentweaver", out _).Should().BeFalse(
            "the workspace config must not shadow the personal hosted 'agentweaver' MCP connection");
    }

    [Fact]
    public void AgentweaverLocalEntry_RunsLocalStdioServer()
    {
        var repoRoot = FindRepoRoot();
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, ".mcp.json")));
        var server = config.RootElement
            .GetProperty("mcpServers")
            .GetProperty("agentweaver_local");

        server.GetProperty("command").GetString().Should().Be("dotnet");
        server.GetProperty("args").EnumerateArray().Select(arg => arg.GetString())
            .Should().Equal("run", "--project", "apps/Agentweaver.Mcp", "--", "--stdio");
        server.GetProperty("env").GetProperty("AGENTWEAVER_API_URL").GetString()
            .Should().Be("http://localhost:5000");
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
