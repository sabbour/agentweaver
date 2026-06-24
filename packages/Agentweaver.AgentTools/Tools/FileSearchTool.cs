using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentTools.Tools;

internal sealed class FileSearchTool : ISandboxTool
{
    public string Name => "file_search";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("Glob pattern to match file paths (e.g. '**/*.cs').")] string pattern,
                [Description("Max number of results. Default 200.")] int? max_results,
                CancellationToken ct = default) =>
            {
                var paths = await ctx.SearchTools.FileSearchAsync(pattern, Math.Min(max_results ?? 200, 1000), ct);
                if (paths.Count == 0) return "No files found.";
                return string.Join("\n", paths);
            },
            Name, "Find files matching a glob pattern under the working directory.");
}
