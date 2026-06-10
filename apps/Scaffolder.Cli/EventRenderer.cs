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
        var color = ColorFor(evt);
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
                var toolName = Str(p, "toolName");
                var path = ArgPath(p);
                return path.Length > 0
                    ? $"{Markup.Escape(toolName)}: {Markup.Escape(path)}"
                    : Markup.Escape(toolName);
            }
            case "tool.result":
            {
                var content = Str(p, "content");
                return content.Length > 0 ? $"OK: {Markup.Escape(Preview(content))}" : "OK";
            }
            case "tool.error":
                return $"ERROR: {Markup.Escape(Str(p, "errorMessage"))}";
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
            case "sandbox.selected":
            {
                var backend = Str(p, "backend");
                var isRealRaw = Str(p, "isRealIsolation");
                var isReal = isRealRaw == "True" || isRealRaw == "true";
                return isReal
                    ? $"Sandbox: {Markup.Escape(backend)} (isolated)"
                    : $"Sandbox: {Markup.Escape(backend)} (no isolation — shell commands denied)";
            }
            case "sandbox.warning":
            {
                var msg = Str(p, "message");
                return $"Warning: {Markup.Escape(msg)}";
            }
            case "shell.approval_required":
            {
                var requestId = Str(p, "requestId");
                return $"Shell approval required (request: {Markup.Escape(requestId)}) — use 'scaffolder run approve {Markup.Escape(requestId)}' to approve";
            }
            case "tool.output":
            {
                var streamType = Str(p, "stream");
                var data = Str(p, "data");
                var prefix = streamType == "stderr" ? "stderr" : "out";
                return $"[{Markup.Escape(prefix)}] {Markup.Escape(Preview(data, 120))}";
            }
            case "tool.exec_result":
            {
                var exitCode = Str(p, "exitCode");
                var timedOut = Str(p, "timedOut") == "True";
                var truncated = Str(p, "outputTruncated") == "True";
                var extra = timedOut ? " (timed out)" : truncated ? " (output truncated)" : "";
                return $"Exit code: {Markup.Escape(exitCode)}{extra}";
            }
            default:
                return Markup.Escape(RawPayload(p));
        }
    }

    private static string ColorFor(RunEvent evt)
    {
        if (evt.Type == "tool.output")
            return Str(evt.Payload, "stream") == "stderr" ? "red" : "grey";
        return ColorFor(evt.Type);
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
        "tool.error" => "red",
        "tool.output" => "grey",
        "tool.exec_result" => "blue",
        "review.requested" => "magenta",
        "review.approved" => "green",
        "review.declined" => "yellow",
        "merge.completed" => "green",
        "merge.failed" => "red",
        "sandbox.selected" => "cyan",
        "sandbox.warning" => "yellow",
        "shell.approval_required" => "yellow",
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

    /// <summary>Reads the <c>path</c> argument from a tool event's <c>arguments</c> object, if present.</summary>
    private static string ArgPath(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("arguments", out var args) &&
            args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("path", out var path) &&
            path.ValueKind == JsonValueKind.String)
        {
            return path.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    /// <summary>Collapses content to a single trimmed line capped at <paramref name="maxLength"/> characters for console display.</summary>
    private static string Preview(string value, int maxLength = 80)
    {
        var single = value.ReplaceLineEndings(" ").Trim();
        return single.Length <= maxLength ? single : single[..maxLength] + "...";
    }

    private static string Short(string? value) =>
        string.IsNullOrEmpty(value) ? string.Empty
        : value.Length <= 8 ? value : value[..8];
}
