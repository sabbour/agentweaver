namespace Agentweaver.Api.Workflows;

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

    /// <summary>Platform-owned build/test/preview gate; emits a peer-review style verdict.</summary>
    BuildTest,

    /// <summary>
    /// Platform-owned, non-agent action that opens a GitHub pull request on the project's connected
    /// repository (Feature: workflows-automation open-pull-request-action). Deterministic — no LLM call.
    /// </summary>
    OpenPullRequest,

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

    /// <summary>
    /// For a <see cref="WorkflowNodeType.OpenPullRequest"/> node: the PR title, supporting the template
    /// placeholders <c>{run_id}</c>, <c>{worktree_branch}</c>, <c>{originating_branch}</c>, and
    /// <c>{outcome_summary}</c>. Null uses the executor's built-in default template.
    /// </summary>
    public string? PrTitle { get; init; }

    /// <summary>
    /// For a <see cref="WorkflowNodeType.OpenPullRequest"/> node: the PR body, supporting the same
    /// template placeholders as <see cref="PrTitle"/>. Null uses the executor's built-in default
    /// template.
    /// </summary>
    public string? PrBody { get; init; }

    /// <summary>
    /// For a <see cref="WorkflowNodeType.OpenPullRequest"/> node: the base branch the PR merges into.
    /// Null defaults to the project's configured default branch (falling back to <c>main</c>).
    /// </summary>
    public string? PrBase { get; init; }

    /// <summary>
    /// For a <see cref="WorkflowNodeType.OpenPullRequest"/> node: the head branch to open the PR from.
    /// Null defaults to the run's produced worktree branch.
    /// </summary>
    public string? PrHead { get; init; }

    /// <summary>
    /// For a <see cref="WorkflowNodeType.OpenPullRequest"/> node: whether the PR is opened as a draft.
    /// Null defaults to false (ready for review).
    /// </summary>
    public bool? PrDraft { get; init; }
}

/// <summary>The kind of automation trigger a workflow declares (issue #53).</summary>
public enum WorkflowTriggerType
{
    /// <summary>Starts a run automatically on a recurring cadence (daily/weekly/monthly).</summary>
    Schedule,

    /// <summary>Starts a run automatically when a named event fires.</summary>
    Event,
}

/// <summary>
/// The recurring cadence unit for a <see cref="WorkflowTriggerType.Schedule"/> trigger. Arbitrary cron
/// expressions and sub-daily precision are explicitly out of scope (see
/// specs/workflows-automation/trigger-tasks-for-scheduled-and-event-workflows.md) — only these three
/// coarse cadences are supported.
/// </summary>
public enum WorkflowScheduleInterval
{
    Daily,
    Weekly,
    Monthly,
}

/// <summary>The allowed pull-request review states for the structured event-trigger predicate DSL.</summary>
public enum WorkflowTriggerReviewState
{
    Approved,
    ChangesRequested,
    Commented,
}

/// <summary>The supported string match modes for the <c>ref</c> predicate.</summary>
public enum WorkflowTriggerMatchMode
{
    Equals,
    Prefix,
}

/// <summary>A typed predicate in an event trigger's <c>if:</c> list. Exactly one property must be set.</summary>
public sealed record WorkflowTriggerPredicate
{
    public WorkflowTriggerLabelPredicate? HasLabel { get; init; }
    public WorkflowTriggerLabelPredicate? IsNotLabeledWith { get; init; }
    public WorkflowTriggerBaseBranchPredicate? BaseBranch { get; init; }
    public WorkflowTriggerReviewStatePredicate? ReviewState { get; init; }
    public WorkflowTriggerRefPredicate? Ref { get; init; }
    public WorkflowTriggerCategoryPredicate? Category { get; init; }
    public WorkflowTriggerCommentMatchesPredicate? CommentMatches { get; init; }
    public IReadOnlyList<WorkflowTriggerPredicate> Or { get; init; } = [];
    public WorkflowTriggerPredicate? Not { get; init; }
}

public sealed record WorkflowTriggerLabelPredicate
{
    public required string Label { get; init; }
}

public sealed record WorkflowTriggerBaseBranchPredicate
{
    public required string Branch { get; init; }
}

public sealed record WorkflowTriggerReviewStatePredicate
{
    public required WorkflowTriggerReviewState State { get; init; }
}

public sealed record WorkflowTriggerRefPredicate
{
    public required string Branch { get; init; }
    public required WorkflowTriggerMatchMode MatchMode { get; init; }
}

public sealed record WorkflowTriggerCategoryPredicate
{
    public required string Name { get; init; }
}

public sealed record WorkflowTriggerCommentMatchesPredicate
{
    public required string Pattern { get; init; }
}

/// <summary>
/// An optional, first-class automation trigger for a workflow (issue #53). When present, a schedule
/// trigger is evaluated by <c>WorkflowScheduleTriggerService</c> and an event trigger is evaluated by
/// <c>WorkflowEventTriggerService</c> — both start a run automatically (via a Ready backlog task bound
/// to this workflow) instead of requiring a manual/on-demand start. A workflow with no
/// <see cref="WorkflowDefinition.Trigger"/> (the default, null) is entirely unaffected and continues to
/// start only via the existing manual/backlog-pickup paths — fully backward compatible.
/// </summary>
public sealed record WorkflowTrigger
{
    public required WorkflowTriggerType Type { get; init; }

    /// <summary>Required for <see cref="WorkflowTriggerType.Schedule"/>: the cadence unit.</summary>
    public WorkflowScheduleInterval? Interval { get; init; }

    /// <summary>Required when <see cref="Interval"/> is <see cref="WorkflowScheduleInterval.Weekly"/>:
    /// the day of week the schedule fires on.</summary>
    public DayOfWeek? DayOfWeek { get; init; }

    /// <summary>Required when <see cref="Interval"/> is <see cref="WorkflowScheduleInterval.Monthly"/>:
    /// the day of month (1-28) the schedule fires on. Capped at 28 so every month has that day (no
    /// drift for shorter months, e.g. February).</summary>
    public int? DayOfMonth { get; init; }

    /// <summary>Required for Schedule triggers: the UTC time of day the schedule fires at.</summary>
    public TimeOnly? TimeOfDay { get; init; }

    /// <summary>
    /// Required for <see cref="WorkflowTriggerType.Event"/>: the event name this workflow starts a run
    /// for (e.g. "issue.opened"). NOTE: matching an inbound event to this name IS implemented (see
    /// <c>WorkflowEventTriggerService</c>), but no concrete external event SOURCE (e.g. a GitHub
    /// webhook receiver) is wired to call it yet — this is the trigger mechanism/interface only.
    /// </summary>
    public string? EventName { get; init; }

    /// <summary>
    /// Optional, structured event-filter predicate list. A plain array is implicitly ANDed; compound
    /// logic uses nested <c>or:</c> and <c>not:</c> wrapper predicates. Ignored for schedule triggers.
    /// </summary>
    public IReadOnlyList<WorkflowTriggerPredicate> If { get; init; } = [];
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
/// id/name, composed of typed nodes connected by edges. Validated before use; the source of a
/// project's effective run graph.
/// </summary>
public sealed record WorkflowDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string? Version { get; init; }

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

    /// <summary>
    /// Optional automation trigger (issue #53). Null (the default) means "no automation" — the
    /// workflow only starts on-demand via the existing manual/backlog-pickup paths, exactly as before
    /// this feature. Non-null means a schedule or event fires a run automatically.
    /// </summary>
    public WorkflowTrigger? Trigger { get; init; }
}
