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
    int MaxIterations,
    IRunWorkflowWiringSupport Wiring);

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
    /// Mutable per-build wiring state threaded through the edge expansion. Accumulates the scribe-output
    /// executors actually wired (canonical merge / no-changes paths AND generic direct-completion paths),
    /// so the terminal "done" sink can declare exactly those as graph outputs (<c>WithOutputFrom</c>) —
    /// a definition that records its outcome without a merge still terminates correctly.
    /// </summary>
    private sealed class WireContext
    {
        public required GraphDescriptorBuilder G { get; init; }
        public required WorkflowDefinition Definition { get; init; }
        public required RunWorkflowBindings B { get; init; }
        public IRunWorkflowWiringSupport S => B.Wiring;
        public List<ExecutorBinding> ScribeOutputs { get; } = new();
    }

    /// <summary>The edge verdicts that make a peer-review node a real verdict GATE (vs. a plain turn).</summary>
    private static readonly HashSet<string> VerdictWhens =
        new(StringComparer.Ordinal) { "approved", "request-changes", "declined", "pass", "fail" };

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

        var ctx = new WireContext { G = builder, Definition = definition, B = bindings };

        // Entry plumbing: the hidden input storer feeds the start node's executor (unconditional). The
        // start node is resolved by its declared id and its TYPE — not a hardcoded "agent". A start that
        // is a producing turn (prompt, or a peer-review used as a plain review turn) enters its per-node
        // agent executor.
        var startNode = GetNode(definition, definition.Start);
        builder.AddEdge(bindings.AgentInputStorer, ResolveEntry(ctx, startNode));

        // Each logical edge expands to its raw executor wiring + predicate.
        foreach (var edge in definition.Edges)
            WireEdge(ctx, edge);

        // Terminal nodes declare which executors are graph outputs (WithOutputFrom).
        foreach (var node in definition.Nodes)
        {
            if (node.Type == WorkflowNodeType.Terminal)
                WireOutputs(ctx, node);
        }
    }

    /// <summary>
    /// Binder DRY-RUN (no executors required): validates that every node in <paramref name="definition"/>
    /// maps to a node kind the binder can wire to a runtime executor, and that every edge references a
    /// declared node. Throws <see cref="WorkflowBindException"/> for the first node/edge that would fail
    /// closed at BUILD time (e.g. fan_out / fan_in / serial / coordinator_composed, which the loader accepts
    /// but have no runtime executor; or a dangling edge reference). <c>peer_review</c> is ACCEPTED — it now
    /// has a runtime executor. Lets callers (save, set-default, generator) reject loader-valid-but-bind-
    /// invalid workflows up front without standing up the full executor graph (which needs DI bindings).
    /// </summary>
    public static void ValidateBindable(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = GetBindabilityErrors(definition);
        if (errors.Count > 0)
            throw new WorkflowBindException(
                "Workflow bindability validation failed: " + string.Join(" ", errors),
                definition.Id);
    }

    public static IReadOnlyList<string> GetBindabilityErrors(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var errors = new List<string>();
        var outgoingByNode = definition.Edges
            .GroupBy(e => e.From, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        foreach (var node in definition.Nodes)
        {
            var kind = NodeClassifier.Classify(node);
            try
            {
                RejectUnwiredKind(node, kind);
            }
            catch (WorkflowBindException ex)
            {
                errors.Add(ex.Message);
                continue;
            }

            if (node.Type != WorkflowNodeType.Terminal &&
                (!outgoingByNode.TryGetValue(node.Id, out var outgoing) || outgoing.Count == 0))
            {
                errors.Add(
                    $"Cannot bind node '{node.Id}' (type='{node.Type}'): non-terminal nodes must have at least one outgoing edge.");
            }

            if (node.Type == WorkflowNodeType.Check && NormalizeDeclaredGateKind(node.GateKind) is null)
            {
                var gate = string.IsNullOrWhiteSpace(node.GateKind) ? "(missing)" : node.GateKind;
                errors.Add(
                    $"Cannot bind check node '{node.Id}': gate_kind is required and must be one of rai, human-review, or rubberduck; got '{gate}'.");
            }

            if (node.Type == WorkflowNodeType.PeerReview && !HasVerdictRouting(definition, node))
            {
                errors.Add(
                    $"Cannot bind peer_review node '{node.Id}': peer_review nodes must declare at least one verdict-routed outgoing edge (approved/request-changes/declined/pass/fail).");
            }
        }

        // Dangling edge endpoints fail closed exactly as they would during a full WireFull build.
        foreach (var edge in definition.Edges)
        {
            WorkflowNode fromNode;
            WorkflowNode toNode;
            try
            {
                fromNode = GetNode(definition, edge.From);
                toNode = GetNode(definition, edge.To);
            }
            catch (WorkflowBindException ex)
            {
                errors.Add(ex.Message);
                continue;
            }

            if (!CanBindTransition(definition, edge, fromNode, toNode))
            {
                var fromKind = EffectiveKind(definition, fromNode);
                var toKind = EffectiveKind(definition, toNode);
                errors.Add(
                    $"Cannot bind edge '{edge.From}'->'{edge.To}' (when='{edge.When}'): no executor wiring for a '{fromKind}'->'{toKind}' transition.");
            }
        }

        return errors;
    }

    /// <summary>Resolves the executor a definition's START node is entered at.</summary>
    private static ExecutorBinding ResolveEntry(WireContext ctx, WorkflowNode startNode) =>
        EffectiveKind(ctx.Definition, startNode) switch
        {
            NodeKind.Agent => ctx.S.ResolveAgentNode(startNode),
            NodeKind.PeerReview => ctx.S.ResolvePeerReviewNode(startNode),
            _ => Factory.ResolveExecutor(startNode, ctx.B),
        };

    /// <summary>
    /// True for a Feature 010 COMPOSED review-policy gate (id prefixed <c>policy-</c>, injected by
    /// <c>ReviewPolicyComposer</c>) — the only gates the dedicated policy plumbing understands (its
    /// neighbour ids are the canonical agent/merge/policy-terminal ids). A catalog CHECK gate keeps a real
    /// executor in <see cref="RunWorkflowBindings.PolicyGateBindings"/> but wires through the canonical path.
    /// </summary>
    private static bool IsComposedPolicyGate(RunWorkflowBindings b, string nodeId) =>
        b.PolicyGateBindings.ContainsKey(nodeId) && nodeId.StartsWith("policy-", StringComparison.Ordinal);

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
    private static void WireEdge(WireContext ctx, WorkflowEdge edge)
    {
        var definition = ctx.Definition;
        var b = ctx.B;
        var fromNode = GetNode(definition, edge.From);
        var toNode = GetNode(definition, edge.To);

        // Node types accepted at load time but not yet wired to a runtime executor fail closed here.
        RejectUnwiredKind(fromNode, NodeClassifier.Classify(fromNode));
        RejectUnwiredKind(toNode, NodeClassifier.Classify(toNode));

        // Wiring keys on the EFFECTIVE kind: a peer-review node with verdict-routed outgoing edges is a
        // real AI review GATE; a peer-review node with a single unconditional outgoing edge is a plain
        // producing turn (it wires identically to an Agent node).
        var fromKind = EffectiveKind(definition, fromNode);
        var toKind = EffectiveKind(definition, toNode);

        // Extra/renamed review-policy gates keep their dedicated policy plumbing (parity with Feature 010
        // multi-gate review policies). Only the COMPOSED policy gates (ids prefixed "policy-", injected by
        // ReviewPolicyComposer) take that path; a catalog CHECK gate (e.g. "rai-check" / "review-gate")
        // still has a real RAI/human-review executor in PolicyGateBindings, but its edges use generic
        // targets, so they wire through the canonical path below (ResolveRai/ResolveReview resolve the
        // per-node executor).
        if (IsComposedPolicyGate(b, edge.From) || IsComposedPolicyGate(b, edge.To))
        {
            if (TryWirePolicyEdge(ctx.G, definition, edge, b))
                return;
        }

        if (TryWireCanonicalEdge(ctx, edge, fromNode, toNode, fromKind, toKind))
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
        WireContext ctx, WorkflowEdge edge, WorkflowNode fromNode, WorkflowNode toNode,
        NodeKind fromKind, NodeKind toKind)
    {
        var g = ctx.G;
        var b = ctx.B;
        var s = ctx.S;
        var when = edge.When;

        switch (fromKind, toKind, when)
        {
            // ───────────────────────── Canonical five-stage pipeline ─────────────────────────
            // (agent / rai / human-review / merge / scribe nodes are resolved by TYPE; the agent
            //  executor is per-node so chained turns each get their own MAF node.)

            // agent turn -> RAI gate (unconditional).
            case (NodeKind.Agent, NodeKind.Rai, null):
                g.AddEdge(s.ResolveAgentNode(fromNode), ResolveRai(toNode, b));
                return true;

            // RAI REVISE (iteration < cap) -> revision adapter -> loop back to agent.
            case (NodeKind.Rai, NodeKind.Agent, "revise"):
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), b.RaiRevisionAdapter,
                    output => output is not null && output.RaiRevisionRequired && output.Iteration < b.MaxIterations)
                 .AddEdge(b.RaiRevisionAdapter, s.ResolveAgentNode(toNode), idempotent: true);
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
                ctx.ScribeOutputs.Add(b.ScribeOutputNoChanges);
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
                 .AddEdge(b.ReviewChangesAdapter, s.ResolveAgentNode(toNode), idempotent: true);
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
                ctx.ScribeOutputs.Add(b.ScribeOutputMerge);
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

            // ───────────────────────── Generic catalog topologies (Feature 015 US3) ─────────────────────────
            // Per-node agent executors + per-edge adapters minted by the wiring support, so chained turns,
            // AI peer-review verdict gates, and direct completions bind onto the real executors.

            // Sequential agent turn: first turn's output feeds the next turn's task (same worktree).
            case (NodeKind.Agent, NodeKind.Agent, null):
            {
                var adapter = s.SequentialAgentAdapter(edge);
                g.AddEdge(s.ResolveAgentNode(fromNode), adapter)
                 .AddEdge(adapter, s.ResolveAgentNode(toNode));
                return true;
            }

            // Producer agent turn -> AI peer-review verdict gate. The produced diff is stored so the
            // gate's downstream merge/scribe adapters can reconstruct it.
            case (NodeKind.Agent, NodeKind.PeerReview, null):
            {
                var storer = s.StoreAgentOutputAdapter(edge);
                g.AddEdge(s.ResolveAgentNode(fromNode), storer)
                 .AddEdge(storer, s.ResolvePeerReviewNode(toNode));
                return true;
            }

            // Agent turn -> direct completion (record the outcome with no merge).
            case (NodeKind.Agent, NodeKind.Scribe, null):
            {
                var path = s.AgentScribePath(edge);
                g.AddEdge(s.ResolveAgentNode(fromNode), path.Input)
                 .AddEdge(path.Input, path.Scribe)
                 .AddEdge(path.Scribe, path.Output);
                ctx.ScribeOutputs.Add(path.Output);
                return true;
            }

            // Producer agent turn -> human review gate directly (no RAI stage in between).
            case (NodeKind.Agent, NodeKind.HumanReview, null):
            {
                var req = s.AgentToReviewRequestAdapter(edge);
                g.AddEdge(s.ResolveAgentNode(fromNode), req)
                 .AddEdge(req, ResolveReview(toNode, b));
                return true;
            }

            // RAI cleared (has a diff) -> merge directly (publish-style: no human gate before merge).
            case (NodeKind.Rai, NodeKind.Merge, "review"):
            {
                var adapter = s.AgentToMergeAdapter(edge);
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), adapter,
                    output => output is not null && !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff))
                 .AddEdge(adapter, b.MergeBinding);
                return true;
            }

            // RAI cleared (has a diff) -> next agent turn (e.g. produce a code review).
            case (NodeKind.Rai, NodeKind.Agent, "review"):
            {
                var adapter = s.SequentialAgentAdapter(edge);
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), adapter,
                    output => output is not null && !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff))
                 .AddEdge(adapter, s.ResolveAgentNode(toNode));
                return true;
            }

            // RAI cleared (has a diff) -> AI peer-review verdict gate.
            case (NodeKind.Rai, NodeKind.PeerReview, "review"):
            {
                var storer = s.StoreAgentOutputAdapter(edge);
                g.AddEdge<AgentTurnOutput>(ResolveRai(fromNode, b), storer,
                    output => output is not null && !output.RaiRevisionRequired && !string.IsNullOrEmpty(output.Diff))
                 .AddEdge(storer, s.ResolvePeerReviewNode(toNode));
                return true;
            }

            // Peer-review APPROVED / PASS -> merge.
            case (NodeKind.PeerReview, NodeKind.Merge, "approved"):
            case (NodeKind.PeerReview, NodeKind.Merge, "pass"):
            {
                var adapter = s.ReviewToMergeAdapter(edge);
                g.AddEdge<WorkflowReviewDecision>(s.ResolvePeerReviewNode(fromNode), adapter,
                    decision => decision is not null && decision.Approved)
                 .AddEdge(adapter, b.MergeBinding);
                return true;
            }

            // Peer-review PASS -> RAI gate (e.g. a QA gate that precedes the safety check).
            case (NodeKind.PeerReview, NodeKind.Rai, "pass"):
            {
                var adapter = s.ReviewToAgentOutputAdapter(edge);
                g.AddEdge<WorkflowReviewDecision>(s.ResolvePeerReviewNode(fromNode), adapter,
                    decision => decision is not null && decision.Approved)
                 .AddEdge(adapter, ResolveRai(toNode, b));
                return true;
            }

            // Peer-review REQUEST-CHANGES / FAIL -> loop back to a producer agent with feedback.
            case (NodeKind.PeerReview, NodeKind.Agent, "request-changes"):
            case (NodeKind.PeerReview, NodeKind.Agent, "fail"):
            {
                var adapter = s.ReviewToAgentReviseAdapter(edge);
                g.AddEdge<WorkflowReviewDecision>(s.ResolvePeerReviewNode(fromNode), adapter,
                    decision => decision is not null && !decision.Approved && decision.RequestChanges)
                 .AddEdge(adapter, s.ResolveAgentNode(toNode), idempotent: true);
                return true;
            }

            // Peer-review hard-declined -> terminal.
            case (NodeKind.PeerReview, NodeKind.Terminal, "declined"):
                g.AddEdge<WorkflowReviewDecision>(s.ResolvePeerReviewNode(fromNode), b.TerminalDeclined,
                    decision => decision is null || (!decision.Approved && !decision.RequestChanges));
                return true;

            // Human review APPROVED -> next agent turn (e.g. postmortem before the scribe).
            case (NodeKind.HumanReview, NodeKind.Agent, "approved"):
            {
                var adapter = s.ReviewToAgentForwardAdapter(edge);
                g.AddEdge<WorkflowReviewDecision>(ResolveReview(fromNode, b), adapter,
                    decision => decision is not null && decision.Approved)
                 .AddEdge(adapter, s.ResolveAgentNode(toNode));
                return true;
            }

            // Human review APPROVED -> direct completion (record with no merge stage).
            case (NodeKind.HumanReview, NodeKind.Scribe, "approved"):
            {
                var path = s.ReviewScribePath(edge);
                g.AddEdge<WorkflowReviewDecision>(ResolveReview(fromNode, b), path.Input,
                    decision => decision is not null && decision.Approved)
                 .AddEdge(path.Input, path.Scribe)
                 .AddEdge(path.Scribe, path.Output);
                ctx.ScribeOutputs.Add(path.Output);
                return true;
            }

            // Merge blocked -> re-enter an AI peer-review verdict gate.
            case (NodeKind.Merge, NodeKind.PeerReview, "blocked"):
            {
                var adapter = s.MergeToAgentOutputAdapter(edge);
                g.AddEdge<MergeOutput>(b.MergeBinding, adapter,
                    output => output is not null && output.Status == "blocked")
                 .AddEdge(adapter, s.ResolvePeerReviewNode(toNode), idempotent: true);
                return true;
            }

            // Merge blocked -> re-enter a producer agent turn.
            case (NodeKind.Merge, NodeKind.Agent, "blocked"):
            {
                var adapter = s.MergeToAgentReviseAdapter(edge);
                g.AddEdge<MergeOutput>(b.MergeBinding, adapter,
                    output => output is not null && output.Status == "blocked")
                 .AddEdge(adapter, s.ResolveAgentNode(toNode), idempotent: true);
                return true;
            }

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
            case NodeKind.CoordinatorComposed:
                throw new WorkflowBindException(
                    $"Cannot bind node '{node.Id}' (type='{node.Type}'): node type '{node.Type}' is accepted by " +
                    "the loader but not yet wired to a runtime executor. fan_out/fan_in map onto the coordinator " +
                    "SubtaskFrontier/AssemblyPlanning seams; serial onto the sequential seam — runtime support is " +
                    "pending.", node.Id);
        }
    }

    /// <summary>
    /// The wiring kind a node behaves as. A <see cref="NodeKind.PeerReview"/> node is always a real AI
    /// review gate; definitions that omit verdict-routed outgoing edges are rejected by
    /// <see cref="ValidateBindable"/> instead of being silently downgraded to agent turns.
    /// </summary>
    private static NodeKind EffectiveKind(WorkflowDefinition def, WorkflowNode node)
    {
        var kind = NodeClassifier.Classify(node);
        return kind;
    }

    /// <summary>True when the node has at least one outgoing edge whose verdict marks it a review gate.</summary>
    private static bool HasVerdictRouting(WorkflowDefinition def, WorkflowNode node) =>
        def.Edges.Any(e => string.Equals(e.From, node.Id, StringComparison.Ordinal)
            && e.When is not null && VerdictWhens.Contains(e.When));

    private static string? NormalizeDeclaredGateKind(string? gateKind)
    {
        if (string.IsNullOrWhiteSpace(gateKind)) return null;
        return gateKind.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant() switch
        {
            "rai" => "rai",
            "review" or "human-review" => "human-review",
            "rubberduck" or "rubber-duck" => "rubberduck",
            _ => null,
        };
    }

    private static bool CanBindTransition(
        WorkflowDefinition definition,
        WorkflowEdge edge,
        WorkflowNode fromNode,
        WorkflowNode toNode)
    {
        var fromKind = EffectiveKind(definition, fromNode);
        var toKind = EffectiveKind(definition, toNode);
        var when = edge.When;

        return (fromKind, toKind, when) switch
        {
            (NodeKind.Agent, NodeKind.Rai, null) => true,
            (NodeKind.Rai, NodeKind.Agent, "revise") => true,
            (NodeKind.Rai, NodeKind.Terminal, "safety-failed") => true,
            (NodeKind.Rai, NodeKind.Scribe, "no-changes") => true,
            (NodeKind.Rai, NodeKind.HumanReview, "review") => true,
            (NodeKind.HumanReview, NodeKind.Merge, "approved") => true,
            (NodeKind.HumanReview, NodeKind.Agent, "request-changes") => true,
            (NodeKind.HumanReview, NodeKind.Terminal, "declined") => true,
            (NodeKind.Merge, NodeKind.Scribe, "merged") => true,
            (NodeKind.Merge, NodeKind.HumanReview, "blocked") => true,
            (NodeKind.Scribe, NodeKind.Terminal, null) => true,
            (NodeKind.Agent, NodeKind.Agent, null) => true,
            (NodeKind.Agent, NodeKind.PeerReview, null) => true,
            (NodeKind.Agent, NodeKind.Scribe, null) => true,
            (NodeKind.Agent, NodeKind.HumanReview, null) => true,
            (NodeKind.Rai, NodeKind.Merge, "review") => true,
            (NodeKind.Rai, NodeKind.Agent, "review") => true,
            (NodeKind.Rai, NodeKind.PeerReview, "review") => true,
            (NodeKind.PeerReview, NodeKind.Merge, "approved" or "pass") => true,
            (NodeKind.PeerReview, NodeKind.Rai, "pass") => true,
            (NodeKind.PeerReview, NodeKind.Agent, "request-changes" or "fail") => true,
            (NodeKind.PeerReview, NodeKind.Terminal, "declined") => true,
            (NodeKind.HumanReview, NodeKind.Agent, "approved") => true,
            (NodeKind.HumanReview, NodeKind.Scribe, "approved") => true,
            (NodeKind.Merge, NodeKind.PeerReview, "blocked") => true,
            (NodeKind.Merge, NodeKind.Agent, "blocked") => true,
            _ => false,
        };
    }

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
    private static void WireOutputs(WireContext ctx, WorkflowNode terminal)
    {
        var g = ctx.G;
        var b = ctx.B;
        var definition = ctx.Definition;

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
        // A terminal reached from a scribe stage is the run's "done" sink; every scribe-output executor
        // actually wired during edge expansion (canonical merge / no-changes paths AND generic
        // direct-completion paths) is declared a graph output.
        if (incoming.Any(e => IsScribeSource(definition, e.From)))
        {
            var outputs = ctx.ScribeOutputs.Distinct().ToArray();
            if (outputs.Length == 0)
                throw new WorkflowBindException(
                    $"Cannot bind outputs for terminal node '{terminal.Id}': it is reached from a scribe stage " +
                    "but no scribe-output executor was wired.", terminal.Id);
            g.WithOutputFrom(outputs);
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
