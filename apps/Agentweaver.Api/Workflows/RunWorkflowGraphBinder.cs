using Microsoft.Agents.AI.Workflows;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Runs.Graph;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// The real executor/binding instances that the live run pipeline is composed from. The binder maps a
/// <see cref="WorkflowDefinition"/>'s logical nodes/edges ONTO these existing executors — it does NOT
/// replace them (Principle VII: no mocks/fakes, bind to the real executors). One instance is constructed
/// per <c>RunWorkflowFactory.BuildWorkflow</c> call with that build's freshly created executors.
/// </summary>
/// <remarks>
/// The descriptor's five visible logical nodes (agent, rai, review, merge, scribe) collapse out the
/// hidden plumbing/adapter executors here; this record is the full (non-collapsed) raw set the
/// definition's edges expand into.
/// </remarks>
internal sealed record RunWorkflowBindings(
    ExecutorBinding AgentInputStorer,
    ExecutorBinding AgentBinding,
    ExecutorBinding RaiBinding,
    ExecutorBinding RaiRevisionAdapter,
    ExecutorBinding TerminalSafetyFailed,
    ExecutorBinding TerminalNoOp,
    ExecutorBinding ScribeInputNoChanges,
    ExecutorBinding ScribeBindingNoChanges,
    ExecutorBinding ScribeOutputNoChanges,
    ExecutorBinding ReviewAdapter,
    ExecutorBinding ReviewBinding,
    ExecutorBinding PolicyAgentTurnStorer,
    ExecutorBinding PolicyAgentOutputAdapter,
    ExecutorBinding PolicyDirectMergeAdapter,
    IReadOnlyDictionary<string, ExecutorBinding> PolicyGateBindings,
    ExecutorBinding MergeAdapter,
    ExecutorBinding MergeBinding,
    ExecutorBinding TerminalMerge,
    ExecutorBinding ScribeInputMerge,
    ExecutorBinding ScribeBindingMerge,
    ExecutorBinding ScribeOutputMerge,
    ExecutorBinding BlockedAdapter,
    ExecutorBinding ReviewChangesAdapter,
    ExecutorBinding TerminalDeclined,
    int MaxIterations);

/// <summary>
/// Binds a <see cref="WorkflowDefinition"/> onto the live MAF graph (Feature 010 wf-maf-binding,
/// generalized in Feature 015 US1). The full run pipeline is assembled by ITERATING the definition's
/// nodes/edges and resolving each node's executor from its <c>type</c> (via
/// <see cref="INodeExecutorFactory"/>) and each <c>(from, to, when)</c> transition from the node TYPES
/// (via <see cref="NodeClassifier"/>) — NOT from hardcoded node ids or literal edge keys. Any authored
/// workflow whose node ids differ from the original five (agent/rai/review/merge/scribe) wires
/// identically when the node TYPES match.
///
/// PARITY-FIRST: the raw <see cref="GraphDescriptorBuilder"/> edges, predicates, idempotent flags, and
/// outputs emitted here are byte-for-byte identical to the previous hand-coded wiring for the default
/// workflow, so the collapsed <see cref="GraphDescriptor"/> and the executed MAF graph are unchanged.
/// Each logical definition edge expands to a subgraph of raw edges (hidden plumbing/adapter executors
/// that the descriptor collapses).
///
/// FAIL-CLOSED: an unresolvable node or an unmapped transition throws a node-scoped
/// <see cref="WorkflowBindException"/> — the drift/governance guard. The binder never silently skips,
/// mis-wires, or partially executes a graph, so governance guarantees (sandbox, RAI, human-approval)
/// cannot be weakened by an authored node's fields.
/// </summary>
internal static class RunWorkflowGraphBinder
{
    private static readonly INodeExecutorFactory Factory = new NodeExecutorRegistry();

