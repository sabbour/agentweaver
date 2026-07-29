using YamlDotNet.Core;
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

        if (!TryMapAndValidate(dto, source, isBuiltIn, out var definition, out var error, out var loadWarnings))
            return WorkflowLoadResult.Invalid(source, error!);

        return WorkflowLoadResult.Valid(source, definition!, isBuiltIn, loadWarnings);
    }

    private static bool TryMapAndValidate(
        WorkflowYamlDto dto,
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
            if (!TryParseTrigger(dto.Trigger, source, out trigger, out error))
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

    internal static bool TryParseTrigger(
        TriggerYamlDto dto, string source, out WorkflowTrigger? trigger, out string? error)
    {
        trigger = null;
        error = null;

        if (string.IsNullOrWhiteSpace(dto.Type))
            return Fail(source, "trigger is missing its required 'type' ('schedule' or 'event').", out error);

        switch (Normalize(dto.Type))
        {
            case "schedule":
            {
                if (dto.If is not null)
                    return Fail(source, "schedule triggers do not support an 'if' predicate list.", out error);

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

                var predicates = new List<WorkflowTriggerPredicate>();
                if (dto.If is { Count: 0 })
                    return Fail(source, "event trigger 'if' must declare at least one predicate when present.", out error);

                if (dto.If is { Count: > 0 })
                {
                    if (!TryGetGitHubEventType(dto.EventName.Trim(), out var eventType))
                        return Fail(source, "event trigger predicates require a GitHub event name in the form 'github.<event>' or 'github.<event>.<action>'.", out error);

                    for (var i = 0; i < dto.If.Count; i++)
                    {
                        if (!TryParsePredicate(dto.If[i], eventType, source, $"trigger.if[{i}]", out var predicate, out error))
                            return false;
                        predicates.Add(predicate!);
                    }
                }

                trigger = new WorkflowTrigger
                {
                    Type = WorkflowTriggerType.Event,
                    EventName = dto.EventName.Trim(),
                    If = predicates,
                };
                return true;
            }

            default:
                return Fail(source, $"trigger has unknown type '{dto.Type}' (expected 'schedule' or 'event').", out error);
        }
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

    private static bool Fail(string source, string message, out string? error)
    {
        error = $"{source}: {message}";
        return false;
    }

    private static bool TryParsePredicate(
        TriggerPredicateYamlDto dto,
        string eventType,
        string source,
        string path,
        out WorkflowTriggerPredicate? predicate,
        out string? error)
    {
        predicate = null;
        error = null;

        var fieldCount =
            (dto.HasLabel is null ? 0 : 1) +
            (dto.IsNotLabeledWith is null ? 0 : 1) +
            (dto.BaseBranch is null ? 0 : 1) +
            (dto.ReviewState is null ? 0 : 1) +
            (dto.Ref is null ? 0 : 1) +
            (dto.Category is null ? 0 : 1) +
            (dto.CommentMatches is null ? 0 : 1) +
            (dto.Or is null ? 0 : 1) +
            (dto.Not is null ? 0 : 1);

        if (fieldCount != 1)
            return Fail(source, $"{path} must declare exactly one predicate kind.", out error);

        if (dto.HasLabel is not null)
        {
            if (!SupportsEventType(eventType, "issues", "pull_request"))
                return Fail(source, $"{path}.has_label is only supported for github.issues and github.pull_request events.", out error);
            if (string.IsNullOrWhiteSpace(dto.HasLabel.Label))
                return Fail(source, $"{path}.has_label.label is required.", out error);

            predicate = new WorkflowTriggerPredicate
            {
                HasLabel = new WorkflowTriggerLabelPredicate { Label = dto.HasLabel.Label.Trim() },
            };
            return true;
        }

        if (dto.IsNotLabeledWith is not null)
        {
            if (!SupportsEventType(eventType, "issues", "pull_request"))
                return Fail(source, $"{path}.is_not_labeled_with is only supported for github.issues and github.pull_request events.", out error);
            if (string.IsNullOrWhiteSpace(dto.IsNotLabeledWith.Label))
                return Fail(source, $"{path}.is_not_labeled_with.label is required.", out error);

            predicate = new WorkflowTriggerPredicate
            {
                IsNotLabeledWith = new WorkflowTriggerLabelPredicate { Label = dto.IsNotLabeledWith.Label.Trim() },
            };
            return true;
        }

        if (dto.BaseBranch is not null)
        {
            if (!SupportsEventType(eventType, "pull_request"))
                return Fail(source, $"{path}.base_branch is only supported for github.pull_request events.", out error);
            if (string.IsNullOrWhiteSpace(dto.BaseBranch.Branch))
                return Fail(source, $"{path}.base_branch.branch is required.", out error);

            predicate = new WorkflowTriggerPredicate
            {
                BaseBranch = new WorkflowTriggerBaseBranchPredicate { Branch = dto.BaseBranch.Branch.Trim() },
            };
            return true;
        }

        if (dto.ReviewState is not null)
        {
            if (!SupportsEventType(eventType, "pull_request_review"))
                return Fail(source, $"{path}.review_state is only supported for github.pull_request_review events.", out error);
            if (string.IsNullOrWhiteSpace(dto.ReviewState.State))
                return Fail(source, $"{path}.review_state.state is required.", out error);
            if (!TryParseReviewState(dto.ReviewState.State, out var reviewState))
                return Fail(source, $"{path}.review_state.state has unknown value '{dto.ReviewState.State}'.", out error);

            predicate = new WorkflowTriggerPredicate
            {
                ReviewState = new WorkflowTriggerReviewStatePredicate { State = reviewState },
            };
            return true;
        }

        if (dto.Ref is not null)
        {
            if (!SupportsEventType(eventType, "push"))
                return Fail(source, $"{path}.ref is only supported for github.push events.", out error);
            if (string.IsNullOrWhiteSpace(dto.Ref.Branch))
                return Fail(source, $"{path}.ref.branch is required.", out error);
            if (string.IsNullOrWhiteSpace(dto.Ref.MatchMode))
                return Fail(source, $"{path}.ref.match_mode is required.", out error);
            if (!TryParseMatchMode(dto.Ref.MatchMode, out var matchMode))
                return Fail(source, $"{path}.ref.match_mode has unknown value '{dto.Ref.MatchMode}'.", out error);

            predicate = new WorkflowTriggerPredicate
            {
                Ref = new WorkflowTriggerRefPredicate
                {
                    Branch = dto.Ref.Branch.Trim(),
                    MatchMode = matchMode,
                },
            };
            return true;
        }

        if (dto.Category is not null)
        {
            if (!SupportsEventType(eventType, "discussion"))
                return Fail(source, $"{path}.category is only supported for github.discussion events.", out error);
            if (string.IsNullOrWhiteSpace(dto.Category.Name))
                return Fail(source, $"{path}.category.name is required.", out error);

            predicate = new WorkflowTriggerPredicate
            {
                Category = new WorkflowTriggerCategoryPredicate { Name = dto.Category.Name.Trim() },
            };
            return true;
        }

        if (dto.CommentMatches is not null)
        {
            if (!SupportsEventType(eventType, "issue_comment"))
                return Fail(source, $"{path}.comment_matches is only supported for github.issue_comment events.", out error);
            if (string.IsNullOrWhiteSpace(dto.CommentMatches.Pattern))
                return Fail(source, $"{path}.comment_matches.pattern is required.", out error);
            if (!WorkflowTriggerRegexPolicy.TryValidatePattern(dto.CommentMatches.Pattern, out var regexError))
                return Fail(source, $"{path}.comment_matches.{regexError}", out error);

            predicate = new WorkflowTriggerPredicate
            {
                CommentMatches = new WorkflowTriggerCommentMatchesPredicate { Pattern = dto.CommentMatches.Pattern },
            };
            return true;
        }

        if (dto.Or is not null)
        {
            if (dto.Or.Count == 0)
                return Fail(source, $"{path}.or must contain at least one predicate.", out error);

            var predicates = new List<WorkflowTriggerPredicate>(dto.Or.Count);
            for (var i = 0; i < dto.Or.Count; i++)
            {
                if (!TryParsePredicate(dto.Or[i], eventType, source, $"{path}.or[{i}]", out var child, out error))
                    return false;
                predicates.Add(child!);
            }

            predicate = new WorkflowTriggerPredicate { Or = predicates };
            return true;
        }

        if (dto.Not is not null)
        {
            if (!TryParsePredicate(dto.Not, eventType, source, $"{path}.not", out var child, out error))
                return false;

            predicate = new WorkflowTriggerPredicate { Not = child };
            return true;
        }

        return Fail(source, $"{path} declared an unsupported predicate kind.", out error);
    }

    private static bool TryParseReviewState(string raw, out WorkflowTriggerReviewState state)
    {
        switch (Normalize(raw))
        {
            case "approved": state = WorkflowTriggerReviewState.Approved; return true;
            case "changes_requested": state = WorkflowTriggerReviewState.ChangesRequested; return true;
            case "commented": state = WorkflowTriggerReviewState.Commented; return true;
            default: state = default; return false;
        }
    }

    private static bool TryParseMatchMode(string raw, out WorkflowTriggerMatchMode mode)
    {
        switch (Normalize(raw))
        {
            case "equals": mode = WorkflowTriggerMatchMode.Equals; return true;
            case "prefix": mode = WorkflowTriggerMatchMode.Prefix; return true;
            default: mode = default; return false;
        }
    }

    private static bool TryGetGitHubEventType(string eventName, out string eventType)
    {
        const string prefix = "github.";
        if (!eventName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            eventType = string.Empty;
            return false;
        }

        var remainder = eventName[prefix.Length..].Trim();
        if (remainder.Length == 0)
        {
            eventType = string.Empty;
            return false;
        }

        var separator = remainder.IndexOf('.');
        eventType = (separator >= 0 ? remainder[..separator] : remainder).Trim().ToLowerInvariant();
        return eventType.Length > 0;
    }

    private static bool SupportsEventType(string actualEventType, params string[] supportedEventTypes) =>
        supportedEventTypes.Any(x => string.Equals(x, actualEventType, StringComparison.OrdinalIgnoreCase));

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
    public List<TriggerPredicateYamlDto>? If { get; set; }
}

internal sealed class TriggerPredicateYamlDto
{
    public TriggerLabelPredicateYamlDto? HasLabel { get; set; }
    public TriggerLabelPredicateYamlDto? IsNotLabeledWith { get; set; }
    public TriggerBaseBranchPredicateYamlDto? BaseBranch { get; set; }
    public TriggerReviewStatePredicateYamlDto? ReviewState { get; set; }
    public TriggerRefPredicateYamlDto? Ref { get; set; }
    public TriggerCategoryPredicateYamlDto? Category { get; set; }
    public TriggerCommentMatchesPredicateYamlDto? CommentMatches { get; set; }
    public List<TriggerPredicateYamlDto>? Or { get; set; }
    public TriggerPredicateYamlDto? Not { get; set; }
}

internal sealed class TriggerLabelPredicateYamlDto
{
    public string? Label { get; set; }
}

internal sealed class TriggerBaseBranchPredicateYamlDto
{
    public string? Branch { get; set; }
}

internal sealed class TriggerReviewStatePredicateYamlDto
{
    public string? State { get; set; }
}

internal sealed class TriggerRefPredicateYamlDto
{
    public string? Branch { get; set; }
    public string? MatchMode { get; set; }
}

internal sealed class TriggerCategoryPredicateYamlDto
{
    public string? Name { get; set; }
}

internal sealed class TriggerCommentMatchesPredicateYamlDto
{
    public string? Pattern { get; set; }
}
