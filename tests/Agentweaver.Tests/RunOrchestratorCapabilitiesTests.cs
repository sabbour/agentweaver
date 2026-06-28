using Agentweaver.Api.Runs;
using FluentAssertions;

namespace Agentweaver.Tests;

/// <summary>
/// Unit tests for <see cref="RunOrchestrator.ComposeCapabilities"/>: the agent-facing capability
/// note (browser preview) must be advertised ONLY when Sandbox:Preview:Enabled=true, and must leave
/// the prompt untouched when disabled (ships dark / default behaviour unchanged).
/// </summary>
public sealed class RunOrchestratorCapabilitiesTests
{
    [Fact]
    public void Disabled_leaves_prompt_unchanged()
    {
        const string prompt = "You are a coding agent.";
        RunOrchestrator.ComposeCapabilities(prompt, previewEnabled: false).Should().Be(prompt);
    }

    [Fact]
    public void Disabled_with_null_returns_empty()
    {
        RunOrchestrator.ComposeCapabilities(null, previewEnabled: false).Should().BeEmpty();
    }

    [Fact]
    public void Enabled_appends_browser_preview_block()
    {
        const string prompt = "You are a coding agent.";
        var result = RunOrchestrator.ComposeCapabilities(prompt, previewEnabled: true);

        result.Should().StartWith(prompt);
        result.Should().Contain("## Browser Preview");
        result.Should().Contain("public HTTPS URL");
        result.Should().Contain("0.0.0.0");
        result.Should().Contain("unguessable");
    }

    [Fact]
    public void Enabled_does_not_advertise_an_mcp_server()
    {
        // Spawned agents run with EnableConfigDiscovery=false and no MCP server in SessionConfig,
        // so the standalone agentweaver MCP server is NOT reachable — we must not advertise it.
        var result = RunOrchestrator.ComposeCapabilities("charter", previewEnabled: true);
        result.Should().NotContain("MCP server");
    }

    [Fact]
    public void Enabled_with_null_prompt_returns_just_the_block()
    {
        var result = RunOrchestrator.ComposeCapabilities(null, previewEnabled: true);
        result.Should().Be(RunOrchestrator.BrowserPreviewCapability);
    }
}
