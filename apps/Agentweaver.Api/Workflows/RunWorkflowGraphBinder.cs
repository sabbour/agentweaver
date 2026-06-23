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
/// Binds a <see cref="WorkflowDefinition"/> onto the live MAF graph (Feature 010, wf-maf-binding). The
/// full run pipeline is assembled by ITERATING the definition's edges and resolving each
/// <c>(from, to, when)</c> verdict to the concrete executor wiring + typed predicate that previously
/// lived inline in <c>RunWorkflowFactory.BuildWorkflow</c>, instead of a hand-written chain.
///
/// PARITY-FIRST: the raw <see cref="GraphDescriptorBuilder"/> edges, predicates, idempotent flags, and
/// outputs emitted here are byte-for-byte identical to the previous hand-coded wiring, so the collapsed
/// <see cref="GraphDescriptor"/> and the executed MAF graph are unchanged. Each logical definition edge
/// expands to a subgraph of raw edges (hidden plumbing/adapter executors that the descriptor collapses).
///
/// An unmapped edge or terminal throws — this is the drift guard: if the default definition diverges
/// from the executor wiring, the build of every run fails loudly rather than silently mis-wiring.
/// </summary>
internal static class RunWorkflowGraphBinder
{
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

        // Entry plumbing: the hidden input storer feeds the start node's executor (unconditional).
        builder.AddEdge(bindings.AgentInputStorer, ResolveEntry(definition.Start, bindings));

        // Each logical edge expands to its raw executor wiring + predicate.
        foreach (var edge in definition.Edges)
            WireEdge(builder, edge, bindings);

