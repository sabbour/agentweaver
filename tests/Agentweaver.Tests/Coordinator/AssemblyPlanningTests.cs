using FluentAssertions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

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
    public void TerminalIneligibleSubtasks_IgnoresChildrenThatAreOnlyNotReadyYet()
    {
        var statusById = new Dictionary<int, string>
        {
            [1] = SubtaskStatus.Pending,
            [2] = SubtaskStatus.Running,
            [3] = SubtaskStatus.Failed,
            [4] = SubtaskStatus.Blocked,
            [5] = SubtaskStatus.Completed,
        };

        AssemblyPlanning.TerminalIneligibleSubtasks(statusById).Should().Equal(3, 4);
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

    // ── stale ineligible_subtasks eligibility-gate reason (#309 follow-up / #314) ────────────────

    [Theory]
    [InlineData("ineligible_subtasks [369,370]", true)]              // the D2 gate's stamped form
    [InlineData("assembly_blocked: ineligible_subtasks [369,370]", true)] // coordinator-run-result form
    [InlineData("ineligible_subtasks", true)]                        // bare marker (no id list)
    [InlineData("build_test_infra_shell_execution_timeout", false)] // a DIFFERENT retryable phase reason
    [InlineData("integration_conflict", false)]                     // a genuine output conflict
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsStaleIneligibleSubtasksReason_MatchesTheEligibilityGateMarkerOnly(string? reason, bool expected) =>
        AssemblyPlanning.IsStaleIneligibleSubtasksReason(reason).Should().Be(expected);

    [Fact]
    public void StaleIneligible_And_RetryableBuildTestInfra_DoNotMisclassifyEachOther_314()
    {
        // #314: CoordinatorSteeringService's redirect branch ORs these two predicates to decide
        // "re-arm assembly only (no subtask reset)". Each reason must match ONLY its own predicate —
        // if a stale ineligible_subtasks reason leaked into the infra predicate (or vice-versa) the
        // classification would still work by luck, but a future edit to either could silently swap
        // behavior. Pin the disjointness so the two phases stay independently recognizable.
        const string ineligible = "ineligible_subtasks [369,370]";
        const string infra = "build_test_infra_shell_execution_timeout";

        AssemblyPlanning.IsStaleIneligibleSubtasksReason(ineligible).Should().BeTrue();
        AssemblyPlanning.IsRetryableBuildTestInfraReason(ineligible).Should().BeFalse();

        AssemblyPlanning.IsRetryableBuildTestInfraReason(infra).Should().BeTrue();
        AssemblyPlanning.IsStaleIneligibleSubtasksReason(infra).Should().BeFalse();
    }

    [Fact]
    public void IneligibleSubtasksReasonMarker_IsTheSubstringTheD2GateStamps()
    {
        // Guards the marker constant against drifting away from what CoordinatorAssemblyService's
        // eligibility gate actually writes ("ineligible_subtasks [...]"). If these diverge, the
        // #314 redirect re-arm would silently stop recognizing stale parks and reset green subtasks.
        AssemblyPlanning.IneligibleSubtasksReasonMarker.Should().Be("ineligible_subtasks");
        AssemblyPlanning.IsStaleIneligibleSubtasksReason(
            $"{AssemblyPlanning.IneligibleSubtasksReasonMarker} [1,2]").Should().BeTrue();
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

    // ── D6 prose-based rejection inference (InferRedispatch) removed by rev8 unified steering ──────
    // The fragile prose-parsing InferRedispatch heuristic and its AssemblyRejectionPlan record were
    // deleted; the coordinator now chooses steering targets explicitly. The former
    // InferRedispatch_* tests exercised removed behavior and were removed with it. ExtractFileTokens
    // / ExtractTouchedFiles remain covered above (output-conflict callers still use them).

    // ── #223 implicated-subtask scoping + two-sets (lockoutSet vs redispatchSet) ────────────────

    private static IReadOnlyDictionary<int, IReadOnlySet<string>> Touched(
        params (int Id, string[] Files)[] entries) =>
        entries.ToDictionary(
            e => e.Id,
            e => (IReadOnlySet<string>)new HashSet<string>(e.Files, StringComparer.Ordinal));

    [Fact]
    public void ScopeImplicatedSubtasks_ExcludesProseSubtaskThatCommittedOnlyUnnamedMarkdown_223Regression()
    {
        // (a) Subtask 1 (backend) committed the implicated file; subtask 2 (a research/PRD pod) committed
        // ONLY an unnamed .md. The reviewer names only the backend file → the prose subtask must NOT be
        // swept in (the #223 collateral-reset+lockout bug).
        var touched = Touched((1, ["backend/api.cs"]), (2, ["docs/prd.md"]));

        var implicated = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, ["backend/api.cs"], out var usedFallback, out var reason);

        implicated.Should().Equal(1);
        usedFallback.Should().BeFalse();
        reason.Should().BeEmpty();
    }

    [Fact]
    public void ScopeImplicatedSubtasks_NamedFileSharedByTwoSubtasks_IncludesBoth()
    {
        // (b) A named file touched by two subtasks implicates BOTH (fail-safe over-include).
        var touched = Touched((1, ["shared/util.cs"]), (2, ["shared/util.cs"]));

        var implicated = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, ["shared/util.cs"], out var usedFallback, out _);

        implicated.Should().Equal(1, 2);
        usedFallback.Should().BeFalse();
    }

    [Fact]
    public void ScopeImplicatedSubtasks_NoTargetFilesField_FallsBackToAllContributors()
    {
        // (c-i) No structured field at all → broad all-contributors fallback, reason = no_target_files_field.
        var touched = Touched((1, ["backend/api.cs"]), (2, ["docs/prd.md"]));

        var implicated = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, targetFiles: null, out var usedFallback, out var reason);

        implicated.Should().Equal(1, 2);
        usedFallback.Should().BeTrue();
        reason.Should().Be(AssemblyPlanning.ScopeFallbackNoField);
    }

    [Fact]
    public void ScopeImplicatedSubtasks_TargetFilesMatchNothing_FallsBackToAllContributors_WithNoMatchReason()
    {
        // (c-ii) Field present but reverse-maps to nothing → broad fallback, reason = target_files_matched_nothing.
        var touched = Touched((1, ["backend/api.cs"]), (2, ["docs/prd.md"]));

        var implicated = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, ["does/not/exist.cs"], out var usedFallback, out var reason);

        implicated.Should().Equal(1, 2);
        usedFallback.Should().BeTrue();
        reason.Should().Be(AssemblyPlanning.ScopeFallbackNoMatch);
    }

    [Fact]
    public void ScopeImplicatedSubtasks_MatchesBareFilenameByTrailingSegment()
    {
        // A reviewer naming a bare filename must match the file whose repo-relative path ends with "/api.cs".
        var touched = Touched((1, ["backend/api.cs"]), (2, ["frontend/app.tsx"]));

        var implicated = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, ["api.cs"], out var usedFallback, out _);

        implicated.Should().Equal(1);
        usedFallback.Should().BeFalse();
    }

    [Fact]
    public void ScopeImplicatedSubtasks_NormalizesBackslashesAndLeadingSlash()
    {
        // Windows-style separators / leading slash in the reviewer's hint normalize to the touched form.
        var touched = Touched((1, ["backend/api.cs"]), (2, ["docs/prd.md"]));

        var implicated = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, ["\\backend\\api.cs"], out var usedFallback, out _);

        implicated.Should().Equal(1);
        usedFallback.Should().BeFalse();
    }

    [Fact]
    public void TwoSets_NamedBackendWithFrontendDependent_DependentInRedispatchButNotLockout()
    {
        // (d) Subtask 1 = backend (named/implicated). Subtask 2 = frontend that DEPENDS ON 1. The edge is
        // (SubtaskId, DependsOnSubtaskId) = (2, 1). The dependent (2) must be re-dispatched (rebuild
        // against the revised contract) but its author must NOT be locked out.
        var touched = Touched((1, ["backend/api.cs"]), (2, ["frontend/app.tsx"]));
        var edges = new[] { (2, 1) };

        var lockoutSet = AssemblyPlanning.ScopeImplicatedSubtasks(
            touched, ["backend/api.cs"], out _, out _);
        var dependents = AssemblyPlanning.TransitiveDependents(lockoutSet, edges);
        var redispatchSet = lockoutSet.Concat(dependents).Distinct().OrderBy(x => x).ToList();

        lockoutSet.Should().Equal(1);        // only the implicated backend author is locked out
        dependents.Should().Equal(2);        // the frontend dependent rebuilds…
        lockoutSet.Should().NotContain(2);   // …but is NOT in the lockout set
        redispatchSet.Should().Equal(1, 2);  // redispatch = implicated ∪ dependents
    }

    [Fact]
    public void TransitiveDependents_FollowsEdgesTransitively_ExcludesSeed()
    {
        // 2 depends on 1; 3 depends on 2. Implicating 1 sweeps 2 AND 3 (transitive) but never the seed.
        var edges = new[] { (2, 1), (3, 2) };

        var dependents = AssemblyPlanning.TransitiveDependents([1], edges);

        dependents.Should().Equal(2, 3);
    }

    [Fact]
    public void TransitiveDependents_NoDependents_ReturnsEmpty()
    {
        // A leaf implicated subtask with no dependents produces an empty redispatch-extension set.
        var edges = new[] { (2, 1) }; // 2 depends on 1; implicating 2 has no dependents

        AssemblyPlanning.TransitiveDependents([2], edges).Should().BeEmpty();
    }
}
