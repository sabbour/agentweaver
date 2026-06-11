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
                // Returns a reminder so the model knows to follow up with the actual tool call
                // rather than treating report_intent as a terminal action.
                _ = intent;
                return Task.FromResult<object?>("Intent recorded. Now call the appropriate tool to perform the action.");
            },
            Name, "Report the agent's current intent for display in the run UI.");
}
