using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools.Tools;

internal sealed class ReportIntentTool : ISandboxTool
{
    public string Name => "report_intent";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            ([Description("Brief description of the agent's current intent or plan step.")] string intent) =>
            {
                var govArgs = new Dictionary<string, object> { ["tool_name"] = Name };
                var (allowed, reason) = ctx.EvaluateToolCall(Name, govArgs);
                if (!allowed) return Task.FromResult<object?>($"Error: {reason}");
                // Intentionally synchronous — just surfaces the intent; no I/O.
                _ = intent;
                return Task.FromResult<object?>("ok");
            },
            Name, "Report the agent's current intent for display in the run UI.");
}
