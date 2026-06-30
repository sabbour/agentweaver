using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.AgentRuntime.Workflow;
using k8s;
using Agentweaver.SandboxExec;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Configures the Kubernetes SandboxClaim backend.
/// Bound from the <c>Sandbox:Kubernetes</c> configuration section.
/// </summary>
public sealed class KubernetesSandboxOptions
{
    public string Namespace { get; init; } = "agentweaver";
    public string TemplateRef { get; init; } = "agentweaver-sandbox";
    /// <summary>
    /// SandboxWarmPool the generic command-exec claim binds to. In the v1beta1 CRD a
    /// <c>SandboxClaim</c> references a <c>SandboxWarmPool</c> (<c>spec.warmPoolRef.name</c>),
    /// which in turn references the SandboxTemplate. Default: <c>agentweaver-sandbox</c>.
    /// </summary>
    public string WarmPoolRef { get; init; } = "agentweaver-sandbox";
    /// <summary>Path where the shared workspace PVC is mounted inside API and sandbox pods.</summary>
    public string WorkspaceMountPath { get; init; } = "/workspace";
    /// <summary>SandboxClaim TTL. Command timeouts are capped below this so controller GC cannot interrupt exec.</summary>
    public int TimeoutSeconds { get; init; } = 600;
    /// <summary>Cluster service CIDR that must be excluded by sandbox egress policy.</summary>
    public string? ServiceCidr { get; init; }
    public IReadOnlyList<string> SandboxEgressCidrExclusions { get; init; } = [];

    // ── Pod-per-run AgentHost lifecycle options (spec §9 / Q3 hybrid) ─────────

    /// <summary>
    /// SandboxWarmPool the AgentHost (pod-per-run) claim binds to in the v0.5.0 v1beta1 CRD
    /// (<c>spec.warmPoolRef.name</c>). The pool itself references the AgentHost SandboxTemplate.
    /// Default: <c>agentweaver-agent-host</c>.
    /// </summary>
    public string AgentHostWarmPoolRef { get; init; } = "agentweaver-agent-host";

    /// <summary>
    /// Port the AgentHost Kestrel listener binds to inside the pod.
    /// Worker builds the A2A endpoint as <c>http://&lt;podIP&gt;:&lt;AgentHostPort&gt;&lt;AgentHostA2APath&gt;</c>.
    /// TLS/mTLS termination is owned by Link (H1) — leave hook here for cert wiring.
    /// Default: 8088.
    /// </summary>
    public int AgentHostPort { get; init; } = 8088;

    /// <summary>
    /// A2A path prefix mounted by <c>MapA2AHttpJson</c> inside the AgentHost pod.
    /// Must match <c>AgentHost:A2APath</c> set in the pod's configuration.
    /// Default: <c>/a2a/agent</c>.
    /// </summary>
    public string AgentHostA2APath { get; init; } = "/a2a/agent";

    /// <summary>
    /// When <see langword="true"/> (default) the AgentHost A2A endpoint uses <c>https</c> with
    /// mTLS (H1). When <see langword="false"/> (PoC only) it uses plain <c>http</c>. Drives the
    /// scheme via <see cref="AgentHostEndpoint"/> and is injected into the pod as
    /// <c>AgentHost__RequireMtls</c>. Config key: <c>Sandbox:AgentHost:RequireMtls</c>.
    /// </summary>
    public bool RequireMtls { get; init; } = true;

    // ── AgentHost readiness gate (A2A cold-start race) ───────────────────────

    /// <summary>
    /// Path the AgentHost exposes for liveness/readiness on <see cref="AgentHostPort"/>. The executor
    /// polls <c>{scheme}://{podIP}:{port}{AgentHostHealthzPath}</c> after the claim binds and BEFORE
    /// returning the A2A endpoint, so the worker never sends the first turn into the Kestrel boot
    /// window (which would be refused). Default: <c>/healthz</c>.
    /// </summary>
    public string AgentHostHealthzPath { get; init; } = "/healthz";

    /// <summary>
    /// Maximum time to wait for the AgentHost to start serving <see cref="AgentHostHealthzPath"/>
    /// before failing the launch deterministically. Default: 90s (covers cold-start Kestrel bind).
    /// </summary>
    public int AgentHostReadyTimeoutSeconds { get; init; } = 90;

    /// <summary>Interval between AgentHost readiness probe attempts. Default: 1000ms.</summary>
    public int AgentHostReadyPollIntervalMs { get; init; } = 1000;

