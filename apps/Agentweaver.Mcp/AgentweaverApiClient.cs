using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;

namespace Agentweaver.Mcp;

/// <summary>Raised when an API call returns a non-success status.</summary>
/// <remarks>
/// Derives from <see cref="McpException"/> so the MCP server surfaces the structured
/// <c>{ error, hint }</c> message to the client. The SDK only forwards an exception's message
/// through the tool-call error content when the exception is an <see cref="McpException"/>;
/// plain exceptions collapse to a generic "An error occurred invoking '&lt;tool&gt;'." string.
/// </remarks>
public sealed class McpApiException : McpException
{
    private static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public int StatusCode { get; }
    public string Error { get; }
    public string Hint { get; }
    public string? RawMessage { get; }
    public string? Path { get; }

    public McpApiException(int statusCode, string message, string? path = null, string? errorCode = null, string? hint = null)
        : this(BuildPayload(statusCode, message, path, errorCode, hint))
    {
    }

    private McpApiException(McpErrorPayload payload)
        : base(JsonSerializer.Serialize(new { error = payload.Error, hint = payload.Hint }, ErrorJsonOptions))
    {
        StatusCode = payload.StatusCode;
        Error = payload.Error;
        Hint = payload.Hint;
        RawMessage = payload.RawMessage;
        Path = payload.Path;
    }

    private static McpErrorPayload BuildPayload(int statusCode, string message, string? path, string? errorCode, string? explicitHint)
    {
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? null : path;
        var normalizedMessage = NormalizeMessage(message);

        if (IsTimeout(statusCode, normalizedMessage))
        {
            return new McpErrorPayload(
                statusCode == 0 ? -32001 : statusCode,
                "Request timed out. The server is likely busy.",
                "Call diagnostics_get to check health, then retry.",
                normalizedMessage,
                normalizedPath);
        }

        if (statusCode == 401)
        {
            return new McpErrorPayload(
                statusCode,
                "Not signed in.",
                "Call github_signin then session_start before retrying.",
                normalizedMessage,
                normalizedPath);
        }

        if (statusCode == 410 && normalizedPath?.Contains("/api/projects/", StringComparison.OrdinalIgnoreCase) == true
            && normalizedPath.EndsWith("/runs", StringComparison.OrdinalIgnoreCase))
        {
            return new McpErrorPayload(
                statusCode,
                "run_submit no longer targets the coordinator workflow.",
                "Call run_task for the common flow or coordinator_start for manual control.",
                normalizedMessage,
                normalizedPath);
        }

        if (statusCode == 404 && TryBuildNotFoundPayload(normalizedPath, normalizedMessage, out var notFound))
            return notFound;

        if (statusCode == 409 && TryBuildConflictPayload(normalizedPath, normalizedMessage, out var conflict))
            return conflict;

        if (statusCode == 400 && TryBuildBadRequestPayload(normalizedPath, normalizedMessage, out var badRequest))
            return badRequest;

        if (statusCode >= 500)
        {
            return new McpErrorPayload(
                statusCode,
                "Agentweaver failed while processing the request.",
                "Call diagnostics_get to check server health, then retry.",
                normalizedMessage,
                normalizedPath);
        }

        if (statusCode == 0)
        {
            return new McpErrorPayload(
                statusCode,
                "The MCP tool failed before Agentweaver returned a response.",
                explicitHint ?? "Retry once. If it keeps failing, call diagnostics_get and inspect the target project or run state.",
                normalizedMessage,
                normalizedPath);
        }

        return new McpErrorPayload(
            statusCode,
            normalizedMessage,
            explicitHint ?? DefaultHintForPath(normalizedPath),
            normalizedMessage,
            normalizedPath);
    }

