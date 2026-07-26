using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.SandboxExec;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agentweaver.AgentRuntime;

/// <summary>
/// Builds the <c>start_preview</c> <see cref="AIFunction"/> that finalizes/publishes a durable,
/// externally-reachable preview URL for a run's sandbox by calling
/// <c>POST /api/runs/{runId}/sandbox/preview</c>.
/// </summary>
/// <remarks>
/// Historically this tool was only wired up via <see cref="AgentweaverApiTools.Build"/>, which is
/// gated on both <c>projectId</c> and <c>agentName</c> being non-empty. Subtask sandboxes driven by
/// <c>PreviewRunnerToolProvider</c> (in Agentweaver.AgentHost) always register
/// <c>start_preview_process</c>/<c>observe_bound_port</c>/<c>health_check</c>/<c>stop_preview_process</c>
/// regardless of that gating, and <c>observe_bound_port</c>'s response text instructs the agent to
/// call <c>start_preview(port=...)</c> next — so this tool must be reachable from that path too, not
/// only from the projectId/agentName-gated one. This type is the single shared implementation both
/// call sites use so the two never drift (see GitHub issue #334).
/// </remarks>
public static class PreviewPublishTool
{
    /// <summary>
    /// Builds the <c>start_preview</c> tool for the given run. The model supplies ONLY the port; the
    /// run ID is bound server-side in the closure so the model cannot target another run.
    /// </summary>
    /// <param name="apiBaseUrl">The Agentweaver API base URL (e.g. <c>http://localhost:5000</c>).</param>
    /// <param name="apiKey">Bearer token for API authentication; may be null for unauthenticated local dev.</param>
    /// <param name="runId">The run ID this preview belongs to.</param>
    /// <param name="httpClientOverride">Optional pre-configured HttpClient (for testing). If null a new client is created from <paramref name="apiBaseUrl"/>/<paramref name="apiKey"/>.</param>
    /// <param name="logger">
    /// Optional logger used to record structured, durable telemetry when the tool call fails (non-success
    /// HTTP response or a caught exception from the underlying request). AgentHost pods are ephemeral and
    /// recycled shortly after a run completes, so without this the only evidence of a failure is whatever
    /// the agent happened to print during its turn (see GitHub issue #528). Response bodies and exception
    /// messages are redacted via <see cref="SandboxOutputRedactor"/> and truncated before logging.
    /// </param>
    public static AIFunction Build(
        string apiBaseUrl, string? apiKey, string runId, HttpClient? httpClientOverride = null,
        ILogger? logger = null)
    {
        var http = httpClientOverride ?? CreateHttpClient(apiBaseUrl, apiKey);

        return AIFunctionFactory.Create(
            async (
                [Description("The port your web server is listening on inside the sandbox, e.g. 3000")] int port,
                CancellationToken ct = default) =>
            {
                HttpResponseMessage response;
                try
                {
                    response = await http.PostAsJsonAsync(
                        $"api/runs/{runId}/sandbox/preview",
                        new { target_port = port },
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var redactedMessage = Redact(ex.Message);
                    logger?.LogError(
                        ex,
                        "Tool call failed: tool={ToolName} runId={RunId} port={Port} exception={ExceptionMessage}",
                        "start_preview", runId, port, redactedMessage);
                    return $"start_preview failed: {redactedMessage}";
                }

                var body = string.Empty;
                try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); } catch { }

                if (response.IsSuccessStatusCode)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("preview_url", out var urlEl) &&
                            urlEl.ValueKind == JsonValueKind.String)
                        {
                            return $"Preview is live at {urlEl.GetString()} — share this URL with the user.";
                        }
                    }
                    catch (JsonException) { }
                    return body;
                }

                var redactedBody = Redact(body);
                logger?.LogWarning(
                    "Tool call failed: tool={ToolName} runId={RunId} port={Port} statusCode={StatusCode} response={ResponseBody}",
                    "start_preview", runId, port, (int)response.StatusCode, redactedBody);

                return $"start_preview failed: HTTP {(int)response.StatusCode} — {body}";
            },
            "start_preview",
            "Expose a web server you started in the sandbox (e.g. on port 3000) so the user can preview it. " +
            "Returns the public preview URL once approved.");
    }

    // Truncated to keep the durable telemetry sink (App Insights trace/exception payload) reasonably
    // sized — full bodies aren't needed to root-cause a failure and larger payloads cost more to ingest.
    private const int MaxLoggedBodyLength = 1000;

    /// <summary>
    /// Redacts anything that looks like a credential/token/secret and truncates before the value is
    /// handed to <see cref="ILogger"/>. The tool's return value to the model is intentionally NOT
    /// redacted (the agent needs the real error to react to) — only what gets logged is scrubbed.
    /// </summary>
    private static string Redact(string text)
    {
        var redacted = SandboxOutputRedactor.Default.Redact(text);
        return redacted.Length > MaxLoggedBodyLength
            ? redacted[..MaxLoggedBodyLength] + "...[truncated]"
            : redacted;
    }

    private static HttpClient CreateHttpClient(string apiBaseUrl, string? apiKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + '/') };
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }
}
