using FluentAssertions;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Workflows;

namespace Agentweaver.Tests.ReviewPolicies;

/// <summary>
/// Tests for <see cref="ReviewPolicyComposer"/> (Feature 010, FR-026/028/030/032). The composer is the
/// pure-logic heart of review policies: given a workflow and a named policy it injects the policy's
/// review steps immediately before the merge node, in declared order. These tests prove correct
/// placement and ordering for the safe default (Rubber-duck + RAI) and the human-review opt-in case,
/// and that the produced graph is structurally valid (every check verdict has a matching edge; every
/// edge endpoint exists).
/// </summary>
public sealed class ReviewPolicyComposerTests
{
    private static WorkflowDefinition DefaultWorkflow =>
        BuiltInWorkflows.Default.Definition!;

    private static ReviewPolicy DefaultPolicy =>
        BuiltInReviewPolicies.Default.Policy!;

    private static ReviewPolicy PolicyOf(params ReviewStepKind[] kinds) => new()
    {
        Name = "test",
        Steps = kinds.Select(k => new ReviewStep { Kind = k }).ToList(),
    };

    [Fact]
    public void DefaultBuiltInPolicy_IsRubberduckAndRai_HumanReviewIsOptIn()
    {
        // FR-028/FR-032: the safe default is the Rubber-duck and RAI steps; human-review is NOT default.
        var kinds = DefaultPolicy.Steps.Select(s => s.Kind).ToList();
        kinds.Should().Equal(ReviewStepKind.Rai, ReviewStepKind.Rubberduck);
        kinds.Should().NotContain(ReviewStepKind.HumanReview);
    }

    [Fact]
    public void Compose_DefaultPolicy_InjectsRaiThenRubberduckImmediatelyBeforeMerge()
    {
        var composition = ReviewPolicyComposer.Compose(DefaultWorkflow, DefaultPolicy);

        composition.AnchorMergeNodeId.Should().Be("merge");
        composition.InjectedNodeIds.Should().Equal("policy-rai", "policy-rubberduck");

        var effective = composition.Effective;

        // Injected nodes exist and are check gates.
        effective.Nodes.Should().Contain(n => n.Id == "policy-rai" && n.Type == WorkflowNodeType.Check);
        effective.Nodes.Should().Contain(n => n.Id == "policy-rubberduck" && n.Type == WorkflowNodeType.Check);

        // Chain order: rai --pass--> rubberduck --pass--> merge (placed immediately before merge).
        effective.Edges.Should().ContainSingle(e => e.From == "policy-rai" && e.To == "policy-rubberduck" && e.When == "pass");
        effective.Edges.Should().ContainSingle(e => e.From == "policy-rubberduck" && e.To == "merge" && e.When == "pass");

        // Every edge that previously fed merge is re-pointed to the first injected gate; the ONLY
        // remaining incoming edge to merge comes from the last injected step.
        effective.Edges.Where(e => e.To == "merge").Select(e => e.From)
            .Should().OnlyContain(from => from == "policy-rubberduck");
    }

    [Fact]
    public void Compose_RaiStep_FailsSafeToTerminalAndLoopsToProducer()
    {
        // FR-030: an RAI step preserves content-safety failure on a dedicated stop path.
        var composition = ReviewPolicyComposer.Compose(DefaultWorkflow, PolicyOf(ReviewStepKind.Rai));
        var effective = composition.Effective;

        var safetyEdge = effective.Edges.Should()
            .ContainSingle(e => e.From == "policy-rai" && e.When == "safety-failed").Subject;
        effective.Nodes.Should().Contain(n => n.Id == safetyEdge.To && n.Type == WorkflowNodeType.Terminal);

        // request-changes loops back to the producer (the workflow start node).
        effective.Edges.Should().ContainSingle(e => e.From == "policy-rai" && e.To == DefaultWorkflow.Start && e.When == "revise");
    }

    [Fact]
    public void Compose_HumanReviewOptIn_PlacedLastGatingMergeOnApproval()
    {
        // FR-029/FR-032: human-review is opt-in; when configured it is the final gate before merge.
        var policy = PolicyOf(ReviewStepKind.Rai, ReviewStepKind.Rubberduck, ReviewStepKind.HumanReview);
        var composition = ReviewPolicyComposer.Compose(DefaultWorkflow, policy);

        composition.InjectedNodeIds.Should().Equal("policy-rai", "policy-rubberduck", "policy-human-review");

        var effective = composition.Effective;

        // Human review is last: it gates merge on explicit approval.
        effective.Edges.Should().ContainSingle(e => e.From == "policy-human-review" && e.To == "merge" && e.When == "approved");
        effective.Edges.Where(e => e.To == "merge").Select(e => e.From)
            .Should().OnlyContain(from => from == "policy-human-review");

        // Declined routes to an injected terminal.
        var declinedEdge = effective.Edges.Should()
            .ContainSingle(e => e.From == "policy-human-review" && e.When == "declined").Subject;
        effective.Nodes.Should().Contain(n => n.Id == declinedEdge.To && n.Type == WorkflowNodeType.Terminal);
    }

    [Fact]
    public void Compose_NoMergeNode_ReturnsWorkflowUnchanged()
    {
        // No irreversible action to gate: the workflow is returned unchanged with no injection.
        var workflow = new WorkflowDefinition
        {
            Id = "no-merge",
            Name = "No merge",
            Trigger = new WorkflowTrigger { Type = WorkflowTriggerType.Manual },
            Start = "agent",
            Nodes =
            [
                new WorkflowNode { Id = "agent", Type = WorkflowNodeType.Prompt, Label = "Agent" },
                new WorkflowNode { Id = "done", Type = WorkflowNodeType.Terminal, Label = "Done" },
            ],
            Edges = [new WorkflowEdge { From = "agent", To = "done" }],
        };

        var composition = ReviewPolicyComposer.Compose(workflow, DefaultPolicy);

        composition.AnchorMergeNodeId.Should().BeNull();
        composition.InjectedNodeIds.Should().BeEmpty();
        composition.Effective.Should().BeSameAs(workflow);
    }

    [Fact]
    public void Compose_ProducesStructurallyValidGraph()
    {
        var policy = PolicyOf(ReviewStepKind.Rai, ReviewStepKind.Rubberduck, ReviewStepKind.HumanReview);
        var effective = ReviewPolicyComposer.Compose(DefaultWorkflow, policy).Effective;

        var nodeIds = effective.Nodes.Select(n => n.Id).ToHashSet();

        // No duplicate node ids.
        effective.Nodes.Select(n => n.Id).Should().OnlyHaveUniqueItems();

        // Every edge references existing nodes (no dangling edges).
        foreach (var edge in effective.Edges)
        {
            nodeIds.Should().Contain(edge.From);
            nodeIds.Should().Contain(edge.To);
        }

        // Every check node declares verdicts and has a matching outgoing edge for each (FR-016 parity).
        foreach (var check in effective.Nodes.Where(n => n.Type == WorkflowNodeType.Check))
        {
            check.Branches.Should().NotBeEmpty();
            var outgoing = effective.Edges.Where(e => e.From == check.Id).Select(e => e.When).ToHashSet();
            foreach (var verdict in check.Branches)
                outgoing.Should().Contain(verdict, $"check '{check.Id}' must route verdict '{verdict}'");
        }
    }
}
