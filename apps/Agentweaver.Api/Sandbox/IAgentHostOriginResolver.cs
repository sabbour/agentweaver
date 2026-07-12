using k8s;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Resolves the AgentHost pod ORIGIN (<c>scheme://podIP:AgentHostPort</c>) for a run — with NO A2A
/// path segment (spec-006 decouple-preview, BLOCKER 1).
///
/// <para>
/// The <c>/preview-runner/*</c> endpoints are ROOT-mounted on the AgentHost (Program.cs), so a
/// platform driver must target the bare origin. Reusing <see cref="KubernetesPodAgentEndpointResolver"/>
/// (which appends <c>AgentHostA2APath</c>) would produce <c>…/a2a/agent/preview-runner/…</c> → 404.
/// This resolver shares the SAME pod-IP lookup + mTLS/scheme logic via
/// <see cref="AgentHostEndpoint.Build(bool, string, int, string?)"/> with <c>path: null</c>.
/// </para>
/// </summary>
public interface IAgentHostOriginResolver
{
    /// <summary>
    /// Returns <c>scheme://podIP:AgentHostPort</c> for the run's bound AgentHost pod, or
    /// <see langword="null"/> when no pod is registered / bound / has an IP yet.
    /// </summary>
    Task<string?> TryResolveOriginAsync(string runId, CancellationToken ct);
}

internal sealed class KubernetesAgentHostOriginResolver : IAgentHostOriginResolver
{
    private readonly IKubernetes _k8sClient;
    private readonly IPodNameRegistry _podRegistry;
    private readonly string _namespace;
    private readonly SandboxAgentOptions _options;
    private readonly ILogger<KubernetesAgentHostOriginResolver> _logger;

    public KubernetesAgentHostOriginResolver(
        IKubernetes k8sClient,
        IPodNameRegistry podRegistry,
        string @namespace,
        SandboxAgentOptions options,
        ILogger<KubernetesAgentHostOriginResolver> logger)
    {
        _k8sClient = k8sClient ?? throw new ArgumentNullException(nameof(k8sClient));
        _podRegistry = podRegistry ?? throw new ArgumentNullException(nameof(podRegistry));
        _namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<string?> TryResolveOriginAsync(string runId, CancellationToken ct)
    {
        var podName = _podRegistry.TryGet(runId);
        if (podName is null)
        {
            _logger.LogWarning(
                "KubernetesAgentHostOriginResolver: no pod registered for run {RunId}; preview-runner origin unavailable.",
                runId);
            return null;
        }

        try
        {
            var pod = await _k8sClient.CoreV1.ReadNamespacedPodAsync(
                podName, _namespace, cancellationToken: ct).ConfigureAwait(false);
            var podIp = pod?.Status?.PodIP;
            if (string.IsNullOrEmpty(podIp))
            {
                _logger.LogWarning(
                    "KubernetesAgentHostOriginResolver: pod {PodName} for run {RunId} has no IP yet (phase={Phase}).",
                    podName, runId, pod?.Status?.Phase);
                return null;
            }

            // Origin ONLY — path: null. The /preview-runner/* routes are root-mounted (BLOCKER 1).
            return AgentHostEndpoint.Build(_options.RequireMtls, podIp, _options.AgentHostPort, path: null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "KubernetesAgentHostOriginResolver: failed to resolve pod IP for run {RunId} (pod={PodName})",
                runId, podName);
            return null;
        }
    }
}

/// <summary>No-op origin resolver for non-Kubernetes environments (local dev/CI).</summary>
internal sealed class NoOpAgentHostOriginResolver : IAgentHostOriginResolver
{
    public Task<string?> TryResolveOriginAsync(string runId, CancellationToken ct)
        => Task.FromResult<string?>(null);
}
