namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Thrown while waiting for an AgentHost <c>SandboxClaim</c> to bind when the claim's status reports
/// a reconciler failure (e.g. the controller could not create the pod because the namespace quota
/// was exceeded — <c>ReconcilerError: exceeded quota</c>). Surfaced so the launch path can fail the
/// run with the precise reason <c>agent_pod_reconciler_error</c> instead of a generic timeout.
/// </summary>
public sealed class AgentHostPodReconcilerErrorException : Exception
{
    public AgentHostPodReconcilerErrorException(string message)
        : base(message)
    {
    }
}
