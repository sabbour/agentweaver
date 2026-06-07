using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scaffolder.Cli.Api;

/// <summary>
/// T053: Typed HTTP client for the Scaffolder API.
/// Generated from contracts/run-api.yaml targeting http://localhost:3000.
/// </summary>
public sealed class ScaffolderApiClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ScaffolderApiClient(string baseUrl = "http://localhost:3000")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
    }

    // ------------------------------------------------------------------
    // POST /runs
    // ------------------------------------------------------------------

    public async Task<RunResponse?> CreateRunAsync(
        CreateRunRequest request,
        string? submittedBy = null,
        CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "runs")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                Encoding.UTF8,
                "application/json")
        };

        if (submittedBy is not null)
        {
            req.Headers.Add("X-Submitted-By", submittedBy);
        }

        var response = await _http.SendAsync(req, ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<RunResponse>(JsonOptions, ct);
    }

    // ------------------------------------------------------------------
    // GET /runs/{runId}
    // ------------------------------------------------------------------

    public async Task<RunResponse?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"runs/{runId}", ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<RunResponse>(JsonOptions, ct);
    }

    // ------------------------------------------------------------------
    // GET /runs/{runId}/diff
    // ------------------------------------------------------------------

    public async Task<string> GetDiffAsync(Guid runId, CancellationToken ct = default)
    {
        var response = await _http.GetAsync($"runs/{runId}/diff", ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadAsStringAsync(ct);
    }

    // ------------------------------------------------------------------
    // GET /runs/{runId}/stream (SSE)
    // ------------------------------------------------------------------

    /// <summary>
    /// Connects to the SSE stream for a run and yields events until the run
    /// reaches a terminal event or the cancellation token fires.
    /// Reconnects automatically using Last-Event-ID.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> StreamRunEventsAsync(
        Guid runId,
        long lastSeenSequence = 0,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var reconnect = true;
        while (reconnect && !ct.IsCancellationRequested)
        {
            reconnect = false;

            var req = new HttpRequestMessage(HttpMethod.Get, $"runs/{runId}/stream");
            if (lastSeenSequence > 0)
            {
                req.Headers.Add("Last-Event-ID", lastSeenSequence.ToString());
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException)
            {
                yield break;
            }

            if (!response.IsSuccessStatusCode)
            {
                yield break;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            string? id = null;
            string? eventType = null;
            var dataBuilder = new StringBuilder();

            while (!ct.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (Exception)
                {
                    // Connection dropped — trigger reconnect
                    reconnect = true;
                    break;
                }

                if (line is null)
                {
                    break; // EOF
                }

                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    id = line[3..].Trim();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line[6..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataBuilder.Append(line[5..].Trim());
                }
                else if (line.Length == 0 && eventType is not null)
                {
                    // Empty line = dispatch event
                    if (long.TryParse(id, out var seq))
                    {
                        lastSeenSequence = seq;
                    }

                    var evt = new SseEvent(
                        Sequence: lastSeenSequence,
                        EventType: eventType,
                        Data: dataBuilder.ToString());

                    yield return evt;

                    // Terminal events close the stream
                    if (IsTerminalEvent(eventType))
                    {
                        reconnect = false;
                        break;
                    }

                    id = null;
                    eventType = null;
                    dataBuilder.Clear();
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // POST /runs/{runId}/review
    // ------------------------------------------------------------------

    public async Task<RunResponse?> ReviewRunAsync(
        Guid runId,
        ReviewDecisionRequest request,
        CancellationToken ct = default)
    {
        var content = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions),
            Encoding.UTF8,
            "application/json");

        var response = await _http.PostAsync($"runs/{runId}/review", content, ct);
        await EnsureSuccessAsync(response);
        return await response.Content.ReadFromJsonAsync<RunResponse>(JsonOptions, ct);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static bool IsTerminalEvent(string eventType) =>
        eventType is "run.failed" or "run.bounded" or "review.declined"
            or "merge.completed" or "merge.failed";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new ApiException((int)response.StatusCode, body);
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed class ApiException(int statusCode, string body)
    : Exception($"API error {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
    public string Body { get; } = body;
}

public sealed record SseEvent(long Sequence, string EventType, string Data);
