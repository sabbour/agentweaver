using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Agentweaver.Api.Sandbox;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox.Preview;

/// <summary>Result of starting a supervised preview process on the AgentHost pod.</summary>
public sealed record PreviewRunnerStartResult(string SessionId, int Pid, string? WorkingDirectory);

/// <summary>Result of the deterministic bound-port observation. <see cref="Port"/> is the pod-IP-reachable
/// public (forwarder) port the Gateway registers; <see cref="AppPort"/> is the app's real loopback port
/// (evidence only). <see cref="Reason"/> carries a distinct failure code (e.g. <c>bound_unreachable</c>).</summary>
public sealed record PreviewRunnerPortResult(
    string SessionId, int Port, bool Healthy, string? Evidence, int AppPort = 0, string? Reason = null);

/// <summary>Result of an HTTP liveness health-check.</summary>
public sealed record PreviewRunnerHealthResult(string SessionId, int Port, bool Healthy, int? StatusCode);

/// <summary>Typed error surfaced when a preview-runner HTTP call fails or is unauthorized.</summary>
public sealed class PreviewRunnerHttpException : Exception
{
    public PreviewRunnerHttpException(string reason, string message, HttpStatusCode? status = null)
        : base(message)
    {
        Reason = reason;
        StatusCode = status;
    }

    /// <summary>Closed-set failure reason (e.g. <c>preview_runner_unauthorized</c>, <c>agenthost_unreachable</c>).</summary>
    public string Reason { get; }

    public HttpStatusCode? StatusCode { get; }
}

/// <summary>
/// Typed client for the AgentHost <c>/preview-runner/*</c> platform endpoints (spec-006
/// decouple-preview). Targets the AgentHost <b>ORIGIN + root path</b> (via
/// <see cref="IAgentHostOriginResolver"/>) — NEVER the A2A URI (BLOCKER 1). The bearer credential is
/// the per-run turn token OR the per-run preview-runner credential (BLOCKER 2/A); a <c>401</c> is
/// surfaced as <see cref="PreviewRunnerHttpException"/> with reason <c>preview_runner_unauthorized</c>.
/// </summary>
public interface IPreviewRunnerHttpClient
{
    Task<PreviewRunnerStartResult> StartProcessAsync(
        string runId, string? bearer, string command, string cwd, int? workPlanId, string? treeHash, CancellationToken ct);

    Task<PreviewRunnerPortResult> ObserveBoundPortAsync(
        string runId, string? bearer, string sessionId, int timeoutSeconds, string healthPath, CancellationToken ct);

    Task<PreviewRunnerHealthResult> HealthCheckAsync(
        string runId, string? bearer, string sessionId, int port, string path, CancellationToken ct);

    Task StopProcessAsync(string runId, string? bearer, string sessionId, string reason, CancellationToken ct);

    /// <summary>Health-check by explicit origin (used by the keepalive dual-touch on either replica).</summary>
    Task<PreviewRunnerHealthResult> HealthCheckByOriginAsync(
        string origin, string? bearer, string sessionId, int port, string path, CancellationToken ct);
}

public sealed class PreviewRunnerHttpClient : IPreviewRunnerHttpClient
{
    /// <summary>Reuses the A2A named client (mTLS/scheme wiring + connect-refused retry).</summary>
    public const string HttpClientName = "a2a-sandbox-pod";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAgentHostOriginResolver _originResolver;
    private readonly ILogger<PreviewRunnerHttpClient> _logger;

    public PreviewRunnerHttpClient(
        IHttpClientFactory httpClientFactory,
        IAgentHostOriginResolver originResolver,
        ILogger<PreviewRunnerHttpClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _originResolver = originResolver;
        _logger = logger;
    }

    public async Task<PreviewRunnerStartResult> StartProcessAsync(
        string runId, string? bearer, string command, string cwd, int? workPlanId, string? treeHash, CancellationToken ct)
    {
        var origin = await ResolveOriginOrThrowAsync(runId, ct).ConfigureAwait(false);
        var body = new { command, cwd, runId, workPlanId = workPlanId?.ToString(), treeHash };
        var resp = await SendAsync(HttpMethod.Post, $"{origin}/preview-runner/processes", bearer, body, ct)
            .ConfigureAwait(false);
        var dto = await ReadJsonAsync<StartResponse>(resp, ct).ConfigureAwait(false);
        return new PreviewRunnerStartResult(dto.SessionId ?? "", dto.Pid, dto.WorkingDirectory);
    }

