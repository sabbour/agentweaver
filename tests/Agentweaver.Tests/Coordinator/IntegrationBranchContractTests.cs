using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Issue #197: the child subtask git contract used to tell the agent to
/// <c>git checkout</c>/<c>commit</c>/<c>push</c> the shared integration branch. Checking out the
/// integration branch detached the worktree from its run branch (so the API's automatic capture
/// committed to the wrong ref and the run's diff came back empty), and <c>git push origin</c> always
/// failed with "no remote configured" — the child's output was silently stranded and lost.
/// These tests lock in the FIXED contract: propagation is entirely API-managed, so the agent is told
/// NOT to run any git commands, and prior subtasks' files are already present in its workspace.
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
    public void Contract_ReferencesIntegrationBranch_ForContext()
    {
        var context = MakeContext("coord-169", "main");
        var subtask = MakeSubtask(7, "Fix the broken index page");

        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(context, subtask);

        var integrationBranch = CoordinatorAssemblyService.IntegrationBranchName("coord-169");
        contract.Should().Contain(integrationBranch);
    }

    [Fact]
    public void Contract_DoesNotInstructAgentToRunActionableGitCommands()
    {
        var context = MakeContext("coord-169", "main");
        var subtask = MakeSubtask(7, "Fix the broken index page");

        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(context, subtask);

        // The data-loss command sequence from the old contract must be gone entirely.
        contract.Should().NotContain("git push origin");
        contract.Should().NotContain("git add -A");
        contract.Should().NotContain("git commit -m");
        contract.Should().NotContain("git checkout ");
        contract.Should().NotContain("git pull --no-edit");
        contract.Should().NotContain("git fetch origin");

        // And the agent is explicitly told it must NOT run git commands.
        contract.Should().ContainEquivalentOf("must NOT");
    }

    [Fact]
    public void Contract_TellsAgentPriorWorkIsAlreadyPresent()
    {
        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(
            MakeContext("coord-abc", "develop"), MakeSubtask(1, "do work"));

        contract.Should().ContainEquivalentOf("already present");
    }

    [Fact]
    public void Contract_TreatsMissingUpstreamArtifactAsErrorNotSilentFallback()
    {
        var contract = CoordinatorDispatchService.BuildIntegrationBranchContract(
            MakeContext("coord-xyz", "main"), MakeSubtask(3, "consume upstream artifact"));

        // Downstream visibility (issue #197 symptom B): a missing upstream file must be reported as an
        // error, not silently replaced by the parent task's goal text.
        contract.Should().ContainEquivalentOf("real error");
        contract.Should().ContainEquivalentOf("NOT silently substitute");
    }

    [Theory]
    [InlineData("research-domain.md", "docs/planning/research-domain.md")]
    [InlineData("research-domain.md", "research-domain.md")]
    [InlineData("docs/planning/research-domain.md", "docs/planning/research-domain.md")]
    [InlineData("docs/planning/research-domain.md", "research-domain.md")]
    public void Contract_FindsPlanningArtifactAcrossLegacyAndCanonicalLocations(
        string upstreamNote,
        string actualLocation)
    {
        var subtask = MakeSubtask(4, $"Read {upstreamNote} and implement its requirements.");
        var task = $"{subtask.Title}\n\n{subtask.Scope}\n\n{CoordinatorDispatchService.BuildIntegrationBranchContract(
            MakeContext("coord-live", "main"), subtask)}";

        task.Should().Contain(upstreamNote);
        task.Should().Contain("absent from BOTH locations");
        if (actualLocation.StartsWith("docs/planning/", StringComparison.Ordinal))
            task.Should().Contain("`docs/planning/<filename>` first");
        else
            task.Should().Contain("fall back to `<filename>`");
    }
}