    /// <summary>
    /// Azure Key Vault URI injected into AgentHost pods as <c>AgentHost__KeyVaultUri</c> so the
    /// warm pod can fetch the run owner's GitHub token via workload identity at /configure-time
    /// (Option C). Sourced from the API's own KV config (<c>Auth:TokenStore:KeyVaultUri</c>). When
    /// null/empty the env var is omitted and the pod falls back to the CSI file-mount path.
    /// </summary>
    public string? KvUri { get; init; }
}

/// <summary>
/// Top-level sandbox runtime options bound from the <c>Sandbox</c> configuration section
/// (not under <c>Sandbox:Kubernetes</c>). Controls the agent-execution mode and
/// the pod-release-on-suspend behaviour (Q3 hybrid).
/// </summary>
public sealed class SandboxRuntimeOptions
{
    /// <summary>
    /// Agent execution mode.
    /// <list type="bullet">
    ///   <item><c>in-api</c> (default) — run agents in-process; instant rollback path (§4.7.6).</item>
    ///   <item><c>pod-per-run</c> — launch a per-run AgentHost sandbox pod; activate A2A transport.</item>
    /// </list>
    /// </summary>
    public string AgentExecutionMode { get; init; } = "in-api";

    /// <summary>
    /// When <c>true</c> (default) and <see cref="AgentExecutionMode"/> is <c>pod-per-run</c>,
    /// the AgentHost pod is released (SandboxClaim deleted) whenever the MAF graph suspends
    /// at a <c>RequestPort</c> (HITL/review gate) or the coordinator idles awaiting children.
    /// Set to <c>false</c> to keep the pod warm across suspension (lower resume latency, higher
    /// resource cost; recommended only for short-wait HITL in dev/staging).
    /// </summary>
    public bool ReleasePodOnSuspend { get; init; } = true;

