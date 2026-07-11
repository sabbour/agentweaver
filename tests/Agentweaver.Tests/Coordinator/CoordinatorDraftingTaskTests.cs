using System.Text;
using Agentweaver.Api.Coordinator;
using FluentAssertions;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for the roster-aware, goal-breadth-faithful outcome-spec drafting prompt
/// (GitHub #235). Mirror the presence/ordering assertions used by
/// <see cref="CoordinatorWorkflowHintTests"/> and build real <c>.squad/team.md</c> fixtures on
/// disk (as <c>SquadReaderWriterTests</c> does) for the roster-reading path.
/// </summary>
public sealed class CoordinatorDraftingTaskTests : IDisposable
{
    // The block header that only appears when a non-empty capability summary is emitted.
    // (The guidance paragraph also mentions "TEAM CAPABILITIES", so absence must be asserted
    // against this specific header, not the bare phrase.)
    private const string CapabilityBlockHeader =
        "TEAM CAPABILITIES (roles available on this project's team";

    private const string GoalBreadthClause = "faithfully represent the full breadth";
    private const string CapabilityFilterClause = "use TEAM CAPABILITIES only as a filter";
    private const string LeanGuardClause = "keep the outcome and scope lean";
    private const string GoalFence = "<<<USER_GOAL>>>";

    private const string SampleGoal =
        "Build me a meal planner web app, take it from the initial idea to a working preview.";

    private readonly string _root;

    public CoordinatorDraftingTaskTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"drafting-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void BuildDraftingTask_IncludesTeamCapabilitiesBlock_WhenSummaryNonEmpty()
    {
        var summary = "- customer-researcher (Customer Researcher)\n- backend-engineer (Backend Engineer)";

        var task = CopilotCoordinatorSpecDrafter.BuildDraftingTask(SampleGoal, string.Empty, summary);

        task.Should().Contain(CapabilityBlockHeader);
        task.Should().Contain("- customer-researcher (Customer Researcher)");
        task.Should().Contain("- backend-engineer (Backend Engineer)");
    }

    [Fact]
    public void BuildDraftingTask_OmitsTeamCapabilitiesBlock_WhenSummaryEmpty()
    {
        var task = CopilotCoordinatorSpecDrafter.BuildDraftingTask(SampleGoal, string.Empty, string.Empty);

        task.Should().NotContain(CapabilityBlockHeader);
        // The guidance still renders and references TEAM CAPABILITIES only as a filter, so the
        // prompt degrades gracefully with no roster.
        task.Should().Contain(CapabilityFilterClause);
    }

    [Fact]
    public void BuildDraftingTask_ContainsGoalBreadthAndLeanGuidance()
    {
        var task = CopilotCoordinatorSpecDrafter.BuildDraftingTask(SampleGoal, string.Empty, string.Empty);

        task.Should().Contain(GoalBreadthClause);
        task.Should().Contain(LeanGuardClause);
    }

    [Fact]
    public void BuildDraftingTask_PlacesGoalBreadthRuleBeforeCapabilityFilterRule()
    {
        var summary = "- customer-researcher (Customer Researcher)";

        var task = CopilotCoordinatorSpecDrafter.BuildDraftingTask(SampleGoal, string.Empty, summary);

        var breadthIndex = task.IndexOf(GoalBreadthClause, StringComparison.Ordinal);
        var filterIndex = task.IndexOf(CapabilityFilterClause, StringComparison.Ordinal);

        breadthIndex.Should().BeGreaterThanOrEqualTo(0);
        filterIndex.Should().BeGreaterThanOrEqualTo(0);
        breadthIndex.Should().BeLessThan(filterIndex, "the goal-breadth rule must precede the capability-filter rule");
    }

