using System.Collections.Concurrent;
using k8s;
using k8s.Autorest;
using Microsoft.Extensions.Logging;
using Agentweaver.AgentRuntime.Providers;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

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
    // Optional run store so a launch that fails for a known, actionable reason (quota exhausted or
    // a controller reconcile error) can terminalize the run with a precise FailureReason code,
    // instead of the worker surfacing the generic "run interrupted" message. Best-effort: a null
    // store (or a missing run row) degrades to the generic failure path unchanged.
    private readonly IRunStore? _runStore;
    private readonly IRunAgentHostContextResolver? _launchContextResolver;
    private readonly IAgentHostDispatchBoundaryValidator? _dispatchBoundaryValidator;
    // Dedupes concurrent launches for the same run (e.g. parallel sub-agent turns) and
    // caches the in-flight/launched task so a run is launched at most once.
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _launches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Lazy<Task<string>>, DispatchMetadata> _launchMetadata = new();

    private sealed record DispatchMetadata(
        AgentHostDispatchBoundary? Boundary,
        string DispatchId);

    public KubernetesPodAgentEndpointResolver(
        IKubernetes k8sClient,
        IPodNameRegistry podRegistry,
        string @namespace,
        SandboxAgentOptions options,
        ILogger<KubernetesPodAgentEndpointResolver> logger,
        IAgentHostPodLifecycle? podLifecycle = null,
        IRunStore? runStore = null,
        IRunAgentHostContextResolver? launchContextResolver = null,
        IAgentHostDispatchBoundaryValidator? dispatchBoundaryValidator = null)
    {
        _k8sClient = k8sClient ?? throw new ArgumentNullException(nameof(k8sClient));
        _podRegistry = podRegistry ?? throw new ArgumentNullException(nameof(podRegistry));
        _namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _podLifecycle = podLifecycle;
        _runStore = runStore;
        _launchContextResolver = launchContextResolver;
        _dispatchBoundaryValidator = dispatchBoundaryValidator;
    }

    /// <inheritdoc />
    public async Task<Uri?> TryResolveEndpointAsync(string runId, CancellationToken ct)
    {
        const int maxEndpointRecoveries = 1;
        for (var recoveryAttempt = 0; ; recoveryAttempt++)
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
            else
            {
                if (!await EnsureAdoptedDispatchAsync(runId, ct).ConfigureAwait(false))
                    podName = await EnsurePodLaunchedAsync(runId, ct).ConfigureAwait(false);
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
                    throw new WorkflowAgentInfrastructureException(
                        "agenthost_ip_not_ready",
                        $"AgentHost pod '{podName}' for run '{runId}' is bound but has no pod IP yet.");
                }

                var endpoint = new Uri(
                    AgentHostEndpoint.Build(
                        _options.RequireMtls, podIp, _options.AgentHostPort, _options.AgentHostA2APath));

                _logger.LogDebug(
                    "KubernetesPodAgentEndpointResolver: run={RunId}, pod={PodName}, endpoint={Endpoint}",
                    runId, podName, endpoint);

                return endpoint;
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                var run = _runStore is not null && RunId.TryParse(runId, out var parsedRunId)
                    ? await _runStore.GetAsync(parsedRunId, ct).ConfigureAwait(false)
                    : null;
                if (run is not null && IsTerminal(run.Status))
                {
                    _logger.LogInformation(
                        "KubernetesPodAgentEndpointResolver: AgentHost pod {PodName} for terminal run {RunId} was reaped.",
                        podName, runId);
                    return null;
                }

                if (_podLifecycle is not null && recoveryAttempt < maxEndpointRecoveries)
                {
                    await ClearDispatchForFreshLaunchAsync(runId).ConfigureAwait(false);
                    _logger.LogWarning(
                        ex,
                        "KubernetesPodAgentEndpointResolver: AgentHost pod {PodName} for non-terminal run {RunId} " +
                        "was reaped; redispatching once (reason=agenthost_pod_reaped, recoveryAttempt={Attempt}, maxRecoveryAttempts={MaxAttempts}).",
                        podName,
                        runId,
                        recoveryAttempt + 1,
                        maxEndpointRecoveries);
                    continue;
                }

                await RecordExhaustionAsync(runId).ConfigureAwait(false);
                await ClearDispatchForFreshLaunchAsync(runId).ConfigureAwait(false);
                throw AgentHostUnavailable(runId, ex);
            }
            catch (WorkflowAgentInfrastructureException ex)
                when (ex.Reason == "agenthost_ip_not_ready" && _podLifecycle is not null)
            {
                if (recoveryAttempt < maxEndpointRecoveries)
                {
                    await ClearDispatchForFreshLaunchAsync(runId).ConfigureAwait(false);
                    continue;
                }

                await RecordExhaustionAsync(runId).ConfigureAwait(false);
                await ClearDispatchForFreshLaunchAsync(runId).ConfigureAwait(false);
                throw AgentHostUnavailable(runId, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (ex is WorkflowAgentInfrastructureException)
                    throw;

                if (_podLifecycle is not null && recoveryAttempt < maxEndpointRecoveries)
                {
                    await ClearDispatchForFreshLaunchAsync(runId).ConfigureAwait(false);
                    _logger.LogWarning(
                        ex,
                        "KubernetesPodAgentEndpointResolver: failed to resolve AgentHost endpoint for run {RunId} " +
                        "(pod={PodName}); redispatching once before delivery " +
                        "(recoveryAttempt={Attempt}, maxRecoveryAttempts={MaxAttempts}).",
                        runId,
                        podName,
                        recoveryAttempt + 1,
                        maxEndpointRecoveries);
                    continue;
                }

                _logger.LogError(
                    ex,
                    "KubernetesPodAgentEndpointResolver: failed to resolve AgentHost endpoint for run {RunId} " +
                    "(pod={PodName}); recovery exhausted.",
                    runId,
                    podName);
                await RecordExhaustionAsync(runId).ConfigureAwait(false);
                await ClearDispatchForFreshLaunchAsync(runId).ConfigureAwait(false);
                throw AgentHostUnavailable(runId, ex);
            }
        }
    }

    public async Task ValidateDeliveryAsync(string runId, CancellationToken ct)
    {
        if (_dispatchBoundaryValidator is null)
            return;

        if (!_launches.TryGetValue(runId, out var launch)
            || !launch.IsValueCreated
            || !_launchMetadata.TryGetValue(launch, out var metadata))
        {
            throw new WorkflowAgentInfrastructureException(
                "agenthost_dispatch_stale",
                $"AgentHost dispatch '{runId}' has no active pre-delivery claim.");
        }

        var current = await _dispatchBoundaryValidator.CaptureAsync(runId, ct).ConfigureAwait(false);
        if (metadata.Boundary != current)
        {
            throw new WorkflowAgentInfrastructureException(
                "agenthost_dispatch_stale",
                $"AgentHost dispatch '{runId}' changed before delivery.");
        }
    }

    private static bool IsTerminal(RunStatus status) => status is
        RunStatus.Completed or RunStatus.Failed or RunStatus.Merged or RunStatus.Declined or
        RunStatus.MergeFailed or RunStatus.AssembleReady;

    /// <inheritdoc />
    public async Task<bool> RequiresPreparedWritebackAsync(string runId, CancellationToken ct)
    {
        if (_launchContextResolver is null)
            return false;

        var context = await _launchContextResolver.ResolveAsync(runId, ct).ConfigureAwait(false);
        return context.Purpose == AgentHostPurpose.ImplementationTurn
            && context.WorkspaceMode == ExecutionWorkspaceMode.LocalWritable;
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

        var launch = await GetOrCreateLaunchAsync(runId, ct).ConfigureAwait(false);

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
            if (_launches.TryRemove(
                    new KeyValuePair<string, Lazy<Task<string>>>(runId, launch)))
            {
                _launchMetadata.TryRemove(launch, out _);
            }

            if (ex is AgentProviderException)
                throw;

            // Map the known, actionable launch failures to a precise FailureReason so the run row
            // (and the run_not_active API response) can explain *why* the run stopped. Note: pod
            // admission/scheduling/queueing is Kubernetes' job (issue #217) — a Pending pod is not a
            // failure here; LaunchAgentHostPodAsync simply waits (emitting provisioning heartbeats)
            // until the claim binds, so there is no capacity/quota exception to translate anymore.
            _logger.LogError(ex,
                "KubernetesPodAgentEndpointResolver: failed to launch AgentHost pod for run {RunId}",
                runId);
            if (ex is WorkflowAgentInfrastructureException infrastructure)
                throw infrastructure;
            throw new WorkflowAgentInfrastructureException(
                "agent_host_unavailable",
                $"AgentHost is unavailable for run '{runId}'. Retry the run.",
                ex,
                isRetryable: true);
        }
    }

    private async Task<Lazy<Task<string>>> GetOrCreateLaunchAsync(string runId, CancellationToken ct)
    {
        while (true)
        {
            var boundary = _dispatchBoundaryValidator is null
                ? null
                : await _dispatchBoundaryValidator.CaptureAsync(runId, ct).ConfigureAwait(false);
            if (_launches.TryGetValue(runId, out var existing))
            {
                if (existing.IsValueCreated
                    && existing.Value.IsCompletedSuccessfully
                    && _podRegistry.TryGet(runId) is null)
                {
                    if (_launches.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(runId, existing)))
                        _launchMetadata.TryRemove(existing, out _);
                    continue;
                }

                if (boundary is null
                    || (_launchMetadata.TryGetValue(existing, out var existingMetadata)
                        && existingMetadata.Boundary == boundary))
                    return existing;

                try
                {
                    await existing.Value.WaitAsync(ct).ConfigureAwait(false);
                }
                catch when (!ct.IsCancellationRequested)
                {
                    // The stale generation is finished; remove it below before creating the new claim.
                }
                if (_launches.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(runId, existing)))
                    _launchMetadata.TryRemove(existing, out _);
                continue;
            }

            var dispatchId = Guid.NewGuid().ToString("N");
            var candidate = new Lazy<Task<string>>(
                () => LaunchPodWithRecoveryAsync(runId, boundary, dispatchId),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _launchMetadata[candidate] = new DispatchMetadata(boundary, dispatchId);
            if (_launches.TryAdd(runId, candidate))
                return candidate;
            _launchMetadata.TryRemove(candidate, out _);
        }
    }

    private async Task<bool> EnsureAdoptedDispatchAsync(string runId, CancellationToken ct)
    {
        if (_dispatchBoundaryValidator is null || _launches.ContainsKey(runId))
            return true;

        var boundary = await _dispatchBoundaryValidator.CaptureAsync(runId, ct).ConfigureAwait(false);
        var persisted = _podLifecycle is null
            ? null
            : await _podLifecycle.GetAgentHostDispatchContextAsync(runId, ct).ConfigureAwait(false);
        if (persisted is null
            || persisted.LifecycleGeneration != boundary.LifecycleGeneration
            || !string.Equals(persisted.DispatchProjectId, boundary.ProjectId, StringComparison.Ordinal)
            || !string.Equals(persisted.DispatchUserId, boundary.UserId, StringComparison.Ordinal)
            || !string.Equals(persisted.DispatchAgentName, boundary.AgentName, StringComparison.Ordinal)
            || !string.Equals(persisted.ProviderSnapshotKey, boundary.ProviderKey, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(persisted.DispatchId))
        {
            if (_podLifecycle is not null)
                await _podLifecycle.ReleaseAgentHostPodAsync(runId, CancellationToken.None).ConfigureAwait(false);
            _podRegistry.Unregister(runId);
            return false;
        }

        var adopted = new Lazy<Task<string>>(
            () => Task.FromResult(string.Empty),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _launchMetadata[adopted] = new DispatchMetadata(boundary, persisted.DispatchId);
        if (_launches.TryAdd(runId, adopted))
        {
            _ = adopted.Value;
            return true;
        }

        _launchMetadata.TryRemove(adopted, out _);
        return true;
    }

    private async Task<string> LaunchPodWithRecoveryAsync(
        string runId,
        AgentHostDispatchBoundary? boundary,
        string dispatchId)
    {
        Exception? lastFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                if (boundary is not null)
                    await ValidateBoundaryAsync(boundary, CancellationToken.None).ConfigureAwait(false);

                var context = _launchContextResolver is null
                    ? new AgentHostLaunchContext(SharedWorkingDirectory: null)
                    : await _launchContextResolver.ResolveAsync(runId, CancellationToken.None).ConfigureAwait(false);
                context = context with
                {
                    HolderToken = dispatchId,
                    DispatchId = dispatchId,
                    LifecycleGeneration = boundary?.LifecycleGeneration,
                    DispatchProjectId = boundary?.ProjectId,
                    DispatchUserId = boundary?.UserId,
                    DispatchAgentName = boundary?.AgentName,
                    ProviderSnapshotKey = boundary?.ProviderKey,
                };

                var endpoint = await _podLifecycle!
                    .LaunchAgentHostPodAsync(runId, context, CancellationToken.None)
                    .ConfigureAwait(false);

                try
                {
                    if (boundary is not null)
                        await ValidateBoundaryAsync(boundary, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    await _podLifecycle.TryReleaseHeldAgentHostPodAsync(
                        runId, dispatchId, CancellationToken.None).ConfigureAwait(false);
                    _podRegistry.Unregister(runId);
                    throw;
                }
                return endpoint;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && IsRecoverablePreDeliveryFailure(ex))
            {
                lastFailure = ex;
                await _podLifecycle!.TryReleaseHeldAgentHostPodAsync(
                    runId, dispatchId, CancellationToken.None).ConfigureAwait(false);
                _podRegistry.Unregister(runId);
                if (attempt == 0)
                    continue;
            }
        }

        await RecordExhaustionAsync(runId, boundary, dispatchId).ConfigureAwait(false);
        throw AgentHostUnavailable(runId, lastFailure);
    }

    private static bool IsRecoverablePreDeliveryFailure(Exception ex) =>
        ex is not AgentProviderException
        && ex is not WorkflowAgentInfrastructureException { Reason: "agenthost_dispatch_stale" or "agenthost_dispatch_inactive" };

    private async Task ValidateBoundaryAsync(AgentHostDispatchBoundary expected, CancellationToken ct)
    {
        var current = await _dispatchBoundaryValidator!.CaptureAsync(expected.DispatchRunId, ct).ConfigureAwait(false);
        if (current != expected)
        {
            throw new WorkflowAgentInfrastructureException(
                "agenthost_dispatch_stale",
                $"AgentHost dispatch '{expected.DispatchRunId}' no longer matches its run generation.");
        }
    }

    private async Task ClearDispatchForFreshLaunchAsync(string runId)
    {
        _podRegistry.Unregister(runId);
        if (!_launches.TryRemove(runId, out var launch))
            return;

        if (_launchMetadata.TryRemove(launch, out var metadata)
            && _podLifecycle is not null)
        {
            await _podLifecycle.TryReleaseHeldAgentHostPodAsync(
                runId, metadata.DispatchId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task RecordExhaustionAsync(string runId)
    {
        if (!_launches.TryGetValue(runId, out var launch)
            || !_launchMetadata.TryGetValue(launch, out var metadata))
        {
            return Task.CompletedTask;
        }

        return RecordExhaustionAsync(runId, metadata.Boundary, metadata.DispatchId);
    }

    private async Task RecordExhaustionAsync(
        string runId,
        AgentHostDispatchBoundary? boundary,
        string dispatchId)
    {
        if (boundary is null || boundary.IsResumableAssistant || _runStore is null
            || !RunId.TryParse(boundary.OwningRunId, out var parsed))
            return;

        var changed = await _runStore.TrySetTerminalOutcomeAsync(
            parsed,
            TerminalRunOutcome.Create(
                RunStatus.Failed,
                EventTypes.RunFailed,
                new
                {
                    errorCode = "agent_host_unavailable",
                    retryable = true,
                    dispatchId,
                },
                DateTimeOffset.UtcNow,
                boundary.LifecycleGeneration),
            "agent_host_unavailable",
            CancellationToken.None).ConfigureAwait(false);
        if (changed)
            return;

        var current = await _runStore.GetAsync(parsed, CancellationToken.None).ConfigureAwait(false);
        if (current is not null
            && current.LifecycleGeneration == boundary.LifecycleGeneration
            && current.Status == RunStatus.InProgress)
        {
            throw new InvalidOperationException(
                $"AgentHost exhaustion for run '{runId}' could not be durably terminalized.");
        }
    }

    private static WorkflowAgentInfrastructureException AgentHostUnavailable(string runId, Exception? inner) =>
        new(
            "agent_host_unavailable",
            $"AgentHost is unavailable for run '{runId}'. Retry the run.",
            inner,
            isRetryable: true);

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