    /// <summary>
    /// Wires the full (non-child) run pipeline from <paramref name="definition"/> onto
    /// <paramref name="builder"/> using the real executors in <paramref name="bindings"/>.
    /// </summary>
    public static void WireFull(
        GraphDescriptorBuilder builder, WorkflowDefinition definition, RunWorkflowBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(bindings);

        // Entry plumbing: the hidden input storer feeds the start node's executor (unconditional). The
        // start node is resolved by its declared id and its TYPE — not a hardcoded "agent".
        var startNode = GetNode(definition, definition.Start);
        builder.AddEdge(bindings.AgentInputStorer, Factory.ResolveExecutor(startNode, bindings));

        // Each logical edge expands to its raw executor wiring + predicate.
        foreach (var edge in definition.Edges)
            WireEdge(builder, definition, edge, bindings);

        // Terminal nodes declare which executors are graph outputs (WithOutputFrom).
        foreach (var node in definition.Nodes)
        {
            if (node.Type == WorkflowNodeType.Terminal)
                WireOutputs(builder, definition, node, bindings);
        }
    }

    private static WorkflowNode GetNode(WorkflowDefinition definition, string nodeId)
    {
        var node = definition.Nodes.FirstOrDefault(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal));
        if (node is null)
            throw new WorkflowBindException(
                $"Cannot bind: workflow references unknown node '{nodeId}'.", nodeId);
        return node;
    }

    /// <summary>
    /// Resolves a single logical edge <c>(from, to, when)</c> onto raw executor wiring. The expansion is
    /// chosen from the node TYPES (classified by <see cref="NodeClassifier"/>), so a renamed node wires
    /// identically. Extra/renamed review-policy gate nodes (those that received a dedicated policy binding)
    /// keep the existing policy plumbing for parity with multi-gate review-policy workflows. The predicates
    /// are the exact lambdas previously inline in <c>BuildWorkflow</c>; do not alter them (parity).
    /// </summary>
    private static void WireEdge(GraphDescriptorBuilder g, WorkflowDefinition definition, WorkflowEdge edge, RunWorkflowBindings b)
    {
        var fromNode = GetNode(definition, edge.From);
        var toNode = GetNode(definition, edge.To);
        var fromKind = NodeClassifier.Classify(fromNode);
        var toKind = NodeClassifier.Classify(toNode);

        // Node types accepted at load time but not yet wired to a runtime executor fail closed here.
        RejectUnwiredKind(fromNode, fromKind);
        RejectUnwiredKind(toNode, toKind);

        // Extra/renamed review-policy gates keep their dedicated policy plumbing (parity with Feature 010
        // multi-gate review policies). The canonical default gates (rai/review) never receive a policy
        // binding, so the default workflow always takes the canonical path below.
        if (b.PolicyGateBindings.ContainsKey(edge.From) || b.PolicyGateBindings.ContainsKey(edge.To))
        {
            if (TryWirePolicyEdge(g, definition, edge, b))
                return;
        }

        if (TryWireCanonicalEdge(g, edge, fromNode, toNode, fromKind, toKind, b))
            return;

        throw new WorkflowBindException(
            $"Cannot bind edge '{edge.From}'->'{edge.To}' (when='{edge.When}'): no executor wiring for a " +
            $"'{fromKind}'->'{toKind}' transition. The workflow definition diverged from the executor wiring.",
            edge.From);
    }

    /// <summary>
    /// Maps a canonical <c>(fromKind, toKind, when)</c> transition onto the same raw executor subgraph the
    /// hand-coded pipeline produced. Returns false when the transition is not a canonical one (the caller
    /// then fails closed). Resolved bindings are id-independent: the agent/rai/review/merge/scribe executors
    /// come from <paramref name="b"/>, picked by TYPE.
    /// </summary>
    private static bool TryWireCanonicalEdge(
        GraphDescriptorBuilder g, WorkflowEdge edge, WorkflowNode fromNode, WorkflowNode toNode,
        NodeKind fromKind, NodeKind toKind, RunWorkflowBindings b)
    {
        var when = edge.When;

        switch (fromKind, toKind, when)
        {
            // agent turn -> RAI gate (unconditional).
            case (NodeKind.Agent, NodeKind.Rai, null):
                g.AddEdge(ResolveAgent(b), ResolveRai(toNode, b));
                return true;

            // RAI REVISE (iteration < cap) -> revision adapter -> loop back to agent.
            case (NodeKind.Rai, NodeKind.Agent, "revise"):
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), b.RaiRevisionAdapter,
                    output => output is not null && output.RaiRevisionRequired && output.Iteration < b.MaxIterations)
                 .AddEdge(b.RaiRevisionAdapter, ResolveAgent(b), idempotent: true);
                return true;

            // Content safety: agent turn flagged on an empty diff -> fail immediately, never reaching review.
            case (NodeKind.Rai, NodeKind.Terminal, "safety-failed"):
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), b.TerminalSafetyFailed,
                    output => output is not null && !output.RaiRevisionRequired
                        && string.IsNullOrEmpty(output.Diff) && output.ContentSafetyFlagged);
                return true;

            // No changes -> no-op -> scribe path.
            case (NodeKind.Rai, NodeKind.Scribe, "no-changes"):
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), b.TerminalNoOp,
                    output => output is not null && !output.RaiRevisionRequired
                        && string.IsNullOrEmpty(output.Diff) && !output.ContentSafetyFlagged)
                 .AddEdge(b.TerminalNoOp, b.ScribeInputNoChanges)
                 .AddEdge(b.ScribeInputNoChanges, b.ScribeBindingNoChanges)
                 .AddEdge(b.ScribeBindingNoChanges, b.ScribeOutputNoChanges);
                return true;

            // Otherwise (OK / RED with a diff / revise-at-cap) -> review adapter -> human review gate.
            case (NodeKind.Rai, NodeKind.HumanReview, "review"):
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), b.ReviewAdapter,
                    output => output is not null && !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff))
                 .AddEdge(b.ReviewAdapter, ResolveReview(toNode, b));
                return true;

            // Approved -> merge adapter -> merge executor.
            case (NodeKind.HumanReview, NodeKind.Merge, "approved"):
                g.AddEdge<WorkflowReviewDecision>(ResolveReview(fromNode, b), b.MergeAdapter,
                    decision => decision is not null && decision.Approved)
                 .AddEdge(b.MergeAdapter, b.MergeBinding);
                return true;

            // Review RequestChanges -> revision adapter -> loop back to agent (no cap).
            case (NodeKind.HumanReview, NodeKind.Agent, "request-changes"):
                g.AddEdge<WorkflowReviewDecision>(ResolveReview(fromNode, b), b.ReviewChangesAdapter,
                    decision => decision is not null && !decision.Approved && decision.RequestChanges)
                 .AddEdge(b.ReviewChangesAdapter, ResolveAgent(b), idempotent: true);
                return true;

            // Hard-declined -> terminal.
            case (NodeKind.HumanReview, NodeKind.Terminal, "declined"):
                g.AddEdge<WorkflowReviewDecision>(ResolveReview(fromNode, b), b.TerminalDeclined,
                    decision => decision is null || (!decision.Approved && !decision.RequestChanges));
                return true;

            // Merge succeeded or failed terminally -> scribe path.
            case (NodeKind.Merge, NodeKind.Scribe, "merged"):
                g.AddEdge<MergeOutput>(b.MergeBinding, b.TerminalMerge,
                    output => output is not null && output.Status != "blocked")
                 .AddEdge(b.TerminalMerge, b.ScribeInputMerge)
                 .AddEdge(b.ScribeInputMerge, b.ScribeBindingMerge)
                 .AddEdge(b.ScribeBindingMerge, b.ScribeOutputMerge);
                return true;

            // Merge blocked -> re-enter the review gate via HITL.
            case (NodeKind.Merge, NodeKind.HumanReview, "blocked"):
                g.AddEdge<MergeOutput>(b.MergeBinding, b.BlockedAdapter,
                    output => output is not null && output.Status == "blocked")
                 .AddEdge(b.BlockedAdapter, ResolveReview(toNode, b), idempotent: true);
                return true;

            // Scribe -> done: the scribe output executors ARE the graph outputs (WithOutputFrom); no raw edge.
            case (NodeKind.Scribe, NodeKind.Terminal, null):
                return true;

            default:
                return false;
        }
    }

    /// <summary>Fails closed for node types accepted by the loader but not yet wired to a runtime executor.</summary>
    private static void RejectUnwiredKind(WorkflowNode node, NodeKind kind)
    {
        switch (kind)
        {
            case NodeKind.FanOut:
            case NodeKind.FanIn:
            case NodeKind.Serial:
            case NodeKind.PeerReview:
            case NodeKind.CoordinatorComposed:
                throw new WorkflowBindException(
                    $"Cannot bind node '{node.Id}' (type='{node.Type}'): node type '{node.Type}' is accepted by " +
                    "the loader but not yet wired to a runtime executor. fan_out/fan_in map onto the coordinator " +
                    "SubtaskFrontier/AssemblyPlanning seams; serial/peer_review onto the sequential/peer-review " +
                    "seams — runtime support is pending.", node.Id);
        }
    }

    private static ExecutorBinding ResolveAgent(RunWorkflowBindings b) => b.AgentBinding;

    private static ExecutorBinding ResolveRai(WorkflowNode node, RunWorkflowBindings b) =>
        b.PolicyGateBindings.TryGetValue(node.Id, out var binding) ? binding : b.RaiBinding;

    private static ExecutorBinding ResolveReview(WorkflowNode node, RunWorkflowBindings b) =>
        b.PolicyGateBindings.TryGetValue(node.Id, out var binding) ? binding : b.ReviewBinding;

    /// <summary>
    /// Wires the graph outputs declared by a terminal definition node. Resolved from the terminal's
    /// INCOMING edges (the verdict that reaches it) rather than its id, so a renamed terminal still binds:
    /// a <c>safety-failed</c> verdict -> the safety terminal, a <c>declined</c> verdict -> the declined
    /// terminal, a scribe-sourced edge -> the scribe outputs. Policy terminals keep their id-prefix routing.
    /// </summary>
    private static void WireOutputs(GraphDescriptorBuilder g, WorkflowDefinition definition, WorkflowNode terminal, RunWorkflowBindings b)
    {
        // Review-policy terminals keep id-prefix routing (parity with Feature 010 multi-gate policies).
        if (terminal.Id.StartsWith("policy-terminal-safety-failed", StringComparison.Ordinal))
        {
            g.WithOutputFrom(b.TerminalSafetyFailed);
            return;
        }
        if (terminal.Id.StartsWith("policy-terminal-declined", StringComparison.Ordinal))
        {
            g.WithOutputFrom(b.TerminalDeclined);
            return;
        }

        var incoming = definition.Edges.Where(e => string.Equals(e.To, terminal.Id, StringComparison.Ordinal)).ToList();

        if (incoming.Any(e => string.Equals(e.When, "safety-failed", StringComparison.Ordinal)))
        {
            g.WithOutputFrom(b.TerminalSafetyFailed);
            return;
        }
        if (incoming.Any(e => string.Equals(e.When, "declined", StringComparison.Ordinal)))
        {
            g.WithOutputFrom(b.TerminalDeclined);
            return;
        }
        // A terminal reached from a scribe stage is the run's "done" sink; both scribe-output executors
        // (merge path + no-changes path) are the graph outputs.
        if (incoming.Any(e => IsScribeSource(definition, e.From)))
        {
            g.WithOutputFrom(b.ScribeOutputMerge, b.ScribeOutputNoChanges);
            return;
        }

        throw new WorkflowBindException(
            $"Cannot bind outputs for terminal node '{terminal.Id}': no incoming verdict (safety-failed / " +
            "declined) and no scribe-sourced edge identifies its output executor.", terminal.Id);
    }

    private static bool IsScribeSource(WorkflowDefinition definition, string nodeId)
    {
        var node = definition.Nodes.FirstOrDefault(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal));
        return node is not null && NodeClassifier.Classify(node) == NodeKind.Scribe;
    }

    // ── Review-policy multi-gate wiring (Feature 010) ──────────────────────────────────────────────
    // Preserved verbatim: extra/renamed review-policy gate nodes (those that received a dedicated
    // per-node policy binding) route through this plumbing for parity with multi-gate review policies.

    private static bool TryWirePolicyEdge(
        GraphDescriptorBuilder g,
        WorkflowDefinition definition,
        WorkflowEdge edge,
        RunWorkflowBindings b)
    {
        var fromKind = GateKindOf(definition, edge.From);
        var toKind = GateKindOf(definition, edge.To);

        if (toKind is not null && b.PolicyGateBindings.ContainsKey(edge.To))
        {
            WireIntoPolicyGate(g, definition, edge, b, toKind);
            return true;
        }

        if (fromKind is not null && b.PolicyGateBindings.ContainsKey(edge.From))
        {
            WireFromPolicyGate(g, edge, b, fromKind);
            return true;
        }

        return false;
    }

    private static void WireIntoPolicyGate(
        GraphDescriptorBuilder g,
        WorkflowDefinition definition,
        WorkflowEdge edge,
        RunWorkflowBindings b,
        string targetKind)
    {
        var source = ResolveSourceBinding(edge.From, b);
        var sourceKind = GateKindOf(definition, edge.From);

        if (SourceOutputsAgentTurn(edge.From, sourceKind))
        {
            if (targetKind == "human-review")
            {
                g.AddEdge<AgentTurnOutput>(source, b.ReviewAdapter, output => AgentTurnPredicate(output, edge.When, sourceKind, b.MaxIterations))
                 .AddEdge(b.ReviewAdapter, b.PolicyGateBindings[edge.To]);
            }
            else
            {
                g.AddEdge<AgentTurnOutput>(source, b.PolicyAgentTurnStorer,
                        output => AgentTurnPredicate(output, edge.When, sourceKind, b.MaxIterations))
                 .AddEdge(b.PolicyAgentTurnStorer, b.PolicyGateBindings[edge.To]);
            }
            return;
        }

        if (SourceOutputsReviewDecision(edge.From, sourceKind))
        {
            if (targetKind == "human-review")
            {
                g.AddEdge<WorkflowReviewDecision>(source, b.PolicyAgentOutputAdapter,
                        decision => ReviewDecisionPredicate(decision, edge.When))
                 .AddEdge(b.PolicyAgentOutputAdapter, b.ReviewAdapter)
                 .AddEdge(b.ReviewAdapter, b.PolicyGateBindings[edge.To]);
            }
            else
            {
                g.AddEdge<WorkflowReviewDecision>(source, b.PolicyAgentOutputAdapter,
                        decision => ReviewDecisionPredicate(decision, edge.When))
                 .AddEdge(b.PolicyAgentOutputAdapter, b.PolicyGateBindings[edge.To]);
            }
            return;
        }

        throw new WorkflowBindException(
            $"Policy gate '{edge.To}' cannot consume edge from '{edge.From}'.", edge.To);
    }

    private static void WireFromPolicyGate(GraphDescriptorBuilder g, WorkflowEdge edge, RunWorkflowBindings b, string sourceKind)
    {
        var source = b.PolicyGateBindings[edge.From];

        if (sourceKind == "rai")
        {
            switch (edge.To)
            {
                case "agent":
                    g.AddEdge<AgentTurnOutput>(source, b.RaiRevisionAdapter,
                            output => AgentTurnPredicate(output, edge.When, sourceKind, b.MaxIterations))
                     .AddEdge(b.RaiRevisionAdapter, b.AgentBinding, idempotent: true);
                    return;
                case "merge":
                    g.AddEdge<AgentTurnOutput>(source, b.PolicyDirectMergeAdapter,
                            output => AgentTurnPredicate(output, edge.When, sourceKind, b.MaxIterations))
                     .AddEdge(b.PolicyDirectMergeAdapter, b.MergeBinding);
                    return;
                case var id when id.StartsWith("policy-terminal-safety-failed", StringComparison.Ordinal):
                    g.AddEdge<AgentTurnOutput>(source, b.TerminalSafetyFailed,
                        output => AgentTurnPredicate(output, edge.When, sourceKind, b.MaxIterations));
                    return;
            }
        }

        if (sourceKind is "rubberduck" or "human-review")
        {
            switch (edge.To)
            {
                case "agent":
                    g.AddEdge<WorkflowReviewDecision>(source, b.ReviewChangesAdapter,
                            decision => ReviewDecisionPredicate(decision, edge.When))
                     .AddEdge(b.ReviewChangesAdapter, b.AgentBinding, idempotent: true);
                    return;
                case "merge":
                    g.AddEdge<WorkflowReviewDecision>(source, b.MergeAdapter,
                            decision => ReviewDecisionPredicate(decision, edge.When))
                     .AddEdge(b.MergeAdapter, b.MergeBinding);
                    return;
                case var id when id.StartsWith("policy-terminal-declined", StringComparison.Ordinal):
                    g.AddEdge<WorkflowReviewDecision>(source, b.TerminalDeclined,
                        decision => ReviewDecisionPredicate(decision, edge.When));
                    return;
            }
        }

        throw new WorkflowBindException(
            $"No MAF binding for policy edge '{edge.From}'->'{edge.To}':{edge.When}'.", edge.From);
    }

    private static ExecutorBinding ResolveSourceBinding(string nodeId, RunWorkflowBindings b)
    {
        if (b.PolicyGateBindings.TryGetValue(nodeId, out var policy))
            return policy;

        return nodeId switch
        {
            "agent" => b.AgentBinding,
            "rai" => b.RaiBinding,
            "review" => b.ReviewBinding,
            _ => throw new WorkflowBindException(
                $"No source binding for workflow node '{nodeId}'.", nodeId),
        };
    }

    private static bool SourceOutputsAgentTurn(string nodeId, string? gateKind) =>
        nodeId == "agent" || gateKind == "rai";

    private static bool SourceOutputsReviewDecision(string nodeId, string? gateKind) =>
        nodeId == "review" || gateKind is "rubberduck" or "human-review";

    private static bool AgentTurnPredicate(AgentTurnOutput? output, string? when, string? sourceKind, int maxIterations)
    {
        if (output is null) return false;
        return when switch
        {
            null or "" => true,
            "pass" => !output.RaiRevisionRequired && !output.ContentSafetyFlagged,
            "review" => !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff),
            "revise" => output.RaiRevisionRequired && output.Iteration < maxIterations,
            "safety-failed" => output.ContentSafetyFlagged,
            "no-changes" => !output.RaiRevisionRequired && string.IsNullOrEmpty(output.Diff) && !output.ContentSafetyFlagged,
            _ => false,
        };
    }

    private static bool ReviewDecisionPredicate(WorkflowReviewDecision? decision, string? when) =>
        when switch
        {
            null or "" => true,
            "approved" or "pass" => decision is not null && decision.Approved,
            "request-changes" or "revise" => decision is not null && !decision.Approved && decision.RequestChanges,
            "declined" => decision is null || (!decision.Approved && !decision.RequestChanges),
            _ => false,
        };

    private static string? GateKindOf(WorkflowDefinition definition, string nodeId)
    {
        var node = definition.Nodes.FirstOrDefault(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal));
        return node is null ? null : NodeClassifier.NormalizeGateKind(node);
    }
}
