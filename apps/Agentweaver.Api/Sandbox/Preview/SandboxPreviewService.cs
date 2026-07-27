using System.Globalization;
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>Result of starting a Gateway-direct preview for a run.</summary>
public sealed record PreviewSession(
    string Token,
    string RunId,
    string PodName,
    int TargetPort,
    string PreviewUrl,
    DateTimeOffset StartedAt);

/// <summary>
/// Creates and tears down the per-preview Kubernetes objects (pod-label patch, ClusterIP
/// Service, and HTTPRoute) that wire the shared preview Gateway directly to a run's sandbox
/// pod. Replaces the in-cluster kubectl port-forward leg (which is replica-unsafe).
///
/// <para><b>Replica-safe:</b> all per-preview state lives in HTTPRoute annotations, never in
/// process memory, so either API replica reconciles (keepalive/reap) identically.</para>
/// </summary>
public interface ISandboxPreviewService
{
    /// <summary>Whether the Gateway preview path is enabled (Sandbox:Preview:Enabled).</summary>
    bool Enabled { get; }

    /// <summary>Lowest target port a preview may expose (Sandbox:Preview:AllowedPortMin).</summary>
    int AllowedPortMin { get; }

    /// <summary>Highest target port a preview may expose (Sandbox:Preview:AllowedPortMax).</summary>
    int AllowedPortMax { get; }

    /// <summary>
    /// Provisions a preview for <paramref name="runId"/> targeting <paramref name="targetPort"/>
    /// on the bound sandbox pod. The pod is resolved from the run's SandboxClaim status in the
    /// cluster (replica-safe), not from any in-process registry. Throws
    /// <see cref="InvalidOperationException"/> when the claim is missing or not yet bound.
    /// </summary>
    /// <param name="previewRunnerSessionId">
    /// Optional PreviewRunner PROCESS session id (spec-006 §3.4). When supplied it is persisted in the
    /// HTTPRoute annotations so keepalive can dual-touch the separate PreviewRunner idle clock.
    /// </param>
    Task<PreviewSession> StartPreviewAsync(
        string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
        string? previewRunnerSessionId = null);

    /// <summary>
    /// Lists active previews for <paramref name="runId"/> from HTTPRoute annotations. Replica-safe.
    /// </summary>
    Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Replica-safe check (issue #542): does <paramref name="runId"/> have at least one preview whose
    /// idle/max expiry has NOT yet elapsed? Unlike <see cref="ListForRunAsync"/> this deliberately does
    /// NOT require the backing pod to still exist — it answers "should the run's sandbox pod be kept
    /// alive to keep a preview reachable?", which is asked precisely at the moment the pod is about to
    /// be torn down (turn-end release / orphan reap), when the pod still exists. Returns
    /// <see langword="false"/> when preview is disabled or no un-expired route exists, so the caller
    /// falls back to normal teardown. Never throws — a lookup failure returns <see langword="false"/>
    /// (leak-safe: defer only on positive evidence of an active preview).
    /// </summary>
    Task<bool> HasActivePreviewAsync(string runId, CancellationToken ct = default);

    /// <summary>Bumps the preview's idle expiry to now + IdleTimeoutMinutes. Idempotent (404 ignored).</summary>
    Task KeepAliveAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Renews the backing SandboxClaim's cluster-side lifecycle TTL for <paramref name="runId"/> so the
    /// sandbox controller does not reap the run's pod out from under a still-active preview (issue #560).
    ///
    /// <para><b>Why this exists:</b> the claim is created with
    /// <c>spec.lifecycle.ttlSecondsAfterFinished = Sandbox:TimeoutSeconds</c> (default 600s). When a
    /// child execution subtask's pod workload finishes, the controller reaps the pod ~TTL seconds later —
    /// independently of the API. Issue #542/#551 only deferred the <em>API-side</em> pod release/orphan
    /// sweep, which cannot stop the cluster controller, so a preview backed by a terminal run still
    /// NXDOMAINs ~TimeoutSeconds after the turn ends. This hook JSON-merge-patches the claim TTL up to
    /// cover the preview's own hard-max lifetime (<see cref="SandboxPreviewOptions.MaxLifetimeHours"/>
    /// plus a margin), so the controller keeps the pod alive exactly as long as a preview may live.</para>
    ///
    /// <para><b>Leak-safe:</b> callers only invoke this while a preview is demonstrably active
    /// (turn-end/reaper deferral gated by <see cref="HasActivePreviewAsync"/>, or keepalive). Bounded
    /// teardown is preserved: the API-side preview reaper + AgentHost reaper still delete the claim
    /// promptly on idle/max expiry, which supersedes the TTL; the extended TTL is only a backstop for
    /// the window a preview is genuinely live. Best-effort and idempotent: a no-op when preview is
    /// disabled, ignores 404s (claim already gone / never created), and never throws.</para>
    /// </summary>
    Task RenewBackingClaimTtlAsync(string runId, CancellationToken ct = default);

    /// <summary>
    /// Replica-safe ownership binding: returns <see langword="true"/> only when an HTTPRoute named
    /// for <paramref name="token"/> exists AND its <c>preview-run</c> annotation matches
    /// <paramref name="runId"/>. Reads cluster annotations, so either replica answers identically.
    /// Callers (keepalive/stop) must reject the request (404) when this is <see langword="false"/>
    /// so one run cannot keep alive or delete another run's preview by guessing the route name.
    /// </summary>
    Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default);