    private static bool TryBuildNotFoundPayload(string? path, string message, out McpErrorPayload payload)
    {
        var segments = GetPathSegments(path);
        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "projects")
        {
            var projectId = Uri.UnescapeDataString(segments[2]);

            // Team/casting sub-resource 404s mean the workspace isn't initialized yet, not that
            // the project is missing. Surfacing "Project not found" here misleads callers into
            // thinking the project ID is wrong when it genuinely exists but has no team cast.
            if (segments.Length >= 4 && segments[3] is "team" or "casting")
            {
                payload = new McpErrorPayload(
                    404,
                    $"No team configured for project '{projectId}'. Cast a team first with team_cast.",
                    "Use team_cast to initialize the team, then retry team_get.",
                    message,
                    path);
                return true;
            }

            payload = new McpErrorPayload(
                404,
                $"Project '{projectId}' not found.",
                "Call project_list to see available projects.",
                message,
                path);
            return true;
        }

        if (segments.Length >= 5 && segments[0] == "api" && segments[1] == "runs" && segments[3] == "files")
        {
            var runId = Uri.UnescapeDataString(segments[2]);
            var filePath = string.Join("/", segments.Skip(4).Select(Uri.UnescapeDataString));
            payload = new McpErrorPayload(
                404,
                $"File '{filePath}' not found for run '{runId}'.",
                "Call run_show_artifacts first to see available file paths.",
                message,
                path);
            return true;
        }

        if (segments.Length >= 3 && segments[0] == "api" && segments[1] == "runs")
        {
            var runId = Uri.UnescapeDataString(segments[2]);
            payload = new McpErrorPayload(
                404,
                $"Run '{runId}' not found.",
                "Call project_list_runs to find a valid run_id for the project.",
                message,
                path);
            return true;
        }

        payload = default;
        return false;
    }

    private static bool TryBuildConflictPayload(string? path, string message, out McpErrorPayload payload)
    {
        if (path?.EndsWith("/review", StringComparison.OrdinalIgnoreCase) == true)
        {
            var currentState = ExtractQuotedValue(message) ?? "unknown";
            payload = new McpErrorPayload(
                409,
                $"Run is not awaiting review (current state: {currentState}).",
                "Call run_status to check current state.",
                message,
                path);
            return true;
        }

        if (path?.Contains("/sandbox/preview", StringComparison.OrdinalIgnoreCase) == true)
        {
            payload = new McpErrorPayload(
                409,
                "Sandbox pod not yet bound. The run's SandboxClaim is still pending.",
                "Retry in a few seconds, or call run_status to confirm the sandbox is ready before retrying start_preview.",
                message,
                path);
            return true;
        }

        if (message.Contains("worktree not available", StringComparison.OrdinalIgnoreCase))
        {
            payload = new McpErrorPayload(
                409,
                "Run artifacts are not ready yet.",
                "Call run_status to confirm the run is in a review-ready or terminal state, then retry.",
                message,
                path);
            return true;
        }

        payload = default;
        return false;
    }

    private static bool TryBuildBadRequestPayload(string? path, string message, out McpErrorPayload payload)
    {
        if (message.Contains("workflow", StringComparison.OrdinalIgnoreCase)
            && (message.Contains("allow", StringComparison.OrdinalIgnoreCase)
                || message.Contains("enabled", StringComparison.OrdinalIgnoreCase)))
        {
            var workflowId = ExtractQuotedValue(message) ?? "requested";
            payload = new McpErrorPayload(
                400,
                $"Workflow '{workflowId}' is not enabled for this project.",
                "Call project_get to see allowed_workflow_ids.",
                message,
                path);
            return true;
        }

        payload = default;
        return false;
    }

    private static string[] GetPathSegments(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split('?', 2)[0]
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ExtractQuotedValue(string message)
    {
        var first = message.IndexOf('\'');
        if (first < 0) return null;
        var second = message.IndexOf('\'', first + 1);
        return second > first ? message[(first + 1)..second] : null;
    }

    private static bool IsTimeout(int statusCode, string message) =>
        statusCode is -32001 or 408 or 504
        || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
        || message.Contains("timeout", StringComparison.OrdinalIgnoreCase);

    private static string DefaultHintForPath(string? path)
    {
        var segments = GetPathSegments(path);
        if (segments.Length >= 2 && segments[1] == "projects")
            return "Call project_get or project_list to confirm the project state, then retry.";

        if (segments.Length >= 2 && segments[1] == "runs")
            return "Call run_status to inspect the run state before retrying.";

        return "Retry once. If it keeps failing, call diagnostics_get and inspect the related project or run state.";
    }

    private static string NormalizeMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "The request failed.";

        var normalized = message.Trim().Replace("\r", " ").Replace("\n", " ");
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);

        return normalized;
    }

    private readonly record struct McpErrorPayload(
        int StatusCode,
        string Error,
        string Hint,
        string? RawMessage,
        string? Path);
}

