using FluentAssertions;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Unit tests for the unified coordinator <see cref="GraphDescriptor"/> builder
/// (<see cref="CoordinatorGraphDescriptor"/>). The coordinator graph is built from domain state (the
/// work plan) — never reflection — so these tests pin the node taxonomy (node_type), the PLANNED
/// collective-assembly stage, dependency-edge wiring, and the <c>child_graph_ref</c> hand-off used
/// by the frontend to expand a dispatched child's own graph.
/// </summary>
public sealed class CoordinatorGraphDescriptorTests
{
    private static Subtask Sub(int id, string title, string agent, string model, string? childRunId) => new()
    {
        Id = id,
        WorkPlanId = 1,
        Title = title,
        Scope = "scope",
        AssignedAgent = agent,
        SelectedModelId = model,
        Phase = "execution",
        IsolationStrategy = "worktree",
        Status = "pending",
        ChildRunId = childRunId,
    };

    // A 3-subtask plan: 1 -> 3 (3 depends on 1), 2 independent. Subtask 1 has a dispatched child run.
    private static (IReadOnlyList<Subtask> Subtasks, IReadOnlyCollection<(int, int)> Deps) SamplePlan() =>
    (
        new[]
        {
            Sub(1, "Build API", "morpheus", "gpt-5.3-codex", childRunId: "run_child1"),
            Sub(2, "Build Web", "trinity", "claude-opus-4.8", childRunId: null),
            Sub(3, "Integrate", "neo", "gpt-5.5", childRunId: null),
        },
        new[] { (3, 1) } // subtask 3 depends on subtask 1
    );

    [Fact]
    public void Build_ProducesCoordinatorVariant_WithExpectedNodes()
    {
        var (subtasks, deps) = SamplePlan();

        var d = CoordinatorGraphDescriptor.Build("coord_run", subtasks, deps);

        d.Variant.Should().Be("coordinator");
        d.GraphId.Should().Be("coordinator:coord_run");
        d.StartNodeId.Should().Be("coordinator");

        d.Nodes.Select(n => n.Id).Should().BeEquivalentTo(new[]
        {
            "coordinator",
            "plan:subtask-1", "plan:subtask-2", "plan:subtask-3",
            "planned:assembly-rai", "planned:assembly-review",
            "planned:assembly-merge", "planned:assembly-scribe",
        });

        var coord = d.Nodes.Single(n => n.Id == "coordinator");
        coord.NodeType.Should().Be("agent");
        coord.Role.Should().Be("coordinator");
        coord.Kind.Should().Be("live");
        coord.Label.Should().Be("Coordinator");
    }

    [Fact]
    public void Build_SubtaskNodes_CarryRichFieldsAndChildGraphRef()
    {
        var (subtasks, deps) = SamplePlan();

        var d = CoordinatorGraphDescriptor.Build("coord_run", subtasks, deps);

        var s1 = d.Nodes.Single(n => n.Id == "plan:subtask-1");
        s1.NodeType.Should().Be("subtask");
        s1.Role.Should().Be("subtask");
        s1.Kind.Should().Be("live");
        s1.Label.Should().Be("Build API");
        s1.Agent.Should().Be("morpheus");
        s1.Model.Should().Be("gpt-5.3-codex");
        s1.Phase.Should().Be("execution");
        s1.Isolation.Should().Be("worktree");
        s1.ChildRunId.Should().Be("run_child1");
        // Dispatched child -> child_graph_ref so the frontend can expand the child's own graph.
        s1.ChildGraphRef.Should().Be("run:run_child1");

        // Undispatched subtask: no child run yet, so no child_graph_ref.
        var s2 = d.Nodes.Single(n => n.Id == "plan:subtask-2");
        s2.ChildRunId.Should().BeNull();
        s2.ChildGraphRef.Should().BeNull();
    }

    [Fact]
    public void Build_PlannedAssemblyNodes_HaveExpectedTaxonomy()
    {
        var (subtasks, deps) = SamplePlan();

        var d = CoordinatorGraphDescriptor.Build("coord_run", subtasks, deps);

        var rai = d.Nodes.Single(n => n.Id == "planned:assembly-rai");
        rai.Kind.Should().Be("planned");
        rai.NodeType.Should().Be("agent");
        rai.Role.Should().Be("rai");
        rai.Label.Should().Be("RAI");

        var review = d.Nodes.Single(n => n.Id == "planned:assembly-review");
        review.Kind.Should().Be("planned");
        review.NodeType.Should().Be("gate");
        review.Role.Should().Be("review");
        review.Label.Should().Be("Human Review");

        var merge = d.Nodes.Single(n => n.Id == "planned:assembly-merge");
        merge.Kind.Should().Be("planned");
        merge.NodeType.Should().Be("action");
        merge.Role.Should().Be("merge");

        var scribe = d.Nodes.Single(n => n.Id == "planned:assembly-scribe");
        scribe.Kind.Should().Be("planned");
        scribe.NodeType.Should().Be("agent");
        scribe.Role.Should().Be("scribe");
    }

