using Microsoft.Extensions.AI;
using Agentweaver.AgentTools;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Optional host-provided tool extension point for runtime agents.
/// AgentHost uses this to add pod-local capabilities, such as the PreviewRunner, without
/// making the shared AgentRuntime assembly depend on the AgentHost application.
/// </summary>
public interface IAgentRuntimeToolProvider
{
    IEnumerable<AIFunction> BuildTools(SandboxToolContext context);
}