/// <summary>Typed thin wrapper over the Agentweaver backend API.</summary>
public sealed class AgentweaverApiClient
{
    private readonly HttpClient _http;
    private readonly McpConfig _config;
    // Injected when registered as scoped/singleton-with-accessor.
    // When present, the caller's own bearer token (API key, or an Agentweaver-minted OAuth access
    // token validated by McpBearerTokenMiddleware) is forwarded to the backend so the backend sees
    // the real caller identity instead of the shared service identity. In stdio mode (no inbound
    // HTTP context) the configured per-user token (AGENTWEAVER_TOKEN) is used; the shared
    // AGENTWEAVER_API_KEY is only a last-resort fallback for in-process/service callers (#474).
    private readonly IHttpContextAccessor? _httpContextAccessor;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public AgentweaverApiClient(HttpClient http, McpConfig config, IHttpContextAccessor? httpContextAccessor = null)
    {
        _http = http;
        _config = config;
        _httpContextAccessor = httpContextAccessor;
        _http.BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>
    /// Returns the Bearer token to use for this request.
    /// Prefers the caller's own token stored in <c>mcp.bearer_token</c> (set by the inbound
    /// middleware after validating the GitHub token), so the backend receives the real caller
    /// identity. In stdio mode there is no inbound HTTP context, so it uses the configured per-user
    /// token (<c>AGENTWEAVER_TOKEN</c>). The shared service key (<c>AGENTWEAVER_API_KEY</c>) is used
    /// only as a last resort — for genuine in-process/service callers — because the API maps it to
    /// the trusted <c>agentweaver-internal</c> identity that bypasses project-ownership checks (#474).
    /// </summary>
    private string GetEffectiveApiKey()
    {
        var ctx = _httpContextAccessor?.HttpContext;
        if (ctx?.Items.TryGetValue("mcp.bearer_token", out var callerToken) == true && callerToken is string token)
            return token;
        if (!string.IsNullOrWhiteSpace(_config.UserToken))
            return _config.UserToken;
        return _config.ApiKey;
    }

    private AuthenticationHeaderValue GetAuthHeader() =>
        new("Bearer", GetEffectiveApiKey());

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, path, ct);
    }

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, path, ct);
    }

    public async Task PostAsync(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, path, ct);
    }

    public async Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, path, ct);
    }

    public async Task PutAsync(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, path, ct);
    }

    public async Task<T> PatchAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, path, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, path.TrimStart('/'));
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, path, ct);
    }

    public IAsyncEnumerable<SseEvent> StreamSseAsync(string path, CancellationToken ct = default)
    {
        var fullUrl = _config.ApiUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var sseClient = new SseClient(_http, GetEffectiveApiKey());
        return sseClient.StreamAsync(fullUrl, ct);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string path, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, path, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new McpApiException((int)response.StatusCode, "Empty response body", path);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string path, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string? error = null;
            string? message = null;
            string? hint = null;
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    error = err.GetString();
                if (doc.RootElement.TryGetProperty("message", out var msg))
                    message = msg.GetString();
                if (doc.RootElement.TryGetProperty("detail", out var detail) && string.IsNullOrWhiteSpace(message))
                    message = detail.GetString();
                if (doc.RootElement.TryGetProperty("hint", out var hintProp))
                    hint = hintProp.GetString();
            }
            catch (JsonException) { }

            throw new McpApiException(
                (int)response.StatusCode,
                message ?? error ?? body,
                path,
                error,
                hint);
        }
    }
}
