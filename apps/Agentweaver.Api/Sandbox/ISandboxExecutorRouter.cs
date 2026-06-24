using Agentweaver.SandboxExec;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Selects the ISandboxExecutor implementation to use based on runtime environment
/// and configuration. Replaces the fragile "last AddSingleton wins" pattern.
/// </summary>
public interface ISandboxExecutorRouter
{
    /// <summary>
    /// Resolves and returns the ISandboxExecutor for this deployment.
    /// Fail-closed: throws <see cref="InvalidOperationException"/> if running in-cluster
    /// but Kubernetes client initialization fails.
    /// </summary>
    ISandboxExecutor Resolve();
}