    /// <inheritdoc cref="AgentExecutionMode"/>
    public bool IsPodPerRun =>
        string.Equals(AgentExecutionMode, "pod-per-run", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Executes sandboxed commands inside a pre-warmed Kubernetes pod obtained via a
/// <c>SandboxClaim</c> CRD.  Lifecycle:
/// <list type="number">
///   <item>Create a <c>SandboxClaim</c> resource (adopts a warm pod from the pool).</item>
///   <item>Poll until the claim transitions to <c>phase: Bound</c> and reports a pod name.</item>
///   <item>Run the command via pod-exec (Kubernetes WebSocket exec API).</item>
///   <item>Delete the claim on completion (controller GC cleans up the pod and service).</item>
/// </list>
/// Automatically selected by the API when <c>KUBERNETES_SERVICE_HOST</c> is present
/// (see <see cref="SandboxExecutorFactory.IsInCluster"/>).
/// </summary>
internal sealed class KubernetesSandboxExecutor : ISandboxExecutor, IAgentHostPodLifecycle
{
    private const string ApiGroup = SandboxClaimConventions.ApiGroup;
    private const string ApiVersion = SandboxClaimConventions.ApiVersion;
    private const string ClaimPlural = SandboxClaimConventions.ClaimPlural;
    private const string ContainerName = "agentweaver-sandbox";

    /// <summary>Namespace ResourceQuota that caps total agent-pod CPU (spec: 24 cores).</summary>
    private const string ResourceQuotaName = "agentweaver-quota";

    /// <summary>CPU cores reserved by a single AgentHost pod (its <c>limits.cpu</c>).</summary>
    private const double AgentPodCpuLimit = 2.0;

    private readonly IKubernetes _client;
    private readonly KubernetesSandboxOptions _options;
    private readonly ILogger<KubernetesSandboxExecutor> _logger;
    private readonly IPodNameRegistry? _podRegistry;
    private readonly IAgentHostTurnTokenRegistry? _turnTokenRegistry;
    // Polls the AgentHost /healthz after bind and before returning the endpoint, closing the
    // A2A cold-start race (pod Running ~20-30s before Kestrel binds :8088). Null in unit tests
    // that only assert the claim body → readiness gate is skipped.
    private readonly IAgentHostReadinessProbe? _readinessProbe;
    // Resolves the run's submitting user so the pod can be scoped (via /configure) to the run owner's
    // Copilot-entitled token instead of the installation token. Null when the run→user lookup is
    // unavailable.
    private readonly IRunSubmittingUserResolver? _submittingUserResolver;
    // Used to POST /configure to the warm pod after bind (warm-pool deferred-config path). Null in
    // unit tests → the /configure call is skipped (same null-skip convention as the readiness probe).
    private readonly IHttpClientFactory? _httpClientFactory;

    public bool IsRealIsolation => true;
    public string BackendName => "kubernetes-sandbox-claim";
    public string SelectionReason =>
        "Kubernetes-native sandbox via SandboxClaim warm pool (Kata VM isolation, NetworkPolicy egress restriction).";
    public bool HasNetworkWarning => false;
    public string? NetworkWarningMessage => null;

    internal KubernetesSandboxExecutor(
        IKubernetes client,
        KubernetesSandboxOptions options,
        ILogger<KubernetesSandboxExecutor> logger,
        IPodNameRegistry? podRegistry = null,
        IAgentHostTurnTokenRegistry? turnTokenRegistry = null,
        IAgentHostReadinessProbe? readinessProbe = null,
        IRunSubmittingUserResolver? submittingUserResolver = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _podRegistry = podRegistry;
        _turnTokenRegistry = turnTokenRegistry;
        _readinessProbe = readinessProbe;
        _submittingUserResolver = submittingUserResolver;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        // Use the Agentweaver run ID as the claim name when available so the pod can be
        // looked up by run ID later (preview port-forward). Fall back to a random ID.
        string claimBase = string.IsNullOrEmpty(command.AgentweaverRunId)
            ? Guid.NewGuid().ToString("N")[..16]
            : command.AgentweaverRunId.Replace("-", "")[..Math.Min(16, command.AgentweaverRunId.Replace("-", "").Length)];
        var claimName = $"run-{claimBase}";

        var requestedTimeoutMs = command.TimeoutMs > 0
            ? command.TimeoutMs
            : _options.TimeoutSeconds * 1000;
        var maxCommandTimeoutMs = Math.Max(1000, (_options.TimeoutSeconds * 1000) - 30_000);
        var timeoutMs = Math.Min(requestedTimeoutMs, maxCommandTimeoutMs);
        if (timeoutMs < requestedTimeoutMs)
        {
            _logger.LogWarning(
                "KubernetesSandboxExecutor: command timeout clamped from {RequestedMs}ms to {TimeoutMs}ms so it stays below SandboxClaim TTL ({TtlSeconds}s)",
                requestedTimeoutMs, timeoutMs, _options.TimeoutSeconds);
        }

        string podWorkingDirectory;
        try
        {
            podWorkingDirectory = ResolvePodWorkingDirectory(command.WorkingDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "KubernetesSandboxExecutor: invalid workspace path {WorkingDirectory}; configured mount is {WorkspaceMountPath}",
                command.WorkingDirectory, _options.WorkspaceMountPath);
            return new SandboxExecResult(1, "", ex.Message, false, false);
        }

        _logger.LogInformation(
            "KubernetesSandboxExecutor: using workspace path {WorkspacePath} for claim {Claim} (requested {RequestedWorkingDirectory})",
            podWorkingDirectory, claimName, command.WorkingDirectory);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        var token = linked.Token;
        var claimCreated = false;

        try
        {
            _logger.LogInformation(
                "KubernetesSandboxExecutor: creating SandboxClaim {Claim}", claimName);
            await CreateClaimAsync(claimName, token);
            claimCreated = true;

            var podName = await WaitForBoundAsync(claimName, token);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: claim {Claim} bound to pod {Pod}", claimName, podName);

            // Register pod name so PortForwardService can locate it by Agentweaver run ID.
            // Run-scoped mappings are cleared by run lifecycle cleanup, not per command, so
            // preview tunnels can remain available for the whole run while the claim TTL is valid.
            if (!string.IsNullOrEmpty(command.AgentweaverRunId))
                _podRegistry?.Register(command.AgentweaverRunId, podName);

            return await ExecInPodAsync(podName, command, podWorkingDirectory, token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "KubernetesSandboxExecutor: timed out waiting for claim {Claim}", claimName);
            return new SandboxExecResult(-1, "", "Timed out waiting for sandbox pod.", true, false);
        }
        finally
        {
            if (claimCreated && string.IsNullOrEmpty(command.AgentweaverRunId))
                await DeleteClaimAsync(claimName);
            else if (claimCreated)
                _logger.LogDebug(
                    "KubernetesSandboxExecutor: retaining SandboxClaim {Claim} for run {RunId} preview until run cleanup or TTL",
                    claimName, command.AgentweaverRunId);
        }
    }

    public async IAsyncEnumerable<SandboxOutputChunk> StreamAsync(
        SandboxCommand command,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var result = await ExecuteAsync(command, ct);
        foreach (var line in result.Stdout.Split('\n'))
            yield return new SandboxOutputChunk(SandboxOutputStream.Stdout, line);
        if (!string.IsNullOrEmpty(result.Stderr))
            foreach (var line in result.Stderr.Split('\n'))
                yield return new SandboxOutputChunk(SandboxOutputStream.Stderr, line);
        yield return new SandboxOutputChunk(SandboxOutputStream.ExitCode, result.ExitCode.ToString());
    }

    // ── IAgentHostPodLifecycle — pod-per-run lifecycle (spec §9 / Q3) ─────────────

    /// <inheritdoc/>
    public async Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default)
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        // Fail fast before creating the claim if the namespace quota cannot admit another agent pod
        // (2 CPU). Without this the claim is accepted but the controller's pod reconcile is rejected
        // with "exceeded quota", which surfaces as a generic mid-turn failure. Throws
        // AgentHostCapacityPendingException so the launch path can park-and-retry instead of failing.
        await CheckQuotaHeadroomAsync(ct).ConfigureAwait(false);

        _logger.LogInformation(
            "KubernetesSandboxExecutor: launching AgentHost pod for run {RunId} via claim {Claim}",
            runId, claimName);

        // Resolve the run's submitting user so the pod can scope GitHub Copilot auth to that user's
        // signed-in token. The user's Key Vault secret name (Option C warm-pool path) is derived here
        // and delivered to the pod via /configure — never another user's secret.
        var submittingUser = await ResolveSubmittingUserAsync(runId, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(submittingUser))
        {
            throw new InvalidOperationException(
                $"Cannot launch AgentHost pod for run '{runId}' without a submitting user; " +
                "the /configure call must scope the pod to the run owner's Key Vault token.");
        }

        _logger.LogInformation(
            "KubernetesSandboxExecutor: resolved submitting user for run {RunId}; will configure pod via /configure.",
            runId);

        // ghtok-user--{base32(userId)} — the SAME mapping the API uses when persisting the token to KV.
        var kvUserSecretName = KeyVaultSecretStore.SanitizeKey("user:" + submittingUser);
        var turnToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var claimCreated = false;
        try
        {
            // Bind to the SHARED, pre-warmed AgentHost warm pool (replicas: 2). No per-run SPC,
            // SandboxTemplate, or warm pool — the pod is already warm and gets its per-run context
            // via the /configure POST below.
            await CreateAgentHostClaimAsync(claimName, _options.AgentHostWarmPoolRef, ct).ConfigureAwait(false);
            claimCreated = true;

            var podName = await WaitForBoundAsync(claimName, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: AgentHost claim {Claim} bound to pod {Pod}", claimName, podName);

            _podRegistry?.Register(runId, podName);
            _turnTokenRegistry?.RegisterTurnToken(runId, turnToken);

            var podIp = await GetPodIpAsync(podName, ct).ConfigureAwait(false);

            var endpointUrl = AgentHostEndpoint.Build(
                _options.RequireMtls, podIp, _options.AgentHostPort, _options.AgentHostA2APath);

            // A2A cold-start gate: the claim binds when the pod is Running, but the AgentHost Kestrel
            // listener takes ~20-30s more to bind :8088. Without this wait the worker's first A2A POST
            // hits a closed port → "Connection refused" → the run fails mid-turn. Poll /healthz until the
            // app is actually serving so a not-yet-ready pod is a deterministic LAUNCH failure instead.
            // NOTE: a warm/standby pod serves /healthz BEFORE /configure (the readiness gate exempts
            // /configure), so this confirms reachability prior to injecting the run context.
            if (_readinessProbe is not null)
            {
                var scheme = AgentHostEndpoint.Scheme(_options.RequireMtls);
                var readinessUrl =
                    $"{scheme}://{podIp}:{_options.AgentHostPort}{_options.AgentHostHealthzPath}";

                _logger.LogInformation(
                    "KubernetesSandboxExecutor: waiting for AgentHost readiness for run {RunId} at {Url}",
                    runId, readinessUrl);

                try
                {
                    await _readinessProbe.WaitUntilReadyAsync(readinessUrl, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"AgentHost pod '{podName}' for run '{runId}' did not become ready at {readinessUrl} " +
                        $"within {_options.AgentHostReadyTimeoutSeconds}s; failing the launch.", ex);
                }
            }

            // Warm-pool deferred /configure: inject the per-run RunId/UserId/TurnBearerToken and the
            // KV secret name into the already-warm pod, which then runs SetupAsync and becomes ready.
            await CallAgentHostConfigureAsync(
                podIp, _options.AgentHostPort, runId, submittingUser, turnToken, kvUserSecretName, ct)
                .ConfigureAwait(false);

            _podRegistry?.RegisterAgentEndpoint(runId, endpointUrl);

            _logger.LogInformation(
                "KubernetesSandboxExecutor: AgentHost A2A endpoint for run {RunId} = {Endpoint}",
                runId, endpointUrl);

            return endpointUrl;
        }
        catch
        {
            if (claimCreated)
                await DeleteClaimAsync(claimName).ConfigureAwait(false);
            _podRegistry?.Unregister(runId);
            _turnTokenRegistry?.UnregisterTurnToken(runId);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        _logger.LogInformation(
            "KubernetesSandboxExecutor: releasing AgentHost pod for run {RunId} (claim {Claim})",
            runId, claimName);

        await DeleteClaimAsync(claimName).ConfigureAwait(false);
        _podRegistry?.Unregister(runId);
        _turnTokenRegistry?.UnregisterTurnToken(runId);

        _logger.LogInformation(
            "KubernetesSandboxExecutor: AgentHost pod released for run {RunId}", runId);
    }

    /// <summary>
    /// Resolves the submitting user for <paramref name="runId"/> via the injected resolver, never
    /// throwing (a lookup failure must not fail the launch — it degrades to omitting the user id).
    /// </summary>
    private async Task<string?> ResolveSubmittingUserAsync(string runId, CancellationToken ct)
    {
        if (_submittingUserResolver is null)
            return null;

        try
        {
            return await _submittingUserResolver.GetSubmittingUserAsync(runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "KubernetesSandboxExecutor: failed to resolve submitting user for run {RunId}; " +
                "AgentHost__UserId will be omitted.",
                runId);
            return null;
        }
    }

    /// <summary>
    /// Creates a <c>SandboxClaim</c> that binds to the SHARED, pre-warmed AgentHost warm pool
    /// (<c>AgentHostWarmPoolRef</c>, replicas: 2). No <c>spec.env</c> is injected — the v0.5.0
    /// controller bypasses warm pool adoption whenever <c>spec.env</c> or
    /// <c>spec.volumeClaimTemplates</c> are present. All static config lives in the SandboxTemplate
    /// or agenthost-config ConfigMap. The per-run context (RunId / UserId / TurnBearerToken /
    /// KV secret name) is delivered after bind via <c>POST /configure</c>
    /// (<see cref="CallAgentHostConfigureAsync"/>).
    /// </summary>
    private Task CreateAgentHostClaimAsync(
        string claimName, string warmPoolName, CancellationToken ct)
    {
        var manifest = new
        {
            apiVersion = $"{ApiGroup}/{ApiVersion}",
            kind = "SandboxClaim",
            metadata = new { name = claimName, @namespace = _options.Namespace },
            spec = new
            {
                // v0.5.0 v1beta1 SandboxClaimSpec: spec.warmPoolRef.name references the
                // SandboxWarmPool to bind from. sandboxTemplateRef+warmpool were the
                // v0.4.x/v1alpha1 deprecated fields.
                warmPoolRef = new { name = warmPoolName },
                lifecycle = new { ttlSecondsAfterFinished = _options.TimeoutSeconds, shutdownPolicy = "Delete" },
            },
        };

        return _client.CustomObjects.CreateNamespacedCustomObjectAsync(
            manifest, ApiGroup, ApiVersion, _options.Namespace, ClaimPlural,
            cancellationToken: ct);
    }

    /// <summary>
    /// Injects the per-run context into an already-warm AgentHost pod via its one-time
    /// <c>POST /configure</c> endpoint. The pod then fetches ONLY <paramref name="kvUserSecretName"/>
    /// from Key Vault (its configured user's token) and runs SetupAsync. The endpoint is guarded by
    /// NetworkPolicy (ingress to AgentHost pods restricted to API/worker), not the TurnBearerToken
    /// (which is itself delivered here). Idempotency: a second call returns 409 and is treated as a
    /// hard launch failure.
    /// </summary>
    private async Task CallAgentHostConfigureAsync(
        string podIp, int port, string runId, string userId, string turnBearerToken,
        string kvUserSecretName, CancellationToken ct)
    {
        if (_httpClientFactory is null)
        {
            // No HttpClient available (unit tests). Mirrors the readiness-probe null-skip; in-cluster
            // the factory is always present, so this never short-circuits a real launch.
            _logger.LogWarning(
                "KubernetesSandboxExecutor: no IHttpClientFactory — skipping /configure for run {RunId}.",
                runId);
            return;
        }

        var scheme = AgentHostEndpoint.Scheme(_options.RequireMtls);
        var configureUrl = $"{scheme}://{podIp}:{port}/configure";
        var body = new
        {
            runId,
            userId,
            turnBearerToken,
            kvUserSecretName,
        };

        _logger.LogInformation(
            "KubernetesSandboxExecutor: configuring AgentHost pod for run {RunId} at {Url}",
            runId, configureUrl);

        using var client = _httpClientFactory.CreateClient(HttpAgentHostReadinessProbe.HttpClientName);
        using var response = await client
            .PostAsJsonAsync(configureUrl, body, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"AgentHost /configure for run '{runId}' failed: HTTP {(int)response.StatusCode} {detail}");
        }
    }

    /// <inheritdoc/>
    public Task CheckAgentHostCapacityAsync(CancellationToken ct = default) =>
        CheckQuotaHeadroomAsync(ct);

    /// <summary>
    /// Pre-launch guard: throws <see cref="AgentHostCapacityPendingException"/> when the namespace
    /// ResourceQuota has less than one agent pod's worth of CPU headroom
    /// (<see cref="AgentPodCpuLimit"/>). Capacity-pending is a <i>retry signal</i>, not a hard
    /// failure: the reaper frees orphaned pods and the node pool can scale out, so the caller queues
    /// and retries. The quota check itself is best-effort: if the quota does not exist or the read
    /// fails, it logs a warning and returns so a transient API/quota issue never blocks a launch that
    /// the controller would otherwise admit.
    /// </summary>
    private async Task CheckQuotaHeadroomAsync(CancellationToken ct)
    {
        double used;
        double hard;
        try
        {
            var quota = await _client.CoreV1.ReadNamespacedResourceQuotaAsync(
                ResourceQuotaName, _options.Namespace, cancellationToken: ct).ConfigureAwait(false);

            var usedStr = TryGetQuotaValue(quota?.Status?.Used, "limits.cpu");
            var hardStr = TryGetQuotaValue(quota?.Status?.Hard, "limits.cpu");

            if (usedStr is null || hardStr is null ||
                !TryParseCpu(usedStr, out used) || !TryParseCpu(hardStr, out hard))
            {
                _logger.LogWarning(
                    "KubernetesSandboxExecutor: agent pod quota '{Quota}' missing or unparseable " +
                    "limits.cpu (used={Used}, hard={Hard}); skipping pre-launch quota check",
                    ResourceQuotaName, usedStr ?? "(none)", hardStr ?? "(none)");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: could not read ResourceQuota '{Quota}' in namespace " +
                "{Namespace}; skipping pre-launch quota check (best-effort)",
                ResourceQuotaName, _options.Namespace);
            return;
        }

        if (hard - used < AgentPodCpuLimit)
        {
            _logger.LogWarning(
                "KubernetesSandboxExecutor: agent pod quota exhausted ({Used}/{Hard} CPU used); " +
                "need {Limit} CPU headroom to launch a new agent pod — signalling capacity-pending retry",
                used, hard, AgentPodCpuLimit);
            throw new AgentHostCapacityPendingException(used, hard, "quota_exceeded");
        }
    }

    private static string? TryGetQuotaValue(
        IDictionary<string, k8s.Models.ResourceQuantity>? map, string key)
    {
        if (map is not null && map.TryGetValue(key, out var quantity) && quantity is not null)
            return quantity.ToString();
        return null;
    }

    /// <summary>
    /// Parses a Kubernetes CPU quantity into whole cores. Handles plain cores (<c>"24"</c>,
    /// <c>"1.5"</c>) and the millicore suffix (<c>"500m"</c> = 0.5 cores). Returns
    /// <see langword="false"/> for an unrecognized format.
    /// </summary>
    internal static bool TryParseCpu(string? value, out double cores)
    {
        cores = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        value = value.Trim();
        if (value.EndsWith("m", StringComparison.Ordinal))
        {
            var millis = value[..^1];
            if (double.TryParse(millis, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var m))
            {
                cores = m / 1000.0;
                return true;
            }
            return false;
        }

        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out cores);
    }

