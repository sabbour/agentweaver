using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>
/// Creates direct, proxy-free transport to a pinned sandbox Pod IP and verifies its bounded
/// liveness response before runtime configuration proceeds.
/// </summary>
public static class RuntimeSandboxHttpTransport
{
    private static readonly TimeSpan HealthRequestTimeout = TimeSpan.FromSeconds(10);

    internal static readonly HttpRequestOptionsKey<SandboxRuntimeHttpsEndpoint> EndpointOption =
        new("Agentweaver.SandboxRuntimeEndpoint");

    /// <summary>Verifies that the fixed sandbox liveness endpoint returns a small no-store live response.</summary>
    public static async Task VerifyHealthAsync(
        HttpClient client,
        SandboxRuntimeHttpsEndpoint endpoint,
        CancellationToken cancellationToken) =>
        await VerifyHealthAsync(client, endpoint, HealthRequestTimeout, cancellationToken).ConfigureAwait(false);

    /// <summary>Time-bounded health check variant used by tests and host startup policy.</summary>
    internal static async Task VerifyHealthAsync(
        HttpClient client,
        SandboxRuntimeHttpsEndpoint endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var configureEndpoint = endpoint.ReceiverConfigureEndpoint
            ?? throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
        endpoint.ValidateForConfigureEndpoint(configureEndpoint);
        var healthAddress = new Uri(configureEndpoint, "/health/live");
        using var request = new HttpRequestMessage(HttpMethod.Get, healthAddress);
        request.Options.Set(EndpointOption, endpoint);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, NoCache = true };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK ||
                response.RequestMessage?.RequestUri != healthAddress ||
                response.Headers.CacheControl?.NoStore != true ||
                response.Content.Headers.ContentType?.MediaType != "application/json" ||
                response.Content.Headers.ContentLength is { } length && (length <= 0 || length > 4096))
                throw new RuntimeAuthorizationException("runtime_sandbox_health_unavailable");

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var body = new MemoryStream();
            var buffer = new byte[1024];
            int count;
            while ((count = await stream.ReadAsync(
                       buffer.AsMemory(0, (int)Math.Min(buffer.Length, 4097 - body.Length)), deadline.Token)
                       .ConfigureAwait(false)) != 0)
            {
                if (body.Length + count > 4096)
                    throw new RuntimeAuthorizationException("runtime_sandbox_health_unavailable");
                body.Write(buffer, 0, count);
            }

            try
            {
                using var document = JsonDocument.Parse(body.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("status", out var status) ||
                    status.ValueKind != JsonValueKind.String || status.GetString() != "live")
                    throw new RuntimeAuthorizationException("runtime_sandbox_health_unavailable");
            }
            catch (JsonException)
            {
                throw new RuntimeAuthorizationException("runtime_sandbox_health_unavailable");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RuntimeAuthorizationException("runtime_sandbox_health_unavailable");
        }
    }

    /// <summary>
    /// Creates a handler that disables redirects and proxies and connects the validated
    /// sandbox hostname directly to its configured Pod address.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.Zero,
        ConnectCallback = async (context, cancellationToken) =>
        {
            EndPoint destination = context.DnsEndPoint;
            if (context.InitialRequestMessage.Options.TryGetValue(EndpointOption, out var endpoint))
            {
                endpoint.Validate();
                var configureEndpoint = endpoint.ReceiverConfigureEndpoint
                    ?? throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
                endpoint.ValidateForConfigureEndpoint(configureEndpoint);
                if (context.DnsEndPoint.Host != configureEndpoint.Host ||
                    context.DnsEndPoint.Port != configureEndpoint.Port)
                    throw new RuntimeAuthorizationException("runtime_sandbox_endpoint_invalid");
                destination = new IPEndPoint(IPAddress.Parse(endpoint.PodAddress), configureEndpoint.Port);
            }
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(destination, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
