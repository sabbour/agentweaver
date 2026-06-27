namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Seam for reading Kubernetes runtime context. Abstracted so tests can stub both
/// the "in-cluster" and "not in Kubernetes" branches without touching real env vars.
/// </summary>
public interface IKubernetesEnvironment
{
    /// <summary>True when <c>KUBERNETES_SERVICE_HOST</c> is set (always injected in-cluster).</summary>
    bool IsKubernetes { get; }

    /// <summary>The pod name (hostname) when running in Kubernetes; null otherwise.</summary>
    string? PodName { get; }
}

/// <summary>
/// Production implementation: detects Kubernetes via <c>KUBERNETES_SERVICE_HOST</c> and
/// reads the pod name from <see cref="System.Environment.MachineName"/> (the pod hostname
/// equals the pod name in a default Kubernetes deployment).
/// </summary>
public sealed class DefaultKubernetesEnvironment : IKubernetesEnvironment
{
    public bool IsKubernetes =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));

    public string? PodName => IsKubernetes ? Environment.MachineName : null;
}
