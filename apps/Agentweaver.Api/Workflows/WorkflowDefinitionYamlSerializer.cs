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
}
