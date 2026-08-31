using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Thin <see cref="CopilotAIAgent"/> subclass for the Scribe built-in agent.
/// Overrides <see cref="SerializeSessionCoreAsync"/> / <see cref="DeserializeSessionCoreAsync"/>
/// as no-ops because Scribe runs ephemeral single-turn tasks that don't need
/// cross-restart resume.
/// </summary>
public sealed class ScribeAIAgent : CopilotAIAgent
{
    public ScribeAIAgent(
        GitHubCopilotClientFactory factory,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger,
        IByokProviderConfigurationProvider? byokProviderConfiguration = null)
        : base(factory, executor, sandboxPolicyStore, approvalStore, toolApprovalGate, logger,
            byokProviderConfiguration: byokProviderConfiguration)
    {
    }

    /// <summary>No-op: Scribe turns are ephemeral and never resumed across restarts.</summary>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession? session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        new(JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>No-op: Scribe turns are ephemeral; restore a fresh session.</summary>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        CreateSessionCoreAsync(cancellationToken);
}
