using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.Domain;
using Agentweaver.SandboxExec;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Thin <see cref="CopilotAIAgent"/> subclass for the Rai built-in RAI reviewer agent.
/// Runs post-work, pre-ship Responsible AI review. Overrides
/// <see cref="SerializeSessionCoreAsync"/> / <see cref="DeserializeSessionCoreAsync"/>
/// as no-ops because Rai runs ephemeral single-turn reviews with no session
/// persistence requirement.
/// </summary>
public sealed class RaiAIAgent : CopilotAIAgent
{
    public RaiAIAgent(
        GitHubCopilotClientFactory factory,
        IGitHubTokenScopeProvider scopeProvider,
        ISandboxExecutor executor,
        ISandboxPolicyStore sandboxPolicyStore,
        IShellApprovalStore approvalStore,
        IToolApprovalGate toolApprovalGate,
        ILogger<CopilotAIAgent> logger)
        : base(factory, scopeProvider, executor, sandboxPolicyStore, approvalStore, toolApprovalGate, logger)
    {
    }

    /// <summary>No-op: Rai reviews are ephemeral and never resumed across restarts.</summary>
    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession? session, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        new(JsonDocument.Parse("{}").RootElement.Clone());

    /// <summary>No-op: Rai reviews are ephemeral; restore a fresh session.</summary>
    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions, CancellationToken cancellationToken) =>
        CreateSessionCoreAsync(cancellationToken);
}
