using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;

namespace Agentweaver.Tests;

/// <summary>
/// Unit tests for <see cref="RunOrchestrator.ComposeCapabilities"/>: the agent-facing capability
/// note (browser preview) must be advertised ONLY when Sandbox:Preview:Enabled=true, and must leave
/// the prompt untouched when disabled (ships dark / default behaviour unchanged). It is additionally
/// gated by <see cref="RunOrchestrator.RunSupportsPreview"/> so the orchestrating Coordinator run —
/// which never runs a server — is not handed the "you MUST preview a server" mandate.
/// </summary>
public sealed class RunOrchestratorCapabilitiesTests
{
    private static Run RunWith(string? agentName, string? parentRunId) => new()
    {
        Id = RunId.New(),
        RepositoryPath = "dummy-repo",
        OriginatingBranch = "main",
        ModelSource = ModelSource.GitHubCopilot,
        Task = "capabilities test",
        SubmittingUser = "tester",
        Status = RunStatus.InProgress,
        StartedAt = DateTimeOffset.UtcNow,
        AgentName = agentName,
        ParentRunId = parentRunId,
    };

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
    public void Enabled_prepends_browser_preview_block()
    {
        const string prompt = "You are a coding agent.";
        var result = RunOrchestrator.ComposeCapabilities(prompt, previewEnabled: true);

        result.Should().StartWith("## Browser Preview");
        result.Should().Contain(prompt);
        result.Should().Contain("## Browser Preview");
        result.Should().Contain("public HTTPS URL");
        result.Should().Contain("0.0.0.0");
        result.Should().Contain("unguessable");
        result.Should().Contain("start_preview(PORT)");
    }

    [Fact]
    public void Enabled_but_unsupported_leaves_prompt_unchanged()
    {
        const string prompt = "You orchestrate the team.";
        RunOrchestrator.ComposeCapabilities(prompt, previewEnabled: true, supportsPreview: false)
            .Should().Be(prompt);
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

    [Fact]
    public void Coordinator_run_does_not_support_preview()
    {
        // The Coordinator orchestrates and dispatches — it never launches a server itself.
        RunOrchestrator.RunSupportsPreview(RunWith("Coordinator", parentRunId: null)).Should().BeFalse();
    }

    [Fact]
    public void Child_worker_run_supports_preview()
    {
        // A dispatched child worker builds runnable output, so it keeps the capability.
        RunOrchestrator.RunSupportsPreview(RunWith("Coordinator", parentRunId: "parent-1")).Should().BeTrue();
        RunOrchestrator.RunSupportsPreview(RunWith("seraph", parentRunId: "parent-1")).Should().BeTrue();
    }

    [Fact]
    public void Single_adhoc_run_supports_preview()
    {
        // Ordinary single-agent runs ("build me a todo app") are exactly where preview shines.
        RunOrchestrator.RunSupportsPreview(RunWith(agentName: null, parentRunId: null)).Should().BeTrue();
        RunOrchestrator.RunSupportsPreview(RunWith("trinity", parentRunId: null)).Should().BeTrue();
    }
}
