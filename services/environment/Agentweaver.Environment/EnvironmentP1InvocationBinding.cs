using Agentweaver.Abstractions;
using Agentweaver.Identity;

namespace Agentweaver.Environment;

/// <summary>Authenticated, no-store read of the Orchestrator's current P1 invocation binding.</summary>
public interface IEnvironmentP1InvocationBindingReader
{
    Task<P1InvocationBinding?> ReadCurrentAsync(
        RuntimeActorAuthorization actor,
        string projectId,
        string runId,
        CancellationToken cancellationToken);
}