    [Fact]
    public void BuildDraftingTask_PlacesCapabilityBlockOutsideUserGoalFence()
    {
        var summary = "- customer-researcher (Customer Researcher)";

        var task = CopilotCoordinatorSpecDrafter.BuildDraftingTask(SampleGoal, string.Empty, summary);

        var headerIndex = task.IndexOf(CapabilityBlockHeader, StringComparison.Ordinal);
        var summaryIndex = task.IndexOf("- customer-researcher (Customer Researcher)", StringComparison.Ordinal);
        // The literal "<<<USER_GOAL>>>" appears twice: once in the SECURITY preamble that names the
        // fences, and again as the actual opening fence right before the untrusted goal. LastIndexOf
        // targets the real opening fence — the capability data must sit before (and never inside) it.
        var fenceIndex = task.LastIndexOf(GoalFence, StringComparison.Ordinal);

        fenceIndex.Should().BeGreaterThanOrEqualTo(0);
        headerIndex.Should().BeGreaterThanOrEqualTo(0);
        summaryIndex.Should().BeGreaterThanOrEqualTo(0);
        headerIndex.Should().BeLessThan(fenceIndex, "capability data is trusted and must live outside the untrusted-goal fence");
        summaryIndex.Should().BeLessThan(fenceIndex, "capability data is trusted and must live outside the untrusted-goal fence");
        // And it must not land inside the fenced untrusted goal.
        var endFenceIndex = task.LastIndexOf("<<<END_USER_GOAL>>>", StringComparison.Ordinal);
        summaryIndex.Should().BeLessThan(fenceIndex);
        endFenceIndex.Should().BeGreaterThan(fenceIndex);
    }

    [Fact]
    public void FormatCapabilities_UsesTerseRoleLineFormat_AndIsEmptyForNoMembers()
    {
        var formatted = CopilotCoordinatorSpecDrafter.FormatCapabilities(new[]
        {
            (RoleId: "customer-researcher", RoleTitle: "Customer Researcher"),
            (RoleId: "backend-engineer", RoleTitle: "Backend Engineer"),
        });

        formatted.Should().Be("- customer-researcher (Customer Researcher)\n- backend-engineer (Backend Engineer)");
        CopilotCoordinatorSpecDrafter.FormatCapabilities(Array.Empty<(string, string)>()).Should().BeEmpty();
    }

    [Fact]
    public void BuildCapabilitySummary_ListsDispatchableRoles_AndExcludesInfraAgents()
    {
        var dir = Path.Combine(_root, "roster");
        WriteTeam(
            dir,
            ("Nova", "Customer Researcher"),
            ("Pax", "Product Marketing Manager"),
            ("Tank", "Backend Engineer"),
            ("Scribe", "Scribe"),
            ("Ralph", "Ralph"),
            ("Rai", "RAI Checker"));

        var summary = CopilotCoordinatorSpecDrafter.BuildCapabilitySummary(dir);

        // Dispatchable specialists are listed with the terse "- {RoleId} ({RoleTitle})" format.
        summary.Should().Contain("- customer-researcher (Customer Researcher)");
        summary.Should().Contain("- product-marketing-manager (Product Marketing Manager)");
        summary.Should().Contain("- backend-engineer (Backend Engineer)");

        // Platform infra agents are excluded via CoordinatorRosterGuard.IsDispatchableMember.
        summary.Should().NotContain("scribe");
        summary.Should().NotContain("Scribe");
        summary.Should().NotContain("ralph");
        summary.Should().NotContain("Ralph");
        summary.Should().NotContain("rai-checker");
        summary.Should().NotContain("RAI Checker");
    }

    [Fact]
    public void BuildCapabilitySummary_ReturnsEmpty_WhenNoTeam()
    {
        var dir = Path.Combine(_root, "no-team");
        Directory.CreateDirectory(dir);

        CopilotCoordinatorSpecDrafter.BuildCapabilitySummary(dir).Should().BeEmpty();
    }

    private static void WriteTeam(string dir, params (string Name, string RoleTitle)[] members)
    {
        var squadDir = Path.Combine(dir, ".squad");
        Directory.CreateDirectory(squadDir);

        var sb = new StringBuilder();
        sb.AppendLine("# Squad Team");
        sb.AppendLine();
        sb.AppendLine("> capability-fixture");
        sb.AppendLine();
        sb.AppendLine("## Members");
        sb.AppendLine();
        sb.AppendLine("| Name | Role | Charter | Status |");
        sb.AppendLine("|------|------|---------|--------|");
        foreach (var (name, roleTitle) in members)
            sb.AppendLine($"| {name} | {roleTitle} | .squad/agents/{name.ToLowerInvariant()}/charter.md | active |");
        sb.AppendLine();
        sb.AppendLine("## Project Context");
        sb.AppendLine();
        sb.AppendLine("- **Universe:** Inception");

        File.WriteAllText(Path.Combine(squadDir, "team.md"), sb.ToString());
    }
}
