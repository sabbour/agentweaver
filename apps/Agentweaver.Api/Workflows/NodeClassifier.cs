namespace Agentweaver.Api.Workflows;

/// <summary>
/// The binder-internal classification of a workflow node, derived from its <see cref="WorkflowNode.Type"/>
/// (and, for gates, its <see cref="WorkflowNode.GateKind"/>) — NEVER from its id. This is the heart of the
/// generalization (Feature 015, US1): the executor wiring and edge expansion that previously keyed on the
/// five hardcoded node ids (<c>agent</c>, <c>rai</c>, <c>review</c>, <c>merge</c>, <c>scribe</c>) and literal
/// edge keys (<c>"agent-&gt;rai:"</c>) now key on this type-derived kind, so any authored workflow whose node
/// ids differ still wires identically when the node TYPES match.
/// </summary>
internal enum NodeKind
{
    /// <summary>A prompt/agent turn (<see cref="WorkflowNodeType.Prompt"/>).</summary>
    Agent,

    /// <summary>A RAI content-safety gate (check node, gate kind <c>rai</c>).</summary>
    Rai,

    /// <summary>A human-in-the-loop review gate (check node, gate kind <c>human-review</c>).</summary>
    HumanReview,

    /// <summary>A rubber-duck review gate (check node, gate kind <c>rubberduck</c>).</summary>
    Rubberduck,

    /// <summary>A generic check/branch node whose gate kind is none of the known review kinds.</summary>
    Check,

    /// <summary>The merge stage (<see cref="WorkflowNodeType.Merge"/>).</summary>
    Merge,

    /// <summary>The scribe stage (<see cref="WorkflowNodeType.Scribe"/>).</summary>
    Scribe,

    /// <summary>A terminal/no-op sink (<see cref="WorkflowNodeType.Terminal"/>).</summary>
    Terminal,

    /// <summary>Parallel dispatch (<see cref="WorkflowNodeType.FanOut"/>).</summary>
    FanOut,

    /// <summary>Parallel join (<see cref="WorkflowNodeType.FanIn"/>).</summary>
    FanIn,

    /// <summary>An ordered sequence of child steps (<see cref="WorkflowNodeType.Serial"/>).</summary>
    Serial,

    /// <summary>A peer-review node that emits a verdict (<see cref="WorkflowNodeType.PeerReview"/>).</summary>
    PeerReview,

    /// <summary>A coordinator-composed stage (<see cref="WorkflowNodeType.CoordinatorComposed"/>).</summary>
    CoordinatorComposed,
}

/// <summary>
/// Classifies a <see cref="WorkflowNode"/> into a binder <see cref="NodeKind"/> from its type and gate kind,
/// independent of the node's id. Centralizes the "what role does this node play" decision so the binder, the
/// executor factory, and the loader agree.
/// </summary>
internal static class NodeClassifier
{
    public static NodeKind Classify(WorkflowNode node) => node.Type switch
    {
        WorkflowNodeType.Prompt              => NodeKind.Agent,
        WorkflowNodeType.Merge               => NodeKind.Merge,
        WorkflowNodeType.Scribe              => NodeKind.Scribe,
        WorkflowNodeType.Terminal            => NodeKind.Terminal,
        WorkflowNodeType.FanOut              => NodeKind.FanOut,
        WorkflowNodeType.FanIn               => NodeKind.FanIn,
        WorkflowNodeType.Serial              => NodeKind.Serial,
        WorkflowNodeType.PeerReview          => NodeKind.PeerReview,
        WorkflowNodeType.CoordinatorComposed => NodeKind.CoordinatorComposed,
        WorkflowNodeType.Check               => ClassifyGate(node),
        _                                    => NodeKind.Check,
    };

    private static NodeKind ClassifyGate(WorkflowNode node) => NormalizeGateKind(node) switch
    {
        "rai"          => NodeKind.Rai,
        "human-review" => NodeKind.HumanReview,
        "rubberduck"   => NodeKind.Rubberduck,
        _              => NodeKind.Check,
    };

    /// <summary>
    /// Canonical gate kind from <see cref="WorkflowNode.GateKind"/>, falling back to the node id for legacy
    /// definitions that predate the explicit <c>gate_kind</c> field. Mirrors the normalization in
    /// <c>RunWorkflowFactory.GateKindOf</c> so the policy-gate bindings and the binder agree.
    /// </summary>
    public static string? NormalizeGateKind(WorkflowNode node)
    {
        var raw = !string.IsNullOrWhiteSpace(node.GateKind) ? node.GateKind! : node.Id;
        return raw.Trim().Replace('_', '-').Replace(' ', '-').ToLowerInvariant() switch
        {
            "rai"                      => "rai",
            "review" or "human-review" => "human-review",
            "rubberduck" or "rubber-duck" => "rubberduck",
            _                          => null,
        };
    }
}