    [Fact]
    public void Build_WiresCoordinatorRootsDependenciesAndAssemblyChain()
    {
        var (subtasks, deps) = SamplePlan();

        var d = CoordinatorGraphDescriptor.Build("coord_run", subtasks, deps);
        var edges = d.Edges.Select(e => (e.From, e.To)).ToHashSet();

        // coordinator -> roots (subtasks 1 and 2; subtask 3 is a dependent, reached via dep edge).
        edges.Should().Contain(("coordinator", "plan:subtask-1"));
        edges.Should().Contain(("coordinator", "plan:subtask-2"));
        edges.Should().NotContain(("coordinator", "plan:subtask-3"));

        // dependency edge: prerequisite (1) -> dependent (3).
        edges.Should().Contain(("plan:subtask-1", "plan:subtask-3"));

        // terminal subtasks (leaves: 2 and 3) -> planned assembly RAI. Subtask 1 is a prerequisite,
        // so it is NOT a leaf and does not feed assembly directly.
        edges.Should().Contain(("plan:subtask-2", "planned:assembly-rai"));
        edges.Should().Contain(("plan:subtask-3", "planned:assembly-rai"));
        edges.Should().NotContain(("plan:subtask-1", "planned:assembly-rai"));

        // planned assembly chain.
        edges.Should().Contain(("planned:assembly-rai", "planned:assembly-review"));
        edges.Should().Contain(("planned:assembly-review", "planned:assembly-merge"));
        edges.Should().Contain(("planned:assembly-merge", "planned:assembly-scribe"));

        // Loopback edges: RAI and human review both flow back to the coordinator (re-dispatch).
        edges.Should().Contain(("planned:assembly-rai", "coordinator"));
        edges.Should().Contain(("planned:assembly-review", "coordinator"));
    }

    [Fact]
    public void Build_AddsRaiAndReviewLoopbacks_WithoutDistortingForwardCardinality()
    {
        var (subtasks, deps) = SamplePlan();

        var d = CoordinatorGraphDescriptor.Build("coord_run", subtasks, deps);

        // Exactly two loopback edges exist: rai -> coordinator and review -> coordinator.
        var loopbacks = d.Edges.Where(e => e.Loopback).Select(e => (e.From, e.To)).ToList();
        loopbacks.Should().BeEquivalentTo(new[]
        {
            ("planned:assembly-rai", "coordinator"),
            ("planned:assembly-review", "coordinator"),
        });

        // Both loopbacks are marked Loopback==true with cardinality "direct".
        foreach (var (from, to) in new[]
                 {
                     ("planned:assembly-rai", "coordinator"),
                     ("planned:assembly-review", "coordinator"),
                 })
        {
            var edge = d.Edges.Single(e => e.From == from && e.To == to);
            edge.Loopback.Should().BeTrue();
            edge.Cardinality.Should().Be("direct");
        }

        // Every other edge stays Loopback==false.
        d.Edges.Where(e => !(e.From.StartsWith("planned:assembly-") && e.To == "coordinator"))
            .Should().OnlyContain(e => e.Loopback == false);

        // The forward chain's fan-out/fan-in are unchanged by the loopbacks: the coordinator still
        // fans out to its two roots, RAI is still fan-in from two leaves, and the rai -> review and
        // review -> merge forward edges stay "direct" (the loopback out of rai/review is excluded
        // from the forward out-degree, so it does not turn them into fan-outs).
        d.Edges.Single(e => e.From == "coordinator" && e.To == "plan:subtask-1")
            .Cardinality.Should().Be("fanout");
        d.Edges.Single(e => e.From == "plan:subtask-2" && e.To == "planned:assembly-rai")
            .Cardinality.Should().Be("fanin");
        d.Edges.Single(e => e.From == "planned:assembly-rai" && e.To == "planned:assembly-review")
            .Cardinality.Should().Be("direct");
        d.Edges.Single(e => e.From == "planned:assembly-review" && e.To == "planned:assembly-merge")
            .Cardinality.Should().Be("direct");
    }

    [Fact]
    public void Build_ComputesFanCardinality()
    {
        var (subtasks, deps) = SamplePlan();

        var d = CoordinatorGraphDescriptor.Build("coord_run", subtasks, deps);

        // coordinator fans out to two roots.
        d.Edges.Single(e => e.From == "coordinator" && e.To == "plan:subtask-1")
            .Cardinality.Should().Be("fanout");
        // assembly RAI is reached from two terminal subtasks (fan-in).
        d.Edges.Single(e => e.From == "plan:subtask-2" && e.To == "planned:assembly-rai")
            .Cardinality.Should().Be("fanin");
        // 1:1 forward chain edge.
        d.Edges.Single(e => e.From == "planned:assembly-rai" && e.To == "planned:assembly-review")
            .Cardinality.Should().Be("direct");
    }
}
