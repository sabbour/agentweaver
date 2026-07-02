using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Bug #169: each coordinator subtask runs in its own isolated sandbox (a separate AgentHost pod /
/// LXC filesystem), so file changes made by one agent are invisible to the next. The shared git
/// integration branch is the only channel between sandboxes. These tests lock in the git contract
/// that <see cref="CoordinatorDispatchService.BuildIntegrationBranchContract"/> injects into every
/// child task: pull-before / commit-push-after, targeting the run's integration branch, and framed
/// as non-fatal so a git failure never aborts the task.
/// </summary>
public sealed class IntegrationBranchContractTests
{
    private static Subtask MakeSubtask(int id, string title) => new()
    {
        Id = id,
        WorkPlanId = 1,
        Title = title,
        Scope = "index.html",
        AssignedAgent = "Backend",
        SelectedModelId = "gpt-4o",
        Phase = "execution",
        IsolationStrategy = "shared",
        Status = SubtaskStatus.Pending,
    };

    private static CoordinatorDispatchContext MakeContext(string runId, string originatingBranch) =>
        new(runId, "/repo", originatingBranch, "user@example.com", null);

    [Fact]
    public void Contract_TargetsRunIntegrationBranch_WithPullBeforeAndPushAfter()
    {
        var context = MakeContext("coord-169", "main");
        var subtask = MakeSubtask(7, "Fix the broken index page");

        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(context, subtask);

        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName("coord-169");
        contract.Should().Contain(integrationBranch);

        // Pull-before contract.
        contract.Should().Contain("git fetch origin");
        contract.Should().Contain($"git pull --no-edit origin {integrationBranch}");

        // Commit-push-after contract, keyed to the subtask.
        contract.Should().Contain("git add -A");
        contract.Should().Contain("subtask 7: Fix the broken index page");
        contract.Should().Contain($"git push origin HEAD:{integrationBranch}");

        // Falls back to the originating branch when the integration branch does not yet exist.
        contract.Should().Contain("main");
    }

    [Fact]
    public void Contract_IsNonFatal_TellsAgentToContinueOnGitFailure()
    {
        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(
            MakeContext("coord-abc", "develop"), MakeSubtask(1, "do work"));

        contract.Should().ContainEquivalentOf("do NOT abort the task");
        contract.Should().ContainEquivalentOf("do NOT fail the task");
    }

    [Fact]
    public void Contract_SanitizesTitleForShellCommitMessage()
    {
        var subtask = MakeSubtask(3, "Add \"quoted\" title\nwith newline and `backtick`");
        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(
            MakeContext("coord-xyz", "main"), subtask);

        // No raw double quotes or newlines from the title leak into the git commit -m instruction.
        contract.Should().Contain("subtask 3: Add 'quoted' title with newline and 'backtick'");
    }
}
