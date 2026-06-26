using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Mcp;

/// <summary>Raised when an API call returns a non-success status.</summary>
public sealed class McpApiException(int statusCode, string message)
    : Exception($"API error {statusCode}: {message}")
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>Typed thin wrapper over the Agentweaver backend API.</summary>
public sealed class AgentweaverApiClient
{
    private readonly HttpClient _http;
    private readonly McpConfig _config;
    // Injected when registered as scoped/singleton-with-accessor.
    // When present, the caller's own bearer token (API key, or an Agentweaver-minted OAuth access
    // token validated by McpBearerTokenMiddleware) is forwarded to the backend so the backend sees
    // the real caller identity instead of the shared service identity. Only stdio mode (no inbound
    // HTTP context) falls back to the shared AGENTWEAVER_API_KEY.
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
    /// identity. Falls back to the configured shared API key only when no per-request token
    /// is available (e.g. stdio mode).
    /// </summary>
    private string GetEffectiveApiKey()
    {
        var ctx = _httpContextAccessor?.HttpContext;
        if (ctx?.Items.TryGetValue("mcp.bearer_token", out var callerToken) == true && callerToken is string token)
            return token;
        return _config.ApiKey;
    }

    private AuthenticationHeaderValue GetAuthHeader() =>
        new("Bearer", GetEffectiveApiKey());

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path.TrimStart('/'));
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task PostAsync(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task PutAsync(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<T> PatchAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Delete, path.TrimStart('/'));
        message.Headers.Authorization = GetAuthHeader();
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public IAsyncEnumerable<SseEvent> StreamSseAsync(string path, CancellationToken ct = default)
    {
        var fullUrl = _config.ApiUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var sseClient = new SseClient(_http, GetEffectiveApiKey());
        return sseClient.StreamAsync(fullUrl, ct);
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new McpApiException((int)response.StatusCode, "Empty response body");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string message = body;
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    message = err.GetString() ?? body;
            }
            catch (JsonException) { }
            throw new McpApiException((int)response.StatusCode, message);
        }
    }
}
