using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Graph;

/// <summary>
/// Tests for the dynamic per-run workflow graph descriptor (Feature: make the visualization
/// dynamic). The descriptor is BUILT FROM THE SAME CODE that wires the MAF workflow — see
/// <see cref="GraphDescriptorBuilder"/> — so these unit tests pin the collapse + re-stitch +
/// loopback contract for both pipeline variants, and the drift-guard test reflects the built
/// MAF graph to convert any future BuildWorkflow drift into a CI failure.
///
/// The class name carries "Coordinator" so it is included by the coordinator-filtered test run;
/// the child variant IS the coordinator child pipeline.
/// </summary>
public sealed class CoordinatorWorkflowGraphDescriptorTests : IClassFixture<CoordinatorWebApplicationFactory>
{
    private readonly CoordinatorWebApplicationFactory _factory;

    public CoordinatorWorkflowGraphDescriptorTests(CoordinatorWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private RunWorkflowFactory Factory =>
        _factory.Services.GetRequiredService<RunWorkflowFactory>();

    private static (string From, string To) E(string from, string to) => (from, to);

    private static GraphEdge? Find(GraphDescriptor d, string from, string to) =>
        d.Edges.FirstOrDefault(e => e.From == from && e.To == to);

    // ── Full variant ─────────────────────────────────────────────────────────

    [Fact]
    public void FullVariant_HasExpectedNodes()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        d.Variant.Should().Be("full");
        d.GraphId.Should().NotBeNullOrEmpty();
        d.StartNodeId.Should().Be("agent");
        d.Nodes.Select(n => n.Id).Should().BeEquivalentTo(
            new[] { "agent", "rai", "review", "merge", "push-pr", "scribe" });
        // No assemble-ready terminal in the full pipeline.
        d.Nodes.Select(n => n.Id).Should().NotContain("assemble-ready");
        // All nodes are live and self-describing.
        d.Nodes.Should().OnlyContain(n => n.Kind == "live");
        d.Nodes.Should().OnlyContain(n => n.ChildGraphRef == null);
        // node_type taxonomy is required on every node and drives the rendered shape.
        d.Nodes.Single(n => n.Id == "agent").NodeType.Should().Be("agent");
        d.Nodes.Single(n => n.Id == "rai").NodeType.Should().Be("agent");
        d.Nodes.Single(n => n.Id == "review").NodeType.Should().Be("gate");
        d.Nodes.Single(n => n.Id == "merge").NodeType.Should().Be("action");
        d.Nodes.Single(n => n.Id == "push-pr").NodeType.Should().Be("action");
        d.Nodes.Single(n => n.Id == "scribe").NodeType.Should().Be("agent");
        // Review gate uses the explicit known-port fallback label.
        d.Nodes.Single(n => n.Id == "review").Label.Should().Be("Human Review");
        d.Nodes.Single(n => n.Id == "review").Role.Should().Be("review");
        d.Nodes.Single(n => n.Id == "push-pr").Label.Should().Be("Push PR");
        d.Nodes.Single(n => n.Id == "push-pr").Role.Should().Be("action");
    }

    [Fact]
    public void FullVariant_HasExpectedCollapsedEdges()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        var edges = d.Edges.Select(e => E(e.From, e.To)).ToHashSet();
        edges.Should().BeEquivalentTo(new[]
        {
            E("agent", "rai"),
            E("rai", "scribe"),
            E("rai", "review"),
            E("rai", "agent"),     // RAI revise loop
            E("review", "merge"),
            E("review", "agent"),  // review request-changes loop
            E("merge", "push-pr"),
            E("push-pr", "scribe"),
            E("merge", "review"),  // merge-blocked re-enter review loop
        });
    }

    [Fact]
    public void FullVariant_FlagsLoopbacks()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        Find(d, "rai", "agent")!.Loopback.Should().BeTrue("RAI revise loops back to the agent");
        Find(d, "review", "agent")!.Loopback.Should().BeTrue("review request-changes loops back to the agent");
        Find(d, "merge", "review")!.Loopback.Should().BeTrue("a blocked merge re-enters the review gate");

        Find(d, "agent", "rai")!.Loopback.Should().BeFalse();
        Find(d, "rai", "scribe")!.Loopback.Should().BeFalse();
        Find(d, "rai", "review")!.Loopback.Should().BeFalse();
        Find(d, "review", "merge")!.Loopback.Should().BeFalse();
        Find(d, "merge", "push-pr")!.Loopback.Should().BeFalse();
        Find(d, "push-pr", "scribe")!.Loopback.Should().BeFalse();
    }

    [Fact]
    public void FullVariant_ComputesCardinality()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        // RAI forward fans out to review + scribe.
        Find(d, "rai", "review")!.Cardinality.Should().Be("fanout");
        Find(d, "rai", "scribe")!.Cardinality.Should().Be("fanout");
        // scribe is reached from both rai and push-pr (forward fan-in).
        Find(d, "push-pr", "scribe")!.Cardinality.Should().Be("fanin");
        // 1:1 forward edges.
        Find(d, "agent", "rai")!.Cardinality.Should().Be("direct");
        Find(d, "review", "merge")!.Cardinality.Should().Be("direct");
        Find(d, "merge", "push-pr")!.Cardinality.Should().Be("direct");
        // Loopback edges are direct back-edges.
        Find(d, "rai", "agent")!.Cardinality.Should().Be("direct");
    }

    // ── Child variant ──────────────────────────────────────────────────────────

    [Fact]
    public void ChildVariant_IsTrimmedAgentAssemble()
    {
        var d = Factory.GetGraphDescriptor(isChild: true);

        d.Variant.Should().Be("child");
        d.StartNodeId.Should().Be("agent");
        // FIX 2: the trimmed child pipeline now has a graph-native failure->terminal node
        // (child-turn-failed) alongside the assemble-ready success terminal.
        d.Nodes.Select(n => n.Id).Should().BeEquivalentTo(new[] { "agent", "assemble-ready", "child-turn-failed" });
        // The trimmed child pipeline has no per-child RAI / review / merge / scribe.
        d.Nodes.Select(n => n.Id).Should().NotContain(new[] { "rai", "review", "merge", "push-pr", "scribe" });

        var assemble = d.Nodes.Single(n => n.Id == "assemble-ready");
        assemble.Label.Should().Be("Assemble-ready");
        assemble.Role.Should().Be("assembly");

        var turnFailed = d.Nodes.Single(n => n.Id == "child-turn-failed");
        turnFailed.Label.Should().Be("Turn failed");
        turnFailed.Role.Should().Be("assembly");
        turnFailed.NodeType.Should().Be("terminal");

        // node_type taxonomy: agent turns are "agent", the assemble-ready checkpoint is "terminal".
        d.Nodes.Single(n => n.Id == "agent").NodeType.Should().Be("agent");
        assemble.NodeType.Should().Be("terminal");
    }

    [Fact]
    public void ChildVariant_HasExpectedDirectEdgeToAssembleReady()
    {
        var d = Factory.GetGraphDescriptor(isChild: true);

        var edges = d.Edges.Select(e => E(e.From, e.To)).ToHashSet();
        edges.Should().BeEquivalentTo(new[]
        {
            E("agent", "assemble-ready"),
            E("agent", "child-turn-failed"),
        });

        Find(d, "agent", "assemble-ready")!.Loopback.Should().BeFalse();
        Find(d, "agent", "child-turn-failed")!.Loopback.Should().BeFalse();
    }
}
