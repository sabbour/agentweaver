using k8s;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Workflow;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Resolves the A2A endpoint URI for a sandbox pod by looking up the pod name from
/// <see cref="IPodNameRegistry"/> and fetching the pod's cluster IP from the Kubernetes API.
///
/// <para>
/// Endpoint URI format: <c>{scheme}://{podIP}:{port}{a2aPath}</c> (e.g.
/// <c>https://10.0.0.42:8080/a2a/agent</c>). The path, port, and scheme come from
/// <see cref="SandboxAgentOptions"/>.
/// </para>
///
/// <para>
/// The pod name is registered in <see cref="IPodNameRegistry"/> by
/// <see cref="KubernetesSandboxExecutor"/> once the <c>SandboxClaim</c> transitions to
/// <c>phase: Bound</c>. In <c>pod-per-run</c> mode the sandbox claim must therefore be
/// bound before this resolver is called (i.e. before <c>RemoteAgentProxy.SetupAsync</c>).
/// </para>
/// </summary>
internal sealed class KubernetesPodAgentEndpointResolver : ISandboxAgentEndpointResolver
{
    private readonly IKubernetes _k8sClient;
    private readonly IPodNameRegistry _podRegistry;
    private readonly string _namespace;
    private readonly SandboxAgentOptions _options;
    private readonly ILogger<KubernetesPodAgentEndpointResolver> _logger;

    public KubernetesPodAgentEndpointResolver(
        IKubernetes k8sClient,
        IPodNameRegistry podRegistry,
        string @namespace,
        SandboxAgentOptions options,
        ILogger<KubernetesPodAgentEndpointResolver> logger)
    {
        _k8sClient = k8sClient ?? throw new ArgumentNullException(nameof(k8sClient));
        _podRegistry = podRegistry ?? throw new ArgumentNullException(nameof(podRegistry));
        _namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct)
    {
        var podName = _podRegistry.TryGet(runId);
        if (podName is null)
        {
            _logger.LogWarning(
                "KubernetesPodAgentEndpointResolver: no pod registered for run {RunId}; " +
                "SandboxClaim may not yet be bound.",
                runId);
            return null;
        }

        try
        {
            var pod = await _k8sClient.CoreV1.ReadNamespacedPodAsync(
                podName, _namespace, cancellationToken: ct)
                .ConfigureAwait(false);

            var podIp = pod?.Status?.PodIP;
            if (string.IsNullOrEmpty(podIp))
            {
                _logger.LogWarning(
                    "KubernetesPodAgentEndpointResolver: pod {PodName} for run {RunId} has no IP yet " +
                    "(phase={Phase}). Returning null.",
                    podName, runId, pod?.Status?.Phase);
                return null;
            }

            var endpoint = new Uri(
                AgentHostEndpoint.Build(
                    _options.RequireMtls, podIp, _options.AgentHostPort, _options.AgentHostA2APath));

            _logger.LogDebug(
                "KubernetesPodAgentEndpointResolver: run={RunId}, pod={PodName}, endpoint={Endpoint}",
                runId, podName, endpoint);

            return endpoint;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "KubernetesPodAgentEndpointResolver: failed to resolve pod IP for run {RunId} (pod={PodName})",
                runId, podName);
            return null;
        }
    }
}

/// <summary>
/// No-op endpoint resolver used in non-Kubernetes environments (local dev, CI).
/// Always returns <see langword="null"/>, causing <c>RemoteAgentProxy.SetupAsync</c> to
/// throw with a clear message. Only encountered when
/// <c>Sandbox:AgentExecutionMode=pod-per-run</c> is set outside a Kubernetes cluster —
/// which is a misconfiguration.
/// </summary>
internal sealed class NoOpSandboxAgentEndpointResolver : ISandboxAgentEndpointResolver
{
    public Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct)
        => Task.FromResult<Uri?>(null);
}
