using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Checks that a provisioned runtime endpoint responds to the shared runtime health verification.</summary>
public sealed class EnvironmentRuntimeEndpointVerifier(HttpClient client)
{
    /// <summary>Verifies the live HTTPS endpoint using the runtime transport's bounded health check.</summary>
    public Task VerifyLiveAsync(
        SandboxRuntimeHttpsEndpoint endpoint,
        CancellationToken cancellationToken) =>
        RuntimeSandboxHttpTransport.VerifyHealthAsync(client, endpoint, cancellationToken);
}
