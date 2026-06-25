using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Resolves a workflow node's PRIMARY executor binding from its <see cref="WorkflowNode.Type"/> (plus its
/// <c>agent</c>/<c>prompt</c>/<c>model</c> fields and gate kind) — the open extension point that replaces the
/// binder's hardcoded switch on five fixed node ids (Feature 015, US1). "Primary" means the single executor a
/// node is entered at (e.g. the agent turn, the RAI gate, the merge stage); the multi-executor subgraph
/// plumbing each logical edge expands into is owned by <see cref="RunWorkflowGraphBinder"/>.
/// </summary>
internal interface INodeExecutorFactory
{
    /// <summary>
    /// Returns the executor a node of <paramref name="node"/>'s type is entered at, drawn from the
    /// pre-built real executors in <paramref name="bindings"/>. Throws <see cref="WorkflowBindException"/>
    /// (fail-closed) for a node whose type/fields cannot be resolved to any executor.
    /// </summary>
    ExecutorBinding ResolveExecutor(WorkflowNode node, RunWorkflowBindings bindings);
}

/// <summary>
/// The default <see cref="INodeExecutorFactory"/>. Maps each <see cref="NodeKind"/> onto the corresponding
/// real executor in <see cref="RunWorkflowBindings"/>. A renamed/extra gate node (gate kind rai / human-review
/// / rubberduck whose id is not the canonical <c>rai</c>/<c>review</c>) resolves to its per-node policy-gate
/// binding when one was built; otherwise it falls back to the canonical RAI/review executor. This is what lets
/// a non-default node id (e.g. a <c>plan</c> prompt or a <c>safety-gate</c> check) execute by TYPE.
/// </summary>
internal sealed class NodeExecutorRegistry : INodeExecutorFactory
{
    public ExecutorBinding ResolveExecutor(WorkflowNode node, RunWorkflowBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(bindings);

        var kind = NodeClassifier.Classify(node);
        switch (kind)
        {
            case NodeKind.Agent:
            case NodeKind.PeerReview:
                // A producing/peer-review node is entered at its OWN per-node executor (minted and cached
                // by the wiring support), so chained turns each get a distinct MAF node. The binder resolves
                // these directly via the wiring support; this branch keeps the factory total for that kind.
                return kind == NodeKind.PeerReview
                    ? bindings.Wiring.ResolvePeerReviewNode(node)
                    : bindings.Wiring.ResolveAgentNode(node);

            case NodeKind.Rai:
                return bindings.PolicyGateBindings.TryGetValue(node.Id, out var rai) ? rai : bindings.RaiBinding;

            case NodeKind.HumanReview:
                return bindings.PolicyGateBindings.TryGetValue(node.Id, out var rev) ? rev : bindings.ReviewBinding;

            case NodeKind.Rubberduck:
                if (bindings.PolicyGateBindings.TryGetValue(node.Id, out var rd))
                    return rd;
                throw new WorkflowBindException(
                    $"Cannot bind node '{node.Id}' (type='{node.Type}', gate='rubberduck'): no rubber-duck gate " +
                    "executor was built for this node.", node.Id);

            case NodeKind.Merge:
                return bindings.MergeBinding;

            case NodeKind.Scribe:
                // Both scribe instances share the logical node; the merge-path instance is the canonical entry.
                return bindings.ScribeBindingMerge;

            case NodeKind.FanOut:
            case NodeKind.FanIn:
            case NodeKind.Serial:
            case NodeKind.CoordinatorComposed:
                // Accepted at load time (US1) and modeled by the schema, but not yet wired to a runtime
                // executor in this binder — fan-out/fan-in map onto the coordinator's SubtaskFrontier /
                // AssemblyPlanning seams and require dispatch infrastructure beyond the per-run graph.
                throw new WorkflowBindException(
                    $"Cannot bind node '{node.Id}' (type='{node.Type}'): node type '{node.Type}' is accepted by " +
                    "the loader but not yet wired to a runtime executor. Use prompt/peer_review/check/merge/scribe/" +
                    "terminal nodes, or wait for fan_out/fan_in/serial runtime support.", node.Id);

            case NodeKind.Terminal:
                throw new WorkflowBindException(
                    $"Cannot resolve a primary executor for terminal node '{node.Id}': terminal executors are " +
                    "resolved from their incoming edges by the binder, not as a node entry point.", node.Id);

            default:
                throw new WorkflowBindException(
                    $"Cannot bind node '{node.Id}' (type='{node.Type}'): no executor registered for this type. " +
                    "Verify the node type is valid and the required fields (agent/prompt) are set.", node.Id);
        }
    }
}
