using System.Runtime.CompilerServices;

namespace Agentweaver.Mcp;

/// <summary>One parsed server-sent event frame.</summary>
public readonly record struct SseEvent(string? Id, string Data);

/// <summary>
/// Minimal server-sent events client over <see cref="HttpClient"/>. Streams
/// frames from an endpoint, parsing <c>id:</c> and <c>data:</c> fields, and
/// reconnects automatically using the last seen id as <c>Last-Event-ID</c>.
/// </summary>
public sealed class SseClient(HttpClient http, string apiKey)
{
    private readonly HttpClient _http = http;
    private readonly string _apiKey = apiKey;

    /// <summary>
    /// Connects to the endpoint and yields each event as it arrives. On a dropped
    /// connection it reconnects, sending the last received id as Last-Event-ID so
    /// the server replays only events after it.
    /// </summary>
    public async IAsyncEnumerable<SseEvent> StreamAsync(
        string url,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string? lastEventId = null;

        while (!ct.IsCancellationRequested)
        {
            Stream? stream = null;
            StreamReader? reader = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
                if (lastEventId is not null)
                {
                    request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
                }

                var response = await _http.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    throw new McpApiException((int)response.StatusCode, body);
                }

                stream = await response.Content.ReadAsStreamAsync(ct);
                reader = new StreamReader(stream);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            string? currentId = lastEventId;
            var dataLines = new List<string>();

            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (IOException)
                {
                    // Connection dropped; break to reconnect with the last id.
                    line = null;
                }

                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (dataLines.Count > 0)
                    {
                        lastEventId = currentId;
                        yield return new SseEvent(currentId, string.Join("\n", dataLines));
                        dataLines.Clear();
                    }
                    continue;
                }

                if (line.StartsWith(':'))
                {
                    continue;
                }

                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    currentId = line[3..].TrimStart();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    dataLines.Add(line[5..].TrimStart());
                }
            }

            reader.Dispose();
            stream?.Dispose();

            if (ct.IsCancellationRequested)
            {
                yield break;
            }

            // Brief pause before reconnecting.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }
}
