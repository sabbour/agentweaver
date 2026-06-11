using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools.Tools;

internal sealed class CreateFileTool : ISandboxTool
{
    public string Name => "create";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                [Description("Content to write to the new file.")] string file_text,
                CancellationToken ct = default) =>
            {
                var (_, failure) = await ctx.FileTools.CreateFileAsync(path, file_text, ct);
                return failure is not null ? $"Error: {failure.Message}" : "ok";
            },
            Name, "Create a new file (fails if the file already exists).");
}
