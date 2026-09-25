using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Api.Workflows;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// Feature 015 US1 — the generalized <see cref="RunWorkflowGraphBinder"/> resolves each node's executor
/// from its TYPE (not a fixed id vocabulary) and wires edges from <c>(from, to, when)</c> generically,
/// while producing a byte-for-byte identical graph for the default workflow's visible stages (the P0
/// parity guarantee as the pipeline evolves).
/// </summary>
public sealed class RunWorkflowGraphBinderTests
{
    // ── Parity (real path): the default workflow built through the new binder with the REAL executors
    //    collapses to the same six-stage graph the hand-wired pipeline now produces. ────────────────
    [Fact]
    public void DefaultWorkflow_RealPath_ProducesCanonicalSixStageGraph()
    {
        using var factory = new WorkflowWebApplicationFactory();
        var workflowFactory = factory.Services.GetRequiredService<RunWorkflowFactory>();

        var descriptor = workflowFactory.GetGraphDescriptor(isChild: false);

        AssertCanonicalDefaultGraph(descriptor);
    }

    // ── Parity (unit path): the default WorkflowDefinition wired through the binder onto fake-but-typed
    //    bindings produces the canonical collapsed graph. This pins the raw edge/predicate/output set. ─
    [Fact]
    public void DefaultDefinition_Binder_ProducesCanonicalSixStageGraph()
    {
        var bindings = FakeBindings.Create();
        var builder = new GraphDescriptorBuilder(bindings.AgentInputStorer);

        RunWorkflowGraphBinder.WireFull(builder, BuiltInWorkflows.Default.Definition!, bindings);
        var descriptor = builder.BuildDescriptor("test-default", "full");

        AssertCanonicalDefaultGraph(descriptor);
    }

    // ── Non-default node ids resolve by TYPE: a definition whose node ids are NOT the original five
    //    produces the IDENTICAL collapsed graph, because the binder keys on type, not id. ─────────────
    [Fact]
    public void RenamedNodeIds_ResolveByType_ProduceIdenticalGraph()
    {
        var bindings = FakeBindings.Create();
        var builder = new GraphDescriptorBuilder(bindings.AgentInputStorer);

        // Same SHAPE as the default workflow, but every node id is renamed; types/gate-kinds unchanged.
        var renamed = RenamedDefaultDefinition();

        RunWorkflowGraphBinder.WireFull(builder, renamed, bindings);
        var descriptor = builder.BuildDescriptor("test-renamed", "full");

        // The descriptor nodes are the EXECUTOR logical ids (resolved by type), not the definition ids,
        // so a renamed definition collapses to the exact same canonical six-stage graph.
        AssertCanonicalDefaultGraph(descriptor);
    }

    // ── Fail closed: a node type accepted by the loader but not yet wired throws a node-scoped error. ─
    [Fact]
    public void UnwiredNodeType_FailsClosed_WithNodeScopedError()
    {
        var bindings = FakeBindings.Create();
        var builder = new GraphDescriptorBuilder(bindings.AgentInputStorer);

        var def = new WorkflowDefinition
        {
            Id = "fan",
            Name = "Fan",
            Start = "agent",
            Nodes =
            [
                Node("agent", WorkflowNodeType.Prompt),
                Node("spread", WorkflowNodeType.FanOut),
            ],
            Edges = [ new WorkflowEdge { From = "agent", To = "spread" } ],
        };

        var act = () => RunWorkflowGraphBinder.WireFull(builder, def, bindings);

        act.Should().Throw<WorkflowBindException>()
            .Which.NodeId.Should().Be("spread");
    }

