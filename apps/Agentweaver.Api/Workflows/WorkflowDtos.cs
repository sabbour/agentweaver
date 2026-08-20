using System.Text.Json.Serialization;

namespace Agentweaver.Api.Workflows;

/// <summary>A workflow in a list response: identity and validation status (FR-002/039/040).</summary>
public sealed record WorkflowSummaryDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_default")] public required bool IsDefault { get; init; }
    /// <summary>Legacy first-trigger alias retained for API compatibility.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    /// <summary>All automation triggers in declaration order.</summary>
    [JsonPropertyName("triggers")] public IReadOnlyList<WorkflowTriggerDto> Triggers { get; init; } = [];
}

/// <summary>A workflow's automation trigger in an API response (issue #53).</summary>
public sealed record WorkflowTriggerDto
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("interval")] public string? Interval { get; init; }
    [JsonPropertyName("day_of_week")] public string? DayOfWeek { get; init; }
    [JsonPropertyName("day_of_month")] public int? DayOfMonth { get; init; }
    [JsonPropertyName("time_of_day")] public string? TimeOfDay { get; init; }
    [JsonPropertyName("event_name")] public string? EventName { get; init; }
    [JsonPropertyName("if")] public IReadOnlyList<WorkflowTriggerPredicateDto>? If { get; init; }
}

public sealed record WorkflowTriggerPredicateDto
{
    [JsonPropertyName("hasLabel")] public WorkflowTriggerLabelPredicateDto? HasLabel { get; init; }
    [JsonPropertyName("isNotLabeledWith")] public WorkflowTriggerLabelPredicateDto? IsNotLabeledWith { get; init; }
    [JsonPropertyName("baseBranch")] public WorkflowTriggerBaseBranchPredicateDto? BaseBranch { get; init; }
    [JsonPropertyName("reviewState")] public WorkflowTriggerReviewStatePredicateDto? ReviewState { get; init; }
    [JsonPropertyName("ref")] public WorkflowTriggerRefPredicateDto? Ref { get; init; }
    [JsonPropertyName("category")] public WorkflowTriggerCategoryPredicateDto? Category { get; init; }
    [JsonPropertyName("commentMatches")] public WorkflowTriggerCommentMatchesPredicateDto? CommentMatches { get; init; }
    [JsonPropertyName("or")] public IReadOnlyList<WorkflowTriggerPredicateDto>? Or { get; init; }
    [JsonPropertyName("not")] public WorkflowTriggerPredicateDto? Not { get; init; }
}

public sealed record WorkflowTriggerLabelPredicateDto
{
    [JsonPropertyName("label")] public string? Label { get; init; }
}

public sealed record WorkflowTriggerBaseBranchPredicateDto
{
    [JsonPropertyName("branch")] public string? Branch { get; init; }
}

public sealed record WorkflowTriggerReviewStatePredicateDto
{
    [JsonPropertyName("state")] public string? State { get; init; }
}

public sealed record WorkflowTriggerRefPredicateDto
{
    [JsonPropertyName("branch")] public string? Branch { get; init; }
    [JsonPropertyName("matchMode")] public string? MatchMode { get; init; }
}

public sealed record WorkflowTriggerCategoryPredicateDto
{
    [JsonPropertyName("name")] public string? Name { get; init; }
}

public sealed record WorkflowTriggerCommentMatchesPredicateDto
{
    [JsonPropertyName("pattern")] public string? Pattern { get; init; }
}

public sealed record WorkflowTriggerConfigResponse
{
    /// <summary>Legacy first-trigger alias retained for API compatibility.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    [JsonPropertyName("triggers")] public IReadOnlyList<WorkflowTriggerDto> Triggers { get; init; } = [];
}

