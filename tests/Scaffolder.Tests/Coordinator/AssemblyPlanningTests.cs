using FluentAssertions;
using Scaffolder.Api.Coordinator;

namespace Scaffolder.Tests.Coordinator;

/// <summary>
/// Unit tests for the pure Phase 3 planning logic (<see cref="AssemblyPlanning"/>): the D2 eligibility
/// gate, the D1 topological merge order, and the D6 rejection-inference rule. No DB / git / agents.
/// </summary>
public sealed class AssemblyPlanningTests
{
    // ── D2 eligibility gate ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(SubtaskStatus.AssembleReady, true)]
    [InlineData(SubtaskStatus.Completed, true)]
    [InlineData(SubtaskStatus.Pending, false)]
    [InlineData(SubtaskStatus.Dispatched, false)]
    [InlineData(SubtaskStatus.Running, false)]
    [InlineData(SubtaskStatus.RaiFlagged, false)]
    [InlineData(SubtaskStatus.Failed, false)]
    public void IsEligible_OnlyAssembleReadyOrCompleted(string status, bool expected) =>
        AssemblyPlanning.IsEligible(status).Should().Be(expected);

    [Fact]
    public void IneligibleSubtasks_ReturnsSortedOffenders_WhenAnyNotEligible()
    {
        var statusById = new Dictionary<int, string>
        {
            [3] = SubtaskStatus.Failed,
            [1] = SubtaskStatus.AssembleReady,
            [2] = SubtaskStatus.Pending,
            [4] = SubtaskStatus.Completed,
        };

        var ineligible = AssemblyPlanning.IneligibleSubtasks(statusById);

        ineligible.Should().Equal(2, 3);
        AssemblyPlanning.AllEligible(statusById).Should().BeFalse();
    }

    [Fact]
    public void AllEligible_True_WhenEverySubtaskAssembleReadyOrCompleted()
    {
        var statusById = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.AssembleReady,
            [2] = SubtaskStatus.Completed,
        };

