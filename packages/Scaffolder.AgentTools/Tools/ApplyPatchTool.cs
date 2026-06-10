using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools.Tools;

internal sealed class ApplyPatchTool : ISandboxTool
{
    public string Name => "apply_patch";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            async (
                [Description("Patch content in the Copilot CLI patch grammar.")] string patch,
                CancellationToken ct = default) =>
            {
                // apply_patch is pre-validated — governance call runs for the audit trail.
                var govArgs = new Dictionary<string, object> { ["tool_name"] = Name };
                var (allowed, reason) = ctx.EvaluateToolCall(Name, govArgs);
                if (!allowed) return $"Error: {reason}";

                var result = await ctx.FileTools.ApplyPatchAsync(patch, ct);
                if (!result.Success) return $"Error: {result.Reason}";
                var summary = string.Join("; ", result.Hunks.Select(h => h.Success ? $"{h.Path}: ok" : $"{h.Path}: {h.Error}"));
                return $"Patch applied. {summary}";
            },
            Name, "Apply a patch in the Copilot CLI patch grammar.");
}
