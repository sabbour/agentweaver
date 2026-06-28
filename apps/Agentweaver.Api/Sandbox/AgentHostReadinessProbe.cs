using Microsoft.Extensions.Logging;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Polls a freshly-launched AgentHost pod until it is actually serving HTTP, closing the A2A
/// cold-start race: the <c>SandboxClaim</c> binds when the pod is <c>Running</c>, but the AgentHost
/// Kestrel listener takes ~20-30s longer to bind <c>:8088</c>. <see cref="KubernetesSandboxExecutor"/>
/// awaits this gate after bind and before returning the A2A endpoint, so the worker never sends the
/// first turn into the boot window (which would be refused, failing the run mid-turn).
/// </summary>
internal interface IAgentHostReadinessProbe
{
    /// <summary>
    /// Polls <paramref name="readinessUrl"/> until it returns a success status, or throws
    /// <see cref="TimeoutException"/> when the AgentHost does not become ready within the budget.
    /// Honors <paramref name="ct"/> (e.g. launch cancellation).
    /// </summary>
    Task WaitUntilReadyAsync(string readinessUrl, CancellationToken ct);
}

/// <summary>
/// <see cref="IAgentHostReadinessProbe"/> backed by the <c>a2a-sandbox-pod</c> named
/// <see cref="HttpClient"/> — the SAME client/handler (incl. the mTLS client cert in production) the
/// real A2A turns use, so a successful probe proves the worker can actually reach the pod.
/// </summary>
internal sealed class HttpAgentHostReadinessProbe : IAgentHostReadinessProbe
{
    /// <summary>Named HttpClient that can reach the sandbox pod IP (shares the A2A mTLS handler).</summary>
    public const string HttpClientName = "a2a-sandbox-pod";

    /// <summary>Per-attempt timeout so a single hung connect cannot consume the whole budget.</summary>
    private static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(5);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _interval;
    private readonly ILogger _logger;

    public HttpAgentHostReadinessProbe(
        IHttpClientFactory httpClientFactory,
        TimeSpan timeout,
        TimeSpan interval,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeout = timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(90);
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromSeconds(1);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task WaitUntilReadyAsync(string readinessUrl, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + _timeout;
        var attempt = 0;
        Exception? lastError = null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            attempt++;

            try
            {
                using var client = _httpClientFactory.CreateClient(HttpClientName);
                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(AttemptTimeout);

                using var response = await client
                    .GetAsync(readinessUrl, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation(
                        "AgentHost readiness confirmed at {Url} after {Attempt} attempt(s).",
                        readinessUrl, attempt);
                    return;
                }

                lastError = new HttpRequestException(
                    $"readiness probe to {readinessUrl} returned HTTP {(int)response.StatusCode}.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The launch itself was cancelled — propagate.
                throw;
            }
            catch (Exception ex)
            {
                // Connection refused (boot window), per-attempt timeout, transient socket error, etc.
                lastError = ex;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"AgentHost did not become ready at {readinessUrl} within {_timeout.TotalSeconds:F0}s " +
                    $"({attempt} attempt(s)).", lastError);
            }

            _logger.LogDebug(
                "AgentHost not ready at {Url} (attempt {Attempt}): {Error}. Retrying in {Interval}ms.",
                readinessUrl, attempt, lastError?.Message, _interval.TotalMilliseconds);

            await Task.Delay(_interval, ct).ConfigureAwait(false);
        }
    }
}