    /// <summary>
    /// Reads the pod IP from the Kubernetes API after the claim is Bound.
    /// Polls every 2 s until <c>status.podIP</c> is non-empty (pod has been scheduled
    /// and assigned a network address).
    /// </summary>
    private async Task<string> GetPodIpAsync(string podName, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var pod = await _client.CoreV1.ReadNamespacedPodAsync(
                podName, _options.Namespace, cancellationToken: ct).ConfigureAwait(false);

            var ip = pod?.Status?.PodIP;
            if (!string.IsNullOrWhiteSpace(ip))
                return ip;

            _logger.LogDebug(
                "KubernetesSandboxExecutor: waiting for pod IP of {Pod} (current: {Ip})",
                podName, ip ?? "(none)");

            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
    }

    // ── Claim management ──────────────────────────────────────────────────────────

    private Task CreateClaimAsync(string claimName, CancellationToken ct)
    {
        // The cluster service CIDR must be present in SandboxEgressCidrExclusions so
        // sandbox NetworkPolicy does not accidentally allow in-cluster service egress.
        var manifest = new
        {
            apiVersion = $"{ApiGroup}/{ApiVersion}",
            kind = "SandboxClaim",
            metadata = new { name = claimName, @namespace = _options.Namespace },
            spec = new
            {
                // v0.5.0 v1beta1 SandboxClaimSpec: spec.warmPoolRef.name references the
                // SandboxWarmPool to bind from. sandboxTemplateRef+warmpool were the
                // v0.4.x/v1alpha1 deprecated fields.
                warmPoolRef = new { name = _options.WarmPoolRef },
                lifecycle = new { ttlSecondsAfterFinished = _options.TimeoutSeconds, shutdownPolicy = "Delete" },
            },
        };

        return _client.CustomObjects.CreateNamespacedCustomObjectAsync(
            manifest, ApiGroup, ApiVersion, _options.Namespace, ClaimPlural,
            cancellationToken: ct);
    }

