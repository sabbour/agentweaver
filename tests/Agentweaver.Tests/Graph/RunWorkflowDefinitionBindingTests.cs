using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Api.ReviewPolicies;
using Agentweaver.Api.Workflows;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Graph;

/// <summary>
/// Golden parity test for wf-maf-binding (Feature 010): the live full run pipeline is now assembled by
/// ITERATING the default <c>WorkflowDefinition</c>'s edges (see <c>RunWorkflowGraphBinder</c>) instead of
/// the previous hand-coded <c>GraphDescriptorBuilder</c> chain. This test freezes the structure of the
/// hand-coded graph as a GOLDEN BASELINE and asserts the definition-driven graph reproduces it exactly:
/// same start node, same visible node set (id + label + role + kind + node_type), and same collapsed
/// edge set (from, to, cardinality, loopback). Because predicate lambdas are not directly comparable,
/// the assertion is on everything the <see cref="GraphDescriptor"/> exposes — which, together with the
/// existing <c>CoordinatorWorkflowGraphDescriptorTests</c> and <c>CoordinatorWorkflowGraphDriftGuard</c>
/// reflection test, pins the wiring.
///
/// The class name carries "Coordinator" so it is included by the coordinator-filtered test run.
/// </summary>
public sealed class CoordinatorRunWorkflowDefinitionBindingTests
    : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public CoordinatorRunWorkflowDefinitionBindingTests(CoordinatorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private RunWorkflowFactory Factory =>
        _factory.Services.GetRequiredService<RunWorkflowFactory>();

    // ── Golden baseline: the hand-coded full pipeline, frozen ───────────────────────────────────
    // Every field the GraphDescriptor exposes for a visible node.
    private sealed record NodeShape(string Id, string Label, string Role, string Kind, string NodeType);

    // Every field the GraphDescriptor exposes for a collapsed edge.
    private sealed record EdgeShape(string From, string To, string Cardinality, bool Loopback);

    private static readonly NodeShape[] GoldenNodes =
    {
        new("agent",  "Agent",        "agent",  "live", "agent"),
        new("rai",    "Rai",          "rai",    "live", "agent"),
        new("review", "Human Review", "review", "live", "gate"),
        new("merge",  "Merge",        "merge",  "live", "action"),
        new("scribe", "Scribe",       "scribe", "live", "agent"),
    };

    private static readonly EdgeShape[] GoldenEdges =
    {
        new("agent",  "rai",    "direct", false),
        new("rai",    "scribe", "fanout", false),
        new("rai",    "review", "fanout", false),
        new("rai",    "agent",  "direct", true),   // RAI revise loop
        new("review", "merge",  "direct", false),
        new("review", "agent",  "direct", true),   // review request-changes loop
        new("merge",  "scribe", "fanin",  false),
        new("merge",  "review", "direct", true),   // merge-blocked re-enter review loop
    };

    [Fact]
    public void FullVariant_DefinitionDrivenGraph_MatchesGoldenNodeSet()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        d.Variant.Should().Be("full");
        d.StartNodeId.Should().Be("agent");

        var actual = d.Nodes
            .Select(n => new NodeShape(n.Id, n.Label, n.Role, n.Kind, n.NodeType))
            .ToHashSet();

        actual.Should().BeEquivalentTo(GoldenNodes);
    }

    [Fact]
    public void FullVariant_DefinitionDrivenGraph_MatchesGoldenEdgeSet()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        var actual = d.Edges
            .Select(e => new EdgeShape(e.From, e.To, e.Cardinality, e.Loopback))
            .ToHashSet();

        actual.Should().BeEquivalentTo(GoldenEdges);
    }

    [Fact]
    public void FullVariant_DefinitionDrivenGraph_IsDeterministicAcrossBuilds()
    {
        // Building twice must yield the identical structural projection — the binder is side-effect free
        // and order-stable, so the per-run descriptor never drifts between builds.
        var first = Project(Factory.GetGraphDescriptor(isChild: false));
        var second = Project(Factory.GetGraphDescriptor(isChild: false));

        second.Nodes.Should().BeEquivalentTo(first.Nodes);
        second.Edges.Should().BeEquivalentTo(first.Edges);
        second.StartNodeId.Should().Be(first.StartNodeId);
    }

    [Fact]
    public void FullVariant_ComposedRubberduckPolicy_BindsInjectedGateIntoLiveGraph()
    {
        var policy = new ReviewPolicy
        {
            Name = "rubberduck-opt-in",
            Steps = [new ReviewStep { Kind = ReviewStepKind.Rubberduck, Label = "Rubber-duck review" }],
        };
        var effective = ReviewPolicyComposer.Compose(BuiltInWorkflows.Default.Definition!, policy).Effective;

        var (_, descriptor) = Factory.BuildWorkflowForTest(isChild: false, effective);

        descriptor.Nodes.Should().Contain(n => n.Id == "policy-rubberduck" && n.NodeType == "gate");
        descriptor.Edges.Should().Contain(e => e.From == "review" && e.To == "policy-rubberduck");
        descriptor.Edges.Should().Contain(e => e.From == "policy-rubberduck" && e.To == "merge");
    }

    [Fact]
    public void FullVariant_DirectAgentTurnPolicyGate_StoresAgentOutputBeforeDecisionGate()
    {
        var policy = new ReviewPolicy
        {
            Name = "rubberduck-required",
            Steps = [new ReviewStep { Kind = ReviewStepKind.Rubberduck, Label = "Rubber-duck review" }],
        };
        var effective = ReviewPolicyComposer.Compose(DirectMergeWorkflow(), policy).Effective;

        var (workflow, descriptor) = Factory.BuildWorkflowForTest(isChild: false, effective);

        workflow.ReflectExecutors().Should().ContainKey("policy-agent-turn-storer",
            "a policy gate fed directly by AgentTurnOutput must persist merge data before a later decision-to-merge adapter reads it");
        descriptor.Edges.Should().Contain(e => e.From == "rai" && e.To == "policy-rubberduck");
        descriptor.Edges.Should().Contain(e => e.From == "policy-rubberduck" && e.To == "merge");
    }

    [Fact]
    public void ChildVariant_RemainsTrimmedAgentAssembleReady_ForStage2Parity()
    {
        var d = Factory.GetGraphDescriptor(isChild: true);

        d.Variant.Should().Be("child");
        d.Nodes.Select(n => n.Id).Should().BeEquivalentTo(["agent", "assemble-ready"]);
        d.Edges.Select(e => new EdgeShape(e.From, e.To, e.Cardinality, e.Loopback)).ToHashSet()
            .Should().BeEquivalentTo(
            [
                new EdgeShape("agent", "assemble-ready", "direct", false),
            ]);
    }

    private static (string StartNodeId, HashSet<NodeShape> Nodes, HashSet<EdgeShape> Edges) Project(
        GraphDescriptor d) =>
    (
        d.StartNodeId,
        d.Nodes.Select(n => new NodeShape(n.Id, n.Label, n.Role, n.Kind, n.NodeType)).ToHashSet(),
        d.Edges.Select(e => new EdgeShape(e.From, e.To, e.Cardinality, e.Loopback)).ToHashSet()
    );

    private static WorkflowDefinition DirectMergeWorkflow() => new()
    {
        Id = "direct-merge",
        Name = "Direct merge",
        Trigger = BuiltInWorkflows.Default.Definition!.Trigger,
        Start = "agent",
        Nodes =
        [
            new WorkflowNode { Id = "agent", Type = WorkflowNodeType.Prompt, Label = "Agent", Role = "agent", Kind = "live" },
            new WorkflowNode
            {
                Id = "rai", Type = WorkflowNodeType.Check, Label = "Rai", Role = "rai", Kind = "live",
                GateKind = "rai", Branches = ["pass", "no-changes"]
            },
            new WorkflowNode { Id = "merge", Type = WorkflowNodeType.Merge, Label = "Merge", Role = "merge", Kind = "live" },
            new WorkflowNode { Id = "scribe", Type = WorkflowNodeType.Scribe, Label = "Scribe", Role = "scribe", Kind = "live" },
            new WorkflowNode { Id = "done", Type = WorkflowNodeType.Terminal, Label = "Done", Role = "plumbing", Kind = "terminal" },
        ],
        Edges =
        [
            new WorkflowEdge { From = "agent", To = "rai" },
            new WorkflowEdge { From = "rai", To = "merge", When = "pass" },
            new WorkflowEdge { From = "rai", To = "scribe", When = "no-changes" },
            new WorkflowEdge { From = "merge", To = "scribe", When = "merged" },
            new WorkflowEdge { From = "scribe", To = "done" },
        ],
    };
}