public sealed record WorkflowTriggerPatchRequest
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("interval")] public string? Interval { get; init; }
    [JsonPropertyName("day_of_week")] public string? DayOfWeek { get; init; }
    [JsonPropertyName("day_of_month")] public int? DayOfMonth { get; init; }
    [JsonPropertyName("time_of_day")] public string? TimeOfDay { get; init; }
    [JsonPropertyName("event_name")] public string? EventName { get; init; }
    [JsonPropertyName("if")] public IReadOnlyList<WorkflowTriggerPredicateDto>? If { get; init; }
}

/// <summary>Response body for GET/POST the project's workflows list.</summary>
public sealed record WorkflowListResponse
{
    [JsonPropertyName("default_workflow_id")] public required string DefaultWorkflowId { get; init; }
    [JsonPropertyName("workflows")] public required IReadOnlyList<WorkflowSummaryDto> Workflows { get; init; }
}

/// <summary>Request body to set (or clear) a workflow selection — the project default (FR-041) or a
/// per-task override (FR-042). A null/omitted <c>workflow_id</c> clears the selection.</summary>
public sealed record SetWorkflowSelectionRequest
{
    [JsonPropertyName("workflow_id")] public string? WorkflowId { get; init; }
}

/// <summary>Response body after setting a per-task workflow override (FR-042).</summary>
public sealed record WorkflowOverrideResponse
{
    [JsonPropertyName("task_id")] public required string TaskId { get; init; }
    [JsonPropertyName("workflow_override_id")] public string? WorkflowOverrideId { get; init; }
}

/// <summary>A node in a workflow detail response.</summary>
public sealed record WorkflowNodeDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("label")] public required string Label { get; init; }
    [JsonPropertyName("role")] public string? Role { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("gate_kind")] public string? GateKind { get; init; }
    [JsonPropertyName("agent")] public string? Agent { get; init; }
    [JsonPropertyName("prompt")] public string? Prompt { get; init; }
    [JsonPropertyName("charter")] public string? Charter { get; init; }
    [JsonPropertyName("target")] public string? Target { get; init; }
    [JsonPropertyName("steps")] public IReadOnlyList<string>? Steps { get; init; }
    [JsonPropertyName("branches")] public IReadOnlyList<string>? Branches { get; init; }
}

/// <summary>An edge in a workflow detail response.</summary>
public sealed record WorkflowEdgeDto
{
    [JsonPropertyName("from")] public required string From { get; init; }
    [JsonPropertyName("to")] public required string To { get; init; }
    [JsonPropertyName("when")] public string? When { get; init; }
}

/// <summary>Full definition for GET a single workflow.</summary>
public sealed record WorkflowDetailDto
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("start")] public required string Start { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_default")] public required bool IsDefault { get; init; }
    [JsonPropertyName("warnings")] public IReadOnlyList<string> Warnings { get; init; } = [];
    [JsonPropertyName("nodes")] public required IReadOnlyList<WorkflowNodeDto> Nodes { get; init; }
    [JsonPropertyName("edges")] public required IReadOnlyList<WorkflowEdgeDto> Edges { get; init; }
    /// <summary>Legacy first-trigger alias retained for API compatibility.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    [JsonPropertyName("triggers")] public IReadOnlyList<WorkflowTriggerDto> Triggers { get; init; } = [];
}

/// <summary>Request body to save (create or update) a workflow definition by YAML (US7).</summary>
public sealed record SaveWorkflowRequest
{
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
}

/// <summary>Response body for GET raw YAML content of a project workflow file (US7).</summary>
public sealed record WorkflowYamlResponse
{
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
}

/// <summary>A node in a workflow graph descriptor (US6). role/node_type match the GraphNode shape
/// consumed by WorkflowGraphPanel on the frontend; kind is always "planned".</summary>
public sealed record WorkflowGraphNodeDto
{
    [JsonPropertyName("id")]        public required string Id       { get; init; }
    [JsonPropertyName("label")]     public required string Label    { get; init; }
    [JsonPropertyName("role")]      public required string Role     { get; init; }
    [JsonPropertyName("kind")]      public required string Kind     { get; init; }
    [JsonPropertyName("node_type")] public string? NodeType { get; init; }
}