    /// <summary>Deletes the HTTPRoute then the Service for the preview. Idempotent (404 ignored).</summary>
    Task StopPreviewAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Lists every Agentweaver preview HTTPRoute and reaps the expired/orphaned ones. Called by
    /// the background reaper on a timer. Returns the number of previews reaped. Replica-safe.
    /// </summary>
    Task<int> ReapAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="ISandboxPreviewService"/>
public sealed class SandboxPreviewService : ISandboxPreviewService
{
    private const string HttpRouteGroup = "gateway.networking.k8s.io";
    private const string HttpRouteVersion = "v1";
    private const string HttpRoutePlural = "httproutes";

    /// <summary>
    /// Host header the gateway rewrites every preview request to before forwarding it to the run's
    /// dev server. Dev-server host allowlists (Vite/CRA/Angular) permit "localhost" by default, so
    /// rewriting to it makes the dynamic per-preview hostname reachable without per-framework config
    /// patching. See <see cref="BuildHttpRoute"/> for the full rationale (#312).
    /// </summary>
    private const string PreviewUpstreamHost = "localhost";

    /// <summary>Minimum age before a route-less preview Service is treated as a leaked orphan.</summary>
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(2);

    private readonly IKubernetes? _client;
    private readonly SandboxPreviewOptions _options;
    private readonly ILogger<SandboxPreviewService> _logger;
    private readonly TimeProvider _clock;
    private readonly IPreviewRunnerHttpClient? _previewRunnerClient;
    private readonly IAgentHostOriginResolver? _originResolver;
    private readonly Agentweaver.Api.Auth.ISecretStore? _secretStore;

    public SandboxPreviewService(
        IKubernetes? client,
        SandboxPreviewOptions options,
        ILogger<SandboxPreviewService> logger,
        TimeProvider? clock = null,
        IPreviewRunnerHttpClient? previewRunnerClient = null,
        IAgentHostOriginResolver? originResolver = null,
        Agentweaver.Api.Auth.ISecretStore? secretStore = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _previewRunnerClient = previewRunnerClient;
        _originResolver = originResolver;
        _secretStore = secretStore;
    }

    public bool Enabled => _options.Enabled && _client is not null;

    public int AllowedPortMin => _options.AllowedPortMin;

    public int AllowedPortMax => _options.AllowedPortMax;

    public async Task<PreviewSession> StartPreviewAsync(
        string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
        string? previewRunnerSessionId = null)
    {
        EnsureReady();
        if (targetPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(targetPort), "targetPort must be between 1 and 65535.");

        var podName = await ResolveBoundPodNameAsync(runId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(podName))
            throw new InvalidOperationException(
                $"No bound sandbox pod for run {runId}. A preview is only available after the run's " +
                "SandboxClaim reports a bound pod (status.phase=Bound).");

        // NOTE: We deliberately do NOT TCP-probe podIP:targetPort from the API pod here.
        // Under the sandbox isolation model (k8s/networkpolicy-sandbox.yaml), preview ports
        // 3000-9000 admit ingress ONLY from the preview Gateway — a direct API->podIP connect is
        // denied by policy and can never succeed. Readiness is already proven upstream by the
        // AgentHost observe step (forwarder-verified, in-pod loopback) before this call, which is
        // the correct readiness signal under isolation.

        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        await EnforcePreviewLimitsAsync(sanitizedRun, ct).ConfigureAwait(false);

        var token = PreviewToken.Generate();
        var now = _clock.GetUtcNow();
        var serviceName = PreviewReaper.ServiceName(token);
        var hostname = $"{PreviewToken.HostLabel(token)}.{_options.ZoneSuffix}";
        var previewUrl = $"https://{hostname}";

        var client = _client!;

        // a/c. Patch the per-run selector label onto the bound pod (JSON merge patch).
        var podPatchJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            metadata = new
            {
                labels = new Dictionary<string, string> { [PreviewReaper.PodPreviewRunLabel] = sanitizedRun },
            },
        });
        var podPatch = new V1Patch(podPatchJson, V1Patch.PatchType.MergePatch);
        await client.CoreV1.PatchNamespacedPodAsync(
            podPatch, podName, _options.Namespace, cancellationToken: ct).ConfigureAwait(false);

