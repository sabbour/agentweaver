namespace Agentweaver.AgentRuntime.Workflow;

/// <summary>
/// Resolves the A2A base endpoint URI for the sandbox pod bound to a given run.
///
/// <para>
/// The endpoint is used by <see cref="RemoteAgentProxy"/> to connect to the pod's
/// <c>CopilotAIAgent</c> hosted via A2A (<c>MapA2A</c>). The returned <see cref="Uri"/>
/// is the pod's A2A base path (e.g. <c>https://10.0.0.5:8080/a2a/agent</c>), from which
/// the A2A client derives the streaming endpoint
/// (<c>…/v1/message:stream</c>) and card endpoint (<c>…/v1/card</c>).
/// </para>
///
/// <para>
/// Implementations resolve the endpoint from the bound <c>SandboxClaim</c> pod name/IP,
/// using <c>IPodNameRegistry</c> for the pod name and the Kubernetes API for the pod IP.
/// Returns <see langword="null"/> when no bound pod exists for the run (e.g. claim not yet
/// created) — callers treat this as a configuration error in <c>pod-per-run</c> mode.
/// </para>
/// </summary>
public interface ISandboxAgentEndpointResolver
{
    /// <summary>
    /// Resolves the A2A base endpoint URI for the pod bound to <paramref name="runId"/>,
    /// or returns <see langword="null"/> if no bound pod is registered for that run.
    /// </summary>
    Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct);
}