/// <summary>An edge in a workflow graph descriptor (US6). cardinality is always "direct";
/// loopback is true when the edge forms a back-edge in topological order (cycle).</summary>
public sealed record WorkflowGraphEdgeDto
{
    [JsonPropertyName("from")]        public required string From        { get; init; }
    [JsonPropertyName("to")]          public required string To          { get; init; }
    [JsonPropertyName("cardinality")] public required string Cardinality { get; init; }
    [JsonPropertyName("loopback")]    public required bool   Loopback    { get; init; }
    [JsonPropertyName("label")]       public string? Label { get; init; }
}

/// <summary>Response body for GET workflow graph (US6). Matches the GraphDescriptor shape
/// consumed by the WorkflowGraphPanel renderer on the frontend.</summary>
public sealed record WorkflowGraphDto
{
    [JsonPropertyName("graph_id")]      public required string GraphId     { get; init; }
    [JsonPropertyName("variant")]       public required string Variant     { get; init; }
    [JsonPropertyName("start_node_id")] public required string StartNodeId { get; init; }
    [JsonPropertyName("nodes")]         public required IReadOnlyList<WorkflowGraphNodeDto> Nodes { get; init; }
    [JsonPropertyName("edges")]         public required IReadOnlyList<WorkflowGraphEdgeDto> Edges { get; init; }
}

/// <summary>Request body to generate a workflow draft from a natural-language description (US10).</summary>
public sealed record GenerateWorkflowRequest
{
    [JsonPropertyName("description")] public required string Description { get; init; }
    /// <summary>Optional saved/built-in workflow id to edit instead of creating from scratch.</summary>
    [JsonPropertyName("base_workflow_id")] public string? BaseWorkflowId { get; init; }
    /// <summary>Optional current draft YAML for iterative edits before the draft is saved.</summary>
    [JsonPropertyName("base_yaml")] public string? BaseYaml { get; init; }
}

/// <summary>Response body for a generated workflow draft (US10). The YAML is unsaved — the client opens
/// it in the editor for review before any save. <c>wasCorrected</c> reports whether the single
/// correction pass (FR-060) was needed.</summary>
public sealed record GenerateWorkflowResponse
{
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
    [JsonPropertyName("workflowId")] public required string WorkflowId { get; init; }
    [JsonPropertyName("wasCorrected")] public required bool WasCorrected { get; init; }
    [JsonPropertyName("mode")] public string Mode { get; init; } = "create";
    [JsonPropertyName("base_workflow_id")] public string? BaseWorkflowId { get; init; }
    [JsonPropertyName("base_workflow_is_built_in")] public bool BaseWorkflowIsBuiltIn { get; init; }
}

