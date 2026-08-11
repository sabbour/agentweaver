using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Infrastructure;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Domain;
using k8s;
using k8s.Autorest;
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
    /// Minimum age before the orphan reaper may delete an AgentHost claim that is absent from the
    /// active-run map. Config key: <c>Sandbox:Kubernetes:AgentHostClaimCreationGraceSeconds</c>.
    /// The effective value is floored above <see cref="AgentHostReadyTimeoutSeconds"/>.
    /// Default: 300s.
    /// </summary>
    public int AgentHostClaimCreationGraceSeconds { get; init; } = 300;

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

    /// <summary>
    /// Bounded attempt count for <see cref="ExecuteK8sWithRetryAsync{T}"/> — the total number of
    /// tries (initial + retries) for a transient Kubernetes API fault (issue #230). A transient
    /// connection reset (SocketException 104 → IOException → HttpRequestException) that used to fail
    /// a subtask fatally is now retried with exponential backoff + jitter.
    /// </summary>
    private const int MaxK8sAttempts = 3;

    /// <summary>
    /// Cadence for the <see cref="EventTypes.SandboxProvisioningPending"/> heartbeat emitted while an
    /// AgentHost <c>SandboxClaim</c> is still being provisioned (unbound). Must stay well under the
    /// parent coordinator's <c>Coordinator:SubtaskStallTimeoutMinutes</c> (default 5 min) so each
    /// provisioning wait window is punctuated by an event that keeps the outbound stream flowing and
    /// resets the stall timer (issue #217, mirrors the #212 tool.approval_pending heartbeat cadence).
    /// </summary>
    internal static readonly TimeSpan SandboxProvisioningHeartbeatInterval = TimeSpan.FromSeconds(20);

    private readonly IKubernetes _client;
    private readonly KubernetesSandboxOptions _options;
    private readonly ILogger<KubernetesSandboxExecutor> _logger;
    private readonly IPodNameRegistry? _podRegistry;
    private readonly IAgentHostTurnTokenRegistry? _turnTokenRegistry;
    private readonly Security.IRunAuthorshipCapabilityStore? _authorshipCapabilityStore;
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
    // Resolves the run owner's GitHub token so the API can pass it in /configure, avoiding the need
    // for the kata VM pod to call Azure AD or Key Vault (blocked by Cilium FQDN policies).
    private readonly IGitHubTokenStore? _tokenStore;
    // Refresh-aware token accessor (issue #523): a Build & Test gate can launch its AgentHost pod for
    // the FIRST time (a fresh pod, not yet /configure'd for this run) many minutes after the run's
    // earlier subtask stages — long enough for the submitting user's Copilot-entitled OAuth access
    // token to cross its expiry skew window. Reading the raw entry via IGitHubTokenStore.GetAsync (as
    // ResolveGitHubAccessTokenAsync previously did) can hand a stale/expired access token to the pod,
    // which the pod then trusts unconditionally (its "fast path" skips its own Key Vault fetch
    // whenever a pre-resolved token arrives) — producing GitHubCopilotUnauthorizedException at
    // /configure. Routing through the same GetValidAccessTokenAsync used by GitHubCopilotClientFactory
    // ensures a near-expiry token is transparently rotated before being handed to a newly-launched pod.
    // Null in unit tests → falls back to the raw (non-refreshing) token store read. When present,
    // it is authoritative: a null/failed refresh must never fall back to the rejected raw token.
    private readonly IGitHubAccessTokenProvider? _accessTokenProvider;
    // Replica-safe run secret store used to persist the per-run preview-runner credential so a
    // reconcile/keepalive on either API replica can re-fetch it, and to durably DELETE it on pod
    // release (spec-006 decouple-preview, BLOCKER A / RESIDUAL). Null in unit tests → no minting.
    private readonly ISecretStore? _secretStore;
    // Durable run-event log used to emit sandbox.provisioning_pending heartbeats into the CHILD run's
    // stream while its AgentHost claim is still being scheduled by Kubernetes (unbound). Keeps the
    // parent coordinator's stall timer alive during a legitimately-long Pending wait (issue #217).
    // Null in unit tests → the heartbeat is skipped (same null-skip convention as the readiness probe).
    private readonly IRunEventStream? _runEventStream;
    // Source of the per-run AutoApproveTools flag propagated to the warm pod via /configure (bug
    // #221). Null in unit tests → the flag defaults false (same null-skip convention as above).
    private readonly IRunOptionsStore? _runOptions;
    // First-class preview lifecycle reconciler. ReleaseAgentHostPodAsync derives durable
    // Previewable/PreviewActive state and applies all retention or cleanup effects before deciding
    // whether to delete the claim.
    private readonly Agentweaver.Api.Sandbox.Preview.ISandboxPreviewService? _previewService;

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
        IHttpClientFactory? httpClientFactory = null,
        IGitHubTokenStore? tokenStore = null,
        ISecretStore? secretStore = null,
        IRunEventStream? runEventStream = null,
        IRunOptionsStore? runOptions = null,
        IGitHubAccessTokenProvider? accessTokenProvider = null,
        Agentweaver.Api.Sandbox.Preview.ISandboxPreviewService? previewService = null,
        Security.IRunAuthorshipCapabilityStore? authorshipCapabilityStore = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _podRegistry = podRegistry;
        _turnTokenRegistry = turnTokenRegistry;
        _readinessProbe = readinessProbe;
        _submittingUserResolver = submittingUserResolver;
        _httpClientFactory = httpClientFactory;
        _tokenStore = tokenStore;
        _secretStore = secretStore;
        _runEventStream = runEventStream;
        _runOptions = runOptions;
        _accessTokenProvider = accessTokenProvider;
        _previewService = previewService;
        _authorshipCapabilityStore = authorshipCapabilityStore;
    }

    public async Task<SandboxExecResult> ExecuteAsync(
        SandboxCommand command, CancellationToken ct = default)
    {
        // Use the Agentweaver run ID as the claim name when available so the pod can be
        // looked up by run ID later (preview port-forward). Fall back to a random ID.
        var claimName = string.IsNullOrEmpty(command.AgentweaverRunId)
            ? $"run-{Guid.NewGuid():N}"[..20]
            : SandboxClaimConventions.DeriveRunCommandClaimName(command.AgentweaverRunId);

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
            claimCreated = await CreateClaimAsync(claimName, token);

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
    public Task<string> LaunchAgentHostPodAsync(string runId, CancellationToken ct = default) =>
        LaunchAgentHostPodAsync(runId, new AgentHostLaunchContext(SharedWorkingDirectory: null), ct);

    /// <inheritdoc/>
    public Task<string> LaunchAgentHostPodAsync(
        string runId,
        string? workingDirectoryOverride,
        CancellationToken ct = default) =>
        LaunchAgentHostPodAsync(
            runId,
            new AgentHostLaunchContext(SharedWorkingDirectory: workingDirectoryOverride),
            ct);

    /// <inheritdoc/>
    public async Task<string> LaunchAgentHostPodAsync(
        string runId,
        AgentHostLaunchContext launchContext,
        CancellationToken ct = default)
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
        var requestedWorkingDirectory = string.IsNullOrWhiteSpace(launchContext.SharedWorkingDirectory)
            ? null
            : Path.GetFullPath(launchContext.SharedWorkingDirectory);

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
        // With Entra sign-in the user's credentials live under the ACTIVE linked GitHub identity's
        // scope (user-link:{oid}:{login}), so resolve the effective scope rather than assuming the
        // legacy per-user scope, which is never written in that mode.
        var effectiveScope = _tokenStore is IEffectiveGitHubTokenScopeResolver scopeResolver
            ? await scopeResolver.ResolveEffectiveScopeAsync(submittingUser!, ct).ConfigureAwait(false)
            : GitHubTokenScope.ForUser(submittingUser!);
        var kvUserSecretName = KeyVaultSecretStore.SanitizeKey(effectiveScope.Key);
        var turnToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var claimCreated = false;
        try
        {
            // Bind to the SHARED, pre-warmed AgentHost warm pool (replicas: 2). No per-run SPC,
            // SandboxTemplate, or warm pool — the pod is already warm and gets its per-run context
            // via the /configure POST below.
            claimCreated = await CreateAgentHostClaimAsync(
                claimName, _options.AgentHostWarmPoolRef, requestedWorkingDirectory, runId, ct).ConfigureAwait(false);

            if (!claimCreated && launchContext.Purpose == AgentHostPurpose.OperatorAssistant)
            {
                // Every operator turn carries the CURRENT browser/platform bearer. An orphaned
                // claim from a crashed prior turn is already configured with the old credential
                // and /configure is intentionally one-shot, so it must never be reused.
                _logger.LogInformation(
                    "KubernetesSandboxExecutor: recreating existing AgentHost claim {Claim} for a fresh operator-assistant caller credential.",
                    claimName);
                await DeleteClaimAsync(claimName).ConfigureAwait(false);
                _podRegistry?.Unregister(runId);
                _turnTokenRegistry?.UnregisterTurnToken(runId);
                await Task.Delay(1000, ct).ConfigureAwait(false);
                claimCreated = await CreateAgentHostClaimAsync(
                    claimName, _options.AgentHostWarmPoolRef, requestedWorkingDirectory, runId, ct).ConfigureAwait(false);
                if (!claimCreated)
                {
                    throw new InvalidOperationException(
                        $"AgentHost claim '{claimName}' was deleted to refresh the operator-assistant caller credential, " +
                        "but the replacement create still conflicted.");
                }
            }
            else if (!claimCreated && launchContext.WorkspaceMode != ExecutionWorkspaceMode.Shared)
            {
                _logger.LogInformation(
                    "KubernetesSandboxExecutor: recreating existing AgentHost claim {Claim} for immutable pod-local workspace configuration (mode={Mode}).",
                    claimName,
                    launchContext.WorkspaceMode);
                await DeleteClaimAsync(claimName).ConfigureAwait(false);
                _podRegistry?.Unregister(runId);
                _turnTokenRegistry?.UnregisterTurnToken(runId);
                await Task.Delay(1000, ct).ConfigureAwait(false);
                claimCreated = await CreateAgentHostClaimAsync(
                    claimName, _options.AgentHostWarmPoolRef, requestedWorkingDirectory, runId, ct).ConfigureAwait(false);
                if (!claimCreated)
                {
                    throw new InvalidOperationException(
                        $"AgentHost claim '{claimName}' was deleted for immutable pod-local workspace configuration, " +
                        "but the replacement create still conflicted.");
                }
            }
            else if (!claimCreated && requestedWorkingDirectory is not null)
            {
                var existingWorkingDirectory = await TryGetAgentHostClaimWorkingDirectoryAsync(claimName, ct)
                    .ConfigureAwait(false);
                var sameWorktree = string.Equals(
                    existingWorkingDirectory, requestedWorkingDirectory, StringComparison.Ordinal);
                var hasTurnToken = !string.IsNullOrWhiteSpace(_turnTokenRegistry?.TryGetTurnToken(runId));

                if (!sameWorktree || !hasTurnToken)
                {
                    _logger.LogWarning(
                        "KubernetesSandboxExecutor: existing AgentHost claim {Claim} for run {RunId} " +
                        "is not reusable (sameWorktree={SameWorktree}, hasTurnToken={HasTurnToken}); recreating.",
                        claimName, runId, sameWorktree, hasTurnToken);
                    await DeleteClaimAsync(claimName).ConfigureAwait(false);
                    _podRegistry?.Unregister(runId);
                    _turnTokenRegistry?.UnregisterTurnToken(runId);
                    await Task.Delay(1000, ct).ConfigureAwait(false);
                    claimCreated = await CreateAgentHostClaimAsync(
                        claimName, _options.AgentHostWarmPoolRef, requestedWorkingDirectory, runId, ct).ConfigureAwait(false);
                    if (!claimCreated)
                    {
                        throw new InvalidOperationException(
                            $"AgentHost claim '{claimName}' for run '{runId}' was deleted for worktree reconfiguration, " +
                            "but the replacement create still conflicted. Retrying later avoids reusing a token-less or stale pod.");
                    }
                }
            }

            var podName = await WaitForBoundWithProvisioningHeartbeatAsync(runId, claimName, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: AgentHost claim {Claim} bound to pod {Pod}", claimName, podName);

            // Register also persists sandbox.execution_pod.bound into the shared RunEvents store so
            // graph snapshots/deltas on any API replica can resolve the execution pod.
            _podRegistry?.Register(runId, podName);
            if (claimCreated)
                _turnTokenRegistry?.RegisterTurnToken(runId, turnToken);

            var activeTurnToken = claimCreated
                ? turnToken
                : _turnTokenRegistry?.TryGetTurnToken(runId);
            if (_authorshipCapabilityStore is not null && !string.IsNullOrWhiteSpace(activeTurnToken))
            {
                await _authorshipCapabilityStore.RegisterAsync(
                    runId, activeTurnToken, DateTimeOffset.UtcNow.AddDays(1), ct).ConfigureAwait(false);
            }

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
            // Normal roles use the shared orchestration worktree. Local workspace modes carry
            // immutable source refs; AgentHost creates their effective root inside execution-scratch.
            if (claimCreated)
            {
                var (configProjectId, configAgentName) = _submittingUserResolver is not null
                    ? await _submittingUserResolver.GetRunIdentityAsync(runId, ct).ConfigureAwait(false)
                    : (null, null);
                var effectiveWorkingDirectory = await CallAgentHostConfigureAsync(
                    podIp, _options.AgentHostPort, runId, submittingUser, turnToken, kvUserSecretName,
                    effectiveScope,
                    await ResolveGitHubAccessTokenAsync(effectiveScope, submittingUser, ct).ConfigureAwait(false),
                    requestedWorkingDirectory ?? await ResolveWorkingDirectoryAsync(runId, ct).ConfigureAwait(false),
                    launchContext,
                    configProjectId,
                    configAgentName,
                    ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(effectiveWorkingDirectory))
                    _podRegistry?.RegisterEffectiveWorkingDirectory(runId, effectiveWorkingDirectory);
            }
            else
            {
                _logger.LogInformation(
                    "KubernetesSandboxExecutor: reusing already-configured AgentHost claim {Claim} for run {RunId}",
                    claimName, runId);
            }

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
            if (_authorshipCapabilityStore is not null)
            {
                await _authorshipCapabilityStore.RemoveAsync(runId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            // Crash/timeout during launch: delete any credential minted before the failure so it is
            // never left behind (spec-006 decouple-preview, RESIDUAL rev3 gap).
            await DeletePreviewRunnerCredentialAsync(runId, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task ReleaseAgentHostPodAsync(string runId, CancellationToken ct = default)
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);

        // Issue #542: if a live preview is still active for this run, releasing the pod here (at the
        // originating subtask's turn end) would 404 the preview URL before any human-review gate or
        // demo viewer can open it. Defer the claim delete while the preview is alive; the preview's own
        // idle/max expiry + the reaper will eventually reap the pod, so this cannot leak.
        if (_previewService is not null &&
            await _previewService.ReconcilePreviewLifecycleAsync(runId, ct).ConfigureAwait(false)
                == Agentweaver.Api.Sandbox.Preview.PreviewLifecycleState.PreviewActive)
        {
            _logger.LogInformation(
                "KubernetesSandboxExecutor: deferring AgentHost pod release for run {RunId} (claim " +
                "{Claim}) — a live preview is still active; the preview idle/max expiry will reap it.",
                runId, claimName);
            return;
        }

        _logger.LogInformation(
            "KubernetesSandboxExecutor: releasing AgentHost pod for run {RunId} (claim {Claim})",
            runId, claimName);

        await DeleteClaimAsync(claimName, ct).ConfigureAwait(false);
        _podRegistry?.Unregister(runId);
        _turnTokenRegistry?.UnregisterTurnToken(runId);
        if (_authorshipCapabilityStore is not null)
            await _authorshipCapabilityStore.RemoveAsync(runId, ct).ConfigureAwait(false);
        await DeletePreviewRunnerCredentialAsync(runId, ct).ConfigureAwait(false);

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
    /// Resolves the per-run working directory (shared orchestration worktree path) for
    /// <paramref name="runId"/> via the injected resolver, never throwing (a lookup failure must not
    /// fail the launch — it degrades to omitting the working directory, so the pod falls back to its
    /// static <c>AgentHost__WorkingDirectory</c> env default).
    /// </summary>
    private async Task<string?> ResolveWorkingDirectoryAsync(string runId, CancellationToken ct)
    {
        if (_submittingUserResolver is null)
            return null;

        try
        {
            return await _submittingUserResolver.GetWorkingDirectoryAsync(runId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "KubernetesSandboxExecutor: failed to resolve working directory for run {RunId}; " +
                "AgentHost__WorkingDirectory env default will be used.",
                runId);
            return null;
        }
    }
    /// (<c>AgentHostWarmPoolRef</c>, replicas: 2). No <c>spec.env</c> is injected — the v0.5.0
    /// controller bypasses warm pool adoption whenever <c>spec.env</c> or
    /// <c>spec.volumeClaimTemplates</c> are present. All static config lives in the SandboxTemplate
    /// or agenthost-config ConfigMap. The per-run context (RunId / UserId / TurnBearerToken /
    /// KV secret name) is delivered after bind via <c>POST /configure</c>
    /// (<see cref="CallAgentHostConfigureAsync"/>).
    /// </summary>
    private async Task<bool> CreateAgentHostClaimAsync(
        string claimName, string warmPoolName, string? workingDirectory, string runId, CancellationToken ct)
    {
        var annotations = new Dictionary<string, string>
        {
            // Persist the ORIGINAL run id so the reaper can recover it from an orphaned claim (the
            // claim name is a lossy 12-char derivation) and delete run-scoped side artifacts such as
            // the per-run preview-runner credential (spec-006 decouple-preview).
            [SandboxClaimConventions.RunIdAnnotation] = runId,
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            annotations["agentweaver.io/working-directory"] = workingDirectory;

        var manifest = new
        {
            apiVersion = $"{ApiGroup}/{ApiVersion}",
            kind = "SandboxClaim",
            metadata = new
            {
                name = claimName,
                @namespace = _options.Namespace,
                annotations = annotations.Count == 0 ? null : annotations,
            },
            spec = new
            {
                // v0.5.0 v1beta1 SandboxClaimSpec: spec.warmPoolRef.name references the
                // SandboxWarmPool to bind from. sandboxTemplateRef+warmpool were the
                // v0.4.x/v1alpha1 deprecated fields.
                warmPoolRef = new { name = warmPoolName },
                lifecycle = new { ttlSecondsAfterFinished = _options.TimeoutSeconds, shutdownPolicy = "Delete" },
            },
        };

        // Idempotent create with bounded transient-fault retry (issue #230). A mid-flight connection
        // reset can commit the SandboxClaim server-side BEFORE we observe the response, so the retry
        // may see a 409 for OUR OWN create — handled attempt-awarely below.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _client.CustomObjects.CreateNamespacedCustomObjectAsync(
                    manifest, ApiGroup, ApiVersion, _options.Namespace, ClaimPlural,
                    cancellationToken: ct).ConfigureAwait(false);
                return true;
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                if (attempt > 1)
                {
                    // Retry-409: a transient reset committed our create server-side before we saw the
                    // response, and this retry now observes our own claim. We own it → return true so
                    // the caller registers the turn token and runs /configure exactly as on a 200,
                    // rather than taking the silent "reuse pre-existing claim" path (which would leave
                    // the pod un-configured and token-less).
                    _logger.LogInformation(
                        "KubernetesSandboxExecutor: SandboxClaim {Claim} returned 409 on retry attempt {Attempt}; " +
                        "treating as our own create that committed before a transient reset — configuring it.",
                        claimName, attempt);
                    return true;
                }

                // First-attempt 409: a genuinely pre-existing claim owned by an earlier launch.
                _logger.LogInformation(
                    "KubernetesSandboxExecutor: SandboxClaim {Claim} already exists; waiting for existing claim",
                    claimName);
                return false;
            }
            catch (Exception ex) when (attempt < MaxK8sAttempts && IsTransientK8sFault(ex, ct))
            {
                var delay = BackoffWithJitter(attempt);
                _logger.LogWarning(ex,
                    "KubernetesSandboxExecutor: transient fault creating SandboxClaim {Claim} on attempt " +
                    "{Attempt}/{Max}; retrying in {DelayMs}ms.",
                    claimName, attempt, MaxK8sAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    // ── Transient Kubernetes API resilience (issue #230) ──────────────────────────

    /// <summary>
    /// Executes an <b>idempotent</b> Kubernetes API call with a bounded retry (<see cref="MaxK8sAttempts"/>
    /// total attempts) over <b>transient</b> faults only — a mid-flight connection reset
    /// (SocketException 104 → IOException → HttpRequestException), a 429/5xx from the API server, or an
    /// HttpClient timeout. Caller cancellation is never retried and aborts the backoff immediately
    /// (<c>await Task.Delay(delay, ct)</c>). Non-transient faults (e.g. 404/409/422) propagate on the
    /// first attempt. MUST NOT wrap non-idempotent calls (e.g. the AgentHost <c>POST /configure</c>,
    /// whose second delivery 409-hard-fails).
    /// </summary>
    private async Task<T> ExecuteK8sWithRetryAsync<T>(
        Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxK8sAttempts && IsTransientK8sFault(ex, ct))
            {
                var delay = BackoffWithJitter(attempt);
                _logger.LogWarning(ex,
                    "KubernetesSandboxExecutor: transient Kubernetes API fault on attempt {Attempt}/{Max}; " +
                    "retrying in {DelayMs}ms.", attempt, MaxK8sAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Exponential backoff (~250ms · 2^(attempt-1), capped at ~2s) plus 0-250ms jitter to de-sync
    /// concurrent launches retrying against the same API server after a blip.
    /// </summary>
    private static TimeSpan BackoffWithJitter(int attempt)
    {
        var baseMs = Math.Min(250 * (1 << (attempt - 1)), 2000);
        var jitterMs = Random.Shared.Next(0, 250);
        return TimeSpan.FromMilliseconds(baseMs + jitterMs);
    }

    /// <summary>
    /// True only for faults worth retrying an idempotent k8s call over: 429/5xx from the API server,
    /// a socket/IO connection reset (directly or nested in an inner exception), or an HttpClient
    /// timeout (<see cref="OperationCanceledException"/> with no caller cancellation). Caller
    /// cancellation short-circuits to false so a genuine cancel is never retried. A 409 Conflict is
    /// intentionally NOT transient here — it is handled separately (idempotent create semantics).
    /// </summary>
    private static bool IsTransientK8sFault(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;                 // caller cancel — never retry
        switch (ex)
        {
            case HttpOperationException k when k.Response is not null:
                var s = (int)k.Response.StatusCode;
                return s == 429 || s >= 500;                          // 409 handled separately, NOT here
            case HttpRequestException: return true;
            case IOException: return true;
            case OperationCanceledException:                          // includes TaskCanceledException (HttpClient timeout)
                return !ct.IsCancellationRequested;
        }
        for (Exception? i = ex.InnerException; i is not null; i = i.InnerException)
            if (i is SocketException or IOException) return true;
        return false;
    }

    private async Task<string?> TryGetAgentHostClaimWorkingDirectoryAsync(string claimName, CancellationToken ct)
    {
        try
        {
            var raw = await _client.CustomObjects.GetNamespacedCustomObjectAsync(
                ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName,
                cancellationToken: ct).ConfigureAwait(false);
            var json = JsonSerializer.Serialize(raw);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("metadata", out var meta) &&
                meta.TryGetProperty("annotations", out var ann) &&
                ann.TryGetProperty("agentweaver.io/working-directory", out var wd) &&
                wd.ValueKind == JsonValueKind.String)
                return wd.GetString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: failed to read working-directory annotation for claim {Claim}",
                claimName);
        }

        return null;
    }

    /// <summary>
    /// Resolves the run owner's GitHub access token from the API-side token store so it can be
    /// forwarded in the /configure body. The kata VM pod cannot reach Azure AD or Key Vault
    /// (Cilium FQDN policies use eBPF interception that doesn't cross the guest kernel boundary).
    /// Never throws — a lookup failure degrades gracefully: the pod will attempt the KV fetch itself
    /// (which may fail) rather than causing a hard launch failure here.
    /// </summary>
    private async Task<string?> ResolveGitHubAccessTokenAsync(
        GitHubTokenScope scope,
        string userId,
        CancellationToken ct)
    {
        // Prefer the refresh-aware provider (issue #523): a fresh AgentHost pod launched late in a
        // long-running assembly (e.g. the Build & Test gate, well after the run's earlier subtask
        // stages) can be handed a near-expiry or already-expired access token if we only ever read
        // the raw stored entry — the pod's "fast path" trusts a pre-resolved token unconditionally
        // and never re-validates it against Key Vault or GitHub. Routing through
        // GetValidAccessTokenAsync mirrors GitHubCopilotClientFactory.CreateClientAsync and
        // transparently rotates the token before it is handed to the pod.
        if (_accessTokenProvider is not null)
        {
            try
            {
                var refreshed = await _accessTokenProvider.GetValidAccessTokenAsync(scope, ct)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(refreshed))
                {
                    _logger.LogWarning(
                        "KubernetesSandboxExecutor: refresh-aware GitHub token provider returned no valid credential " +
                        "for {UserId} (scope {Scope}); refusing raw-token fallback.",
                        userId,
                        scope.Key);
                }
                return refreshed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "KubernetesSandboxExecutor: failed to resolve/refresh GitHub token for {UserId} via " +
                    "IGitHubAccessTokenProvider (scope {Scope}); refusing raw-token fallback.",
                    userId,
                    scope.Key);
                return null;
            }
        }

        if (_tokenStore is null)
            return null;

        try
        {
            var entry = await _tokenStore.GetAsync(scope, ct).ConfigureAwait(false);
            if (entry.Status == GitHubTokenStatus.SignedIn && !string.IsNullOrEmpty(entry.AccessToken))
                return entry.AccessToken;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: failed to pre-resolve GitHub token for {UserId} — pod will fall back to KV.",
                userId);
        }

        return null;
    }

    /// <summary>
    /// Injects the per-run context into an already-warm AgentHost pod via its one-time
    /// <c>POST /configure</c> endpoint. The pod then fetches ONLY <paramref name="kvUserSecretName"/>
    /// from Key Vault (its configured user's token) and runs SetupAsync. The endpoint is guarded by
    /// NetworkPolicy (ingress to AgentHost pods restricted to API/worker), not the TurnBearerToken
    /// (which is itself delivered here). Idempotency: a second call returns 409 and is treated as a
    /// hard launch failure.
    /// </summary>
    private async Task<string?> CallAgentHostConfigureAsync(
        string podIp, int port, string runId, string userId, string turnBearerToken,
        string kvUserSecretName, GitHubTokenScope tokenScope, string? gitHubAccessToken,
        string? sharedWorkingDirectory,
        AgentHostLaunchContext launchContext,
        string? projectId,
        string? agentName,
        CancellationToken ct)
    {
        if (_httpClientFactory is null)
        {
            // No HttpClient available (unit tests). Mirrors the readiness-probe null-skip; in-cluster
            // the factory is always present, so this never short-circuits a real launch.
            _logger.LogWarning(
                "KubernetesSandboxExecutor: no IHttpClientFactory — skipping /configure for run {RunId}.",
                runId);
            return null;
        }

        var scheme = AgentHostEndpoint.Scheme(_options.RequireMtls);
        var configureUrl = $"{scheme}://{podIp}:{port}/configure";

        // Mint a FRESH per-run preview-runner credential (spec-006 decouple-preview, BLOCKER A).
        // Delivered in-memory via this /configure body ONLY (never pod env/file), and persisted to the
        // run secret store so any replica can re-fetch it for reconcile/keepalive. Durably deleted on
        // pod release. Every launch/relaunch mints a new value — the old one is never reused.
        var previewRunnerCredential = await MintPreviewRunnerCredentialAsync(runId, ct).ConfigureAwait(false);

        var body = new
        {
            runId,
            userId,
            turnBearerToken,
            kvUserSecretName,
            gitHubAccessToken,
            callerBearerToken = launchContext.CallerBearerToken,
            // Keep the legacy property during rolling upgrades; new AgentHosts prefer the explicit
            // sharedWorkingDirectory descriptor and create any local workspace inside the pod.
            workingDirectory = sharedWorkingDirectory,
            sharedWorkingDirectory,
            previewRunnerCredential,
            purpose = launchContext.Purpose.ToString(),
            launchContext.SourceRepositoryPath,
            launchContext.SourceRef,
            launchContext.BaseCommitSha,
            launchContext.ExpectedTreeHash,
            workspaceMode = launchContext.WorkspaceMode.ToString(),
            launchContext.ScratchRoot,
            launchContext.CommitAuthorName,
            launchContext.CommitAuthorEmail,
            // Per-run AutoApproveTools flag (bug #221). Resolved from the API-side run-options store
            // keyed by the child runId; defaults false when the store is unavailable (unit tests).
            autoApproveTools = _runOptions?.Get(runId).AutoApproveTools ?? false,
            // Per-run project/agent identity (#335). Delivered so the in-pod agent's tool schema
            // includes the Agentweaver API tools (record_memory, get_memory, submit_decision,
            // list_decisions, list_inbox). Warm pods boot with an empty static AgentHost__ProjectId
            // /AgentName, so without these the memory/decision tools never reach the agent.
            projectId,
            agentName,
        };

        _logger.LogInformation(
            "KubernetesSandboxExecutor: configuring AgentHost pod for run {RunId} at {Url}",
            runId, configureUrl);

        using var client = _httpClientFactory.CreateClient(HttpAgentHostReadinessProbe.HttpClientName);
        using var response = await client
            .PostAsJsonAsync(configureUrl, body, ct)
            .ConfigureAwait(false);
        var detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var reason = "agenthost_configure_failed";
            try
            {
                using var document = JsonDocument.Parse(detail);
                if (document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(error.GetString()))
                    reason = error.GetString()!;
            }
            catch (JsonException)
            {
                // Plain-text legacy errors keep the generic typed reason.
            }

            if (string.Equals(
                    reason,
                    "agenthost_configure_copilot_unauthorized",
                    StringComparison.Ordinal) &&
                _accessTokenProvider is not null)
            {
                var refreshed = await _accessTokenProvider
                    .RefreshAfterUnauthorizedAsync(tokenScope, gitHubAccessToken, ct)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(refreshed) &&
                    !string.Equals(refreshed, gitHubAccessToken, StringComparison.Ordinal))
                {
                    _logger.LogWarning(
                        "KubernetesSandboxExecutor: AgentHost /configure rejected the Copilot credential for run {RunId}; " +
                        "scope {Scope} was refreshed and the pod must be recreated (recoveryAttempt=1, maxRecoveryAttempts=1).",
                        runId,
                        tokenScope.Key);
                    throw new AgentHostConfigureException(
                        "agenthost_configure_copilot_token_refreshed",
                        $"AgentHost /configure rejected the Copilot credential for run '{runId}'. " +
                        "The credential was refreshed; recreate the one-time-configured pod and retry once.",
                        (int)response.StatusCode,
                        retryable: true,
                        recoveryAction: "recreate_pod_with_refreshed_credential");
                }

                _logger.LogWarning(
                    "KubernetesSandboxExecutor: AgentHost /configure rejected the Copilot credential for run {RunId}; " +
                    "scope {Scope} could not produce a different refreshed credential, so the failure is not retryable.",
                    runId,
                    tokenScope.Key);
            }

            throw new AgentHostConfigureException(
                reason,
                $"AgentHost /configure for run '{runId}' failed: HTTP {(int)response.StatusCode} {detail}",
                (int)response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(detail))
            return null;

        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.TryGetProperty("effectiveWorkingDirectory", out var path)
                && path.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(path.GetString()))
            {
                return path.GetString();
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "KubernetesSandboxExecutor: AgentHost /configure for run {RunId} returned an invalid success body; preview will use the shared working directory.",
                runId);
        }

        return null;
    }

    /// <summary>
    /// Mints and persists a fresh per-run preview-runner credential and returns it for in-memory
    /// delivery via <c>/configure</c>. Returns <see cref="string.Empty"/> when no secret store is
    /// available (unit tests) — the pod then relies on the turn token only. The persisted key is
    /// derived deterministically from the run id (<see cref="Preview.PreviewRunnerCredential.SecretKey"/>)
    /// so the release-time delete matches (spec-006 decouple-preview, BLOCKER A).
    /// </summary>
    private async Task<string> MintPreviewRunnerCredentialAsync(string runId, CancellationToken ct)
    {
        if (_secretStore is null)
            return string.Empty;

        var credential = Preview.PreviewRunnerCredential.Mint();
        var key = Preview.PreviewRunnerCredential.SecretKey(runId);
        try
        {
            await _secretStore.SetSecretAsync(key, credential, etag: null, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "KubernetesSandboxExecutor: minted per-run preview-runner credential for run {RunId}", runId);
            return credential;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: a persist failure must not fail the launch. The pod still receives the
            // credential in-memory (same-process affinity uses the turn token anyway), but a
            // cross-replica reconcile could not re-fetch it — acceptable degradation.
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: failed to persist preview-runner credential for run {RunId}; " +
                "delivering in-memory only.", runId);
            return credential;
        }
    }

    /// <summary>
    /// Durably deletes the per-run preview-runner credential from the run secret store. No-op when
    /// absent (<see cref="ISecretStore.DeleteSecretAsync"/> ignores a missing key). Never throws —
    /// a delete failure must not break terminal cleanup. Called on EVERY terminal path (happy
    /// release + crash/timeout/failed-run via the pod-release seam) so the credential's durable
    /// lifetime is bounded by the pod's (spec-006 decouple-preview, RESIDUAL rev3 gap).
    /// </summary>
    private async Task DeletePreviewRunnerCredentialAsync(string runId, CancellationToken ct)
    {
        if (_secretStore is null)
            return;

        try
        {
            await _secretStore.DeleteSecretAsync(Preview.PreviewRunnerCredential.SecretKey(runId), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: failed to delete preview-runner credential for run {RunId} (best-effort)",
                runId);
        }
    }

    /// <summary>
    /// Waits for the AgentHost <c>SandboxClaim</c> to bind while emitting periodic
    /// <see cref="EventTypes.SandboxProvisioningPending"/> heartbeats into the CHILD run's event
    /// stream. Scheduling is Kubernetes' job: a claim may sit unbound (pod Pending) for a while until
    /// a node frees up or the pool autoscales — that is FINE and must not fail the run (issue #217).
    /// The heartbeat keeps the parent coordinator's subtask-stall timer alive during that legitimate
    /// wait, mirroring the #212 tool.approval_pending heartbeat. Best-effort: if no
    /// <see cref="IRunEventStream"/> is wired (unit tests) this degrades to a plain
    /// <see cref="WaitForBoundAsync"/>.
    /// </summary>
    private async Task<string> WaitForBoundWithProvisioningHeartbeatAsync(
        string runId, string claimName, CancellationToken ct)
    {
        if (_runEventStream is null)
            return await WaitForBoundAsync(claimName, ct).ConfigureAwait(false);

        var boundTask = WaitForBoundAsync(claimName, ct);
        while (true)
        {
            var delayTask = Task.Delay(SandboxProvisioningHeartbeatInterval, ct);
            var completed = await Task.WhenAny(boundTask, delayTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, boundTask))
                return await boundTask.ConfigureAwait(false); // propagates the bound pod name / any error

            // The claim is still unbound after the heartbeat interval — emit a non-terminal
            // heartbeat so the coordinator's stall window resets while Kubernetes schedules the pod.
            await delayTask.ConfigureAwait(false); // observe cancellation
            await EmitProvisioningPendingAsync(runId, claimName, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Appends a single <see cref="EventTypes.SandboxProvisioningPending"/> heartbeat to
    /// <paramref name="runId"/>'s durable event stream. Best-effort: a stream-append failure is
    /// logged and swallowed so it can never fail a launch that Kubernetes would otherwise admit.
    /// </summary>
    private async Task EmitProvisioningPendingAsync(string runId, string claimName, CancellationToken ct)
    {
        try
        {
            await _runEventStream!.AppendAsync(runId, new RunEvent(0, EventTypes.SandboxProvisioningPending, new
            {
                claimName,
                timestamp_utc = DateTimeOffset.UtcNow.ToString("O"),
            }), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "KubernetesSandboxExecutor: failed to emit sandbox.provisioning_pending heartbeat for run {RunId} (best-effort)",
                runId);
        }
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

            var pod = await ExecuteK8sWithRetryAsync(
                token => _client.CoreV1.ReadNamespacedPodAsync(
                    podName, _options.Namespace, cancellationToken: token),
                ct).ConfigureAwait(false);

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

    private async Task<bool> CreateClaimAsync(string claimName, CancellationToken ct)
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

        try
        {
            await _client.CustomObjects.CreateNamespacedCustomObjectAsync(
                manifest, ApiGroup, ApiVersion, _options.Namespace, ClaimPlural,
                cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogInformation(
                "KubernetesSandboxExecutor: SandboxClaim {Claim} already exists; waiting for existing claim",
                claimName);
            return false;
        }
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

            var raw = await ExecuteK8sWithRetryAsync(
                token => _client.CustomObjects.GetNamespacedCustomObjectAsync(
                    ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName,
                    cancellationToken: token),
                ct).ConfigureAwait(false);

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

    private async Task DeleteClaimAsync(string claimName, CancellationToken ct = default)
    {
        try
        {
            await _client.CustomObjects.DeleteNamespacedCustomObjectAsync(
                ApiGroup, ApiVersion, _options.Namespace, ClaimPlural, claimName, cancellationToken: ct);
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
