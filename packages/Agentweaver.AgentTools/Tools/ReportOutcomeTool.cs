using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Agentweaver.AgentTools.Tools;

internal sealed class ReportOutcomeTool : ISandboxTool
{
    public string Name => "report_outcome";

    public AIFunction CreateFunction(SandboxToolContext ctx) =>
        AIFunctionFactory.Create(
            ([Description("true if the task was fully completed, false if any critical step failed or was blocked.")] bool achieved,
             [Description("One-sentence explanation of the outcome.")] string reason) =>
            {
                _ = achieved;
                _ = reason;
                return Task.FromResult<object?>("Outcome recorded.");
            },
            Name, "Call this ONCE at the very end to self-assess whether the task was achieved.");
}