/// <summary>Maps the workflow domain model to API DTOs (server-side only, Principles III/IV).</summary>
public static class WorkflowDtoMapper
{
    public static string NodeTypeToApi(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Prompt => "prompt",
        WorkflowNodeType.PeerReview => "peer-review",
        WorkflowNodeType.BuildTest => "build-test",
        WorkflowNodeType.OpenPullRequest => "open-pull-request",
        WorkflowNodeType.Publish => "publish",
        WorkflowNodeType.Check => "check",
        WorkflowNodeType.FanOut => "fan-out",
        WorkflowNodeType.FanIn => "fan-in",
        WorkflowNodeType.CoordinatorComposed => "coordinator-composed",
        WorkflowNodeType.Serial => "serial",
        WorkflowNodeType.Merge => "merge",
        WorkflowNodeType.Scribe => "scribe",
        WorkflowNodeType.Terminal => "terminal",
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    public static WorkflowSummaryDto ToSummary(WorkflowLoadResult result, string effectiveDefaultId)
    {
        var def = result.Definition;
        return new WorkflowSummaryDto
        {
            Id = def?.Id,
            Name = def?.Name,
            Description = def?.Description,
            Source = result.Source,
            Valid = result.IsValid,
            Error = result.Error,
            Warnings = result.Warnings,
            IsBuiltIn = result.IsBuiltIn,
            IsDefault = def is not null && string.Equals(def.Id, effectiveDefaultId, StringComparison.Ordinal),
            Trigger = def?.Triggers.FirstOrDefault() is { } trigger ? ToTriggerDto(trigger) : null,
            Triggers = def?.Triggers.Select(ToTriggerDto).ToList() ?? [],
        };
    }

    public static WorkflowTriggerDto ToTriggerDto(WorkflowTrigger trigger) => new()
    {
        Type = trigger.Type switch
        {
            WorkflowTriggerType.Schedule => "schedule",
            WorkflowTriggerType.Event => "event",
            _ => throw new ArgumentOutOfRangeException(nameof(trigger)),
        },
        Interval = trigger.Interval switch
        {
            WorkflowScheduleInterval.Daily => "daily",
            WorkflowScheduleInterval.Weekly => "weekly",
            WorkflowScheduleInterval.Monthly => "monthly",
            _ => null,
        },
        DayOfWeek = trigger.DayOfWeek?.ToString().ToLowerInvariant(),
        DayOfMonth = trigger.DayOfMonth,
        TimeOfDay = trigger.TimeOfDay?.ToString("HH:mm"),
        EventName = trigger.EventName,
        If = trigger.If.Count == 0 ? null : trigger.If.Select(ToTriggerPredicateDto).ToList(),
    };

    public static WorkflowTriggerConfigResponse ToTriggerConfigResponse(IReadOnlyList<WorkflowTrigger> triggers)
    {
        var legacyTrigger = triggers.FirstOrDefault();
        return new WorkflowTriggerConfigResponse
        {
            Trigger = legacyTrigger is null ? null : ToTriggerDto(legacyTrigger),
            Triggers = triggers.Select(ToTriggerDto).ToList(),
        };
    }

    internal static TriggerYamlDto ToTriggerYamlDto(WorkflowTriggerDto trigger) => new()
    {
        Type = trigger.Type,
        Interval = trigger.Interval,
        DayOfWeek = trigger.DayOfWeek,
        DayOfMonth = trigger.DayOfMonth,
        TimeOfDay = trigger.TimeOfDay,
        EventName = trigger.EventName,
        If = trigger.If?.Select(ToTriggerPredicateYamlDto).ToList(),
    };

    internal static WorkflowTriggerDto MergeTriggerPatch(WorkflowTriggerDto? current, WorkflowTriggerPatchRequest patch)
    {
        var type = patch.Type ?? current?.Type;
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentException("Trigger type is required.", nameof(patch));

        var normalizedType = type.Trim().ToLowerInvariant();
        var ifPredicates = normalizedType == "schedule"
            ? []
            : patch.If ?? current?.If;

        return new WorkflowTriggerDto
        {
            Type = type,
            Interval = patch.Interval ?? current?.Interval,
            DayOfWeek = patch.DayOfWeek ?? current?.DayOfWeek,
            DayOfMonth = patch.DayOfMonth ?? current?.DayOfMonth,
            TimeOfDay = patch.TimeOfDay ?? current?.TimeOfDay,
            EventName = patch.EventName ?? current?.EventName,
            If = ifPredicates,
        };
    }

    private static WorkflowTriggerPredicateDto ToTriggerPredicateDto(WorkflowTriggerPredicate predicate) => new()
    {
        HasLabel = predicate.HasLabel is null ? null : new WorkflowTriggerLabelPredicateDto { Label = predicate.HasLabel.Label },
        IsNotLabeledWith = predicate.IsNotLabeledWith is null ? null : new WorkflowTriggerLabelPredicateDto { Label = predicate.IsNotLabeledWith.Label },
        BaseBranch = predicate.BaseBranch is null ? null : new WorkflowTriggerBaseBranchPredicateDto { Branch = predicate.BaseBranch.Branch },
        ReviewState = predicate.ReviewState is null ? null : new WorkflowTriggerReviewStatePredicateDto { State = ReviewStateToApi(predicate.ReviewState.State) },
        Ref = predicate.Ref is null ? null : new WorkflowTriggerRefPredicateDto
        {
            Branch = predicate.Ref.Branch,
            MatchMode = MatchModeToApi(predicate.Ref.MatchMode),
        },
        Category = predicate.Category is null ? null : new WorkflowTriggerCategoryPredicateDto { Name = predicate.Category.Name },
        CommentMatches = predicate.CommentMatches is null ? null : new WorkflowTriggerCommentMatchesPredicateDto { Pattern = predicate.CommentMatches.Pattern },
        Or = predicate.Or.Count == 0 ? null : predicate.Or.Select(ToTriggerPredicateDto).ToList(),
        Not = predicate.Not is null ? null : ToTriggerPredicateDto(predicate.Not),
    };

    private static TriggerPredicateYamlDto ToTriggerPredicateYamlDto(WorkflowTriggerPredicateDto predicate) => new()
    {
        HasLabel = predicate.HasLabel is null ? null : new TriggerLabelPredicateYamlDto { Label = predicate.HasLabel.Label },
        IsNotLabeledWith = predicate.IsNotLabeledWith is null ? null : new TriggerLabelPredicateYamlDto { Label = predicate.IsNotLabeledWith.Label },
        BaseBranch = predicate.BaseBranch is null ? null : new TriggerBaseBranchPredicateYamlDto { Branch = predicate.BaseBranch.Branch },
        ReviewState = predicate.ReviewState is null ? null : new TriggerReviewStatePredicateYamlDto { State = predicate.ReviewState.State },
        Ref = predicate.Ref is null ? null : new TriggerRefPredicateYamlDto
        {
            Branch = predicate.Ref.Branch,
            MatchMode = predicate.Ref.MatchMode,
        },
        Category = predicate.Category is null ? null : new TriggerCategoryPredicateYamlDto { Name = predicate.Category.Name },
        CommentMatches = predicate.CommentMatches is null ? null : new TriggerCommentMatchesPredicateYamlDto { Pattern = predicate.CommentMatches.Pattern },
        Or = predicate.Or?.Select(ToTriggerPredicateYamlDto).ToList(),
        Not = predicate.Not is null ? null : ToTriggerPredicateYamlDto(predicate.Not),
    };

    private static string ReviewStateToApi(WorkflowTriggerReviewState state) => state switch
    {
        WorkflowTriggerReviewState.Approved => "approved",
        WorkflowTriggerReviewState.ChangesRequested => "changes_requested",
        WorkflowTriggerReviewState.Commented => "commented",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string MatchModeToApi(WorkflowTriggerMatchMode mode) => mode switch
    {
        WorkflowTriggerMatchMode.Equals => "equals",
        WorkflowTriggerMatchMode.Prefix => "prefix",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static string NodeRoleForGraph(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Check              => "rai",
        WorkflowNodeType.PeerReview         => "review",
        WorkflowNodeType.BuildTest          => "review",
        WorkflowNodeType.OpenPullRequest    => "action",
        WorkflowNodeType.Publish            => "agent",
        WorkflowNodeType.Merge              => "merge",
        WorkflowNodeType.Scribe             => "scribe",
        WorkflowNodeType.CoordinatorComposed => "coordinator",
        WorkflowNodeType.Terminal           => "assembly",
        _                                   => "agent",
    };

    private static string NodeTypeForGraph(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Terminal   => "terminal",
        WorkflowNodeType.Check      => "gate",
        WorkflowNodeType.PeerReview => "gate",
        WorkflowNodeType.BuildTest  => "gate",
        WorkflowNodeType.OpenPullRequest => "action",
        WorkflowNodeType.Publish    => "action",
        WorkflowNodeType.FanOut     => "action",
        WorkflowNodeType.FanIn      => "action",
        WorkflowNodeType.Merge      => "action",
        WorkflowNodeType.Scribe     => "action",
        _                           => "agent",
    };

    /// <summary>Detects back-edges (loopbacks) via DFS so dagre layout can skip them.</summary>
    private static HashSet<(string From, string To)> DetectLoopbacks(WorkflowDefinition def)
    {
        var adjacency = def.Edges
            .GroupBy(e => e.From)
            .ToDictionary(g => g.Key, g => g.Select(e => e.To).ToList());

        var visited  = new HashSet<string>(StringComparer.Ordinal);
        var inStack  = new HashSet<string>(StringComparer.Ordinal);
        var loopbacks = new HashSet<(string, string)>();

        void Dfs(string node)
        {
            visited.Add(node);
            inStack.Add(node);
            foreach (var neighbor in adjacency.GetValueOrDefault(node, []))
            {
                if (inStack.Contains(neighbor))
                    loopbacks.Add((node, neighbor));
                else if (!visited.Contains(neighbor))
                    Dfs(neighbor);
            }
            inStack.Remove(node);
        }

        foreach (var n in def.Nodes)
            if (!visited.Contains(n.Id))
                Dfs(n.Id);

        return loopbacks;
    }

    public static WorkflowGraphDto ToGraph(WorkflowDefinition def)
    {
        var loopbacks = DetectLoopbacks(def);
        return new WorkflowGraphDto
        {
            GraphId     = def.Id,
            Variant     = "workflow",
            StartNodeId = def.Start,
            Nodes = def.Nodes.Select(n => new WorkflowGraphNodeDto
            {
                Id       = n.Id,
                Label    = n.Label,
                Role     = NodeRoleForGraph(n.Type),
                Kind     = "planned",
                NodeType = NodeTypeForGraph(n.Type),
            }).ToList(),
            Edges = def.Edges.Select(e => new WorkflowGraphEdgeDto
            {
                From        = e.From,
                To          = e.To,
                Cardinality = "direct",
                Loopback    = loopbacks.Contains((e.From, e.To)),
                Label       = e.When,
            }).ToList(),
        };
    }

    public static WorkflowDetailDto ToDetail(WorkflowLoadResult result, string effectiveDefaultId)
    {
        var def = result.Definition!;
        return new WorkflowDetailDto
        {
            Id = def.Id,
            Name = def.Name,
            Description = def.Description,
            Start = def.Start,
            Source = result.Source,
            IsBuiltIn = result.IsBuiltIn,
            IsDefault = string.Equals(def.Id, effectiveDefaultId, StringComparison.Ordinal),
            Warnings = result.Warnings,
            Nodes = def.Nodes.Select(n => new WorkflowNodeDto
            {
                Id = n.Id,
                Type = NodeTypeToApi(n.Type),
                Label = n.Label,
                Role = n.Role,
                Kind = n.Kind,
                GateKind = n.GateKind,
                Agent = n.Agent,
                Prompt = n.Prompt,
                Charter = n.Charter,
                Target = n.Target,
                Steps = n.Steps.Count == 0 ? null : n.Steps,
                Branches = n.Branches.Count == 0 ? null : n.Branches,
            }).ToList(),
            Edges = def.Edges.Select(e => new WorkflowEdgeDto
            {
                From = e.From,
                To = e.To,
                When = e.When,
            }).ToList(),
            Trigger = def.Triggers.FirstOrDefault() is { } trigger ? ToTriggerDto(trigger) : null,
            Triggers = def.Triggers.Select(ToTriggerDto).ToList(),
        };
    }
}
