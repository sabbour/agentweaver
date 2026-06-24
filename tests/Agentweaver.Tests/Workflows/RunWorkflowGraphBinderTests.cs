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
/// while producing a byte-for-byte identical graph for the default workflow (the P0 parity guarantee).
/// </summary>
public sealed class RunWorkflowGraphBinderTests
{
    // ── Parity (real path): the default workflow built through the new binder with the REAL executors
    //    collapses to the same five-stage graph the hand-wired pipeline produced. ────────────────────
    [Fact]
    public void DefaultWorkflow_RealPath_ProducesCanonicalFiveStageGraph()
    {
        using var factory = new WorkflowWebApplicationFactory();
        var workflowFactory = factory.Services.GetRequiredService<RunWorkflowFactory>();

        var descriptor = workflowFactory.GetGraphDescriptor(isChild: false);

        AssertCanonicalDefaultGraph(descriptor);
    }

    // ── Parity (unit path): the default WorkflowDefinition wired through the binder onto fake-but-typed
    //    bindings produces the canonical collapsed graph. This pins the raw edge/predicate/output set. ─
    [Fact]
    public void DefaultDefinition_Binder_ProducesCanonicalFiveStageGraph()
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
        // so a renamed definition collapses to the exact same canonical five-stage graph.
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
            Trigger = new WorkflowTrigger { Type = WorkflowTriggerType.Manual },
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

    // ── Loader: fan_out / fan_in / serial / peer_review are no longer rejected at load time. ──────────
    [Theory]
    [InlineData("fan_out")]
    [InlineData("fan_in")]
    [InlineData("serial")]
    [InlineData("peer_review")]
    public void Loader_Accepts_PreviouslyRejectedNodeTypes(string nodeType)
    {
        var yaml = $"""
            id: custom
            name: Custom
            trigger:
              type: manual
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

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────

    private static void AssertCanonicalDefaultGraph(GraphDescriptor descriptor)
    {
        descriptor.StartNodeId.Should().Be("agent");

        descriptor.Nodes.Select(n => n.Id).Should().BeEquivalentTo(
            new[] { "agent", "rai", "review", "merge", "scribe" });

        var edges = descriptor.Edges.Select(e => (e.From, e.To, e.Loopback)).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("agent",  "rai",    false),
            ("rai",    "agent",  true),   // RAI revise loop
            ("rai",    "scribe", false),  // no-changes path
            ("rai",    "review", false),
            ("review", "merge",  false),
            ("review", "agent",  true),   // request-changes loop
            ("merge",  "scribe", false),  // merged path
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

    private static WorkflowDefinition RenamedDefaultDefinition() => new()
    {
        Id = "renamed",
        Name = "Renamed",
        Trigger = new WorkflowTrigger { Type = WorkflowTriggerType.Manual },
        Start = "plan",
        Nodes =
        [
            Node("plan", WorkflowNodeType.Prompt),
            Node("safety", WorkflowNodeType.Check, gateKind: "rai"),
            Node("approve", WorkflowNodeType.Check, gateKind: "human-review"),
            Node("apply", WorkflowNodeType.Merge),
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
            new WorkflowEdge { From = "apply", To = "record", When = "merged" },
            new WorkflowEdge { From = "apply", To = "approve", When = "blocked" },
            new WorkflowEdge { From = "record", To = "finished" },
        ],
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
        var terminalMerge = Exec("terminal-merge", "terminal-merge", "plumbing", "terminal", hidden: true);
        var blockedAdapter = Exec("blocked-adapter", "blocked-adapter", "plumbing", "action", hidden: true);
        var reviewChangesAdapter = Exec("review-changes-adapter", "review-changes-adapter", "plumbing", "action", hidden: true);
        var terminalDeclined = Exec("terminal-declined", "terminal-declined", "plumbing", "terminal", hidden: true);

        return new RunWorkflowBindings(
            AgentInputStorer: agentInputStorer,
            AgentBinding: agent,
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
            PolicyGateBindings: new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal),
            MergeAdapter: mergeAdapter,
            MergeBinding: merge,
            TerminalMerge: terminalMerge,
            ScribeInputMerge: scribeInputMerge,
            ScribeBindingMerge: scribeMerge,
            ScribeOutputMerge: scribeOutputMerge,
            BlockedAdapter: blockedAdapter,
            ReviewChangesAdapter: reviewChangesAdapter,
            TerminalDeclined: terminalDeclined,
            MaxIterations: 3);
    }

    private static ExecutorBinding Exec(string id, string logicalId, string role, string nodeType, bool hidden) =>
        new VisualFunctionExecutor<object, object>(
            id, logicalId, logicalId, role, nodeType, hidden,
            (input, ctx, ct) => new ValueTask<object>(input));
}