    /// <summary>
    /// Polls every 2 s until the claim's <c>Ready</c> condition is <c>True</c>; returns the bound
    /// pod name from <c>status.sandbox.name</c>.
    /// </summary>
    private async Task<string> WaitForBoundAsync(string claimName, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var raw = await _client.CustomObjects.GetNamespacedCustomObjectAsync(
                ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName,
                cancellationToken: ct);

            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);

            // Surface a controller reconcile failure (e.g. "exceeded quota") as a deterministic
            // launch failure with a precise reason instead of polling until the caller times out.
            var reconcilerError = SandboxClaimConventions.TryGetReconcilerError(doc.RootElement);
            if (reconcilerError is not null)
            {
                _logger.LogWarning(
                    "KubernetesSandboxExecutor: claim {Claim} reconcile failed: {Error}",
                    claimName, reconcilerError);
                throw new AgentHostPodReconcilerErrorException(
                    $"SandboxClaim '{claimName}' could not be provisioned: {reconcilerError}");
            }

            var podName = SandboxClaimConventions.TryGetBoundPodName(doc.RootElement);
            if (!string.IsNullOrEmpty(podName))
                return podName;

            await Task.Delay(2000, ct);
        }
    }

    private async Task DeleteClaimAsync(string claimName)
    {
        try
        {
            await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: deleted claim {Claim}", claimName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: could not delete claim {Claim} (best-effort)", claimName);
        }
    }

    // ── Command execution ─────────────────────────────────────────────────────────

    private async Task<SandboxExecResult> ExecInPodAsync(
        string podName, SandboxCommand command, string podWorkingDirectory, CancellationToken ct)
    {
        const int maxOutputBytes = 4 * 1024 * 1024;

        var shellScript = BuildShellScript(command, podWorkingDirectory);

        var ws = await _client.WebSocketNamespacedPodExecAsync(
            podName, _options.Namespace,
            new[] { "/bin/sh", "-c", shellScript },
            container: ContainerName,
            stdin: false, stdout: true, stderr: true, tty: false,
            cancellationToken: ct);

        using var demux = new StreamDemuxer(ws, StreamType.RemoteCommand);
        demux.Start();

        using var stdoutStream = demux.GetStream(ChannelIndex.StdOut, null);
        using var stderrStream = demux.GetStream(ChannelIndex.StdErr, null);
        // Channel 3 (Error) carries the terminal v1.Status payload with the real exit code.
        using var statusStream = demux.GetStream(ChannelIndex.Error, null);

        var stdoutTask = ReadBoundedAsync(stdoutStream, maxOutputBytes, ct);
        var stderrTask = ReadBoundedAsync(stderrStream, maxOutputBytes, ct);
        var statusTask = ReadBoundedAsync(statusStream, maxOutputBytes, ct);

        await Task.WhenAll(stdoutTask, stderrTask, statusTask);

        var (stdoutBytes, stdoutTruncated) = await stdoutTask;
        var (stderrBytes, stderrTruncated) = await stderrTask;
        var (statusBytes, _) = await statusTask;

        var stdout = SandboxOutputRedactor.Default.Redact(Encoding.UTF8.GetString(stdoutBytes));
        var stderr = SandboxOutputRedactor.Default.Redact(Encoding.UTF8.GetString(stderrBytes));
        var exitCode = ParseExitCode(Encoding.UTF8.GetString(statusBytes));

        return new SandboxExecResult(
            exitCode, stdout, stderr, false, stdoutTruncated || stderrTruncated);
    }

    /// <summary>
    /// Reads up to <paramref name="maxBytes"/> from a stream, stopping at the cap.
    /// Returns the bytes collected and whether the output was truncated.
    /// </summary>
    private static async Task<(byte[] Bytes, bool Truncated)> ReadBoundedAsync(
        Stream stream, int maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        bool truncated = false;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            int remaining = maxBytes - (int)buffer.Length;
            if (remaining <= 0) { truncated = true; break; }
            int take = Math.Min(read, remaining);
            buffer.Write(chunk, 0, take);
            if (take < read) { truncated = true; break; }
        }
        return (buffer.ToArray(), truncated);
    }

    /// <summary>
    /// Parses the terminal v1.Status JSON emitted on channel 3.
    /// <c>status: "Success"</c> → exit 0. <c>status: "Failure"</c> → the ExitCode
    /// cause from <c>details.causes</c> (defaulting to 1 if not present).
    /// </summary>
    private static int ParseExitCode(string statusJson)
    {
        if (string.IsNullOrWhiteSpace(statusJson))
            return 0;

        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
                return 0;

            if (root.TryGetProperty("details", out var details) &&
                details.TryGetProperty("causes", out var causes) &&
                causes.ValueKind == JsonValueKind.Array)
            {
                foreach (var cause in causes.EnumerateArray())
                {
                    var reason = cause.TryGetProperty("reason", out var r) ? r.GetString() : null;
                    if (string.Equals(reason, "ExitCode", StringComparison.OrdinalIgnoreCase) &&
                        cause.TryGetProperty("message", out var m) &&
                        int.TryParse(m.GetString(), out var code))
                        return code;
                }
            }

            // Failure status with no parseable ExitCode cause → non-zero.
            return 1;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private string ResolvePodWorkingDirectory(string requestedWorkingDirectory)
    {
        var mountPath = NormalizeUnixPath(_options.WorkspaceMountPath, forceAbsolute: true);
        if (string.IsNullOrWhiteSpace(requestedWorkingDirectory))
            return mountPath;

        var requested = NormalizeUnixPath(requestedWorkingDirectory, forceAbsolute: false);
        if (IsSameOrChildPath(requested, mountPath))
            return requested;

        throw new InvalidOperationException(
            $"Kubernetes sandbox working directory '{requestedWorkingDirectory}' is not under mounted workspace '{mountPath}'. " +
            "Configure Workspace:PersistentVolume:MountRoot/Workspace:Path to match the workspace PVC mount used by sandbox pods.");
    }

    private static bool IsSameOrChildPath(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || (root == "/" && path.StartsWith("/", StringComparison.Ordinal))
        || path.StartsWith(root + "/", StringComparison.Ordinal);

    private static string NormalizeUnixPath(string path, bool forceAbsolute)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.Contains("//", StringComparison.Ordinal))
            normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
        if (forceAbsolute && !normalized.StartsWith("/", StringComparison.Ordinal))
            normalized = "/" + normalized;
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }

    private static string BuildShellScript(SandboxCommand command, string podWorkingDirectory)
    {
        var sb = new StringBuilder();

        if (command.Environment is { Count: > 0 })
        {
            foreach (var (key, value) in command.Environment)
                sb.AppendLine($"export {key}={ShellSingleQuote(value)}");
        }

        sb.AppendLine($"cd {ShellSingleQuote(podWorkingDirectory)}");

        sb.Append(command.CommandLine);
        return sb.ToString();
    }

    private static string ShellSingleQuote(string s) =>
        "'" + s.Replace("'", "'\\''") + "'";
}
