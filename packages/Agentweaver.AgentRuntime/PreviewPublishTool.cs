using System.ComponentModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;

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
    public static AIFunction Build(
        string apiBaseUrl, string? apiKey, string runId, HttpClient? httpClientOverride = null)
    {
        var http = httpClientOverride ?? CreateHttpClient(apiBaseUrl, apiKey);

        return AIFunctionFactory.Create(
            async (
                [Description("The port your web server is listening on inside the sandbox, e.g. 3000")] int port,
                CancellationToken ct = default) =>
            {
                var response = await http.PostAsJsonAsync(
                    $"api/runs/{runId}/sandbox/preview",
                    new { target_port = port },
                    ct).ConfigureAwait(false);

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

                return $"start_preview failed: HTTP {(int)response.StatusCode} — {body}";
            },
            "start_preview",
            "Expose a web server you started in the sandbox (e.g. on port 3000) so the user can preview it. " +
            "Returns the public preview URL once approved.");
    }

    private static HttpClient CreateHttpClient(string apiBaseUrl, string? apiKey)
    {
        var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl.TrimEnd('/') + '/') };
        if (!string.IsNullOrEmpty(apiKey))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return http;
    }
}