        // Terminal nodes declare which executors are graph outputs (WithOutputFrom).
        foreach (var node in definition.Nodes)
        {
            if (node.Type == WorkflowNodeType.Terminal)
                WireOutputs(builder, node.Id, bindings);
        }
    }

    /// <summary>Resolves the start node's primary executor (the descriptor's entry binding).</summary>
    private static ExecutorBinding ResolveEntry(string startNodeId, RunWorkflowBindings b) => startNodeId switch
    {
        "agent" => b.AgentBinding,
        _ => throw new InvalidOperationException(
            $"RunWorkflowGraphBinder: unsupported start node '{startNodeId}'. " +
            "The default WorkflowDefinition drifted from the executor wiring."),
    };

    /// <summary>
    /// Resolves a single logical edge <c>(from, to, when)</c> to its raw executor wiring. The predicates
    /// are the exact lambdas previously inline in <c>BuildWorkflow</c>; do not alter them (parity).
    /// </summary>
    private static void WireEdge(GraphDescriptorBuilder g, WorkflowEdge edge, RunWorkflowBindings b)
    {
        var key = $"{edge.From}->{edge.To}:{edge.When}";
        switch (key)
        {
            // agent turn -> Rai RAI gate (unconditional).
            case "agent->rai:":
                g.AddEdge(b.AgentBinding, b.RaiBinding);
                break;

            // Rai REVISE (iteration < cap) -> revision adapter -> loop back to agent.
            case "rai->agent:revise":
                g.AddEdge<AgentTurnOutput>(b.RaiBinding, b.RaiRevisionAdapter,
                    output => output is not null && output.RaiRevisionRequired && output.Iteration < b.MaxIterations)
                 .AddEdge(b.RaiRevisionAdapter, b.AgentBinding, idempotent: true);
                break;

            // Content safety: agent turn flagged on an empty diff -> fail immediately, never reaching review.
            case "rai->terminal-safety-failed:safety-failed":
                g.AddEdge<AgentTurnOutput>(b.RaiBinding, b.TerminalSafetyFailed,
                    output => output is not null && !output.RaiRevisionRequired
                        && string.IsNullOrEmpty(output.Diff) && output.ContentSafetyFlagged);
                break;

            // No changes -> no-op -> scribe path.
            case "rai->scribe:no-changes":
                g.AddEdge<AgentTurnOutput>(b.RaiBinding, b.TerminalNoOp,
                    output => output is not null && !output.RaiRevisionRequired
                        && string.IsNullOrEmpty(output.Diff) && !output.ContentSafetyFlagged)
                 .AddEdge(b.TerminalNoOp, b.ScribeInputNoChanges)
                 .AddEdge(b.ScribeInputNoChanges, b.ScribeBindingNoChanges)
                 .AddEdge(b.ScribeBindingNoChanges, b.ScribeOutputNoChanges);
                break;

            // Otherwise (OK / RED with a diff / revise-at-cap) -> review adapter -> human review gate.
            case "rai->review:review":
                g.AddEdge<AgentTurnOutput>(b.RaiBinding, b.ReviewAdapter,
                    output => output is not null && !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff))
                 .AddEdge(b.ReviewAdapter, b.ReviewBinding);
                break;

            // Approved -> merge adapter -> merge executor.
            case "review->merge:approved":
                g.AddEdge<WorkflowReviewDecision>(b.ReviewBinding, b.MergeAdapter,
                    decision => decision is not null && decision.Approved)
                 .AddEdge(b.MergeAdapter, b.MergeBinding);
                break;

            // Review RequestChanges -> revision adapter -> loop back to agent (no cap).
            case "review->agent:request-changes":
                g.AddEdge<WorkflowReviewDecision>(b.ReviewBinding, b.ReviewChangesAdapter,
                    decision => decision is not null && !decision.Approved && decision.RequestChanges)
                 .AddEdge(b.ReviewChangesAdapter, b.AgentBinding, idempotent: true);
                break;

            // Hard-declined -> terminal.
            case "review->terminal-declined:declined":
                g.AddEdge<WorkflowReviewDecision>(b.ReviewBinding, b.TerminalDeclined,
                    decision => decision is null || (!decision.Approved && !decision.RequestChanges));
                break;

            // Merge succeeded or failed terminally -> scribe path.
            case "merge->scribe:merged":
                g.AddEdge<MergeOutput>(b.MergeBinding, b.TerminalMerge,
                    output => output is not null && output.Status != "blocked")
                 .AddEdge(b.TerminalMerge, b.ScribeInputMerge)
                 .AddEdge(b.ScribeInputMerge, b.ScribeBindingMerge)
                 .AddEdge(b.ScribeBindingMerge, b.ScribeOutputMerge);
                break;

            // Merge blocked -> re-enter the review gate via HITL.
            case "merge->review:blocked":
                g.AddEdge<MergeOutput>(b.MergeBinding, b.BlockedAdapter,
                    output => output is not null && output.Status == "blocked")
                 .AddEdge(b.BlockedAdapter, b.ReviewBinding, idempotent: true);
                break;

            // Scribe -> done: the scribe output executors ARE the graph outputs (WithOutputFrom); no raw edge.
            case "scribe->done:":
                break;

            default:
                throw new InvalidOperationException(
                    $"RunWorkflowGraphBinder: no MAF binding for workflow edge '{key}'. " +
                    "The default WorkflowDefinition drifted from the executor wiring.");
        }
    }

    /// <summary>Wires the graph outputs declared by a terminal definition node.</summary>
    private static void WireOutputs(GraphDescriptorBuilder g, string terminalNodeId, RunWorkflowBindings b)
    {
        switch (terminalNodeId)
        {
            case "done":
                g.WithOutputFrom(b.ScribeOutputMerge, b.ScribeOutputNoChanges);
                break;
            case "terminal-safety-failed":
                g.WithOutputFrom(b.TerminalSafetyFailed);
                break;
            case "terminal-declined":
                g.WithOutputFrom(b.TerminalDeclined);
                break;
            default:
                throw new InvalidOperationException(
                    $"RunWorkflowGraphBinder: no output binding for terminal node '{terminalNodeId}'. " +
                    "The default WorkflowDefinition drifted from the executor wiring.");
        }
    }
}
