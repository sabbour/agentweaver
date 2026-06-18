using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentTools.Tools;

internal sealed class ApplyPatchTool : ISandboxTool
{
    public string Name => "apply_patch";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("Patch content in the Copilot CLI patch grammar.")] string patch,
                CancellationToken ct = default) =>
            {
                var result = await ctx.FileTools.ApplyPatchAsync(patch, ct);
                if (!result.Success) return $"Error: {result.Reason}";
                var summary = string.Join("; ", result.Hunks.Select(h => h.Success ? $"{h.Path}: ok" : $"{h.Path}: {h.Error}"));
                return $"Patch applied. {summary}";
            },
            Name, "Apply a patch in the Copilot CLI patch grammar.");
}
