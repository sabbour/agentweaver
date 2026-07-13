using System.Net.Sockets;
using k8s;
using k8s.Autorest;
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
    private const int MaxAttempts = 3;
    internal static readonly TimeSpan DefaultAttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly IKubernetes _k8sClient;
    private readonly IPodNameRegistry _podRegistry;
    private readonly string _namespace;
    private readonly SandboxAgentOptions _options;
    private readonly ILogger<KubernetesAgentHostOriginResolver> _logger;
    private readonly TimeSpan _attemptTimeout;
    private readonly Func<int, TimeSpan> _backoff;

    public KubernetesAgentHostOriginResolver(
        IKubernetes k8sClient,
        IPodNameRegistry podRegistry,
        string @namespace,
        SandboxAgentOptions options,
        ILogger<KubernetesAgentHostOriginResolver> logger)
        : this(k8sClient, podRegistry, @namespace, options, logger, DefaultAttemptTimeout, BackoffWithJitter)
    {
    }

    internal KubernetesAgentHostOriginResolver(
        IKubernetes k8sClient,
        IPodNameRegistry podRegistry,
        string @namespace,
        SandboxAgentOptions options,
        ILogger<KubernetesAgentHostOriginResolver> logger,
        TimeSpan attemptTimeout,
        Func<int, TimeSpan> backoff)
    {
        _k8sClient = k8sClient ?? throw new ArgumentNullException(nameof(k8sClient));
        _podRegistry = podRegistry ?? throw new ArgumentNullException(nameof(podRegistry));
        _namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _attemptTimeout = attemptTimeout;
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
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
            var pod = await ReadPodWithRetryAsync(podName, runId, ct).ConfigureAwait(false);
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

    private async Task<k8s.Models.V1Pod> ReadPodWithRetryAsync(
        string podName, string runId, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(_attemptTimeout);
                return await _k8sClient.CoreV1.ReadNamespacedPodAsync(
                    podName, _namespace, cancellationToken: attemptCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransientK8sFault(ex, ct))
            {
                ct.ThrowIfCancellationRequested();
                var delay = _backoff(attempt);
                _logger.LogWarning(ex,
                    "KubernetesAgentHostOriginResolver: transient pod-origin lookup fault for run {RunId} " +
                    "(pod={PodName}) on attempt {Attempt}/{Max}; retrying in {DelayMs}ms.",
                    runId, podName, attempt, MaxAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    private static TimeSpan BackoffWithJitter(int attempt)
    {
        var baseMs = Math.Min(250 * (1 << (attempt - 1)), 2000);
        return TimeSpan.FromMilliseconds(baseMs + Random.Shared.Next(0, 250));
    }

    private static bool IsTransientK8sFault(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        switch (ex)
        {
            case HttpOperationException k when k.Response is not null:
                var status = (int)k.Response.StatusCode;
                return status == 429 || status >= 500;
            case HttpRequestException:
            case IOException:
            case OperationCanceledException:
                return true;
        }

        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException or IOException)
                return true;
        }

        return false;
    }
}

/// <summary>No-op origin resolver for non-Kubernetes environments (local dev/CI).</summary>
internal sealed class NoOpAgentHostOriginResolver : IAgentHostOriginResolver
{
    public Task<string?> TryResolveOriginAsync(string runId, CancellationToken ct)
        => Task.FromResult<string?>(null);
}
