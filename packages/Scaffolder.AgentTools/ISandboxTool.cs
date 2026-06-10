using Microsoft.Extensions.AI;

namespace Scaffolder.AgentTools;

/// <summary>
/// A single sandboxed tool that can be exposed to the model as an AIFunction.
/// </summary>
public interface ISandboxTool
{
    /// <summary>The canonical tool name (must match the Copilot built-in name where applicable).</summary>
    string Name { get; }

    /// <summary>
    /// Creates the AIFunction to register with the model.
    /// The function performs its own governance check via context.EvaluateToolCall before executing.
    /// </summary>
    AIFunction CreateFunction(SandboxToolContext context);
}
