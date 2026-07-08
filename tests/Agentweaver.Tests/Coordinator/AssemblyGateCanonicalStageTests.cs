using FluentAssertions;
using Agentweaver.Api.Coordinator;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests for the assembly-gate stuck-run root cause: a workflow-derived human-review gate
/// must persist the CANONICAL <see cref="AssemblyStage.Review"/> ("review") stage id — never the raw
/// workflow node id ("human-review"). If it persists the node id, the work plan's <c>AssemblyStage</c>
/// never equals <see cref="AssemblyStage.Review"/>, so
/// <c>CoordinatorAssemblyReviewPersistence.IsWorkPlanAwaitingReviewAsync</c> returns false, the
/// <c>POST /assembly/review</c> approval 409s with <c>no_assembly_review_pending</c>, and the run
/// parks forever in <c>awaiting_review</c>. Blank/default-workflow projects hit this every time
/// because the default coordinator workflow's human-review node id is "human-review".
/// </summary>
public sealed class AssemblyGateCanonicalStageTests
{
    [Fact]
    public void CanonicalStageId_HumanReviewKind_MapsToReviewStage()
    {
        // The workflow node id is arbitrary ("human-review"); the persisted stage MUST be canonical.
        CoordinatorGraphDescriptor.CanonicalStageId("human-review", "human-review")
            .Should().Be(AssemblyStage.Review);

        // Even if a workflow authors the node id as "review" (gate_kind still normalizes to
        // human-review), the canonical stage is unchanged.
        CoordinatorGraphDescriptor.CanonicalStageId("human-review", "review")
            .Should().Be(AssemblyStage.Review);
    }

    [Fact]
    public void CanonicalStageId_RaiKind_MapsToRaiStage()
    {
        CoordinatorGraphDescriptor.CanonicalStageId("rai", "rai")
            .Should().Be(AssemblyStage.Rai);
        CoordinatorGraphDescriptor.CanonicalStageId("rai", "content-safety")
            .Should().Be(AssemblyStage.Rai);
    }

    [Fact]
    public void CanonicalStageId_KindsWithoutCanonicalStage_FallBackToNodeId()
    {
        CoordinatorGraphDescriptor.CanonicalStageId("build-test", "verify").Should().Be("verify");
        CoordinatorGraphDescriptor.CanonicalStageId("rubberduck", "duck").Should().Be("duck");
    }

    [Fact]
    public void HumanReviewGate_BuiltCanonically_MatchesValidationAndGraphContract()
    {
        // Mirror how ResolveAssemblyGatesAsync now builds a workflow-derived human-review gate.
        var gate = new CoordinatorGraphDescriptor.AssemblyGateNode(
            CoordinatorGraphDescriptor.CanonicalStageId("human-review", "human-review"),
            "Human Review",
            "human-review");

        // StageId is what gets persisted as WorkPlan.AssemblyStage and validated against
        // AssemblyStage.Review by IsWorkPlanAwaitingReviewAsync.
        gate.StageId.Should().Be(AssemblyStage.Review);

        // GraphNodeId must match the canonical planned node id the frontend renders (same as the
        // default gates), not a divergent "planned:assembly-human-review".
        gate.GraphNodeId.Should().Be(CoordinatorGraphDescriptor.AssemblyReviewNodeId);

        gate.Role.Should().Be("review");
    }

    [Fact]
    public void DefaultAndWorkflowDerivedHumanReviewGates_ProduceIdenticalStageId()
    {
        var defaultHumanGate = CoordinatorGraphDescriptor.DefaultAssemblyGates
            .Single(g => g.GateKind == "human-review");

        var workflowDerived = new CoordinatorGraphDescriptor.AssemblyGateNode(
            CoordinatorGraphDescriptor.CanonicalStageId("human-review", "human-review"),
            "Human Review",
            "human-review");

        workflowDerived.StageId.Should().Be(defaultHumanGate.StageId);
        workflowDerived.GraphNodeId.Should().Be(defaultHumanGate.GraphNodeId);
    }
}
