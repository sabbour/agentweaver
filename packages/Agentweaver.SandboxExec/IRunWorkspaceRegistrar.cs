namespace Agentweaver.SandboxExec;

/// <summary>
/// Immutable, platform-owned registration of the roots a run is allowed to see. The AgentHost
/// registers the run workspace and the run-scoped HOME before any model-controlled tool can execute,
/// and the enforcing executor derives every later mount plan from that registration only.
/// </summary>
/// <remarks>
/// Implemented both by the in-sidecar mount-namespace executor and by the AgentHost-side client that
/// forwards registrations to the executor sidecar, so callers never depend on which side of the pod
/// boundary the enforcement happens on.
/// </remarks>
public interface IRunWorkspaceRegistrar
{
    /// <summary>
    /// Captures the run workspace (and its linked-worktree git metadata) before model execution.
    /// Re-registering the same workspace with different metadata is rejected.
    /// </summary>
    void RegisterTrustedWorkspace(string workingDirectory);

    /// <summary>
    /// Registers the exact run-scoped HOME created by the platform for a workspace. The mapping is
    /// immutable and is the only source used for HOME/XDG environment values and mounts.
    /// </summary>
    void RegisterRuntimeHome(string workingDirectory, string runtimeHome);
}