        // d. ClusterIP Service: selector = preview-run label, port 80 -> targetPort.
        var service = new V1Service
        {
            Metadata = new V1ObjectMeta
            {
                Name = serviceName,
                NamespaceProperty = _options.Namespace,
                Labels = new Dictionary<string, string>
                {
                    [PreviewReaper.LabelPartOf] = PreviewReaper.LabelPartOfValue,
                    [PreviewReaper.LabelToken] = token,
                    [PreviewReaper.LabelRun] = sanitizedRun,
                },
            },
            Spec = new V1ServiceSpec
            {
                Type = "ClusterIP",
                Selector = new Dictionary<string, string>
                {
                    [PreviewReaper.PodPreviewRunLabel] = sanitizedRun,
                },
                Ports =
                [
                    new V1ServicePort
                    {
                        Port = 80,
                        TargetPort = targetPort,
                        Protocol = "TCP",
                    },
                ],
            },
        };
        await CreateServiceIdempotentAsync(service, ct).ConfigureAwait(false);

        // e. HTTPRoute (gateway.networking.k8s.io/v1) attaching to the shared preview Gateway.
        var expiresAt = now.AddMinutes(_options.IdleTimeoutMinutes);
        var maxUntil = now.AddHours(_options.MaxLifetimeHours);
        var httpRoute = BuildHttpRoute(
            token, sanitizedRun, ownerUserId, podName, targetPort, hostname, serviceName, now, expiresAt, maxUntil,
            runId, previewRunnerSessionId);

        try
        {
            await client.CustomObjects.CreateNamespacedCustomObjectAsync(
                httpRoute, HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogInformation(
                "SandboxPreviewService: HTTPRoute already exists for run {RunId} (idempotent)", runId);
        }
        catch (Exception ex)
        {
            // Any non-Conflict failure leaves the just-created Service orphaned (no HTTPRoute will
            // ever reference it, and the reaper used to sweep only HTTPRoutes). Best-effort delete
            // the Service before rethrowing so a retrying caller cannot accumulate leaked ClusterIPs.
            _logger.LogWarning(ex,
                "SandboxPreviewService: HTTPRoute create failed for run {RunId}; rolling back orphaned Service {Fingerprint}",
                runId, Fingerprint(serviceName));
            // Use None so the rollback still runs even when the original request was cancelled.
            await DeleteServiceIdempotentAsync(serviceName, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        // Never log the token or preview URL (the URL is an unauthenticated capability). A short,
        // non-reversible fingerprint is logged for cross-line correlation; RunId is the safe key.
        _logger.LogInformation(
            "SandboxPreviewService: started preview {Fingerprint} for run {RunId} -> pod {Pod} port {Port}",
            Fingerprint(token), runId, podName, targetPort);

        return new PreviewSession(token, runId, podName, targetPort, previewUrl, now);
    }

    public async Task KeepAliveAsync(string token, CancellationToken ct = default)
    {
        EnsureReady();
        if (!PreviewToken.IsValidLabel(token))
            throw new ArgumentException("Invalid preview token.", nameof(token));

        var expiresAt = _clock.GetUtcNow().AddMinutes(_options.IdleTimeoutMinutes);
        var serviceName = PreviewReaper.ServiceName(token);
        var patchJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            metadata = new
            {
                annotations = new Dictionary<string, string> { [PreviewReaper.AnnotationExpiresAt] = Rfc3339(expiresAt) },
            },
        });
        var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

        try
        {
            await _client!.CustomObjects.PatchNamespacedCustomObjectAsync(
                patch, HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural, serviceName,
                cancellationToken: ct).ConfigureAwait(false);
            _logger.LogDebug(
                "SandboxPreviewService: keepalive bumped preview {Fingerprint} idle expiry to {ExpiresAt}",
                Fingerprint(token), Rfc3339(expiresAt));
        }

        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogInformation(
                "SandboxPreviewService: keepalive for unknown preview {Fingerprint} ignored (404)",
                Fingerprint(token));
            return;
        }

        // spec-006 §3.4 (BLOCKER B): dual-touch the SEPARATE PreviewRunner process idle clock so the
        // backing app process is not reaped while the Gateway route is still being kept alive. Reads
        // the durable preview_runner_session_id + run-id + target-port annotations off the route and
        // calls /preview-runner/processes/{sessionId}/health-check. Best-effort: never fails keepalive.
        await TryTouchPreviewRunnerProcessAsync(serviceName, ct).ConfigureAwait(false);

