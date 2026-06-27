using System.Collections.Concurrent;
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
    // spec-018 P1.5: pod-per-run launch lifecycle. The endpoint resolver is the single
    // chokepoint every pod-per-run turn passes through (via RemoteAgentProxy.SetupAsync),
    // so it lazily launches the AgentHost pod on first resolve for a run when none is
    // registered yet. Null when the lifecycle is unavailable (non-cluster / misconfig).
    private readonly IAgentHostPodLifecycle? _podLifecycle;
    // Dedupes concurrent launches for the same run (e.g. parallel sub-agent turns) and
    // caches the in-flight/launched task so a run is launched at most once.
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _launches = new(StringComparer.Ordinal);

    public KubernetesPodAgentEndpointResolver(
        IKubernetes k8sClient,
        IPodNameRegistry podRegistry,
        string @namespace,
        SandboxAgentOptions options,
        ILogger<KubernetesPodAgentEndpointResolver> logger,
        IAgentHostPodLifecycle? podLifecycle = null)
    {
        _k8sClient = k8sClient ?? throw new ArgumentNullException(nameof(k8sClient));
        _podRegistry = podRegistry ?? throw new ArgumentNullException(nameof(podRegistry));
        _namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _podLifecycle = podLifecycle;
    }

    /// <inheritdoc />
    public async Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct)
    {
        var podName = _podRegistry.TryGet(runId);
        if (podName is null)
        {
            // Lazily launch the AgentHost pod for this run. This is the only place the
            // pod is provisioned in pod-per-run mode — LaunchAgentHostPodAsync creates the
            // SandboxClaim, waits for it to bind, and registers the pod name + endpoint.
            if (_podLifecycle is not null)
            {
                podName = await EnsurePodLaunchedAsync(runId, ct).ConfigureAwait(false);
            }

            if (podName is null)
            {
                _logger.LogWarning(
                    "KubernetesPodAgentEndpointResolver: no pod registered for run {RunId}; " +
                    "SandboxClaim may not yet be bound.",
                    runId);
                return null;
            }
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

    /// <summary>
    /// Launches the AgentHost pod for <paramref name="runId"/> exactly once, deduping
    /// concurrent callers. Returns the bound pod name (now registered in
    /// <see cref="IPodNameRegistry"/>), or <see langword="null"/> if the launch failed.
    /// </summary>
    private async Task<string?> EnsurePodLaunchedAsync(string runId, CancellationToken ct)
    {
        // A run already registered (raced ahead of us) — nothing to launch.
        var existing = _podRegistry.TryGet(runId);
        if (existing is not null)
            return existing;

        var launch = _launches.GetOrAdd(
            runId,
            id => new Lazy<Task<string>>(
                // Use a non-cancelable token: the pod's lifetime spans the whole run, not a
                // single turn's cancellation scope. Released by RunWatchLoopService on suspend.
                () => _podLifecycle!.LaunchAgentHostPodAsync(id, CancellationToken.None)));

        try
        {
            _logger.LogInformation(
                "KubernetesPodAgentEndpointResolver: no pod registered for run {RunId}; " +
                "launching AgentHost pod.",
                runId);

            await launch.Value.WaitAsync(ct).ConfigureAwait(false);
            return _podRegistry.TryGet(runId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Drop the cached failed launch so a subsequent turn can retry.
            _launches.TryRemove(runId, out _);
            _logger.LogError(ex,
                "KubernetesPodAgentEndpointResolver: failed to launch AgentHost pod for run {RunId}",
                runId);
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
