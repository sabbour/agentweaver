using Microsoft.Agents.AI.Workflows;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// The per-node / per-edge executor mint used by <see cref="RunWorkflowGraphBinder"/> to wire the
/// generic (non-canonical) topologies the Feature 015 US3 catalog workflows introduce — chained
/// agent turns (<c>Agent → Agent</c>), AI peer-review verdict gates, direct <c>Agent → Scribe</c>
/// completion, and <c>Review → Scribe</c> short-circuits.
///
/// <para>
/// The canonical five-stage pipeline binds onto a single shared agent executor and a fixed set of
/// adapters (held directly on <see cref="RunWorkflowBindings"/>). A definition that chains multiple
/// distinct agent nodes (triage → fix → verify) cannot reuse one shared instance: every logical node
/// must be its OWN MAF executor, and every cross-type transition needs its OWN adapter, otherwise the
/// graph self-loops or fans a single hidden adapter into an ambiguous multi-input node. This seam lets
/// the binder ask the factory (which owns the real DI dependencies — Principle VII, no mocks) to mint
/// those per-node executors and per-edge adapters on demand while the binder still owns the topology.
/// </para>
///
/// <para>
/// Implementations MUST cache the per-node executors (<see cref="ResolveAgentNode"/> /
/// <see cref="ResolvePeerReviewNode"/>) by node id so repeated edges resolving the same node share one
/// executor, and MUST mint a FRESH adapter for every edge (keyed by the edge's from/to/when) so no
/// hidden plumbing node receives two inputs.
/// </para>
/// </summary>
internal interface IRunWorkflowWiringSupport
{
    /// <summary>The per-node agent-turn executor for a prompt (or pass-through peer-review) node.</summary>
    ExecutorBinding ResolveAgentNode(WorkflowNode node);

    /// <summary>The per-node AI peer-review verdict gate (emits a <c>WorkflowReviewDecision</c>).</summary>
    ExecutorBinding ResolvePeerReviewNode(WorkflowNode node);

    /// <summary><c>AgentTurnOutput → AgentTurnInput</c>: feed one agent turn's result forward as the
    /// next agent turn's task (continuing the same worktree). For sequential <c>Agent → Agent</c>.</summary>
    ExecutorBinding SequentialAgentAdapter(WorkflowEdge edge);

    /// <summary><c>WorkflowReviewDecision → AgentTurnInput</c>: continue forward into the next agent turn
    /// after a gate verdict (e.g. approved → postmortem, or a pass-through review → next step).</summary>
    ExecutorBinding ReviewToAgentForwardAdapter(WorkflowEdge edge);

    /// <summary><c>WorkflowReviewDecision → AgentTurnInput</c>: loop back to a producer agent with the
    /// reviewer's feedback (request-changes / fail), incrementing the iteration counter.</summary>
    ExecutorBinding ReviewToAgentReviseAdapter(WorkflowEdge edge);

    /// <summary><c>AgentTurnOutput → AgentTurnOutput</c>: persist the produced diff to workflow state so a
    /// downstream verdict gate's merge/scribe adapters can reconstruct it, then pass it through.</summary>
    ExecutorBinding StoreAgentOutputAdapter(WorkflowEdge edge);

    /// <summary><c>WorkflowReviewDecision → AgentTurnOutput</c>: reconstruct the stored produced diff so a
    /// peer-review verdict can flow into an executor that consumes an <c>AgentTurnOutput</c> (e.g. RAI).</summary>
    ExecutorBinding ReviewToAgentOutputAdapter(WorkflowEdge edge);

    /// <summary><c>WorkflowReviewDecision → MergeInput</c>: reconstruct the stored diff and route an
    /// approved peer-review verdict into the merge stage.</summary>
    ExecutorBinding ReviewToMergeAdapter(WorkflowEdge edge);

    /// <summary><c>AgentTurnOutput → WorkflowReviewRequest</c>: store the diff and raise a human review
    /// request directly from an agent turn (a producer that flows straight into a human-review gate).</summary>
    ExecutorBinding AgentToReviewRequestAdapter(WorkflowEdge edge);

    /// <summary><c>AgentTurnOutput → MergeInput</c>: route a gate-cleared agent/RAI output directly into
    /// the merge stage (publish-style workflows with no human gate before merge).</summary>
    ExecutorBinding AgentToMergeAdapter(WorkflowEdge edge);

    /// <summary><c>MergeOutput → AgentTurnOutput</c>: reconstruct the stored diff after a blocked merge so
    /// the run can re-enter an AI peer-review gate.</summary>
    ExecutorBinding MergeToAgentOutputAdapter(WorkflowEdge edge);

    /// <summary><c>MergeOutput → AgentTurnInput</c>: re-enter a producer agent after a blocked merge.</summary>
    ExecutorBinding MergeToAgentReviseAdapter(WorkflowEdge edge);

    /// <summary>A direct <c>Agent → Scribe</c> completion sub-path: an input adapter
    /// (<c>AgentTurnOutput → ScribeTurnInput</c>), a dedicated scribe executor, and the scribe-output
    /// executor that becomes a graph output. For workflows that record an outcome without a merge.</summary>
    ScribeSubPath AgentScribePath(WorkflowEdge edge);

    /// <summary>A direct <c>Review → Scribe</c> completion sub-path (approved verdict → record), with a
    /// <c>WorkflowReviewDecision → ScribeTurnInput</c> input adapter.</summary>
    ScribeSubPath ReviewScribePath(WorkflowEdge edge);
}

/// <summary>The three executors of a generic scribe completion sub-path; <see cref="Output"/> is the
/// scribe-output executor the binder registers as a graph output (<c>WithOutputFrom</c>).</summary>
internal readonly record struct ScribeSubPath(
    ExecutorBinding Input, ExecutorBinding Scribe, ExecutorBinding Output);
