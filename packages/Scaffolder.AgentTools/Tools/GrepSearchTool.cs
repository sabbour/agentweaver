using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools.Tools;

internal sealed class GrepSearchTool : ISandboxTool
{
    public string Name => "grep_search";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("Search pattern (literal string or regex).")] string pattern,
                [Description("Whether pattern is a regex. Default false.")] bool? is_regex,
                [Description("Glob pattern to filter files (e.g. '**/*.cs').")] string? include_pattern,
                [Description("Max number of results. Default 50.")] int? max_results,
                CancellationToken ct = default) =>
            {
                // grep_search is in KnownSearchTools — backend allows unconditionally.
                // Governance call still runs for the audit trail.
                var govArgs = new Dictionary<string, object> { ["tool_name"] = Name };
                var (allowed, reason) = ctx.EvaluateToolCall(Name, govArgs);
                if (!allowed) return $"Error: {reason}";

                var matches = await ctx.SearchTools.GrepSearchAsync(pattern, is_regex ?? false, include_pattern, max_results ?? 50, caseSensitive: false, ct);
                if (matches.Count == 0) return "No matches found.";
                return string.Join("\n", matches.Select(m => $"{m.RelativePath}:{m.LineNumber}: {m.LineContent}"));
            },
            Name, "Search for a pattern in files under the working directory.");
}
