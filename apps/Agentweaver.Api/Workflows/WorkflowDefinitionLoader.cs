using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Agentweaver.Api.Workflows;

/// <summary>
/// Parses and validates a single workflow YAML document into a <see cref="WorkflowDefinition"/>
/// (Feature 010, FR-001/002/003/004). All discovery, validation, and composition is server-side; a
/// client never recomputes any of it (Principles III, IV). Parsing never throws to the caller: a
/// malformed or schema-invalid document is returned as an <see cref="WorkflowLoadResult.Invalid"/>
/// with a specific, actionable, file-scoped message so the rest of the set keeps loading.
/// </summary>
public static class WorkflowDefinitionLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Parses+validates a YAML document. Always returns a result (never throws).</summary>
    public static WorkflowLoadResult Load(string yaml, string source, bool isBuiltIn = false)
    {
        WorkflowYamlDto? dto;
        try
        {
            dto = Deserializer.Deserialize<WorkflowYamlDto>(yaml);
        }
        catch (YamlException ex)
        {
            return WorkflowLoadResult.Invalid(source, $"{source}: malformed YAML — {ex.Message}");
        }

        if (dto is null)
            return WorkflowLoadResult.Invalid(source, $"{source}: empty or null workflow document.");

        if (!TryMapAndValidate(dto, source, isBuiltIn, out var definition, out var error))
            return WorkflowLoadResult.Invalid(source, error!);

        return WorkflowLoadResult.Valid(source, definition!, isBuiltIn);
    }

    private static bool TryMapAndValidate(
        WorkflowYamlDto dto, string source, bool isBuiltIn, out WorkflowDefinition? definition, out string? error)
    {
        definition = null;
        error = null;

        if (string.IsNullOrWhiteSpace(dto.Id))
            return Fail(source, "missing required field 'id'.", out error);
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Fail(source, "missing required field 'name'.", out error);

        // Trigger (FR-020/021/022/024).
        if (dto.Trigger is null || string.IsNullOrWhiteSpace(dto.Trigger.Type))
            return Fail(source, "missing required 'trigger.type' (manual | heartbeat | event).", out error);
        if (!TryParseTriggerType(dto.Trigger.Type, out var triggerType))
            return Fail(source, $"unsupported trigger type '{dto.Trigger.Type}' (expected manual | heartbeat | event).", out error);

        WorkflowEventType? eventType = null;
        if (triggerType == WorkflowTriggerType.Event)
        {
            if (string.IsNullOrWhiteSpace(dto.Trigger.Event))
                return Fail(source, "an event trigger requires 'trigger.event' (only 'task-added-to-ready' is supported).", out error);
            if (!TryParseEventType(dto.Trigger.Event, out var ev))
                return Fail(source, $"unsupported event '{dto.Trigger.Event}' (only 'task-added-to-ready' is supported this iteration).", out error);
            eventType = ev;
        }

        // Nodes.
        if (dto.Nodes is null || dto.Nodes.Count == 0)
            return Fail(source, "a workflow must declare at least one node.", out error);

        var nodes = new List<WorkflowNode>(dto.Nodes.Count);
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in dto.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id))
                return Fail(source, "a node is missing its required 'id'.", out error);
            if (!nodeIds.Add(n.Id))
                return Fail(source, $"duplicate node id '{n.Id}'.", out error);
            if (string.IsNullOrWhiteSpace(n.Type))
                return Fail(source, $"node '{n.Id}' is missing its required 'type'.", out error);
            if (!TryParseNodeType(n.Type, out var nodeType))
                return Fail(source, $"node '{n.Id}' has unknown type '{n.Type}'.", out error);

            nodes.Add(new WorkflowNode
            {
                Id = n.Id,
                Type = nodeType,
                Label = string.IsNullOrWhiteSpace(n.Label) ? n.Id : n.Label,
                Role = n.Role,
                Kind = n.Kind,
                GateKind = string.IsNullOrWhiteSpace(n.GateKind) ? null : n.GateKind,
                Agent = n.Agent,
                Prompt = n.Prompt,
                Charter = string.IsNullOrWhiteSpace(n.Charter) ? null : n.Charter,
                Target = n.Target,
                Steps = n.Steps is null ? [] : [.. n.Steps],
                Branches = n.Branches is null ? [] : [.. n.Branches],
            });
        }

        // Start node must exist (FR-019).
        if (string.IsNullOrWhiteSpace(dto.Start))
            return Fail(source, "missing required field 'start' (the entry node id).", out error);
        if (!nodeIds.Contains(dto.Start))
            return Fail(source, $"'start' references unknown node '{dto.Start}'.", out error);

        // Edges: from/to must reference existing nodes (no dangling edges, FR-004/019).
        var edges = new List<WorkflowEdge>();
        if (dto.Edges is not null)
        {
            foreach (var e in dto.Edges)
            {
                if (string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To))
                    return Fail(source, "an edge is missing its required 'from'/'to'.", out error);
                if (!nodeIds.Contains(e.From))
                    return Fail(source, $"edge references unknown source node '{e.From}'.", out error);
                if (!nodeIds.Contains(e.To))
                    return Fail(source, $"edge references unknown target node '{e.To}'.", out error);

                edges.Add(new WorkflowEdge { From = e.From, To = e.To, When = string.IsNullOrWhiteSpace(e.When) ? null : e.When });
            }
        }

        // Node-type-specific structural checks.
        foreach (var node in nodes)
        {
            switch (node.Type)
            {
                case WorkflowNodeType.Check:
                    // FR-016: a check must route on at least one verdict, and every declared verdict
                    // must have a matching outgoing edge.
                    var outgoing = edges.Where(x => string.Equals(x.From, node.Id, StringComparison.Ordinal)).ToList();
                    if (outgoing.Count == 0)
                        return Fail(source, $"check node '{node.Id}' has no outgoing edges to route verdicts.", out error);
                    if (node.Branches.Count == 0)
                        return Fail(source, $"check node '{node.Id}' must declare the verdicts ('branches') it routes on.", out error);
                    foreach (var verdict in node.Branches)
                    {
                        if (!outgoing.Any(x => string.Equals(x.When, verdict, StringComparison.Ordinal)))
                            return Fail(source, $"check node '{node.Id}' declares verdict '{verdict}' but has no outgoing edge for it.", out error);
                    }
                    break;

                case WorkflowNodeType.Serial:
                    foreach (var step in node.Steps)
                        if (!nodeIds.Contains(step))
                            return Fail(source, $"serial node '{node.Id}' references unknown step '{step}'.", out error);
                    break;

                case WorkflowNodeType.PeerReview:
                case WorkflowNodeType.FanIn:
                    if (node.Target is not null && !nodeIds.Contains(node.Target))
                        return Fail(source, $"node '{node.Id}' references unknown target '{node.Target}'.", out error);
                    break;
            }
        }

        // Feature 015 US1: the generalized RunWorkflowGraphBinder resolves a node's executor from its
        // TYPE (not a fixed id vocabulary), so fan_out/fan_in/serial/peer_review/coordinator_composed are
        // no longer rejected at load time. A node whose type cannot be wired to a runtime executor fails
        // closed at BUILD time with a node-scoped WorkflowBindException (the binder is the single guard),
        // rather than the loader pre-rejecting an entire authored workflow.

        // Parse optional explicit board stage definitions (FR-kanban-dynamic-columns).
        var stages = new List<WorkflowStageDefinition>();
        if (dto.Stages is not null)
        {
            foreach (var s in dto.Stages)
            {
                if (string.IsNullOrWhiteSpace(s.Id))
                    return Fail(source, "a stage is missing its required 'id'.", out error);
                if (string.IsNullOrWhiteSpace(s.Label))
                    return Fail(source, $"stage '{s.Id}' is missing its required 'label'.", out error);
                stages.Add(new WorkflowStageDefinition { Id = s.Id, Label = s.Label, Order = s.Order });
            }
        }

        definition = new WorkflowDefinition
        {
            Id = dto.Id!,
            Name = dto.Name!,
            Description = dto.Description,
            Trigger = new WorkflowTrigger { Type = triggerType, Event = eventType },
            Start = dto.Start!,
            Nodes = nodes,
            Edges = edges,
            Stages = stages,
        };
        return true;
    }

    private static bool Fail(string source, string message, out string? error)
    {
        error = $"{source}: {message}";
        return false;
    }

    private static string Normalize(string raw) =>
        raw.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

    private static bool TryParseTriggerType(string raw, out WorkflowTriggerType type)
    {
        switch (Normalize(raw))
        {
            case "manual": type = WorkflowTriggerType.Manual; return true;
            case "heartbeat":
            case "heartbeat_schedule":
            case "schedule": type = WorkflowTriggerType.Heartbeat; return true;
            case "event": type = WorkflowTriggerType.Event; return true;
            default: type = default; return false;
        }
    }

    private static bool TryParseEventType(string raw, out WorkflowEventType type)
    {
        switch (Normalize(raw))
        {
            case "task_added_to_ready": type = WorkflowEventType.TaskAddedToReady; return true;
            default: type = default; return false;
        }
    }

    private static bool TryParseNodeType(string raw, out WorkflowNodeType type)
    {
        switch (Normalize(raw))
        {
            case "prompt": type = WorkflowNodeType.Prompt; return true;
            case "peer_review": type = WorkflowNodeType.PeerReview; return true;
            case "check": type = WorkflowNodeType.Check; return true;
            case "fan_out": type = WorkflowNodeType.FanOut; return true;
            case "fan_in": type = WorkflowNodeType.FanIn; return true;
            case "coordinator_composed": type = WorkflowNodeType.CoordinatorComposed; return true;
            case "serial": type = WorkflowNodeType.Serial; return true;
            case "merge": type = WorkflowNodeType.Merge; return true;
            case "scribe": type = WorkflowNodeType.Scribe; return true;
            case "terminal": type = WorkflowNodeType.Terminal; return true;
            default: type = default; return false;
        }
    }
}

