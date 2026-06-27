namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Manages the lifecycle of per-run <c>Agentweaver.AgentHost</c> pods — the sandbox
/// pods that host the A2A-exposed <c>CopilotAIAgent</c> leaf when
/// <c>Sandbox:AgentExecutionMode=pod-per-run</c>.
///
/// <para>
/// When the workflow graph suspends (HITL <c>RequestPort</c> gate, coordinator-idle
/// awaiting children), the pod is checkpoint-released via
/// <see cref="ReleaseAgentHostPodAsync"/>; on resume it is re-claimed and rehydrated
/// from the durable DB-backed <c>ICheckpointStore</c> (Q3 hybrid, spec §9/§12.2).
/// </para>
///
/// <para>
/// Seam for Tank: the returned endpoint URL is persisted in
/// <see cref="IPodNameRegistry"/> so the worker-side <c>RemoteAgentProxy</c> can
/// build the A2A client pointing at <c>{endpointUrl}/v1/message:stream</c>.
/// </para>
/// </summary>
public interface IAgentHostPodLifecycle
{
    /// <summary>
    /// Provisions (or re-provisions after a suspend-release) an AgentHost pod for the
    /// given run. Waits until the pod is <c>Bound</c> and returns the base A2A endpoint
    /// URL (<c>http[s]://&lt;podIP&gt;:&lt;port&gt;&lt;a2aPath&gt;</c>).
    ///
    /// <para>Registers the endpoint in <see cref="IPodNameRegistry"/> on success.</para>
    /// </summary>
    /// <returns>
    /// The fully-qualified A2A base URL, e.g.
    /// <c>http://10.0.1.5:8080/a2a/agent</c>.
    /// </returns>
    Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Releases the AgentHost pod for the given run by deleting its
    /// <c>SandboxClaim</c>. Called on workflow suspension (HITL / coordinator-idle)
    /// when <c>Sandbox:ReleasePodOnSuspend=true</c>.
    /// </summary>
    Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default);
}
