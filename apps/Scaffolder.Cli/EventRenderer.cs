using System.Text.Json;
using Spectre.Console;

namespace Scaffolder.Cli;

/// <summary>Formats run events into aligned, colored console lines.</summary>
public static class EventRenderer
{
    /// <summary>Returns a Spectre markup line describing the event.</summary>
    public static string Format(RunEvent evt)
    {
        var label = $"[{evt.Type}]".PadRight(20);
        var message = Describe(evt);
        var color = ColorFor(evt.Type);
        return $"[{color}]{Markup.Escape(label)}[/] {message}";
    }

    private static string Describe(RunEvent evt)
    {
        var p = evt.Payload;
        switch (evt.Type)
        {
            case "run.started":
                return $"Run {Markup.Escape(Short(evt.RunId))} started ({Markup.Escape(Str(p, "model_source"))})";
            case "run.completed":
                return $"Run complete after {Str(p, "step_count")} steps";
            case "run.failed":
                return $"Run failed: {Markup.Escape(Str(p, "reason"))}";
            case "run.bounded":
                return $"Run bounded: {Markup.Escape(Str(p, "limit_type"))} limit reached after {Str(p, "step_count")} steps";
            case "agent.message":
                return Markup.Escape(Str(p, "text"));
            case "tool.call":
            {
                var op = Str(p, "operation");
                var verb = op.Equals("write", StringComparison.OrdinalIgnoreCase) ? "write_file" : "read_file";
                return $"{verb}: {Markup.Escape(Str(p, "path"))}";
            }
            case "tool.result":
                return $"OK {Markup.Escape(Str(p, "path"))} ({Str(p, "bytes_read_or_written")} bytes)";
            case "tool.rejected":
                return $"REJECTED {Markup.Escape(Str(p, "path"))}: {Markup.Escape(Str(p, "reason"))}";
            case "tool.error":
                return $"ERROR {Markup.Escape(Str(p, "path"))}: {Markup.Escape(Str(p, "error_message"))}";
            case "review.requested":
                return $"Awaiting review (tree: {Markup.Escape(Str(p, "tree_hash"))})";
            case "review.approved":
                return $"Review approved by {Markup.Escape(Str(p, "approved_by"))}";
            case "review.declined":
                return $"Review declined by {Markup.Escape(Str(p, "declined_by"))}";
            case "merge.completed":
                return $"Merge completed: {Markup.Escape(Str(p, "merged_commit_hash"))}";
            case "merge.failed":
                return $"Merge failed: {Markup.Escape(Str(p, "reason"))}";
            default:
                return Markup.Escape(RawPayload(p));
        }
    }

    private static string ColorFor(string type) => type switch
    {
        "run.started" => "blue",
        "run.completed" => "green",
        "run.failed" => "red",
        "run.bounded" => "yellow",
        "agent.message" => "white",
        "tool.call" => "cyan",
        "tool.result" => "green",
        "tool.rejected" => "yellow",
        "tool.error" => "red",
        "review.requested" => "magenta",
        "review.approved" => "green",
        "review.declined" => "yellow",
        "merge.completed" => "green",
        "merge.failed" => "red",
        _ => "grey"
    };

    /// <summary>True for events that terminate a run's stream.</summary>
    public static bool IsTerminal(string type) =>
        type is "run.completed" or "run.failed" or "run.bounded";

    private static string Str(JsonElement payload, string name)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(name, out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                _ => value.GetRawText()
            };
        }

        return string.Empty;
    }

    private static string RawPayload(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Undefined ? string.Empty : payload.GetRawText();

    private static string Short(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= 8 ? value : value[..8];
}
