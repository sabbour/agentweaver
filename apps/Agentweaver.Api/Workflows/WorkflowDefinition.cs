namespace Agentweaver.Api.Workflows;

/// <summary>
/// How a workflow's runs are started (Feature 010, FR-020/021/022). Trigger is a property of the
/// workflow and does NOT determine which workflow a project/task selects.
/// </summary>
public enum WorkflowTriggerType
{
    /// <summary>A person or client explicitly starts a run (FR-020).</summary>
    Manual,

    /// <summary>The coordinator picks up eligible work on its periodic heartbeat (FR-021).</summary>
    Heartbeat,

    /// <summary>The workflow starts in response to a declared event (FR-022).</summary>
    Event,
}

/// <summary>
/// Supported event triggers. For this iteration the ONLY valid event is "task added to Ready"
/// (FR-022). The enum is intentionally narrow; any other declared event fails validation. The schema
/// may grow new members in a future iteration without breaking existing definitions.
/// </summary>
public enum WorkflowEventType
{
    /// <summary>A task entered the project's Ready bucket (Feature 009).</summary>
    TaskAddedToReady,
}

/// <summary>
/// The typed building blocks a workflow node can be (Feature 010, FR-012..FR-017). The runtime does
/// not yet execute every type; this foundation models and round-trips all of them so authored YAML is
/// faithfully parsed and validated.
/// </summary>
public enum WorkflowNodeType
{
    /// <summary>An agent turn against a prompt (maps onto AgentTurnExecutor) — FR-012.</summary>
    Prompt,

    /// <summary>A reviewing agent evaluates another node's output and emits a verdict — FR-015.</summary>
    PeerReview,

    /// <summary>A gate/condition that routes on an upstream verdict/predicate — FR-016.</summary>
    Check,

    /// <summary>Dispatch multiple parallel branches/subtasks (maps onto SubtaskFrontier) — FR-014.</summary>
    FanOut,

    /// <summary>Join that waits for all required branches (maps onto AssemblyPlanning) — FR-014.</summary>
    FanIn,

    /// <summary>A stage the coordinator decomposes into subtasks at runtime — FR-017.</summary>
    CoordinatorComposed,

    /// <summary>An ordered sequence whose child steps run strictly in declared order — FR-013.</summary>
    Serial,

    /// <summary>Applies a produced change (an irreversible action gated by review) — the merge stage.</summary>
    Merge,

    /// <summary>Records the run outcome — the scribe stage.</summary>
    Scribe,

    /// <summary>A terminal/no-op sink (FR-018 zero-subtask resolution, no-op, declined, capped, failed).</summary>
    Terminal,
}

/// <summary>The declared start condition of a workflow.</summary>
public sealed record WorkflowTrigger
{
    public required WorkflowTriggerType Type { get; init; }

    /// <summary>Set iff <see cref="Type"/> is <see cref="WorkflowTriggerType.Event"/>.</summary>
    public WorkflowEventType? Event { get; init; }
}

/// <summary>A typed unit within a workflow definition. Carries render metadata equivalent to the
/// runtime's IWorkflowNodeMeta (logical id, label, role, node type, kind).</summary>
public sealed record WorkflowNode
{
    public required string Id { get; init; }
    public required WorkflowNodeType Type { get; init; }

    /// <summary>Human-readable label for the rendered graph (defaults to <see cref="Id"/>).</summary>
    public required string Label { get; init; }

    /// <summary>Render role (e.g. "agent", "review", "merge", "scribe", "assembly", "plumbing").</summary>
    public string? Role { get; init; }

    /// <summary>Render kind (e.g. "live", "action", "terminal", "agent", "gate").</summary>
    public string? Kind { get; init; }

    /// <summary>
    /// Canonical review-policy gate kind for deduplication (e.g. "rai", "human-review",
    /// "rubberduck"). Null for non-review gates and older workflow files; the composer falls back to
    /// legacy built-in ids for backward compatibility.
    /// </summary>
    public string? GateKind { get; init; }

    /// <summary>The agent name that performs this step (prompt / peer-review).</summary>
    public string? Agent { get; init; }

    /// <summary>The prompt text for a <see cref="WorkflowNodeType.Prompt"/> node.</summary>
    public string? Prompt { get; init; }

    /// <summary>
    /// Optional inline charter for a bespoke (non-catalog) agent role. Set ONLY when the node's
    /// <see cref="Role"/> is a bespoke id that no catalog role covers; defines the agent's persona,
    /// domain expertise, and approach (2-4 sentences). Null when the node uses a catalog role id, in
    /// which case the catalog charter resolves automatically. Round-trips through the YAML `charter`
    /// key and feeds the run's agent charter at execution time.
    /// </summary>
    public string? Charter { get; init; }

    /// <summary>For a <see cref="WorkflowNodeType.PeerReview"/> or <see cref="WorkflowNodeType.FanIn"/>
    /// node: the id of the node whose output is reviewed/joined.</summary>
    public string? Target { get; init; }

    /// <summary>For a <see cref="WorkflowNodeType.Serial"/> node: the ordered child node ids.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];

    /// <summary>For a <see cref="WorkflowNodeType.Check"/> node: the set of verdicts it routes on. Each
    /// verdict MUST have a matching outgoing edge (validated, FR-016).</summary>
    public IReadOnlyList<string> Branches { get; init; } = [];
}

/// <summary>
/// An explicit board column declared in a workflow definition. When a workflow declares at least one
/// stage the Kanban board derives its columns from this list instead of the hardcoded defaults.
/// Workflows that omit the <c>stages:</c> key fall back to the canonical four-bucket layout
/// (Problems, Human Review, Active, Done) for full backward compatibility.
/// </summary>
public sealed record WorkflowStageDefinition
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public int Order { get; init; }
}

/// <summary>A directed connection between two nodes, optionally guarded by a verdict/predicate label.</summary>
public sealed record WorkflowEdge
{
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>The verdict/predicate this edge fires on (e.g. "approved", "request-changes",
    /// "revise", "rai-red", "no-changes"). Null means an unconditional edge.</summary>
    public string? When { get; init; }
}

/// <summary>
/// A declarative, YAML-authored description of a run pipeline (Feature 010). Identified by a stable
/// id/name, composed of typed nodes connected by edges, plus a declared trigger. Validated before use;
/// the source of a project's effective run graph.
/// </summary>
public sealed record WorkflowDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required WorkflowTrigger Trigger { get; init; }

    /// <summary>The id of the entry node where execution begins.</summary>
    public required string Start { get; init; }

    public required IReadOnlyList<WorkflowNode> Nodes { get; init; }
    public required IReadOnlyList<WorkflowEdge> Edges { get; init; }

    /// <summary>
    /// Optional explicit board column definitions. When non-empty the Kanban board derives its columns
    /// from this list; when empty (the default) the board falls back to the four hardcoded buckets
    /// (Problems, Human Review, Active, Done) for full backward compatibility.
    /// </summary>
    public IReadOnlyList<WorkflowStageDefinition> Stages { get; init; } = [];
}