    public async Task<PreviewRunnerPortResult> ObserveBoundPortAsync(
        string runId, string? bearer, string sessionId, int timeoutSeconds, string healthPath, CancellationToken ct)
    {
        var origin = await ResolveOriginOrThrowAsync(runId, ct).ConfigureAwait(false);
        var body = new { timeoutSeconds, healthPath };
        var resp = await SendAsync(
            HttpMethod.Post, $"{origin}/preview-runner/processes/{Uri.EscapeDataString(sessionId)}/observe-bound-port",
            bearer, body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<ObserveResponse>(resp, ct).ConfigureAwait(false);
        return new PreviewRunnerPortResult(dto.SessionId ?? sessionId, dto.Port, dto.Healthy, dto.Evidence, dto.AppPort, dto.Reason);
    }

    public Task<PreviewRunnerHealthResult> HealthCheckAsync(
        string runId, string? bearer, string sessionId, int port, string path, CancellationToken ct) =>
        HealthCheckCoreAsync(() => ResolveOriginOrThrowAsync(runId, ct), bearer, sessionId, port, path, ct);

    public Task<PreviewRunnerHealthResult> HealthCheckByOriginAsync(
        string origin, string? bearer, string sessionId, int port, string path, CancellationToken ct) =>
        HealthCheckCoreAsync(() => Task.FromResult(origin.TrimEnd('/')), bearer, sessionId, port, path, ct);

    private async Task<PreviewRunnerHealthResult> HealthCheckCoreAsync(
        Func<Task<string>> originFactory, string? bearer, string sessionId, int port, string path, CancellationToken ct)
    {
        var origin = await originFactory().ConfigureAwait(false);
        var body = new { port, path };
        var resp = await SendAsync(
            HttpMethod.Post, $"{origin}/preview-runner/processes/{Uri.EscapeDataString(sessionId)}/health-check",
            bearer, body, ct).ConfigureAwait(false);
        var dto = await ReadJsonAsync<HealthResponse>(resp, ct).ConfigureAwait(false);
        return new PreviewRunnerHealthResult(dto.SessionId ?? sessionId, dto.Port, dto.Healthy, dto.StatusCode);
    }

    public async Task StopProcessAsync(string runId, string? bearer, string sessionId, string reason, CancellationToken ct)
    {
        var origin = await ResolveOriginOrThrowAsync(runId, ct).ConfigureAwait(false);
        var url = $"{origin}/preview-runner/processes/{Uri.EscapeDataString(sessionId)}?reason={Uri.EscapeDataString(reason)}";
        await SendAsync(HttpMethod.Delete, url, bearer, body: null, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveOriginOrThrowAsync(string runId, CancellationToken ct)
    {
        var origin = await _originResolver.TryResolveOriginAsync(runId, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(origin))
            throw new PreviewRunnerHttpException(
                "agenthost_unreachable", $"No reachable AgentHost pod origin for run '{runId}'.");
        return origin.TrimEnd('/');
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string url, string? bearer, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(bearer))
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
        if (body is not null)
            request.Content = JsonContent.Create(body);

        var client = _httpClientFactory.CreateClient(HttpClientName);
        HttpResponseMessage resp;
        try
        {
            resp = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new PreviewRunnerHttpException(
                "agenthost_unreachable", $"preview-runner call to {method} failed: {ex.Message}");
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            resp.Dispose();
            throw new PreviewRunnerHttpException(
                "preview_runner_unauthorized",
                "AgentHost rejected the preview-runner credential (401).",
                HttpStatusCode.Unauthorized);
        }

        if (!resp.IsSuccessStatusCode)
        {
            var status = resp.StatusCode;
            string detail;
            try { detail = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
            catch { detail = string.Empty; }
            resp.Dispose();
            throw new PreviewRunnerHttpException(
                "preview_runner_error", $"preview-runner call failed: HTTP {(int)status} {detail}", status);
        }

        return resp;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        where T : new()
    {
        try
        {
            var dto = await resp.Content.ReadFromJsonAsync<T>(ct).ConfigureAwait(false);
            return dto ?? new T();
        }
        finally
        {
            resp.Dispose();
        }
    }

    private sealed record StartResponse
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [JsonPropertyName("pid")] public int Pid { get; init; }
        [JsonPropertyName("working_directory")] public string? WorkingDirectory { get; init; }
    }

    private sealed record ObserveResponse
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [JsonPropertyName("port")] public int Port { get; init; }
        [JsonPropertyName("app_port")] public int AppPort { get; init; }
        [JsonPropertyName("healthy")] public bool Healthy { get; init; }
        [JsonPropertyName("evidence")] public string? Evidence { get; init; }
        [JsonPropertyName("reason")] public string? Reason { get; init; }
    }

    private sealed record HealthResponse
    {
        [JsonPropertyName("session_id")] public string? SessionId { get; init; }
        [JsonPropertyName("port")] public int Port { get; init; }
        [JsonPropertyName("healthy")] public bool Healthy { get; init; }
        [JsonPropertyName("status_code")] public int? StatusCode { get; init; }
    }
}
