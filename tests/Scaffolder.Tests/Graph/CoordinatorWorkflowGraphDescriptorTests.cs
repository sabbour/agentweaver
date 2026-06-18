using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.AgentRuntime.Workflow;
using Scaffolder.Api.Runs;
using Scaffolder.Api.Runs.Graph;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Graph;

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
            new[] { "agent", "rai", "review", "merge", "scribe" });
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
        d.Nodes.Single(n => n.Id == "scribe").NodeType.Should().Be("agent");
        // Review gate uses the explicit known-port fallback label.
        d.Nodes.Single(n => n.Id == "review").Label.Should().Be("Human Review");
        d.Nodes.Single(n => n.Id == "review").Role.Should().Be("review");
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
            E("merge", "scribe"),
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
        Find(d, "merge", "scribe")!.Loopback.Should().BeFalse();
    }

    [Fact]
    public void FullVariant_ComputesCardinality()
    {
        var d = Factory.GetGraphDescriptor(isChild: false);

        // RAI forward fans out to review + scribe.
        Find(d, "rai", "review")!.Cardinality.Should().Be("fanout");
        Find(d, "rai", "scribe")!.Cardinality.Should().Be("fanout");
        // scribe is reached from both rai and merge (forward fan-in).
        Find(d, "merge", "scribe")!.Cardinality.Should().Be("fanin");
        // 1:1 forward edges.
        Find(d, "agent", "rai")!.Cardinality.Should().Be("direct");
        Find(d, "review", "merge")!.Cardinality.Should().Be("direct");
        // Loopback edges are direct back-edges.
        Find(d, "rai", "agent")!.Cardinality.Should().Be("direct");
    }

    // ── Child variant ──────────────────────────────────────────────────────────

    [Fact]
    public void ChildVariant_IsTrimmedAgentRaiAssemble()
    {
        var d = Factory.GetGraphDescriptor(isChild: true);

        d.Variant.Should().Be("child");
        d.StartNodeId.Should().Be("agent");
        d.Nodes.Select(n => n.Id).Should().BeEquivalentTo(new[] { "agent", "rai", "assemble-ready" });
        // The trimmed child pipeline has no per-child review / merge / scribe.
        d.Nodes.Select(n => n.Id).Should().NotContain(new[] { "review", "merge", "scribe" });

        var assemble = d.Nodes.Single(n => n.Id == "assemble-ready");
        assemble.Label.Should().Be("Assemble-ready");
        assemble.Role.Should().Be("assembly");

        // node_type taxonomy: agent turns are "agent", the assemble-ready checkpoint is "terminal".
        d.Nodes.Single(n => n.Id == "agent").NodeType.Should().Be("agent");
        d.Nodes.Single(n => n.Id == "rai").NodeType.Should().Be("agent");
        assemble.NodeType.Should().Be("terminal");
    }

    [Fact]
    public void ChildVariant_HasExpectedEdgesWithRaiLoopback()
    {
        var d = Factory.GetGraphDescriptor(isChild: true);

        var edges = d.Edges.Select(e => E(e.From, e.To)).ToHashSet();
        edges.Should().BeEquivalentTo(new[]
        {
            E("agent", "rai"),
            E("rai", "agent"),            // RAI revise loop
            E("rai", "assemble-ready"),
        });

        Find(d, "rai", "agent")!.Loopback.Should().BeTrue();
        Find(d, "agent", "rai")!.Loopback.Should().BeFalse();
        Find(d, "rai", "assemble-ready")!.Loopback.Should().BeFalse();
    }
}