    [Fact]
    public void ValidStaticFanTopology_PassesTopologyValidation_ButRemainsRuntimeUnbindable()
    {
        var definition = StaticFanDefinition();

        RunWorkflowGraphBinder.GetTopologyErrors(definition).Should().BeEmpty();
        var bindability = RunWorkflowGraphBinder.GetBindabilityErrors(definition);
        bindability.Should().Contain(error => error.Contains("fan_out", StringComparison.OrdinalIgnoreCase));
        bindability.Should().Contain(error => error.Contains("fan_in", StringComparison.OrdinalIgnoreCase));
        bindability.Should().NotContain(error => error.Contains("no executor wiring", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticFanTopology_RejectsNestedPairs()
    {
        var definition = StaticFanDefinition() with
        {
            Nodes =
            [
                .. StaticFanDefinition().Nodes,
                Node("nested-fan", WorkflowNodeType.FanOut),
                Node("nested-join", WorkflowNodeType.FanIn),
            ],
        };

        RunWorkflowGraphBinder.GetTopologyErrors(definition).Should().ContainSingle()
            .Which.Should().Contain("exactly one fan_out node and exactly one fan_in node");
    }

    [Fact]
    public void StaticFanTopology_RejectsDynamicAndPartialPolicies()
    {
        var baseline = StaticFanDefinition();
        var definition = baseline with
        {
            Nodes = baseline.Nodes.Select(node =>
                node.Id == "join" ? node with { Branches = ["first-success"] } : node).ToList(),
            Edges = baseline.Edges.Select(edge =>
                edge.From == "fan" && edge.To == "branch-a"
                    ? edge with { When = "approved" }
                    : edge).ToList(),
        };

        var errors = RunWorkflowGraphBinder.GetTopologyErrors(definition);
        errors.Should().Contain(error => error.Contains("must all be unconditional"));
        errors.Should().Contain(error => error.Contains("quorum, or partial"));
    }

    [Fact]
    public void StaticFanTopology_RejectsAmbiguousBranchEdgesAndMismatchedJoin()
    {
        var baseline = StaticFanDefinition();
        var definition = baseline with
        {
            Nodes = baseline.Nodes.Select(node =>
                node.Id == "join" ? node with { Target = "different-fan" } : node).ToList(),
            Edges =
            [
                .. baseline.Edges,
                new WorkflowEdge { From = "entry", To = "branch-a" },
                new WorkflowEdge { From = "branch-b", To = "done" },
            ],
        };

        var errors = RunWorkflowGraphBinder.GetTopologyErrors(definition);
        errors.Should().Contain(error => error.Contains("target must be null or match"));
        errors.Should().Contain(error => error.Contains("branch node 'branch-a' must have exactly one"));
        errors.Should().Contain(error => error.Contains("branch node 'branch-b' must have exactly one"));
    }

    // ── Loader: fan_out / fan_in / peer_review are no longer rejected at load time. ───────────────────
    [Theory]
    [InlineData("fan_out")]
    [InlineData("fan_in")]
    [InlineData("peer_review")]
    public void Loader_Accepts_PreviouslyRejectedNodeTypes(string nodeType)
    {
        var yaml = $"""
            id: custom
            name: Custom
            start: a
            nodes:
              - id: a
                type: prompt
                prompt: do work
              - id: b
                type: {nodeType}
            edges:
              - from: a
                to: b
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "custom.yaml", isBuiltIn: false);

        result.IsValid.Should().BeTrue(
            because: $"node type '{nodeType}' must load after US1 removed the bindable-type gate; error was: {result.Error}");
        result.Definition!.Nodes.Should().Contain(n => n.Id == "b");
    }

    [Fact]
    public void Loader_RejectsSerialNodeType_WithSequentialEdgesGuidance()
    {
        var yaml = """
            id: serial-workflow
            name: Serial workflow
            start: a
            nodes:
              - id: a
                type: prompt
                prompt: do work
              - id: b
                type: serial
                steps:
                  - a
            edges:
              - from: a
                to: b
            """;

        var result = WorkflowDefinitionLoader.Load(yaml, "serial-workflow.yaml", isBuiltIn: false);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("unsupported node type 'serial'");
        result.Error.Should().Contain("ordinary workflow edges");
    }

    [Theory]
    [InlineData("rai", "safety-failed", "rai")]
    [InlineData("review", "declined", "human-review")]
    [InlineData("rubberduck", "pass", "rubberduck")]
    public void LegacyPersistedGateIds_LoadBindAndReserializeWithExplicitGateKind(
        string nodeId,
        string verdict,
        string expectedGateKind)
    {
        var optionalApprovalNode = nodeId == "rubberduck"
            ? """
              - id: approval
                type: check
                gate_kind: human-review
                branches:
                  - declined
            """
            : "";
        var gateTarget = nodeId == "rubberduck" ? "approval" : "done";
        var optionalApprovalEdge = nodeId == "rubberduck"
            ? """
              - from: approval
                to: done
                when: declined
            """
            : "";
        var yaml = $"""
            id: legacy-gate
            name: Legacy gate
            start: author
            nodes:
              - id: author
                type: prompt
              - id: {nodeId}
                type: check
                branches:
                  - {verdict}
            {optionalApprovalNode}
              - id: done
                type: terminal
            edges:
              - from: author
                to: {nodeId}
              - from: {nodeId}
                to: {gateTarget}
                when: {verdict}
            {optionalApprovalEdge}
            """;

        var legacyLoad = WorkflowDefinitionLoader.Load(yaml, "persisted.yaml");

        legacyLoad.IsValid.Should().BeTrue(legacyLoad.Error);
        legacyLoad.Warnings.Should().ContainSingle(message =>
            message.Contains($"inferred gate_kind '{expectedGateKind}'", StringComparison.Ordinal));
        RunWorkflowGraphBinder.GetBindabilityErrors(legacyLoad.Definition!).Should().BeEmpty();

        var migratedYaml = WorkflowDefinitionYamlSerializer.Serialize(legacyLoad.Definition!);
        migratedYaml.Should().Contain($"gate_kind: {expectedGateKind}");
        WorkflowDefinitionLoader.Load(
                migratedYaml,
                "migrated.yaml",
                validationMode: WorkflowDefinitionValidationMode.Authoring)
            .IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("rai")]
    [InlineData("review")]
    [InlineData("rubberduck")]
    public void AuthoringContract_RejectsLegacyGateIdFallback(string nodeId)
    {
        var result = WorkflowDefinitionLoader.Load(
            $"""
            id: new-gate
            name: New gate
            start: author
            nodes:
              - id: author
                type: prompt
              - id: {nodeId}
                type: check
                branches:
                  - pass
              - id: done
                type: terminal
            edges:
              - from: author
                to: {nodeId}
              - from: {nodeId}
                to: done
                when: pass
            """,
            "new.yaml",
            validationMode: WorkflowDefinitionValidationMode.Authoring);

        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("must declare explicit 'gate_kind' for authoring");
    }

    [Fact]
    public void EveryPublishedTransition_WiresThroughFullBinder()
    {
        var failures = new List<string>();

        foreach (var transition in WorkflowGrammarContract.Transitions)
        {
            foreach (var condition in transition.Conditions)
            {
                var definition = DefinitionForPublishedTransition(
                    transition.From,
                    transition.To,
                    condition);
                var bindings = FakeBindings.Create();
                var builder = new GraphDescriptorBuilder(bindings.AgentInputStorer);

                try
                {
                    RunWorkflowGraphBinder.WireFull(builder, definition, bindings);
                    builder.BuildDescriptor(definition.Id, "full");
                }
                catch (Exception ex)
                {
                    failures.Add(
                        $"{transition.From}->{transition.To} when '{condition ?? "(unconditional)"}': {ex.Message}");
                }
            }
        }

        failures.Should().BeEmpty(
            "the published grammar must contain only transitions with concrete full-binder wiring");
    }

    [Theory]
    [InlineData(WorkflowNodeType.PeerReview)]
    [InlineData(WorkflowNodeType.BuildTest)]
    public void VerdictStyleNode_AsStart_IsRejectedBeforeRuntime(WorkflowNodeType verdictNodeType)
    {
        var definition = new WorkflowDefinition
        {
            Id = "invalid-verdict-start",
            Name = "Invalid verdict start",
            Start = "review",
            Nodes =
            [
                Node("review", verdictNodeType),
                Node("continue", WorkflowNodeType.Prompt),
                Node("scribe", WorkflowNodeType.Scribe),
                Node("done", WorkflowNodeType.Terminal),
            ],
            Edges =
            [
                new WorkflowEdge { From = "review", To = "continue", When = "approved" },
                new WorkflowEdge { From = "continue", To = "scribe" },
                new WorkflowEdge { From = "scribe", To = "done" },
            ],
        };

        var errors = RunWorkflowGraphBinder.GetBindabilityErrors(definition);

        errors.Should().ContainSingle()
            .Which.Should().ContainAll(
                "Cannot bind start node 'review'",
                "require an AgentTurnOutput from a preceding producer",
                "Choose a prompt node as start");

        var bind = () => RunWorkflowGraphBinder.ValidateBindable(definition);
        bind.Should().Throw<WorkflowBindException>()
            .WithMessage("*Cannot bind start node 'review'*");

        var bindings = FakeBindings.Create();
        var wire = () => RunWorkflowGraphBinder.WireFull(
            new GraphDescriptorBuilder(bindings.AgentInputStorer),
            definition,
            bindings);
        wire.Should().Throw<WorkflowBindException>()
            .Which.NodeId.Should().Be("review");
    }

    [Fact]
    public void TransitionContract_RejectsEdgesWithoutRuntimeWiring_AndReturnsAlternatives()
    {
        var definition = new WorkflowDefinition
        {
            Id = "unsupported-review-edge",
            Name = "Unsupported review edge",
            Start = "implement",
            Nodes =
            [
                Node("implement", WorkflowNodeType.Prompt),
                Node("rai", WorkflowNodeType.Check, "rai"),
                Node("build-test", WorkflowNodeType.BuildTest),
                Node("done", WorkflowNodeType.Terminal),
            ],
            Edges =
            [
                new WorkflowEdge { From = "implement", To = "rai" },
                new WorkflowEdge { From = "rai", To = "build-test", When = "no-changes" },
                new WorkflowEdge { From = "build-test", To = "done", When = "approved" },
            ],
        };

        RunWorkflowGraphBinder.GetBindabilityErrors(definition).Should().Contain(error =>
            error.Contains("'Rai'->'PeerReview'", StringComparison.Ordinal));

        var issue = RunWorkflowGraphBinder.GetTransitionIssues(definition).Should().ContainSingle().Subject;
        issue.From.Should().Be("rai");
        issue.To.Should().Be("build-test");
        issue.When.Should().Be("no-changes");
        issue.Alternatives.Should().Contain("rai -> peer-review/build-test (when: approved | pass | review)");
    }

    [Fact]
    public void AdvancedReleaseReviewChain_BuildsThroughTheFullRuntimeBinder()
    {
        using var factory = new WorkflowWebApplicationFactory();
        var workflowFactory = factory.Services.GetRequiredService<RunWorkflowFactory>();
        var definition = new WorkflowDefinition
        {
            Id = "release-readiness",
            Name = "Release readiness",
            Start = "implement",
            Nodes =
            [
                Node("implement", WorkflowNodeType.Prompt),
                Node("rai", WorkflowNodeType.Check, "rai"),
                Node("build-test", WorkflowNodeType.BuildTest),
                Node("peer-review", WorkflowNodeType.PeerReview),
                Node("human-review", WorkflowNodeType.Check, "human-review"),
                Node("declined", WorkflowNodeType.Terminal),
                Node("done", WorkflowNodeType.Terminal),
            ],
            Edges =
            [
                new WorkflowEdge { From = "implement", To = "rai" },
                new WorkflowEdge { From = "rai", To = "build-test", When = "pass" },
                new WorkflowEdge { From = "rai", To = "implement", When = "revise" },
                new WorkflowEdge { From = "build-test", To = "peer-review", When = "approved" },
                new WorkflowEdge { From = "build-test", To = "implement", When = "request-changes" },
                new WorkflowEdge { From = "build-test", To = "declined", When = "declined" },
                new WorkflowEdge { From = "peer-review", To = "human-review", When = "approved" },
                new WorkflowEdge { From = "peer-review", To = "implement", When = "request-changes" },
                new WorkflowEdge { From = "peer-review", To = "declined", When = "declined" },
                new WorkflowEdge { From = "human-review", To = "done", When = "approved" },
                new WorkflowEdge { From = "human-review", To = "implement", When = "request-changes" },
                new WorkflowEdge { From = "human-review", To = "declined", When = "declined" },
            ],
        };

        RunWorkflowGraphBinder.GetBindabilityErrors(definition).Should().BeEmpty();
        var (_, descriptor) = workflowFactory.BuildWorkflowForTest(isChild: false, definition);

        var edges = descriptor.Edges.Select(edge => (edge.From, edge.To)).ToList();
        edges.Should().Contain(("rai", "build-test"));
        edges.Should().Contain(("build-test", "peer-review"));
        edges.Should().Contain(("peer-review", "human-review"));
    }

    [Fact]
    public void DirectAgentCompletion_ClaimedByContract_BuildsThroughTheFullRuntimeBinder()
    {
        using var factory = new WorkflowWebApplicationFactory();
        var workflowFactory = factory.Services.GetRequiredService<RunWorkflowFactory>();
        var definition = new WorkflowDefinition
        {
            Id = "direct-completion",
            Name = "Direct completion",
            Start = "work",
            Nodes =
            [
                Node("work", WorkflowNodeType.Prompt),
                Node("done", WorkflowNodeType.Terminal),
            ],
            Edges = [new WorkflowEdge { From = "work", To = "done" }],
        };

        RunWorkflowGraphBinder.GetBindabilityErrors(definition).Should().BeEmpty();
        var act = () => workflowFactory.BuildWorkflowForTest(isChild: false, definition);

        act.Should().NotThrow();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static void AssertCanonicalDefaultGraph(GraphDescriptor descriptor)
    {
        descriptor.StartNodeId.Should().Be("agent");

        descriptor.Nodes.Select(n => n.Id).Should().BeEquivalentTo(
            new[] { "agent", "rai", "review", "merge", "push-pr", "scribe" });

        var edges = descriptor.Edges.Select(e => (e.From, e.To, e.Loopback)).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("agent",  "rai",    false),
            ("rai",    "agent",  true),   // RAI revise loop
            ("rai",    "scribe", false),  // no-changes path
            ("rai",    "review", false),
            ("review", "merge",  false),
            ("review", "agent",  true),   // request-changes loop
            ("merge",  "push-pr", false), // merged path
            ("push-pr","scribe", false),  // record published/reused PR outcome
            ("merge",  "review", true),   // blocked re-review loop
        });
    }

    private static WorkflowNode Node(string id, WorkflowNodeType type, string? gateKind = null) => new()
    {
        Id = id,
        Type = type,
        Label = id,
        GateKind = gateKind,
    };

    private static WorkflowDefinition StaticFanDefinition() => new()
    {
        Id = "static-fan",
        Name = "Static fan",
        Start = "entry",
        Nodes =
        [
            Node("entry", WorkflowNodeType.Prompt),
            Node("fan", WorkflowNodeType.FanOut),
            Node("branch-a", WorkflowNodeType.Prompt),
            Node("branch-b", WorkflowNodeType.BuildTest),
            Node("join", WorkflowNodeType.FanIn) with { Target = "fan" },
            Node("done", WorkflowNodeType.Terminal),
        ],
        Edges =
        [
            new WorkflowEdge { From = "entry", To = "fan" },
            new WorkflowEdge { From = "fan", To = "branch-a" },
            new WorkflowEdge { From = "fan", To = "branch-b" },
            new WorkflowEdge { From = "branch-a", To = "join" },
            new WorkflowEdge { From = "branch-b", To = "join" },
            new WorkflowEdge { From = "join", To = "done" },
        ],
    };

    private static WorkflowDefinition RenamedDefaultDefinition() => new()
    {
        Id = "renamed",
        Name = "Renamed",
        Start = "plan",
        Nodes =
        [
            Node("plan", WorkflowNodeType.Prompt),
            Node("safety", WorkflowNodeType.Check, gateKind: "rai"),
            Node("approve", WorkflowNodeType.Check, gateKind: "human-review"),
            Node("apply", WorkflowNodeType.Merge),
            Node("publish", WorkflowNodeType.OpenPullRequest),
            Node("record", WorkflowNodeType.Scribe),
            Node("safety-stop", WorkflowNodeType.Terminal),
            Node("rejected", WorkflowNodeType.Terminal),
            Node("finished", WorkflowNodeType.Terminal),
        ],
        Edges =
        [
            new WorkflowEdge { From = "plan", To = "safety" },
            new WorkflowEdge { From = "safety", To = "plan", When = "revise" },
            new WorkflowEdge { From = "safety", To = "safety-stop", When = "safety-failed" },
            new WorkflowEdge { From = "safety", To = "record", When = "no-changes" },
            new WorkflowEdge { From = "safety", To = "approve", When = "review" },
            new WorkflowEdge { From = "approve", To = "apply", When = "approved" },
            new WorkflowEdge { From = "approve", To = "plan", When = "request-changes" },
            new WorkflowEdge { From = "approve", To = "rejected", When = "declined" },
            new WorkflowEdge { From = "apply", To = "publish", When = "merged" },
            new WorkflowEdge { From = "apply", To = "approve", When = "blocked" },
            new WorkflowEdge { From = "publish", To = "record" },
            new WorkflowEdge { From = "record", To = "finished" },
        ],
    };

    private static WorkflowDefinition DefinitionForPublishedTransition(
        NodeKind fromKind,
        NodeKind toKind,
        string? condition)
    {
        var nodes = new List<WorkflowNode>();
        var edges = new List<WorkflowEdge>();
        var entry = Node("entry-agent", WorkflowNodeType.Prompt);
        nodes.Add(entry);

        var from = NodeForKind("from", fromKind);
        var to = NodeForKind("to", toKind);
        if (from.Id != entry.Id)
        {
            AddEntryPath(nodes, edges, entry, from, fromKind);
            nodes.Add(from);
        }

        if (nodes.All(node => node.Id != to.Id))
            nodes.Add(to);
        edges.Add(new WorkflowEdge { From = from.Id, To = to.Id, When = condition });

        return new WorkflowDefinition
        {
            Id = "published-transition",
            Name = "Published transition",
            Start = entry.Id,
            Nodes = nodes,
            Edges = edges,
        };
    }

    private static void AddEntryPath(
        List<WorkflowNode> nodes,
        List<WorkflowEdge> edges,
        WorkflowNode entry,
        WorkflowNode source,
        NodeKind sourceKind)
    {
        switch (sourceKind)
        {
            case NodeKind.Rai:
            case NodeKind.HumanReview:
            case NodeKind.Rubberduck:
            case NodeKind.PeerReview:
            case NodeKind.OpenPullRequest:
            case NodeKind.Scribe:
                edges.Add(new WorkflowEdge { From = entry.Id, To = source.Id });
                return;
            case NodeKind.Merge:
                var review = NodeForKind("entry", NodeKind.PeerReview);
                nodes.Add(review);
                edges.Add(new WorkflowEdge { From = entry.Id, To = review.Id });
                edges.Add(new WorkflowEdge { From = review.Id, To = source.Id, When = "approved" });
                return;
            default:
                throw new InvalidOperationException($"No entry path for published source kind '{sourceKind}'.");
        }
    }

    private static WorkflowNode NodeForKind(string prefix, NodeKind kind) => kind switch
    {
        NodeKind.Agent => Node(
            prefix == "from" ? "entry-agent" : $"{prefix}-agent",
            WorkflowNodeType.Prompt),
        NodeKind.Rai => Node($"{prefix}-rai", WorkflowNodeType.Check, "rai"),
        NodeKind.HumanReview => Node($"{prefix}-human-review", WorkflowNodeType.Check, "human-review"),
        NodeKind.Rubberduck => Node($"{prefix}-rubberduck", WorkflowNodeType.Check, "rubberduck"),
        NodeKind.PeerReview => Node($"{prefix}-peer-review", WorkflowNodeType.PeerReview),
        NodeKind.Merge => Node($"{prefix}-merge", WorkflowNodeType.Merge),
        NodeKind.Scribe => Node($"{prefix}-scribe", WorkflowNodeType.Scribe),
        NodeKind.Terminal => Node($"{prefix}-terminal", WorkflowNodeType.Terminal),
        NodeKind.OpenPullRequest => Node($"{prefix}-open-pull-request", WorkflowNodeType.OpenPullRequest),
        _ => throw new InvalidOperationException($"Published transition uses unsupported kind '{kind}'."),
    };
}

/// <summary>
/// Builds a <see cref="RunWorkflowBindings"/> from typed <see cref="VisualFunctionExecutor{TInput,TOutput}"/>
/// stand-ins that carry the SAME render metadata (logical ids, hidden flags) as the real executors, so the
/// collapsed descriptor matches production. No real agent/sandbox/IO is exercised — the binder under test
/// only wires edges; the descriptor is computed from the wiring, not from running the graph.
/// </summary>
internal static class FakeBindings
{
    public static RunWorkflowBindings Create()
    {
        // Visible business stages (hidden: false) — these survive the descriptor collapse.
        var agent = Exec("agent-turn", "agent", "agent", "agent", hidden: false);
        var rai = Exec("rai-turn", "rai", "review", "gate", hidden: false);
        var review = Exec("review-gate", "review", "review", "gate", hidden: false);
        var merge = Exec("merge", "merge", "merge", "action", hidden: false);
        var openPr = Exec("open-pr", "push-pr", "action", "action", hidden: false);
        var scribeMerge = Exec("scribe-turn-merge", "scribe", "scribe", "agent", hidden: false);
        var scribeNoChanges = Exec("scribe-turn-no-changes", "scribe", "scribe", "agent", hidden: false);
        var scribeInputMerge = Exec("scribe-input-merge", "scribe", "scribe", "agent", hidden: false);
        var scribeOutputMerge = Exec("scribe-output-merge", "scribe", "scribe", "agent", hidden: false);
        var scribeInputNoChanges = Exec("scribe-input-no-changes", "scribe", "scribe", "agent", hidden: false);
        var scribeOutputNoChanges = Exec("scribe-output-no-changes", "scribe", "scribe", "agent", hidden: false);

        // Hidden plumbing/adapters/terminals (hidden: true) — dropped from the descriptor, edges re-stitched.
        var agentInputStorer = Exec("agent-input-storer", "agent-input-storer", "plumbing", "action", hidden: true);
        var raiRevisionAdapter = Exec("rai-revision-adapter", "rai-revision-adapter", "plumbing", "action", hidden: true);
        var terminalSafetyFailed = Exec("terminal-safety-failed", "terminal-safety-failed", "plumbing", "terminal", hidden: true);
        var terminalNoOp = Exec("terminal-no-op", "terminal-no-op", "plumbing", "terminal", hidden: true);
        var reviewAdapter = Exec("review-adapter", "review-adapter", "plumbing", "action", hidden: true);
        var policyAgentTurnStorer = Exec("policy-agent-turn-storer", "policy-agent-turn-storer", "plumbing", "action", hidden: true);
        var policyAgentOutputAdapter = Exec("policy-agent-output-adapter", "policy-agent-output-adapter", "plumbing", "action", hidden: true);
        var policyDirectMergeAdapter = Exec("policy-direct-merge-adapter", "policy-direct-merge-adapter", "plumbing", "action", hidden: true);
        var mergeAdapter = Exec("merge-adapter", "merge-adapter", "plumbing", "action", hidden: true);
        var mergeToOutputAdapter = Exec("merge-to-output-adapter", "merge-to-output-adapter", "plumbing", "action", hidden: true);
        var terminalMerge = Exec("terminal-merge", "terminal-merge", "plumbing", "terminal", hidden: true);
        var blockedAdapter = Exec("blocked-adapter", "blocked-adapter", "plumbing", "action", hidden: true);
        var reviewChangesAdapter = Exec("review-changes-adapter", "review-changes-adapter", "plumbing", "action", hidden: true);
        var terminalDeclined = Exec("terminal-declined", "terminal-declined", "plumbing", "terminal", hidden: true);

        return new RunWorkflowBindings(
            AgentInputStorer: agentInputStorer,
            AgentBinding: agent,
            TerminalTurnFailed: Exec("terminal-turn-failed", "terminal-turn-failed", "plumbing", "terminal", hidden: true),
            RaiBinding: rai,
            RaiRevisionAdapter: raiRevisionAdapter,
            TerminalSafetyFailed: terminalSafetyFailed,
            TerminalNoOp: terminalNoOp,
            ScribeInputNoChanges: scribeInputNoChanges,
            ScribeBindingNoChanges: scribeNoChanges,
            ScribeOutputNoChanges: scribeOutputNoChanges,
            ReviewAdapter: reviewAdapter,
            ReviewBinding: review,
            PolicyAgentTurnStorer: policyAgentTurnStorer,
            PolicyAgentOutputAdapter: policyAgentOutputAdapter,
            PolicyDirectMergeAdapter: policyDirectMergeAdapter,
            PolicyGateBindings: new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal)
            {
                ["from-rubberduck"] = review,
                ["to-rubberduck"] = review,
            },
            MergeAdapter: mergeAdapter,
            MergeBinding: merge,
            TerminalMerge: terminalMerge,
            ScribeInputMerge: scribeInputMerge,
            ScribeBindingMerge: scribeMerge,
            ScribeOutputMerge: scribeOutputMerge,
            BlockedAdapter: blockedAdapter,
            ReviewChangesAdapter: reviewChangesAdapter,
            TerminalDeclined: terminalDeclined,
            MaxIterations: 3,
            Wiring: new FakeWiring(agent, openPr, mergeToOutputAdapter, new ScribeSubPath(scribeInputMerge, scribeMerge, scribeOutputMerge)));
    }

    private static ExecutorBinding Exec(string id, string logicalId, string role, string nodeType, bool hidden) =>
        new VisualFunctionExecutor<object, object>(
            id, logicalId, logicalId, role, nodeType, hidden,
            (input, ctx, ct) => new ValueTask<object>(input));
}

/// <summary>
/// Fake wiring support for the binder unit tests. The default / renamed-default definitions only ever
/// take the canonical Agent path, so <see cref="ResolveAgentNode"/> returns the single fake agent binding
/// (logical id "agent") for ANY node id — preserving the id-independent collapse to the canonical graph.
/// The generic catalog adapters are exercised by the factory-level binding test, not here, so they throw.
/// </summary>
internal sealed class FakeWiring(
    ExecutorBinding agent,
    ExecutorBinding openPr,
    ExecutorBinding mergeToOutputAdapter,
    ScribeSubPath openPrScribePath) : IRunWorkflowWiringSupport
{
    public ExecutorBinding ResolveAgentNode(WorkflowNode node) => agent;
    public ExecutorBinding ResolvePeerReviewNode(WorkflowNode node) => agent;
    public ExecutorBinding ResolveOpenPullRequestNode(WorkflowNode node) => openPr;
    public ExecutorBinding SequentialAgentAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding ReviewToAgentForwardAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding ReviewToAgentReviseAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding StoreAgentOutputAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding ReviewToAgentOutputAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding ReviewToMergeAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding AgentToReviewRequestAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding ReviewToReviewRequestAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding ReviewToTerminalAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding AgentToTerminalAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding AgentToMergeAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding MergeToAgentOutputAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ExecutorBinding MergeToAgentReviseAdapter(WorkflowEdge edge) => mergeToOutputAdapter;
    public ScribeSubPath AgentScribePath(WorkflowEdge edge) => openPrScribePath;
    public ScribeSubPath OpenPullRequestScribePath(WorkflowEdge edge) => openPrScribePath;
    public ScribeSubPath ReviewScribePath(WorkflowEdge edge) => openPrScribePath;
}
