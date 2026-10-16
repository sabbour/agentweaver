using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Reads the orchestrator's current owner context for a runtime actor and coordination session.</summary>
public sealed class EnvironmentRuntimeOwnerContextClient(
    HttpClient client, EnvironmentRuntimeBootstrapDeliveryOptions? options = null)
{
    /// <summary>Requests the session-bound owner context, failing closed when the owner service is not configured.</summary>
    /// <exception cref="RuntimeAuthorizationException">Owner-context access is unavailable or rejected.</exception>
    public Task<RuntimeOwnerContext> ReadAsync(
        RuntimeActorAuthorization actor, string projectId, string runId, string sessionId,
        CancellationToken cancellationToken)
    {
        if (options is null)
            throw new RuntimeAuthorizationException("runtime_owner_context_unavailable");
        return RuntimeOwnerHttpTransport.SendAsync<RuntimeOwnerContext>(
            client, options.OrchestratorOwnerAddress,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            $"/coordination/sessions/{Uri.EscapeDataString(sessionId)}/runtime-owner-context",
            actor, null, cancellationToken);
    }
}
