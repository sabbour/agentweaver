using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentTools.Tools;

internal sealed class ReadFileTool : ISandboxTool
{
    public string Name => "read_file";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("File path relative to the working directory.")] string path,
                CancellationToken ct = default) =>
            {
                var (content, failure) = await ctx.FileTools.ReadFileAsync(path, ct);
                return failure is not null ? $"Error: {failure.Message}" : content!;
            },
            Name, "Read the contents of a file.");
}
