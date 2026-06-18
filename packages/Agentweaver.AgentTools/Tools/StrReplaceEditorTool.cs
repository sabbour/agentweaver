using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentTools.Tools;

internal sealed class StrReplaceEditorTool : ISandboxTool
{
    public string Name => "str_replace_editor";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Exact string to replace (must be unique in the file).")] string old_str,
                [Description("Replacement string.")] string new_str,
                CancellationToken ct = default) =>
            {
                var (replaced, failure) = await ctx.FileTools.StrReplaceAsync(path, old_str, new_str, ct);
                return failure is not null ? $"Error: {failure.Message}" : (replaced ? "ok" : "not replaced");
            },
            Name, "Replace a unique string in a file with a new string.");
}
