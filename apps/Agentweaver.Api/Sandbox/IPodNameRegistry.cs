namespace Agentweaver.Api.Sandbox;

/// <summary>
/// In-memory registry that maps an Agentweaver run ID to the Kubernetes pod name
/// bound by its SandboxClaim. Populated by <see cref="KubernetesSandboxExecutor"/>
/// once the claim transitions to <c>phase: Bound</c>; consumed by
/// <see cref="PortForwardService"/> to look up the target pod for preview tunnels.
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
}
