using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Agentweaver.Squad.Catalog;

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
        if (yaml.Length > 262_144)
            return WorkflowLoadResult.Invalid(source, $"{source}: workflow resource exceeds the 262144 character limit.");

        YamlMappingNode? rootNode = null;
        WorkflowYamlDto? dto;
        try
        {
            rootNode = TryParseRootMapping(yaml);
            dto = Deserializer.Deserialize<WorkflowYamlDto>(yaml);
        }
        catch (YamlException ex)
        {
            return WorkflowLoadResult.Invalid(source, $"{source}: malformed YAML — {ex.Message}");
        }

        if (dto is null)
            return WorkflowLoadResult.Invalid(source, $"{source}: empty or null workflow document.");

        if (!TryMapAndValidate(dto, rootNode, source, isBuiltIn, out var definition, out var error, out var loadWarnings))
            return WorkflowLoadResult.Invalid(source, error!);

        return WorkflowLoadResult.Valid(source, definition!, isBuiltIn, loadWarnings);
    }

    private static bool TryMapAndValidate(
        WorkflowYamlDto dto,
        YamlMappingNode? rootNode,
        string source,
        bool isBuiltIn,
        out WorkflowDefinition? definition,
        out string? error,
        out IReadOnlyList<string> warnings)
    {
        definition = null;
        error = null;
        var collectedWarnings = new List<string>();
        warnings = collectedWarnings;

        if (string.IsNullOrWhiteSpace(dto.Id))
            return Fail(source, "missing required field 'id'.", out error);
        if (isBuiltIn && CatalogIdentifier.ValidationError(dto.Id, "workflow id") is { } workflowIdError)
            return Fail(source, workflowIdError, out error);
        if (string.IsNullOrWhiteSpace(dto.Name))
            return Fail(source, "missing required field 'name'.", out error);

        // Nodes.
        if (dto.Nodes is null || dto.Nodes.Count == 0)
            return Fail(source, "a workflow must declare at least one node.", out error);
        if (dto.Nodes.Count > 128)
            return Fail(source, "a workflow cannot declare more than 128 nodes.", out error);

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
            if (n.Prompt?.Length > 16_384)
                return Fail(source, $"node '{n.Id}' prompt exceeds the 16384 character limit.", out error);
            if (n.Charter?.Length > 8_192)
                return Fail(source, $"node '{n.Id}' charter exceeds the 8192 character limit.", out error);

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
                Branches = n.Branches is null ? [] : [.. n.Branches
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Select(b => b.Trim().ToLowerInvariant())],
                PrTitle = string.IsNullOrWhiteSpace(n.Title) ? null : n.Title,
                PrBody = string.IsNullOrWhiteSpace(n.Body) ? null : n.Body,
                PrBase = string.IsNullOrWhiteSpace(n.Base) ? null : n.Base.Trim(),
                PrHead = string.IsNullOrWhiteSpace(n.Head) ? null : n.Head.Trim(),
                PrDraft = n.Draft,
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
            if (dto.Edges.Count > 512)
                return Fail(source, "a workflow cannot declare more than 512 edges.", out error);
            foreach (var e in dto.Edges)
            {
                if (string.IsNullOrWhiteSpace(e.From) || string.IsNullOrWhiteSpace(e.To))
                    return Fail(source, "an edge is missing its required 'from'/'to'.", out error);
                if (!nodeIds.Contains(e.From))
                    return Fail(source, $"edge references unknown source node '{e.From}'.", out error);
                if (!nodeIds.Contains(e.To))
                    return Fail(source, $"edge references unknown target node '{e.To}'.", out error);

                edges.Add(new WorkflowEdge
                {
                    From = e.From,
                    To = e.To,
                    When = string.IsNullOrWhiteSpace(e.When) ? null : e.When.Trim().ToLowerInvariant(),
                });
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
                case WorkflowNodeType.BuildTest:
                    if (!string.IsNullOrWhiteSpace(node.Target))
                        collectedWarnings.Add(
                            $"{source}: {node.Type.ToString().ToLowerInvariant()} node '{node.Id}' declares target '{node.Target}', but the runtime currently ignores target.");
                    goto case WorkflowNodeType.FanIn;
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

        // Parse optional automation trigger (issue #53). Malformed/unsupported cadences are rejected
        // at definition load time with a clear message rather than silently never firing.
        WorkflowTrigger? trigger = null;
        if (dto.Trigger is not null)
        {
            if (!TryParseTrigger(dto.Trigger, GetChildMapping(rootNode, "trigger"), source, out trigger, out error))
                return false;
        }

        definition = new WorkflowDefinition
        {
            Id = dto.Id!,
            Name = dto.Name!,
            Description = dto.Description,
            Version = string.IsNullOrWhiteSpace(dto.Version) ? null : dto.Version.Trim(),
            Start = dto.Start!,
            Nodes = nodes,
            Edges = edges,
            Stages = stages,
            Trigger = trigger,
        };
        return true;
    }

    private static bool TryParseTrigger(
        TriggerYamlDto dto, YamlMappingNode? triggerNode, string source, out WorkflowTrigger? trigger, out string? error)
    {
        trigger = null;
        error = null;

        if (string.IsNullOrWhiteSpace(dto.Type))
            return Fail(source, "trigger is missing its required 'type' ('schedule' or 'event').", out error);

        switch (Normalize(dto.Type))
        {
            case "schedule":
            {
                if (string.IsNullOrWhiteSpace(dto.Interval))
                    return Fail(source, "schedule trigger is missing its required 'interval' ('daily', 'weekly', or 'monthly').", out error);
                if (!TryParseInterval(dto.Interval, out var interval))
                    return Fail(source, $"schedule trigger has unknown interval '{dto.Interval}'.", out error);

                if (string.IsNullOrWhiteSpace(dto.TimeOfDay))
                    return Fail(source, "schedule trigger is missing its required 'time_of_day' (24h 'HH:mm', UTC).", out error);
                if (!TimeOnly.TryParseExact(dto.TimeOfDay.Trim(), "HH:mm", out var timeOfDay))
                    return Fail(source, $"schedule trigger has malformed 'time_of_day' '{dto.TimeOfDay}' — expected 24h 'HH:mm'.", out error);

                DayOfWeek? dayOfWeek = null;
                if (interval == WorkflowScheduleInterval.Weekly)
                {
                    if (string.IsNullOrWhiteSpace(dto.DayOfWeek))
                        return Fail(source, "weekly schedule trigger is missing its required 'day_of_week'.", out error);
                    if (!TryParseDayOfWeek(dto.DayOfWeek, out var dow))
                        return Fail(source, $"schedule trigger has unknown day_of_week '{dto.DayOfWeek}'.", out error);
                    dayOfWeek = dow;
                }

                int? dayOfMonth = null;
                if (interval == WorkflowScheduleInterval.Monthly)
                {
                    if (dto.DayOfMonth is null)
                        return Fail(source, "monthly schedule trigger is missing its required 'day_of_month'.", out error);
                    if (dto.DayOfMonth.Value is < 1 or > 28)
                        return Fail(source, $"schedule trigger has out-of-range day_of_month {dto.DayOfMonth.Value} (must be 1-28).", out error);
                    dayOfMonth = dto.DayOfMonth.Value;
                }

                trigger = new WorkflowTrigger
                {
                    Type = WorkflowTriggerType.Schedule,
                    Interval = interval,
                    DayOfWeek = dayOfWeek,
                    DayOfMonth = dayOfMonth,
                    TimeOfDay = timeOfDay,
                };
                return true;
            }

            case "event":
            {
                if (string.IsNullOrWhiteSpace(dto.EventName))
                    return Fail(source, "event trigger is missing its required 'event_name'.", out error);
                if (!TryClassifyEventName(dto.EventName, out var eventFamily))
                    return Fail(
                        source,
                        $"event trigger has unsupported event_name '{dto.EventName}'. Use github.push or github.<issues|issue_comment|pull_request|pull_request_review|release|discussion>[.<action>].",
                        out error);

                var conditions = Array.Empty<WorkflowEventPredicate>();
                if (!TryParseEventConditions(triggerNode, eventFamily, source, out conditions, out error))
                    return false;

                trigger = new WorkflowTrigger
                {
                    Type = WorkflowTriggerType.Event,
                    EventName = dto.EventName.Trim(),
                    Conditions = conditions,
                };
                return true;
            }

            default:
                return Fail(source, $"trigger has unknown type '{dto.Type}' (expected 'schedule' or 'event').", out error);
        }
    }

    private static bool TryParseEventConditions(
        YamlMappingNode? triggerNode,
        WorkflowEventFamily eventFamily,
        string source,
        out WorkflowEventPredicate[] conditions,
        out string? error)
    {
        conditions = [];
        error = null;
        if (triggerNode is null) return true;

        var ifNode = GetChild(triggerNode, "if");
        if (ifNode is null) return true;
        if (ifNode is not YamlSequenceNode sequence)
            return Fail(source, "event trigger 'if' must be a YAML list.", out error);

        var parsed = new List<WorkflowEventPredicate>(sequence.Children.Count);
        for (var i = 0; i < sequence.Children.Count; i++)
        {
            if (!TryParseEventPredicate(
                    sequence.Children[i],
                    eventFamily,
                    source,
                    $"trigger.if[{i}]",
                    out var predicate,
                    out error))
            {
                return false;
            }

            parsed.Add(predicate);
        }

        conditions = [.. parsed];
        return true;
    }

    private static bool TryParseEventPredicate(
        YamlNode node,
        WorkflowEventFamily eventFamily,
        string source,
        string path,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;

        if (node is not YamlMappingNode mapping)
            return Fail(source, $"{path} must be a mapping with exactly one predicate key.", out error);
        if (mapping.Children.Count != 1)
            return Fail(source, $"{path} must contain exactly one predicate key.", out error);

        var entry = mapping.Children.Single();
        if (entry.Key is not YamlScalarNode keyNode || string.IsNullOrWhiteSpace(keyNode.Value))
            return Fail(source, $"{path} must use a scalar predicate key.", out error);

        var key = keyNode.Value!.Trim();
        switch (NormalizePredicateKey(key))
        {
            case "haslabel":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.HasLabel))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseLabelPredicate(entry.Value, source, path, key, WorkflowEventPredicateType.HasLabel, out predicate, out error);

            case "isnotlabeledwith":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.IsNotLabeledWith))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseLabelPredicate(entry.Value, source, path, key, WorkflowEventPredicateType.IsNotLabeledWith, out predicate, out error);

            case "basebranch":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.BaseBranch))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseExactStringPredicate(entry.Value, source, path, key, "equals", WorkflowEventPredicateType.BaseBranch, out predicate, out error);

            case "reviewstate":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.ReviewState))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseNamedStringPredicate(entry.Value, source, path, key, "state", WorkflowEventPredicateType.ReviewState, out predicate, out error);

            case "ref":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.Ref))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseRefPredicate(entry.Value, source, path, out predicate, out error);

            case "category":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.Category))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseNamedStringPredicate(entry.Value, source, path, key, "name", WorkflowEventPredicateType.Category, out predicate, out error);

            case "commentmatches":
                if (!IsAllowedPredicate(eventFamily, WorkflowEventPredicateType.CommentMatches))
                    return Fail(source, $"{path} predicate '{key}' is not allowed for event '{EventFamilyName(eventFamily)}'.", out error);
                return TryParseCommentMatchesPredicate(entry.Value, source, path, out predicate, out error);

            case "or":
                return TryParseCompoundPredicate(entry.Value, eventFamily, source, path, WorkflowEventPredicateType.Or, out predicate, out error);

            case "not":
                return TryParseCompoundPredicate(entry.Value, eventFamily, source, path, WorkflowEventPredicateType.Not, out predicate, out error);

            default:
                return Fail(source, $"{path} uses unknown predicate '{key}'.", out error);
        }
    }

    private static bool TryParseLabelPredicate(
        YamlNode node,
        string source,
        string path,
        string key,
        WorkflowEventPredicateType type,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;
        if (!TryGetRequiredStringField(node, "label", source, $"{path}.{key}", out var label, out error))
            return false;

        predicate = new WorkflowEventPredicate
        {
            Type = type,
            Label = label,
        };
        return true;
    }

    private static bool TryParseExactStringPredicate(
        YamlNode node,
        string source,
        string path,
        string key,
        string fieldName,
        WorkflowEventPredicateType type,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;

        var value = TryGetScalarValue(node);
        if (string.IsNullOrWhiteSpace(value)
            && !TryGetRequiredStringField(node, fieldName, source, $"{path}.{key}", out value, out error))
        {
            return false;
        }

        predicate = new WorkflowEventPredicate
        {
            Type = type,
            Exact = value,
        };
        return true;
    }

    private static bool TryParseNamedStringPredicate(
        YamlNode node,
        string source,
        string path,
        string key,
        string fieldName,
        WorkflowEventPredicateType type,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;

        var value = TryGetScalarValue(node);
        if (string.IsNullOrWhiteSpace(value)
            && !TryGetRequiredStringField(node, fieldName, source, $"{path}.{key}", out value, out error))
        {
            return false;
        }

        predicate = type switch
        {
            WorkflowEventPredicateType.ReviewState => new WorkflowEventPredicate { Type = type, State = value },
            WorkflowEventPredicateType.Category => new WorkflowEventPredicate { Type = type, Name = value },
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
        return true;
    }

    private static bool TryParseRefPredicate(
        YamlNode node,
        string source,
        string path,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;

        var scalar = TryGetScalarValue(node);
        if (!string.IsNullOrWhiteSpace(scalar))
        {
            predicate = new WorkflowEventPredicate
            {
                Type = WorkflowEventPredicateType.Ref,
                Exact = scalar,
            };
            return true;
        }

        if (node is not YamlMappingNode mapping)
            return Fail(source, $"{path}.ref must be a scalar ref or a mapping with 'equals' or 'prefix'.", out error);

        var exact = TryGetOptionalStringField(mapping, "equals");
        var prefix = TryGetOptionalStringField(mapping, "prefix");
        var specified = (string.IsNullOrWhiteSpace(exact) ? 0 : 1) + (string.IsNullOrWhiteSpace(prefix) ? 0 : 1);
        if (specified != 1)
            return Fail(source, $"{path}.ref must set exactly one of 'equals' or 'prefix'.", out error);

        predicate = new WorkflowEventPredicate
        {
            Type = WorkflowEventPredicateType.Ref,
            Exact = exact,
            Prefix = prefix,
        };
        return true;
    }

    private static bool TryParseCommentMatchesPredicate(
        YamlNode node,
        string source,
        string path,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;
        if (!TryGetRequiredStringField(node, "pattern", source, $"{path}.commentMatches", out var pattern, out error))
            return false;

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
        }
        catch (ArgumentException ex)
        {
            return Fail(source, $"{path}.commentMatches.pattern is not a valid regex: {ex.Message}", out error);
        }

        predicate = new WorkflowEventPredicate
        {
            Type = WorkflowEventPredicateType.CommentMatches,
            Pattern = pattern,
        };
        return true;
    }

    private static bool TryParseCompoundPredicate(
        YamlNode node,
        WorkflowEventFamily eventFamily,
        string source,
        string path,
        WorkflowEventPredicateType type,
        out WorkflowEventPredicate predicate,
        out string? error)
    {
        predicate = null!;
        error = null;

        List<YamlNode>? childNodes = node switch
        {
            YamlSequenceNode sequence => [.. sequence.Children],
            YamlMappingNode mapping => [mapping],
            _ => null,
        };
        if (childNodes is null || childNodes.Count == 0)
            return Fail(source, $"{path}.{(type == WorkflowEventPredicateType.Or ? "or" : "not")} must contain at least one nested predicate.", out error);

        var parsed = new List<WorkflowEventPredicate>(childNodes.Count);
        for (var i = 0; i < childNodes.Count; i++)
        {
            if (!TryParseEventPredicate(
                    childNodes[i],
                    eventFamily,
                    source,
                    $"{path}.{(type == WorkflowEventPredicateType.Or ? "or" : "not")}[{i}]",
                    out var nested,
                    out error))
            {
                return false;
            }

            parsed.Add(nested);
        }

        predicate = new WorkflowEventPredicate
        {
            Type = type,
            Predicates = [.. parsed],
        };
        return true;
    }

    private static bool TryParseInterval(string raw, out WorkflowScheduleInterval interval)
    {
        switch (Normalize(raw))
        {
            case "daily": interval = WorkflowScheduleInterval.Daily; return true;
            case "weekly": interval = WorkflowScheduleInterval.Weekly; return true;
            case "monthly": interval = WorkflowScheduleInterval.Monthly; return true;
            default: interval = default; return false;
        }
    }

    private static bool TryParseDayOfWeek(string raw, out DayOfWeek dayOfWeek)
    {
        switch (Normalize(raw))
        {
            case "sunday": dayOfWeek = DayOfWeek.Sunday; return true;
            case "monday": dayOfWeek = DayOfWeek.Monday; return true;
            case "tuesday": dayOfWeek = DayOfWeek.Tuesday; return true;
            case "wednesday": dayOfWeek = DayOfWeek.Wednesday; return true;
            case "thursday": dayOfWeek = DayOfWeek.Thursday; return true;
            case "friday": dayOfWeek = DayOfWeek.Friday; return true;
            case "saturday": dayOfWeek = DayOfWeek.Saturday; return true;
            default: dayOfWeek = default; return false;
        }
    }

    private static YamlMappingNode? TryParseRootMapping(string yaml)
    {
        using var reader = new StringReader(yaml);
        var stream = new YamlStream();
        stream.Load(reader);
        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
    }

    private static YamlNode? GetChild(YamlMappingNode? mapping, string key)
    {
        if (mapping is null) return null;
        foreach (var child in mapping.Children)
        {
            if (child.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return child.Value;
            }
        }

        return null;
    }

    private static YamlMappingNode? GetChildMapping(YamlMappingNode? mapping, string key) => GetChild(mapping, key) as YamlMappingNode;

    private static string? TryGetScalarValue(YamlNode node) =>
        node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value.Trim()
            : null;

    private static string? TryGetOptionalStringField(YamlMappingNode mapping, string key) =>
        GetChild(mapping, key) is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
            ? scalar.Value.Trim()
            : null;

    private static bool TryGetRequiredStringField(
        YamlNode node,
        string field,
        string source,
        string path,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = null;
        if (node is not YamlMappingNode mapping)
            return Fail(source, $"{path} must be a mapping containing '{field}'.", out error);

        var raw = TryGetOptionalStringField(mapping, field);
        if (string.IsNullOrWhiteSpace(raw))
            return Fail(source, $"{path} is missing its required '{field}' field.", out error);

        value = raw;
        return true;
    }

    private static string NormalizePredicateKey(string raw) =>
        raw.Trim().Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static bool TryClassifyEventName(string raw, out WorkflowEventFamily family)
    {
        var value = raw.Trim();
        if (string.Equals(value, "github.push", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("github.push.", StringComparison.OrdinalIgnoreCase))
        {
            family = WorkflowEventFamily.Push;
            return true;
        }

        if (MatchesEventFamily(value, "github.issues"))
        {
            family = WorkflowEventFamily.Issues;
            return true;
        }

        if (MatchesEventFamily(value, "github.issue_comment"))
        {
            family = WorkflowEventFamily.IssueComment;
            return true;
        }

        if (MatchesEventFamily(value, "github.pull_request"))
        {
            family = WorkflowEventFamily.PullRequest;
            return true;
        }

        if (MatchesEventFamily(value, "github.pull_request_review"))
        {
            family = WorkflowEventFamily.PullRequestReview;
            return true;
        }

        if (MatchesEventFamily(value, "github.release"))
        {
            family = WorkflowEventFamily.Release;
            return true;
        }

        if (MatchesEventFamily(value, "github.discussion"))
        {
            family = WorkflowEventFamily.Discussion;
            return true;
        }

        family = default;
        return false;
    }

    private static bool MatchesEventFamily(string eventName, string prefix) =>
        string.Equals(eventName, prefix, StringComparison.OrdinalIgnoreCase) ||
        eventName.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);

    private static bool IsAllowedPredicate(WorkflowEventFamily family, WorkflowEventPredicateType type) => family switch
    {
        WorkflowEventFamily.Issues => type is WorkflowEventPredicateType.HasLabel or WorkflowEventPredicateType.IsNotLabeledWith,
        WorkflowEventFamily.PullRequest => type is WorkflowEventPredicateType.HasLabel or WorkflowEventPredicateType.IsNotLabeledWith or WorkflowEventPredicateType.BaseBranch,
        WorkflowEventFamily.PullRequestReview => type == WorkflowEventPredicateType.ReviewState,
        WorkflowEventFamily.Push => type == WorkflowEventPredicateType.Ref,
        WorkflowEventFamily.Release => false,
        WorkflowEventFamily.Discussion => type == WorkflowEventPredicateType.Category,
        WorkflowEventFamily.IssueComment => type == WorkflowEventPredicateType.CommentMatches,
        _ => false,
    };

    private static string EventFamilyName(WorkflowEventFamily family) => family switch
    {
        WorkflowEventFamily.Issues => "github.issues.*",
        WorkflowEventFamily.IssueComment => "github.issue_comment.*",
        WorkflowEventFamily.PullRequest => "github.pull_request.*",
        WorkflowEventFamily.PullRequestReview => "github.pull_request_review.*",
        WorkflowEventFamily.Push => "github.push",
        WorkflowEventFamily.Release => "github.release.*",
        WorkflowEventFamily.Discussion => "github.discussion.*",
        _ => "unknown",
    };

    private static bool Fail(string source, string message, out string? error)
    {
        error = $"{source}: {message}";
        return false;
    }

    private static string Normalize(string raw) =>
        raw.Trim().Replace('-', '_').Replace(' ', '_').ToLowerInvariant();

    private static bool TryParseNodeType(string raw, out WorkflowNodeType type)
    {
        switch (Normalize(raw))
        {
            case "prompt": type = WorkflowNodeType.Prompt; return true;
            case "peer_review": type = WorkflowNodeType.PeerReview; return true;
            case "build_test": type = WorkflowNodeType.BuildTest; return true;
            case "open_pull_request": type = WorkflowNodeType.OpenPullRequest; return true;
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

    private enum WorkflowEventFamily
    {
        Issues,
        IssueComment,
        PullRequest,
        PullRequestReview,
        Push,
        Release,
        Discussion,
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
    public string? Version { get; set; }
    public string? Start { get; set; }
    public List<NodeYamlDto>? Nodes { get; set; }
    public List<EdgeYamlDto>? Edges { get; set; }
    public List<StageYamlDto>? Stages { get; set; }
    public TriggerYamlDto? Trigger { get; set; }
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

    // ── open_pull_request fields (Feature: workflows-automation open-pull-request-action) ──
    /// <summary>PR title template. Supports {run_id}/{worktree_branch}/{originating_branch}/{outcome_summary}.</summary>
    public string? Title { get; set; }
    /// <summary>PR body template. Supports the same placeholders as <see cref="Title"/>.</summary>
    public string? Body { get; set; }
    /// <summary>Base branch the PR merges into. Defaults to the project's default branch.</summary>
    public string? Base { get; set; }
    /// <summary>Head branch to open the PR from. Defaults to the run's worktree branch.</summary>
    public string? Head { get; set; }
    /// <summary>Whether to open the PR as a draft. Defaults to false.</summary>
    public bool? Draft { get; set; }
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

/// <summary>YAML DTO for the optional <c>trigger:</c> block (issue #53).</summary>
internal sealed class TriggerYamlDto
{
    public string? Type { get; set; }
    public string? Interval { get; set; }
    public string? DayOfWeek { get; set; }
    public int? DayOfMonth { get; set; }
    public string? TimeOfDay { get; set; }
    public string? EventName { get; set; }
}
