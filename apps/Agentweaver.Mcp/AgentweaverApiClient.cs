using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public AgentweaverApiClient(HttpClient http, McpConfig config)
    {
        _http = http;
        _config = config;
        _http.BaseAddress = new Uri(config.ApiUrl.TrimEnd('/') + "/");
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path.TrimStart('/'), ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task<T> PostAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task PostAsync(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<T> PutAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task PutAsync(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Put, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        using var response = await _http.SendAsync(message, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task<T> PatchAsync<T>(string path, object? body, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Patch, path.TrimStart('/'))
        {
            Content = body is not null ? JsonContent.Create(body, options: JsonOptions) : null
        };
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<T>(response, ct);
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.DeleteAsync(path.TrimStart('/'), ct);
        await EnsureSuccessAsync(response, ct);
    }

    public IAsyncEnumerable<SseEvent> StreamSseAsync(string path, CancellationToken ct = default)
    {
        var fullUrl = _config.ApiUrl.TrimEnd('/') + "/" + path.TrimStart('/');
        var sseClient = new SseClient(_http, _config.ApiKey);
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
