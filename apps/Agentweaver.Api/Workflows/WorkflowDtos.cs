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
    /// <summary>The workflow's automation trigger (issue #53), or null when it only starts on-demand.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
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
    [JsonPropertyName("if")] public IReadOnlyList<WorkflowEventPredicateDto>? If { get; init; }
}

public sealed record WorkflowEventPredicateDto
{
    [JsonPropertyName("hasLabel")] public WorkflowEventLabelPredicateDto? HasLabel { get; init; }
    [JsonPropertyName("isNotLabeledWith")] public WorkflowEventLabelPredicateDto? IsNotLabeledWith { get; init; }
    [JsonPropertyName("baseBranch")] public WorkflowEventExactMatchDto? BaseBranch { get; init; }
    [JsonPropertyName("reviewState")] public WorkflowEventStatePredicateDto? ReviewState { get; init; }
    [JsonPropertyName("ref")] public WorkflowEventRefPredicateDto? Ref { get; init; }
    [JsonPropertyName("category")] public WorkflowEventNamePredicateDto? Category { get; init; }
    [JsonPropertyName("commentMatches")] public WorkflowEventPatternPredicateDto? CommentMatches { get; init; }
    [JsonPropertyName("or")] public IReadOnlyList<WorkflowEventPredicateDto>? Or { get; init; }
    [JsonPropertyName("not")] public IReadOnlyList<WorkflowEventPredicateDto>? Not { get; init; }
}

public sealed record WorkflowEventLabelPredicateDto
{
    [JsonPropertyName("label")] public required string Label { get; init; }
}

public sealed record WorkflowEventExactMatchDto
{
    [JsonPropertyName("equals")] public required string Exact { get; init; }
}

public sealed record WorkflowEventStatePredicateDto
{
    [JsonPropertyName("state")] public required string State { get; init; }
}

public sealed record WorkflowEventRefPredicateDto
{
    [JsonPropertyName("equals")] public string? Exact { get; init; }
    [JsonPropertyName("prefix")] public string? Prefix { get; init; }
}

public sealed record WorkflowEventNamePredicateDto
{
    [JsonPropertyName("name")] public required string Name { get; init; }
}

public sealed record WorkflowEventPatternPredicateDto
{
    [JsonPropertyName("pattern")] public required string Pattern { get; init; }
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
    /// <summary>The workflow's automation trigger (issue #53), or null when it only starts on-demand.</summary>
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
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
            Trigger = def?.Trigger is null ? null : ToTriggerDto(def.Trigger),
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
        If = trigger.Conditions.Count == 0 ? null : trigger.Conditions.Select(ToEventPredicateDto).ToList(),
    };

    private static WorkflowEventPredicateDto ToEventPredicateDto(WorkflowEventPredicate predicate) => predicate.Type switch
    {
        WorkflowEventPredicateType.HasLabel => new WorkflowEventPredicateDto
        {
            HasLabel = new WorkflowEventLabelPredicateDto { Label = predicate.Label! },
        },
        WorkflowEventPredicateType.IsNotLabeledWith => new WorkflowEventPredicateDto
        {
            IsNotLabeledWith = new WorkflowEventLabelPredicateDto { Label = predicate.Label! },
        },
        WorkflowEventPredicateType.BaseBranch => new WorkflowEventPredicateDto
        {
            BaseBranch = new WorkflowEventExactMatchDto { Exact = predicate.Exact! },
        },
        WorkflowEventPredicateType.ReviewState => new WorkflowEventPredicateDto
        {
            ReviewState = new WorkflowEventStatePredicateDto { State = predicate.State! },
        },
        WorkflowEventPredicateType.Ref => new WorkflowEventPredicateDto
        {
            Ref = new WorkflowEventRefPredicateDto { Exact = predicate.Exact, Prefix = predicate.Prefix },
        },
        WorkflowEventPredicateType.Category => new WorkflowEventPredicateDto
        {
            Category = new WorkflowEventNamePredicateDto { Name = predicate.Name! },
        },
        WorkflowEventPredicateType.CommentMatches => new WorkflowEventPredicateDto
        {
            CommentMatches = new WorkflowEventPatternPredicateDto { Pattern = predicate.Pattern! },
        },
        WorkflowEventPredicateType.Or => new WorkflowEventPredicateDto
        {
            Or = predicate.Predicates.Select(ToEventPredicateDto).ToList(),
        },
        WorkflowEventPredicateType.Not => new WorkflowEventPredicateDto
        {
            Not = predicate.Predicates.Select(ToEventPredicateDto).ToList(),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(predicate), predicate.Type, null),
    };

    private static string NodeRoleForGraph(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Check              => "rai",
        WorkflowNodeType.PeerReview         => "review",
        WorkflowNodeType.BuildTest          => "review",
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
            Trigger = def.Trigger is null ? null : ToTriggerDto(def.Trigger),
        };
    }
}
