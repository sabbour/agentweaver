using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Reads the orchestrator's current owner context for a runtime actor and coordination session.</summary>
public sealed class EnvironmentRuntimeOwnerContextClient(
    HttpClient client, EnvironmentRuntimeBootstrapDeliveryOptions? options = null)
    : IEnvironmentP1InvocationBindingReader
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

    /// <summary>Reads Core's no-store current P1 binding using the same authenticated run-bound actor.</summary>
    public async Task<P1InvocationBinding?> ReadCurrentAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        string runId,
        CancellationToken cancellationToken)
    {
        if (options is null)
            throw new RuntimeAuthorizationException("p1_invocation_binding_unavailable");
        return await RuntimeOwnerHttpTransport.SendAsync<P1InvocationBinding>(
            client,
            options.OrchestratorOwnerAddress,
            $"/internal/projects/{Uri.EscapeDataString(projectId)}/runs/{Uri.EscapeDataString(runId)}" +
            "/p1-invocation-binding",
            actor,
            null,
            cancellationToken).ConfigureAwait(false);
    }
}
