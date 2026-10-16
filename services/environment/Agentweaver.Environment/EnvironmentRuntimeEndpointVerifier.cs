using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

public sealed class EnvironmentRuntimeEndpointVerifier(HttpClient client)
{
    public Task VerifyLiveAsync(
        SandboxRuntimeHttpsEndpoint endpoint,
        CancellationToken cancellationToken) =>
        RuntimeSandboxHttpTransport.VerifyHealthAsync(client, endpoint, cancellationToken);
}
