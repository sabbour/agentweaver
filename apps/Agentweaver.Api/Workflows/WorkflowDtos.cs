using System.Text.Json.Serialization;

namespace Agentweaver.Api.Workflows;

/// <summary>Trigger shape in API responses (snake_case).</summary>
public sealed record WorkflowTriggerDto
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("event")] public string? Event { get; init; }
}

/// <summary>A workflow in a list response: identity, trigger, and validation status (FR-002/039/040).</summary>
public sealed record WorkflowSummaryDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("trigger")] public WorkflowTriggerDto? Trigger { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("valid")] public required bool Valid { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_default")] public required bool IsDefault { get; init; }
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
    [JsonPropertyName("trigger")] public required WorkflowTriggerDto Trigger { get; init; }
    [JsonPropertyName("start")] public required string Start { get; init; }
    [JsonPropertyName("source")] public required string Source { get; init; }
    [JsonPropertyName("is_built_in")] public required bool IsBuiltIn { get; init; }
    [JsonPropertyName("is_default")] public required bool IsDefault { get; init; }
    [JsonPropertyName("nodes")] public required IReadOnlyList<WorkflowNodeDto> Nodes { get; init; }
    [JsonPropertyName("edges")] public required IReadOnlyList<WorkflowEdgeDto> Edges { get; init; }
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
}

/// <summary>Response body for a generated workflow draft (US10). The YAML is unsaved — the client opens
/// it in the editor for review before any save. <c>wasCorrected</c> reports whether the single
/// correction pass (FR-060) was needed.</summary>
public sealed record GenerateWorkflowResponse
{
    [JsonPropertyName("yaml")] public required string Yaml { get; init; }
    [JsonPropertyName("workflowId")] public required string WorkflowId { get; init; }
    [JsonPropertyName("wasCorrected")] public required bool WasCorrected { get; init; }
}

/// <summary>Maps the workflow domain model to API DTOs (server-side only, Principles III/IV).</summary>
public static class WorkflowDtoMapper
{
    public static string TriggerTypeToApi(WorkflowTriggerType t) => t switch
    {
        WorkflowTriggerType.Manual => "manual",
        WorkflowTriggerType.Heartbeat => "heartbeat",
        WorkflowTriggerType.Event => "event",
        _ => "manual",
    };

    public static string EventTypeToApi(WorkflowEventType e) => e switch
    {
        WorkflowEventType.TaskAddedToReady => "task-added-to-ready",
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    public static string NodeTypeToApi(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Prompt => "prompt",
        WorkflowNodeType.PeerReview => "peer-review",
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

    public static WorkflowTriggerDto ToDto(WorkflowTrigger trigger) => new()
    {
        Type = TriggerTypeToApi(trigger.Type),
        Event = trigger.Event is null ? null : EventTypeToApi(trigger.Event.Value),
    };

    public static WorkflowSummaryDto ToSummary(WorkflowLoadResult result, string effectiveDefaultId)
    {
        var def = result.Definition;
        return new WorkflowSummaryDto
        {
            Id = def?.Id,
            Name = def?.Name,
            Description = def?.Description,
            Trigger = def is null ? null : ToDto(def.Trigger),
            Source = result.Source,
            Valid = result.IsValid,
            Error = result.Error,
            IsBuiltIn = result.IsBuiltIn,
            IsDefault = def is not null && string.Equals(def.Id, effectiveDefaultId, StringComparison.Ordinal),
        };
    }

    private static string NodeRoleForGraph(WorkflowNodeType t) => t switch
    {
        WorkflowNodeType.Check              => "rai",
        WorkflowNodeType.PeerReview         => "review",
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
            Trigger = ToDto(def.Trigger),
            Start = def.Start,
            Source = result.Source,
            IsBuiltIn = result.IsBuiltIn,
            IsDefault = string.Equals(def.Id, effectiveDefaultId, StringComparison.Ordinal),
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
        };
    }
}
