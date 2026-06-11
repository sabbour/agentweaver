using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Scaffolder.Cli;

/// <summary>Raised when an API call returns a non-success status.</summary>
public sealed class ApiException(int statusCode, string body)
    : Exception($"API request failed with status {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}

/// <summary>
/// Raised when POST /review returns 409 indicating a retriable precondition failure.
/// The run remains awaiting_review; the caller may fix the condition and re-submit.
/// </summary>
public sealed class RetriableReviewException(string serverMessage, string runStatus)
    : Exception($"Review cannot proceed: {serverMessage}")
{
    public string ServerMessage { get; } = serverMessage;
    public string RunStatus { get; } = runStatus;
}

/// <summary>Typed thin wrapper over the Scaffolder backend API.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;
    private readonly CliConfig _config;

    public ApiClient(CliConfig config, HttpClient? http = null)
    {
        _config = config;
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", config.ApiKey);
    }

    public string ApiUrl => _config.ApiUrl;

    public SseClient CreateSseClient() => new(_http, _config.ApiKey);

    public async Task<SubmitRunResponse> SubmitRunAsync(
        SubmitRunRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{_config.ApiUrl}/api/runs")
        {
            Content = JsonContent.Create(request, options: JsonConfig.Options)
        };
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<SubmitRunResponse>(response, ct);
    }

    public async Task<RunDetail> GetRunAsync(string runId, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync($"{_config.ApiUrl}/api/runs/{runId}", ct);
        return await ReadJsonAsync<RunDetail>(response, ct);
    }

    public async Task<IReadOnlyList<RunEvent>> GetEventsAsync(
        string runId, int afterSequence = -1, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(
            $"{_config.ApiUrl}/api/runs/{runId}/events?afterSequence={afterSequence}", ct);
        return await ReadJsonAsync<List<RunEvent>>(response, ct);
    }

    public async Task<ReviewSubmitResponse> SubmitReviewAsync(
        string runId, bool approved, CancellationToken ct = default)
    {
        var request = new ReviewSubmitRequest { Approved = approved };
        using var message = new HttpRequestMessage(
            HttpMethod.Post, $"{_config.ApiUrl}/api/runs/{runId}/review")
        {
            Content = JsonContent.Create(request, options: JsonConfig.Options)
        };
        using var response = await _http.SendAsync(message, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if ((int)response.StatusCode == 409)
        {
            RetriableReviewErrorBody? errorBody = null;
            try
            {
                errorBody = JsonSerializer.Deserialize<RetriableReviewErrorBody>(
                    body, JsonConfig.Options);
            }
            catch (JsonException) { }

            if (errorBody is not null)
                throw new RetriableReviewException(errorBody.Error, errorBody.Status);
            throw new ApiException(409, body);
        }

        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode, body);

        var value = JsonSerializer.Deserialize<ReviewSubmitResponse>(body, JsonConfig.Options);
        if (value is null)
            throw new ApiException((int)response.StatusCode, "Response body could not be parsed.");
        return value;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new ApiException((int)response.StatusCode, body);
        }

        var value = JsonSerializer.Deserialize<T>(body, JsonConfig.Options);
        if (value is null)
        {
            throw new ApiException((int)response.StatusCode,
                "Response body could not be parsed.");
        }

        return value;
    }

    /// <summary>Builds the SSE stream URL for a run.</summary>
    public string StreamUrl(string runId) => $"{_config.ApiUrl}/api/runs/{runId}/stream";

    public async Task<SandboxPolicy> GetSandboxPolicyAsync(
        string repositoryPath, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(
            $"{_config.ApiUrl}/api/sandbox-policy?repository_path={Uri.EscapeDataString(repositoryPath)}", ct);
        return await ReadJsonAsync<SandboxPolicy>(response, ct);
    }

    public async Task<SandboxPolicy> SetSandboxPolicyAsync(
        SetSandboxPolicyRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(
            HttpMethod.Put, $"{_config.ApiUrl}/api/sandbox-policy")
        {
            Content = JsonContent.Create(request, options: JsonConfig.Options)
        };
        using var response = await _http.SendAsync(message, ct);
        return await ReadJsonAsync<SandboxPolicy>(response, ct);
    }

    public async Task<IReadOnlyList<WorkspaceFileEntry>> GetRunFilesAsync(
        string runId, string filter = "all", CancellationToken ct = default)
    {
        var url = $"{_config.ApiUrl}/api/runs/{Uri.EscapeDataString(runId)}/files?filter={Uri.EscapeDataString(filter)}";
        using var response = await _http.GetAsync(url, ct);
        return await ReadJsonAsync<List<WorkspaceFileEntry>>(response, ct);
    }

    public async Task<WorkspaceFileDiff> GetRunFileDiffAsync(
        string runId, string path, CancellationToken ct = default)
    {
        var url = $"{_config.ApiUrl}/api/runs/{Uri.EscapeDataString(runId)}/files/{Uri.EscapeDataString(path)}";
        using var response = await _http.GetAsync(url, ct);
        return await ReadJsonAsync<WorkspaceFileDiff>(response, ct);
    }
}
