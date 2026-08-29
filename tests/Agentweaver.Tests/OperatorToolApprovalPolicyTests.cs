using System.Reflection;
using Agentweaver.AgentRuntime;
using Agentweaver.Mcp.Tools;
using FluentAssertions;
using ModelContextProtocol.Server;
using Xunit;

namespace Agentweaver.Tests.Runtime;

/// <summary>
/// Regression tests for the fail-closed operator tool-approval policy (findings-agent-runtime
/// Alert 5). The policy previously approved anything not on its gated deny-list, so a new
/// consequential MCP tool ran without operator consent. It now fails closed: only the explicit
/// read/low-consequence allow-list runs without a prompt; gated mutators AND any unrecognized tool
/// require approval.
/// </summary>
public sealed class OperatorToolApprovalPolicyTests
{
    [Theory]
    // Unknown / new tool names must fail closed.
    [InlineData("totally_new_unlisted_tool")]
    [InlineData("sandbox_policy_delete")]
    [InlineData("")]
    [InlineData(null)]
    // Security-sensitive mutators flagged by the finding are now gated.
    [InlineData("sandbox_policy_set")]
    [InlineData("memory_import")]
    [InlineData("skill_import")]
    [InlineData("skill_assign")]
    [InlineData("skill_marketplace_source_add")]
    [InlineData("skill_marketplace_source_remove")]
    [InlineData("workflow_save")]
    // Pre-existing consequential mutators stay gated.
    [InlineData("coordinator_start")]
    [InlineData("project_delete")]
    [InlineData("run_submit")]
    [InlineData("github_repository_selection_issue")]
    public void RequiresApproval_is_true_for_unknown_and_mutating_tools(string? toolName)
    {
        OperatorToolApprovalPolicy.RequiresApproval(toolName).Should().BeTrue();
    }

    [Theory]
    // Read / discovery tools continue to run without a prompt.
    [InlineData("project_get")]
    [InlineData("project_list")]
    [InlineData("run_status")]
    [InlineData("memory_search")]
    [InlineData("sandbox_policy_get")]
    [InlineData("skill_marketplace_sources_list")]
    [InlineData("list_project_workspace")]
    [InlineData("github_repository_selections_list")]
    public void RequiresApproval_is_false_for_ungated_read_tools(string toolName)
    {
        OperatorToolApprovalPolicy.RequiresApproval(toolName).Should().BeFalse();
    }

    /// <summary>
    /// Drift guard: every MCP tool exposed to the operator assistant must be deliberately classified
    /// (gated or ungated). A newly added tool that nobody classified would fail this test, forcing an
    /// explicit decision — even though <see cref="OperatorToolApprovalPolicy.RequiresApproval"/> would
    /// already fail closed and gate it by default.
    /// </summary>
    [Fact]
    public void Every_mcp_tool_is_classified_by_the_approval_policy()
    {
        var mcpAssembly = typeof(SandboxPolicyTools).Assembly;
        var toolNames = mcpAssembly.GetTypes()
            .SelectMany(t => t.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct()
            .ToList();

        toolNames.Should().NotBeEmpty("the MCP tool surface must be discoverable via reflection");

        var unclassified = toolNames
            .Where(name => !OperatorToolApprovalPolicy.IsClassified(name))
            .OrderBy(name => name)
            .ToList();

        unclassified.Should().BeEmpty(
            "every MCP tool must be explicitly classified as gated or ungated in OperatorToolApprovalPolicy; "
            + "unclassified tools fail closed by default but must be triaged: "
            + string.Join(", ", unclassified));
    }
}