        // #560: renew the backing SandboxClaim's cluster-side lifecycle TTL so the sandbox controller
        // does not reap the run's pod out from under this still-active preview. The route annotation
        // bump above only keeps the API-side reaper from deleting the route/pod; it does NOT stop the
        // controller's ttlSecondsAfterFinished reap once a child subtask's workload finishes. Reading
        // the run id off the route makes this replica-safe and keyed to the preview being kept alive.
        var routeRunId = await TryReadRouteRunIdAsync(serviceName, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(routeRunId))
            await RenewBackingClaimTtlAsync(routeRunId!, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort read of the durable <c>preview-run-id</c> annotation off a preview HTTPRoute so
    /// keepalive can renew the backing claim TTL for the right run (replica-safe). Returns
    /// <see langword="null"/> on 404 or any read failure — the caller then skips claim renewal.
    /// </summary>
    private async Task<string?> TryReadRouteRunIdAsync(string routeName, CancellationToken ct)
    {
        try
        {
            var raw = await _client!.CustomObjects.GetNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural, routeName,
                cancellationToken: ct).ConfigureAwait(false);
            var json = System.Text.Json.JsonSerializer.Serialize(raw);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("metadata", out var meta) &&
                   meta.TryGetProperty("annotations", out var ann)
                ? GetString(ann, PreviewReaper.AnnotationRunId)
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    public async Task RenewBackingClaimTtlAsync(string runId, CancellationToken ct = default)
    {
        // Leak-safe: only meaningful while a preview is active; callers gate on HasActivePreviewAsync
        // (turn-end/reaper deferral) or keepalive. Never throws — a renewal failure must not fail the
        // teardown-deferral or keepalive path it hangs off. No-op when preview is disabled.
        if (!Enabled || string.IsNullOrEmpty(runId))
            return;

        // Cover the preview's own hard-max lifetime plus a margin so the controller keeps the pod for
        // exactly as long as a preview may live. The API-side reaper still deletes the claim promptly
        // on idle/max expiry (which supersedes this TTL), so the extended value is only a backstop.
        var renewedTtlSeconds = checked(_options.MaxLifetimeHours * 3600 + 600);
        var patchJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            spec = new { lifecycle = new { ttlSecondsAfterFinished = renewedTtlSeconds } },
        });
        var patch = new V1Patch(patchJson, V1Patch.PatchType.MergePatch);

        // A run's preview is backed by either its agent-host claim (agent-*) or its run-command claim
        // (run-*); patch whichever exists. A non-existent candidate 404s and is ignored — if neither
        // exists this is a harmless no-op. MergePatch preserves the sibling shutdownPolicy.
        foreach (var claimName in new[]
        {
            SandboxClaimConventions.DeriveAgentHostClaimName(runId),
            SandboxClaimConventions.DeriveRunCommandClaimName(runId),
        }.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await _client!.CustomObjects.PatchNamespacedCustomObjectAsync(
                    patch, SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion,
                    _options.Namespace, SandboxClaimConventions.ClaimPlural, claimName,
                    cancellationToken: ct).ConfigureAwait(false);
                _logger.LogDebug(
                    "SandboxPreviewService: renewed backing claim {Claim} TTL to {Ttl}s for run {RunId} (#560)",
                    claimName, renewedTtlSeconds, runId);
            }
            catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Candidate claim does not exist for this run — expected for whichever of agent-*/run-*
                // is not the backing claim; ignore.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "SandboxPreviewService: best-effort claim TTL renewal failed for {Claim} (run {RunId}); " +
                    "preview may NXDOMAIN when the cluster TTL elapses (#560)",
                    claimName, runId);
            }
        }
    }

    /// <summary>
    /// Best-effort PreviewRunner-process idle-clock touch for keepalive (spec-006 §3.4). Reads the
    /// durable route annotations (<c>preview-runner-session-id</c>, <c>preview-run-id</c>,
    /// <c>preview-target-port</c>), resolves the AgentHost origin + per-run credential, and calls
    /// <c>/preview-runner/processes/{sessionId}/health-check</c>. Never throws — a failure here must
    /// not fail the Gateway keepalive.
    /// </summary>
    private async Task TryTouchPreviewRunnerProcessAsync(string routeName, CancellationToken ct)
    {
        if (_previewRunnerClient is null || _originResolver is null)
            return;

        try
        {
            var raw = await _client!.CustomObjects.GetNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural, routeName,
                cancellationToken: ct).ConfigureAwait(false);

            var annotations = ExtractAnnotations(raw);
            if (annotations is null)
                return;

            annotations.TryGetValue(PreviewReaper.AnnotationPreviewRunnerSessionId, out var sessionId);
            annotations.TryGetValue(PreviewReaper.AnnotationRunId, out var runId);
            annotations.TryGetValue(PreviewReaper.AnnotationTargetPort, out var portText);

            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(runId))
                return; // No PreviewRunner process bound to this route (e.g. legacy/manual preview).

            var port = TryParseInt(portText) ?? 0;

            var origin = await _originResolver.TryResolveOriginAsync(runId, ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(origin))
            {
                _logger.LogDebug(
                    "SandboxPreviewService: keepalive dual-touch skipped for {Fingerprint} — no AgentHost origin.",
                    Fingerprint(routeName));
                return;
            }

            var bearer = await ResolvePreviewRunnerBearerAsync(runId, ct).ConfigureAwait(false);

            await _previewRunnerClient.HealthCheckByOriginAsync(origin, bearer, sessionId!, port, "/", ct)
                .ConfigureAwait(false);
            _logger.LogDebug(
                "SandboxPreviewService: keepalive dual-touched PreviewRunner process for {Fingerprint}.",
                Fingerprint(routeName));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SandboxPreviewService: keepalive dual-touch failed for {Fingerprint} (ignored).",
                Fingerprint(routeName));
        }
    }

    private async Task<string?> ResolvePreviewRunnerBearerAsync(string runId, CancellationToken ct)
    {
        if (_secretStore is null)
            return null;
        try
        {
            var result = await _secretStore.GetSecretAsync(PreviewRunnerCredential.SecretKey(runId), ct)
                .ConfigureAwait(false);
            return result.Found ? result.Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "SandboxPreviewService: failed to fetch preview-runner credential for keepalive dual-touch.");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? ExtractAnnotations(object raw)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(raw);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("metadata", out var meta) ||
            !meta.TryGetProperty("annotations", out var ann) ||
            ann.ValueKind != System.Text.Json.JsonValueKind.Object)
            return null;

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in ann.EnumerateObject())
        {
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                result[prop.Name] = prop.Value.GetString() ?? "";
        }
        return result;
    }

    public async Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default)
    {
        EnsureReady();
        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        var raw = await _client!.CustomObjects.ListNamespacedCustomObjectAsync(
            HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural,
            labelSelector: $"{PreviewReaper.LabelPartOf}={PreviewReaper.LabelPartOfValue}",
            cancellationToken: ct).ConfigureAwait(false);

        var sessions = new List<PreviewSession>();
        foreach (var route in ParsePreviewRoutes(raw).Where(r =>
                     string.Equals(r.SanitizedRun, sanitizedRun, StringComparison.Ordinal)))
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(route.PodName) || route.TargetPort is null or <= 0)
                continue;

            // Liveness proxy under isolation: we cannot TCP-probe podIP:targetPort from the API pod
            // (denied by the sandbox NetworkPolicy). Use the allowed control-plane pod-existence check
            // (label selector) instead so a torn-down pod stops being reported as active.
            if (!await PodExistsForRunAsync(sanitizedRun, ct).ConfigureAwait(false))
            {
                _logger.LogInformation(
                    "SandboxPreviewService: preview {Fingerprint} for run {RunId} is not reporting active because no bound pod exists for the run",
                    Fingerprint(route.Token), runId);
                continue;
            }

            sessions.Add(new PreviewSession(
                route.Token,
                runId,
                route.PodName,
                route.TargetPort.Value,
                $"https://{PreviewToken.HostLabel(route.Token)}.{_options.ZoneSuffix}",
                PreviewReaper.ParseTimestamp(route.StartedAt) ?? DateTimeOffset.MinValue));
        }

        return sessions;
    }

    public async Task<bool> HasActivePreviewAsync(string runId, CancellationToken ct = default)
    {
        // Leak-safe: only defer a pod teardown on POSITIVE evidence of a live preview. When preview is
        // disabled, or the lookup fails, or no un-expired route exists, return false so the caller
        // performs its normal teardown (issue #542).
        if (!Enabled)
            return false;

        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
        var now = _clock.GetUtcNow();

        try
        {
            var raw = await _client!.CustomObjects.ListNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural,
                labelSelector: $"{PreviewReaper.LabelPartOf}={PreviewReaper.LabelPartOfValue}",
                cancellationToken: ct).ConfigureAwait(false);

            foreach (var route in ParsePreviewRoutes(raw).Where(r =>
                         string.Equals(r.SanitizedRun, sanitizedRun, StringComparison.Ordinal)))
            {
                // podExists:true — we are deciding whether to KEEP the pod for this preview, so pod
                // existence is not a signal here (the pod is present at the teardown boundary). Only the
                // idle (keepalive) and hard-max expiries bound the deferral, matching the reaper's own
                // ExpiredIdle / ExpiredMax axes so a preview and its pod expire together.
                var decision = PreviewReaper.Decide(
                    now,
                    PreviewReaper.ParseTimestamp(route.ExpiresAt),
                    PreviewReaper.ParseTimestamp(route.MaxUntil),
                    podExists: true);
                if (decision == PreviewReapReason.Alive)
                    return true;
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "SandboxPreviewService: active-preview probe failed for run {RunId}; treating as no active preview",
                runId);
            return false;
        }
    }

    public async Task StopPreviewAsync(string token, CancellationToken ct = default)
    {
        EnsureReady();
        var serviceName = PreviewReaper.ServiceName(token);

        await DeleteHttpRouteIdempotentAsync(serviceName, ct).ConfigureAwait(false);
        await DeleteServiceIdempotentAsync(serviceName, ct).ConfigureAwait(false);

        _logger.LogInformation("SandboxPreviewService: stopped preview {Fingerprint}", Fingerprint(token));
    }

    public async Task<int> ReapAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return 0;

        var now = _clock.GetUtcNow();
        var client = _client!;

        var raw = await client.CustomObjects.ListNamespacedCustomObjectAsync(
            HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural,
            labelSelector: $"{PreviewReaper.LabelPartOf}={PreviewReaper.LabelPartOfValue}",
            cancellationToken: ct).ConfigureAwait(false);

        var routes = ParsePreviewRoutes(raw);
        var reaped = 0;

        foreach (var route in routes)
        {
            ct.ThrowIfCancellationRequested();

            var podExists = await PodExistsForRunAsync(route.SanitizedRun, ct).ConfigureAwait(false);
            var decision = PreviewReaper.Decide(
                now,
                PreviewReaper.ParseTimestamp(route.ExpiresAt),
                PreviewReaper.ParseTimestamp(route.MaxUntil),
                podExists);

            if (decision == PreviewReapReason.Alive)
                continue;

            _logger.LogInformation(
                "SandboxPreviewService: reaping preview {Fingerprint} (run {Run}) reason={Reason}",
                Fingerprint(route.Token), route.SanitizedRun, decision);

            try
            {
                await StopPreviewAsync(route.Token, ct).ConfigureAwait(false);
                reaped++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SandboxPreviewService: failed to reap preview {Fingerprint} (best-effort)",
                    Fingerprint(route.Token));
            }
        }

        // Orphan ClusterIP sweep: a preview Service whose HTTPRoute never got created (e.g. the
        // process died between Service-create and HTTPRoute-create, before the inline rollback ran)
        // would otherwise leak forever — the route-driven loop above never sees it. Sweep any
        // preview-* Service that has no matching HTTPRoute so retries cannot accumulate ClusterIPs.
        reaped += await SweepOrphanServicesAsync(now, ct).ConfigureAwait(false);

        return reaped;
    }

    /// <summary>
    /// Deletes <c>preview-*</c> Services that have no matching HTTPRoute (same name). A short
    /// minimum-age grace window protects a Service whose HTTPRoute is still being created in a
    /// concurrent <see cref="StartPreviewAsync"/> on either replica.
    /// </summary>
    private async Task<int> SweepOrphanServicesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var client = _client!;

        V1ServiceList services;
        object rawRoutes;
        try
        {
            services = await client.CoreV1.ListNamespacedServiceAsync(
                _options.Namespace,
                labelSelector: $"{PreviewReaper.LabelPartOf}={PreviewReaper.LabelPartOfValue}",
                cancellationToken: ct).ConfigureAwait(false);

            rawRoutes = await client.CustomObjects.ListNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural,
                labelSelector: $"{PreviewReaper.LabelPartOf}={PreviewReaper.LabelPartOfValue}",
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SandboxPreviewService: orphan-Service sweep listing failed (best-effort)");
            return 0;
        }

        // Only consider Services that have aged past the grace window, so a Service created moments
        // ago (HTTPRoute not yet posted) is never mistaken for an orphan.
        var graceCutoff = now - OrphanGrace;
        var serviceNames = services.Items
            .Where(s => s.Metadata?.Name is not null &&
                        s.Metadata.Name.StartsWith("preview-", StringComparison.Ordinal) &&
                        (s.Metadata.CreationTimestamp is null ||
                         s.Metadata.CreationTimestamp <= graceCutoff.UtcDateTime))
            .Select(s => s.Metadata.Name)
            .ToList();

        var routeNames = ParsePreviewRouteNames(rawRoutes);
        var orphans = PreviewReaper.FindOrphanServiceNames(serviceNames, routeNames);

        var swept = 0;
        foreach (var name in orphans)
        {
            ct.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "SandboxPreviewService: sweeping orphaned preview Service {Fingerprint} (no HTTPRoute)",
                Fingerprint(name));
            await DeleteServiceIdempotentAsync(name, ct).ConfigureAwait(false);
            swept++;
        }

        return swept;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private void EnsureReady()
    {
        if (!Enabled)
            throw new InvalidOperationException(
                "SandboxPreviewService is disabled or has no in-cluster Kubernetes client " +
                "(set Sandbox:Preview:Enabled=true and run in-cluster).");
    }

    /// <summary>
    /// Resolves the run's bound sandbox pod name from <b>cluster state</b> (the run's SandboxClaim
    /// <c>status</c>), NOT from the in-process pod registry. The registry is only populated on the
    /// replica that launched the pod, so on a multi-replica deployment a preview-start request
    /// hitting the other replica would otherwise spuriously fail. Reading the claim is replica-safe.
    /// Preview supports both retained claim conventions for the same run: AgentHost pod-per-run
    /// claims (<c>agent-*</c>) and in-process command sandbox claims (<c>run-*</c>). Returns
    /// <see langword="null"/> when neither claim is bound.
    /// </summary>
    private async Task<string?> ResolveBoundPodNameAsync(string runId, CancellationToken ct)
    {
        foreach (var claimName in new[]
        {
            SandboxClaimConventions.DeriveAgentHostClaimName(runId),
            SandboxClaimConventions.DeriveRunCommandClaimName(runId),
        }.Distinct(StringComparer.Ordinal))
        {
            var podName = await TryResolveBoundPodNameFromClaimAsync(claimName, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(podName))
                return podName;
        }

        return null;
    }

    private async Task<string?> TryResolveBoundPodNameFromClaimAsync(string claimName, CancellationToken ct)
    {
        try
        {
            var raw = await _client!.CustomObjects.GetNamespacedCustomObjectAsync(
                SandboxClaimConventions.ApiGroup, SandboxClaimConventions.ApiVersion,
                _options.Namespace, SandboxClaimConventions.ClaimPlural, claimName,
                cancellationToken: ct).ConfigureAwait(false);

            return SandboxClaimConventions.TryGetBoundPodName(raw);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default)
    {
        EnsureReady();
        if (!PreviewToken.IsValidLabel(token) || string.IsNullOrEmpty(runId))
            return false;

        var routeName = PreviewReaper.ServiceName(token);
        try
        {
            var raw = await _client!.CustomObjects.GetNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural, routeName,
                cancellationToken: ct).ConfigureAwait(false);

            var annotationRun = ReadRunAnnotation(raw);
            return PreviewReaper.RunMatches(annotationRun, runId);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static string? ReadRunAnnotation(object rawRoute)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(rawRoute);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("metadata", out var meta) &&
               meta.TryGetProperty("annotations", out var ann)
            ? GetString(ann, PreviewReaper.AnnotationRun)
            : null;
    }

    private async Task EnforcePreviewLimitsAsync(string sanitizedRun, CancellationToken ct)
    {
        object raw;
        try
        {
            raw = await _client!.CustomObjects.ListNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural,
                labelSelector: $"{PreviewReaper.LabelPartOf}={PreviewReaper.LabelPartOfValue}",
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        var routes = ParsePreviewRoutes(raw);

        var maxPerRun = Math.Max(1, _options.MaxConcurrentSessionsPerRun);
        var globalMax = Math.Max(1, _options.MaxConcurrentSessionsGlobal);
        if (routes.Count(r => string.Equals(r.SanitizedRun, sanitizedRun, StringComparison.Ordinal)) >= maxPerRun)
            throw new PortForwardLimitExceededException(
                $"Port-forward session limit exceeded for run {sanitizedRun}. Limit: {maxPerRun}.");
        if (routes.Count >= globalMax)
            throw new PortForwardLimitExceededException(
                $"Global port-forward session limit exceeded. Limit: {globalMax}.");
    }

    private object BuildHttpRoute(
        string token, string sanitizedRun, string ownerUserId, string podName, int targetPort, string hostname,
        string serviceName, DateTimeOffset startedAt, DateTimeOffset expiresAt, DateTimeOffset maxUntil,
        string runId, string? previewRunnerSessionId)
    {
        var annotations = new Dictionary<string, string>
        {
            [PreviewReaper.AnnotationExpiresAt] = Rfc3339(expiresAt),
            [PreviewReaper.AnnotationMaxUntil] = Rfc3339(maxUntil),
            [PreviewReaper.AnnotationRun] = sanitizedRun,
            [PreviewReaper.AnnotationRunId] = runId,
            [PreviewReaper.AnnotationToken] = token,
            [PreviewReaper.AnnotationOwner] = ownerUserId ?? "",
            [PreviewReaper.AnnotationPod] = podName,
            [PreviewReaper.AnnotationTargetPort] = targetPort.ToString(CultureInfo.InvariantCulture),
            [PreviewReaper.AnnotationStartedAt] = Rfc3339(startedAt),
        };
        if (!string.IsNullOrWhiteSpace(previewRunnerSessionId))
            annotations[PreviewReaper.AnnotationPreviewRunnerSessionId] = previewRunnerSessionId;

        return new
        {
            apiVersion = $"{HttpRouteGroup}/{HttpRouteVersion}",
            kind = "HTTPRoute",
            metadata = new
            {
                name = serviceName,
                @namespace = _options.Namespace,
                labels = new Dictionary<string, string>
                {
                    [PreviewReaper.LabelPartOf] = PreviewReaper.LabelPartOfValue,
                    [PreviewReaper.LabelToken] = token,
                },
                annotations,
            },
            spec = new
            {
                parentRefs = new[]
                {
                    new { name = _options.GatewayName, @namespace = _options.GatewayNamespace },
                },
                hostnames = new[] { hostname },
                rules = new[]
                {
                    new
                    {
                        // Rewrite the upstream Host header to "localhost" before the request reaches
                        // the run's dev server. Modern dev servers (Vite 5+/6, CRA, Angular) ship a
                        // DNS-rebinding host allowlist that rejects any Host they weren't explicitly
                        // told about with HTTP 403 "Blocked request ... add to server.allowedHosts".
                        // The dynamic per-preview hostname ({token}-preview.{zone}) is never in that
                        // allowlist, so without this rewrite a healthy, correctly-bound app would be
                        // unreachable through the gateway even though preview readiness (a pod-local
                        // 127.0.0.1 probe) reports "ready". "localhost" is allowed by default across
                        // these frameworks, making this a single framework-agnostic fix. The preview
                        // token stays the browser-facing secret; the app just never sees it. (#312)
                        filters = new[]
                        {
                            new
                            {
                                type = "URLRewrite",
                                urlRewrite = new { hostname = PreviewUpstreamHost },
                            },
                        },
                        backendRefs = new[]
                        {
                            new { name = serviceName, port = 80 },
                        },
                    },
                },
            },
        };
    }

    private async Task CreateServiceIdempotentAsync(V1Service service, CancellationToken ct)
    {
        try
        {
            await _client!.CoreV1.CreateNamespacedServiceAsync(service, _options.Namespace, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogInformation(
                "SandboxPreviewService: Service {Fingerprint} already exists (idempotent)",
                Fingerprint(service.Metadata.Name));
        }
    }

    private async Task DeleteHttpRouteIdempotentAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client!.CustomObjects.DeleteNamespacedCustomObjectAsync(
                HttpRouteGroup, HttpRouteVersion, _options.Namespace, HttpRoutePlural, name,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // already gone — idempotent
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SandboxPreviewService: could not delete HTTPRoute {Fingerprint} (best-effort)",
                Fingerprint(name));
        }
    }

    private async Task DeleteServiceIdempotentAsync(string name, CancellationToken ct)
    {
        try
        {
            await _client!.CoreV1.DeleteNamespacedServiceAsync(name, _options.Namespace, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (HttpOperationException ex) when (ex.Response?.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // already gone — idempotent
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "SandboxPreviewService: could not delete Service {Fingerprint} (best-effort)",
                Fingerprint(name));
        }
    }

    private async Task<bool> PodExistsForRunAsync(string sanitizedRun, CancellationToken ct)
    {
        try
        {
            var pods = await _client!.CoreV1.ListNamespacedPodAsync(
                _options.Namespace,
                labelSelector: $"{PreviewReaper.PodPreviewRunLabel}={sanitizedRun}",
                cancellationToken: ct).ConfigureAwait(false);
            return pods.Items.Count > 0;
        }
        catch (Exception ex)
        {
            // Fail-safe: on a transient API error, treat the pod as present so a blip never
            // causes the reaper to tear down a live preview. Idle/max expiry still bounds lifetime.
            _logger.LogWarning(ex,
                "SandboxPreviewService: pod-existence probe failed for run {Run}; assuming alive", sanitizedRun);
            return true;
        }
    }

    private static IReadOnlyList<PreviewRouteInfo> ParsePreviewRoutes(object raw)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(raw);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var result = new List<PreviewRouteInfo>();

        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != System.Text.Json.JsonValueKind.Array)
            return result;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("metadata", out var meta) ||
                !meta.TryGetProperty("annotations", out var ann))
                continue;

            var token = GetString(ann, PreviewReaper.AnnotationToken);
            if (string.IsNullOrEmpty(token))
                continue;

            result.Add(new PreviewRouteInfo(
                token,
                GetString(ann, PreviewReaper.AnnotationRun) ?? "",
                GetString(ann, PreviewReaper.AnnotationExpiresAt),
                GetString(ann, PreviewReaper.AnnotationMaxUntil),
                GetString(ann, PreviewReaper.AnnotationPod),
                TryParseInt(GetString(ann, PreviewReaper.AnnotationTargetPort)),
                GetString(ann, PreviewReaper.AnnotationStartedAt)));
        }

        return result;
    }

    private static IReadOnlyList<string> ParsePreviewRouteNames(object raw)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(raw);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var names = new List<string>();

        if (!doc.RootElement.TryGetProperty("items", out var items) ||
            items.ValueKind != System.Text.Json.JsonValueKind.Array)
            return names;

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("metadata", out var meta) &&
                meta.TryGetProperty("name", out var n) &&
                n.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var name = n.GetString();
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        return names;
    }

    private static string? GetString(System.Text.Json.JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString()
            : null;

    private static int? TryParseInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static string Rfc3339(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Short, non-reversible fingerprint (first 4 bytes of SHA-256, hex) used for log correlation
    /// WITHOUT ever emitting the secret token / capability URL into logs (Seraph requirement).
    /// </summary>
    private static string Fingerprint(string value)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private sealed record PreviewRouteInfo(
        string Token,
        string SanitizedRun,
        string? ExpiresAt,
        string? MaxUntil,
        string? PodName,
        int? TargetPort,
        string? StartedAt);
}