        AssemblyPlanning.IneligibleSubtasks(statusById).Should().BeEmpty();
        AssemblyPlanning.AllEligible(statusById).Should().BeTrue();
    }

    // ── D1 topological merge order ─────────────────────────────────────────────────────────────

    [Fact]
    public void TopologicalOrder_PlacesDependenciesBeforeDependents_TiesById()
    {
        // 3 depends on 1; 2 depends on 1; 4 depends on 3. Edge = (subtask, dependsOn).
        var ids = new[] { 1, 2, 3, 4 };
        var edges = new[] { (3, 1), (2, 1), (4, 3) };

        var order = AssemblyPlanning.TopologicalOrder(ids, edges).ToList();

        order.Should().HaveCount(4);
        order.IndexOf(1).Should().BeLessThan(order.IndexOf(2));
        order.IndexOf(1).Should().BeLessThan(order.IndexOf(3));
        order.IndexOf(3).Should().BeLessThan(order.IndexOf(4));
        // Independent of 1, ties broken by id => 1 then 2 are the first ready set.
        order[0].Should().Be(1);
    }

    [Fact]
    public void TopologicalOrder_Cycle_DegradesGracefully_NoInfiniteLoop()
    {
        var ids = new[] { 1, 2 };
        var edges = new[] { (1, 2), (2, 1) }; // cycle

        var order = AssemblyPlanning.TopologicalOrder(ids, edges);

        order.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    // ── D6 file-token + touched-file parsing ───────────────────────────────────────────────────

    [Fact]
    public void ExtractFileTokens_ParsesPathsAndBareFilenames_Deduplicated()
    {
        const string feedback =
            "The change in src/auth/login.ts is wrong and config.yaml needs a tweak. Also see src/auth/login.ts again.";

        var tokens = AssemblyPlanning.ExtractFileTokens(feedback);

        tokens.Should().Contain("src/auth/login.ts");
        tokens.Should().Contain("config.yaml");
        tokens.Count(t => t == "src/auth/login.ts").Should().Be(1);
    }

    [Fact]
    public void ExtractTouchedFiles_ReadsDiffGitHeaders()
    {
        const string diff =
            "diff --git a/src/api/users.cs b/src/api/users.cs\n" +
            "index 111..222 100644\n" +
            "--- a/src/api/users.cs\n" +
            "+++ b/src/api/users.cs\n" +
            "@@ -1 +1 @@\n-old\n+new\n" +
            "diff --git a/README.md b/README.md\n" +
            "--- a/README.md\n+++ b/README.md\n@@ -1 +1 @@\n-x\n+y\n";

        var touched = AssemblyPlanning.ExtractTouchedFiles(diff);

        touched.Should().Contain("src/api/users.cs");
        touched.Should().Contain("README.md");
    }

    // ── D6 rejection inference ─────────────────────────────────────────────────────────────────

    [Fact]
    public void InferRedispatch_SelectsMatchingChildren_PlusDependents()
    {
        // Subtask 1 touched login.ts; 2 touched users.cs; 3 depends on 1 (dependent), touched other.cs.
        var touched = new Dictionary<int, IReadOnlySet<string>>
        {
            [1] = new HashSet<string> { "src/auth/login.ts" },
            [2] = new HashSet<string> { "src/api/users.cs" },
            [3] = new HashSet<string> { "src/other.cs" },
        };
        var edges = new[] { (3, 1) }; // 3 depends on 1

        var plan = AssemblyPlanning.InferRedispatch(
            feedback: "Please fix src/auth/login.ts",
            targetFiles: null,
            touchedFilesBySubtask: touched,
            edges: edges);

        plan.FellBackToAll.Should().BeFalse();
        plan.SubtaskIds.Should().BeEquivalentTo(new[] { 1, 3 }); // matched 1 + dependent 3
        plan.SubtaskIds.Should().NotContain(2);
        plan.InferredFiles.Should().Contain("src/auth/login.ts");
    }

    [Fact]
    public void InferRedispatch_ExplicitTargetFiles_AreHonored()
    {
        var touched = new Dictionary<int, IReadOnlySet<string>>
        {
            [1] = new HashSet<string> { "src/auth/login.ts" },
            [2] = new HashSet<string> { "src/api/users.cs" },
        };

        var plan = AssemblyPlanning.InferRedispatch(
            feedback: "see attached file list",
            targetFiles: new[] { "src/api/users.cs" },
            touchedFilesBySubtask: touched,
            edges: Array.Empty<(int, int)>());

        plan.FellBackToAll.Should().BeFalse();
        plan.SubtaskIds.Should().BeEquivalentTo(new[] { 2 });
    }

    [Fact]
    public void InferRedispatch_NoFilesInferred_FallsBackToAll()
    {
        var touched = new Dictionary<int, IReadOnlySet<string>>
        {
            [1] = new HashSet<string> { "a.cs" },
            [2] = new HashSet<string> { "b.cs" },
        };

        var plan = AssemblyPlanning.InferRedispatch(
            feedback: "this is bad, redo it", // no path-like tokens
            targetFiles: null,
            touchedFilesBySubtask: touched,
            edges: Array.Empty<(int, int)>());

        plan.FellBackToAll.Should().BeTrue();
        plan.SubtaskIds.Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void InferRedispatch_FilesInferredButNoChildMatches_FallsBackToAll()
    {
        var touched = new Dictionary<int, IReadOnlySet<string>>
        {
            [1] = new HashSet<string> { "a.cs" },
            [2] = new HashSet<string> { "b.cs" },
        };

        var plan = AssemblyPlanning.InferRedispatch(
            feedback: "fix src/unrelated/zzz.ts",
            targetFiles: null,
            touchedFilesBySubtask: touched,
            edges: Array.Empty<(int, int)>());

        plan.FellBackToAll.Should().BeTrue();
        plan.SubtaskIds.Should().BeEquivalentTo(new[] { 1, 2 });
    }
}
