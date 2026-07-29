using System.Text;

namespace Agentweaver.Api.Workflows;

/// <summary>Serializes an in-memory workflow definition back to the editable YAML shape.</summary>
public static class WorkflowDefinitionYamlSerializer
{
    public static string Serialize(WorkflowDefinition definition)
    {
        var sb = new StringBuilder();
        Line(sb, "id", definition.Id);
        Line(sb, "name", definition.Name);
        Line(sb, "description", definition.Description);
        Line(sb, "version", definition.Version);
        sb.AppendLine();
        Line(sb, "start", definition.Start);
        sb.AppendLine();
        sb.AppendLine("nodes:");
        foreach (var node in definition.Nodes)
        {
            Line(sb, "  - id", node.Id);
            Line(sb, "    type", NodeType(node.Type));
            Line(sb, "    label", node.Label);
            Line(sb, "    role", node.Role);
            Line(sb, "    kind", node.Kind);
            Line(sb, "    gate_kind", node.GateKind);
            Line(sb, "    agent", node.Agent);
            BlockOrLine(sb, "    prompt", node.Prompt);
            BlockOrLine(sb, "    charter", node.Charter);
            Line(sb, "    target", node.Target);
            List(sb, "    steps", node.Steps);
            List(sb, "    branches", node.Branches);
            BlockOrLine(sb, "    title", node.PrTitle);
            BlockOrLine(sb, "    body", node.PrBody);
            Line(sb, "    base", node.PrBase);
            Line(sb, "    head", node.PrHead);
            if (node.PrDraft.HasValue)
                sb.AppendLine($"    draft: {(node.PrDraft.Value ? "true" : "false")}");
            sb.AppendLine();
        }

        sb.AppendLine("edges:");
        foreach (var edge in definition.Edges)
        {
            Line(sb, "  - from", edge.From);
            Line(sb, "    to", edge.To);
            Line(sb, "    when", edge.When);
        }

        if (definition.Stages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("stages:");
            foreach (var stage in definition.Stages)
            {
                Line(sb, "  - id", stage.Id);
                Line(sb, "    label", stage.Label);
                sb.AppendLine($"    order: {stage.Order}");
            }
        }

        if (definition.Trigger is { } trigger)
        {
            sb.AppendLine();
            sb.AppendLine("trigger:");
            Line(sb, "  type", TriggerType(trigger.Type));
            if (trigger.Type == WorkflowTriggerType.Schedule)
            {
                Line(sb, "  interval", ScheduleInterval(trigger.Interval));
                if (trigger.Interval == WorkflowScheduleInterval.Weekly && trigger.DayOfWeek is { } dow)
                    Line(sb, "  day_of_week", dow.ToString().ToLowerInvariant());
                if (trigger.Interval == WorkflowScheduleInterval.Monthly && trigger.DayOfMonth is { } dom)
                    sb.AppendLine($"  day_of_month: {dom}");
                if (trigger.TimeOfDay is { } timeOfDay)
                    Line(sb, "  time_of_day", timeOfDay.ToString("HH:mm"));
            }
            else if (trigger.Type == WorkflowTriggerType.Event)
            {
                Line(sb, "  event_name", trigger.EventName);
                if (trigger.If.Count > 0)
                {
                    sb.AppendLine("  if:");
                    foreach (var predicate in trigger.If)
                        WritePredicate(sb, "    - ", "      ", predicate);
                }
            }
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static void Line(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append(key).Append(": ").Append(YamlScalar(value)).AppendLine();
    }

    private static void BlockOrLine(StringBuilder sb, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (!value.Contains('\n') && value.Length < 100)
        {
            Line(sb, key, value);
            return;
        }

        sb.Append(key).AppendLine(": |-");
        foreach (var line in value.Replace("\r\n", "\n").Split('\n'))
            sb.Append("      ").AppendLine(line);
    }

    private static void List(StringBuilder sb, string key, IReadOnlyList<string> values)
    {
        if (values.Count == 0) return;
        sb.Append(key).AppendLine(":");
        foreach (var value in values)
            sb.Append("      - ").Append(YamlScalar(value)).AppendLine();
    }

    private static string YamlScalar(string value)
    {
        if (value.Length == 0) return "\"\"";
        if (value.Any(c => (c is ':' or '#' or '"' or '\'' or '{' or '}' or '[' or ']' or ',') || char.IsWhiteSpace(c)) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
        return value;
    }

    private static string NodeType(WorkflowNodeType type) => type switch
    {
        WorkflowNodeType.Prompt => "prompt",
        WorkflowNodeType.PeerReview => "peer_review",
        WorkflowNodeType.BuildTest => "build_test",
        WorkflowNodeType.OpenPullRequest => "open_pull_request",
        WorkflowNodeType.Check => "check",
        WorkflowNodeType.FanOut => "fan_out",
        WorkflowNodeType.FanIn => "fan_in",
        WorkflowNodeType.CoordinatorComposed => "coordinator_composed",
        WorkflowNodeType.Serial => "serial",
        WorkflowNodeType.Merge => "merge",
        WorkflowNodeType.Scribe => "scribe",
        WorkflowNodeType.Terminal => "terminal",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static string TriggerType(WorkflowTriggerType type) => type switch
    {
        WorkflowTriggerType.Schedule => "schedule",
        WorkflowTriggerType.Event => "event",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    private static string? ScheduleInterval(WorkflowScheduleInterval? interval) => interval switch
    {
        WorkflowScheduleInterval.Daily => "daily",
        WorkflowScheduleInterval.Weekly => "weekly",
        WorkflowScheduleInterval.Monthly => "monthly",
        _ => null,
    };

    private static void WritePredicate(StringBuilder sb, string firstKeyPrefix, string nestedIndent, WorkflowTriggerPredicate predicate)
    {
        if (predicate.HasLabel is { } hasLabel)
        {
            sb.Append(firstKeyPrefix).Append("has_label: ").AppendLine(InlineMap(("label", hasLabel.Label)));
            return;
        }

        if (predicate.IsNotLabeledWith is { } isNotLabeledWith)
        {
            sb.Append(firstKeyPrefix).Append("is_not_labeled_with: ").AppendLine(InlineMap(("label", isNotLabeledWith.Label)));
            return;
        }

        if (predicate.BaseBranch is { } baseBranch)
        {
            sb.Append(firstKeyPrefix).Append("base_branch: ").AppendLine(InlineMap(("branch", baseBranch.Branch)));
            return;
        }

        if (predicate.ReviewState is { } reviewState)
        {
            sb.Append(firstKeyPrefix).Append("review_state: ").AppendLine(InlineMap(("state", ReviewState(reviewState.State))));
            return;
        }

        if (predicate.Ref is { } refPredicate)
        {
            sb.Append(firstKeyPrefix).Append("ref: ").AppendLine(InlineMap(
                ("branch", refPredicate.Branch),
                ("match_mode", MatchMode(refPredicate.MatchMode))));
            return;
        }

        if (predicate.Category is { } category)
        {
            sb.Append(firstKeyPrefix).Append("category: ").AppendLine(InlineMap(("name", category.Name)));
            return;
        }

        if (predicate.CommentMatches is { } commentMatches)
        {
            sb.Append(firstKeyPrefix).Append("comment_matches: ").AppendLine(InlineMap(("pattern", commentMatches.Pattern)));
            return;
        }

        if (predicate.Or.Count > 0)
        {
            sb.Append(firstKeyPrefix).AppendLine("or:");
            foreach (var child in predicate.Or)
                WritePredicate(sb, nestedIndent + "- ", nestedIndent + "  ", child);
            return;
        }

        if (predicate.Not is { } not)
        {
            sb.Append(firstKeyPrefix).AppendLine("not:");
            WritePredicate(sb, nestedIndent, nestedIndent + "  ", not);
            return;
        }

        throw new ArgumentException("Predicate must declare exactly one kind.", nameof(predicate));
    }

    private static string InlineMap(params (string Key, string Value)[] entries) =>
        "{ " + string.Join(", ", entries.Select(x => $"{x.Key}: {YamlScalar(x.Value)}")) + " }";

    private static string ReviewState(WorkflowTriggerReviewState state) => state switch
    {
        WorkflowTriggerReviewState.Approved => "approved",
        WorkflowTriggerReviewState.ChangesRequested => "changes_requested",
        WorkflowTriggerReviewState.Commented => "commented",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string MatchMode(WorkflowTriggerMatchMode mode) => mode switch
    {
        WorkflowTriggerMatchMode.Equals => "equals",
        WorkflowTriggerMatchMode.Prefix => "prefix",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
