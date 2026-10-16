using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimeOwnerContextClient(
    HttpClient client, EnvironmentRuntimeBootstrapDeliveryOptions? options = null)
{
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
