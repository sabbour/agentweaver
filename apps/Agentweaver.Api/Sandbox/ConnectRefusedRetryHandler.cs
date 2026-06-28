using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Defense-in-depth for the A2A cold-start race on the <c>a2a-sandbox-pod</c> HttpClient: retries a
/// request ONLY when the TCP connect itself is refused (the AgentHost Kestrel listener has not bound
/// <c>:8088</c> yet). This is safe even for non-idempotent streaming sends because a connection-refused
/// failure happens BEFORE any request bytes leave the socket — nothing was delivered, so there is no
/// duplicate side effect. Any other failure (mid-stream, HTTP error, non-connect socket error) is NOT
/// retried and surfaces to the caller unchanged.
/// </summary>
internal sealed class ConnectRefusedRetryHandler : DelegatingHandler
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly ILogger? _logger;

    public ConnectRefusedRetryHandler(int maxAttempts = 5, TimeSpan? baseDelay = null, ILogger? logger = null)
    {
        _maxAttempts = maxAttempts < 1 ? 1 : maxAttempts;
        _baseDelay = baseDelay ?? TimeSpan.FromMilliseconds(250);
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _maxAttempts && IsConnectionRefused(ex))
            {
                var delay = TimeSpan.FromMilliseconds(_baseDelay.TotalMilliseconds * attempt);
                _logger?.LogDebug(
                    "a2a-sandbox-pod connect refused for {Uri} (attempt {Attempt}/{Max}); retrying in {Delay}ms.",
                    request.RequestUri, attempt, _maxAttempts, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// True only when the failure is a refused TCP connect — the AgentHost has not started listening
    /// yet. Scoped tightly so we never retry mid-stream or other transport faults.
    /// </summary>
    private static bool IsConnectionRefused(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.ConnectionError)
        {
            return true;
        }

        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException { SocketErrorCode: SocketError.ConnectionRefused })
            {
                return true;
            }
        }

        return false;
    }
}
