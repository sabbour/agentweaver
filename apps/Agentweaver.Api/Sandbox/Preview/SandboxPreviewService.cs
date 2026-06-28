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
    Task<PreviewSession> StartPreviewAsync(string runId, int targetPort, string ownerUserId, CancellationToken ct = default);

    /// <summary>Bumps the preview's idle expiry to now + IdleTimeoutMinutes. Idempotent (404 ignored).</summary>
    Task KeepAliveAsync(string token, CancellationToken ct = default);

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

    /// <summary>Minimum age before a route-less preview Service is treated as a leaked orphan.</summary>
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromMinutes(2);

    private readonly IKubernetes? _client;
    private readonly SandboxPreviewOptions _options;
    private readonly ILogger<SandboxPreviewService> _logger;
    private readonly TimeProvider _clock;

    public SandboxPreviewService(
        IKubernetes? client,
        SandboxPreviewOptions options,
        ILogger<SandboxPreviewService> logger,
        TimeProvider? clock = null)
    {
        _client = client;
        _options = options;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public bool Enabled => _options.Enabled && _client is not null;

    public int AllowedPortMin => _options.AllowedPortMin;

    public int AllowedPortMax => _options.AllowedPortMax;

    public async Task<PreviewSession> StartPreviewAsync(
        string runId, int targetPort, string ownerUserId, CancellationToken ct = default)
    {
        EnsureReady();
        if (targetPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(targetPort), "targetPort must be between 1 and 65535.");

        var podName = await ResolveBoundPodNameAsync(runId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(podName))
            throw new InvalidOperationException(
                $"No bound sandbox pod for run {runId}. A preview is only available after the run's " +
                "SandboxClaim reports a bound pod (status.phase=Bound).");

        var sanitizedRun = PreviewReaper.PerRunLabel(runId);
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
        var httpRoute = BuildHttpRoute(token, sanitizedRun, ownerUserId, hostname, serviceName, expiresAt, maxUntil);

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
        }

        // TODO(morpheus): renew the backing SandboxClaim/pod TTL here once the claim-retention
        // seam (KubernetesSandboxExecutor claim retention) exposes a per-run renew hook. Today the
        // claim TTL is set at creation; the annotation bump above keeps the preview route alive and
        // the reaper's orphan check covers the pod-gone case, so keepalive never blocks on this.
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
    /// hitting the other replica would otherwise spuriously fail. Reading the claim is replica-safe:
    /// every replica sees the same claim status. Returns <see langword="null"/> when the claim is
    /// missing or not yet bound (a correct, deterministic "not ready" for ALL replicas).
    /// </summary>
    private async Task<string?> ResolveBoundPodNameAsync(string runId, CancellationToken ct)
    {
        var claimName = SandboxClaimConventions.DeriveAgentHostClaimName(runId);
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
            // No claim for this run yet (or already released) — not ready, deterministically.
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

    private object BuildHttpRoute(
        string token, string sanitizedRun, string ownerUserId, string hostname,
        string serviceName, DateTimeOffset expiresAt, DateTimeOffset maxUntil) => new
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
            annotations = new Dictionary<string, string>
            {
                [PreviewReaper.AnnotationExpiresAt] = Rfc3339(expiresAt),
                [PreviewReaper.AnnotationMaxUntil] = Rfc3339(maxUntil),
                [PreviewReaper.AnnotationRun] = sanitizedRun,
                [PreviewReaper.AnnotationToken] = token,
                [PreviewReaper.AnnotationOwner] = ownerUserId ?? "",
            },
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
                    backendRefs = new[]
                    {
                        new { name = serviceName, port = 80 },
                    },
                },
            },
        },
    };

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
                GetString(ann, PreviewReaper.AnnotationMaxUntil)));
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

    private sealed record PreviewRouteInfo(string Token, string SanitizedRun, string? ExpiresAt, string? MaxUntil);
}
