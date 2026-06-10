using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools.Tools;

internal sealed class EditFileTool : ISandboxTool
{
    public string Name => "edit";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Content to write.")] string content,
                CancellationToken ct = default) =>
            {
                var govArgs = new Dictionary<string, object> { ["path"] = path, ["tool_name"] = Name };
                var (allowed, reason) = ctx.EvaluateToolCall(Name, govArgs);
                if (!allowed) return $"Error: {reason}";

                var (_, failure) = await ctx.FileTools.WriteFileAsync(path, content, ct);
                return failure is not null ? $"Error: {failure.Message}" : "ok";
            },
            Name, "Write content to a file (creates or overwrites).");
}