// ── YAML DTOs (snake_case via UnderscoredNamingConvention) ──────────────────────────────────────

/// <summary>Root YAML DTO for a workflow document. All fields nullable; required-ness is enforced by
/// <see cref="WorkflowDefinitionLoader"/> with file-scoped messages.</summary>
internal sealed class WorkflowYamlDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public TriggerYamlDto? Trigger { get; set; }
    public string? Start { get; set; }
    public List<NodeYamlDto>? Nodes { get; set; }
    public List<EdgeYamlDto>? Edges { get; set; }
    public List<StageYamlDto>? Stages { get; set; }
}

internal sealed class TriggerYamlDto
{
    public string? Type { get; set; }
    public string? Event { get; set; }
}

internal sealed class NodeYamlDto
{
    public string? Id { get; set; }
    public string? Type { get; set; }
    public string? Label { get; set; }
    public string? Role { get; set; }
    public string? Kind { get; set; }
    public string? GateKind { get; set; }
    public string? Agent { get; set; }
    public string? Prompt { get; set; }
    public string? Charter { get; set; }
    public string? Target { get; set; }
    public List<string>? Steps { get; set; }
    public List<string>? Branches { get; set; }
}

internal sealed class EdgeYamlDto
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? When { get; set; }
}

internal sealed class StageYamlDto
{
    public string? Id { get; set; }
    public string? Label { get; set; }
    public int Order { get; set; }
}
