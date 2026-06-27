namespace Agentweaver.Api.Sandbox;

/// <summary>
/// In-memory registry that maps an Agentweaver run ID to the Kubernetes pod name
/// bound by its SandboxClaim. Populated by <see cref="KubernetesSandboxExecutor"/>
/// once the claim transitions to <c>phase: Bound</c>; consumed by
/// <see cref="PortForwardService"/> to look up the target pod for preview tunnels.
///
/// <para>
/// Extended for pod-per-run A2A: also stores the fully-qualified A2A endpoint URL
/// (<c>http[s]://&lt;podIP&gt;:&lt;port&gt;&lt;a2aPath&gt;</c>) so Tank's
/// <c>RemoteAgentProxy</c> can resolve the per-run agent endpoint without probing
/// the Kubernetes API on every turn.
/// </para>
/// </summary>
public interface IPodNameRegistry
{
    /// <summary>Registers the pod name for the given run. Overwrites any prior entry.</summary>
    void Register(string runId, string podName);

    /// <summary>Removes the pod mapping when the sandbox claim has been deleted.</summary>
    void Unregister(string runId);

    /// <summary>
    /// Returns the pod name for the given run, or <see langword="null"/> if not registered.
    /// </summary>
    string? TryGet(string runId);

    // ── A2A endpoint registry (pod-per-run) ────────────────────────────────────

    /// <summary>
    /// Registers the fully-qualified A2A base endpoint URL for the given run's AgentHost pod.
    /// Called by <see cref="KubernetesSandboxExecutor.LaunchAgentHostPodAsync"/> once
    /// the pod IP is known. Example: <c>http://10.0.1.5:8080/a2a/agent</c>.
    /// </summary>
    void RegisterAgentEndpoint(string runId, string endpointUrl);

    /// <summary>
    /// Returns the A2A base endpoint URL for the given run's AgentHost pod, or
    /// <see langword="null"/> if the pod has not been launched or has been released.
    /// </summary>
    string? TryGetAgentEndpoint(string runId);
}
