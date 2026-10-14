using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Xunit;

namespace Agentweaver.Identity.Tests;

public sealed class RuntimeSandboxHttpTransportTests
{
    [Fact]
    public async Task VerifyHealthUsesTheOwnedConfigureAudienceWithoutSendingCredentials()
    {
        var endpoint = Endpoint();
        using var handler = new HealthHandler("{\"status\":\"live\"}");
        using var client = new HttpClient(handler);

        await RuntimeSandboxHttpTransport.VerifyHealthAsync(client, endpoint, CancellationToken.None);

        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal(new Uri(endpoint.Address, "/health/live"), handler.Address);
        Assert.Null(handler.Authorization);
        Assert.Equal(endpoint, handler.EndpointOption);
    }

    [Fact]
    public async Task VerifyHealthUsesReceiverDnsSeparateFromOwnedServiceAddress()
    {
        var endpoint = Endpoint() with
        {
            ReceiverConfigureEndpoint =
                new Uri("https://runtime-receiver.example.test:8443/runtime/v1/configure")
        };
        using var handler = new HealthHandler("{\"status\":\"live\"}");
        using var client = new HttpClient(handler);

        await RuntimeSandboxHttpTransport.VerifyHealthAsync(client, endpoint, CancellationToken.None);

        Assert.Equal(new Uri(endpoint.ReceiverConfigureEndpoint!, "/health/live"), handler.Address);
    }

    [Fact]
    public async Task VerifyHealthDeadlineCoversResponseBodyReads()
    {
        using var handler = new BlockingHealthHandler();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RuntimeSandboxHttpTransport.VerifyHealthAsync(
                client, Endpoint(), TimeSpan.FromMilliseconds(100), CancellationToken.None));

        Assert.Equal("runtime_sandbox_health_unavailable", failure.Code);
        Assert.True(handler.Stream.ReadStarted.Task.IsCompleted);
    }

    [Fact]
    public void CreatedHandlerDoesNotReuseConnectionsAcrossPodVerifications()
    {
        using var handler = RuntimeSandboxHttpTransport.CreateHandler();

        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, "application/json", "{\"status\":\"live\"}")]
    [InlineData(HttpStatusCode.OK, false, "application/json", "{\"status\":\"live\"}")]
    [InlineData(HttpStatusCode.OK, true, "text/plain", "{\"status\":\"live\"}")]
    [InlineData(HttpStatusCode.OK, true, "application/json", "{\"status\":\"starting\"}")]
    [InlineData(HttpStatusCode.OK, true, "application/json", "not-json")]
    public async Task VerifyHealthRejectsUntrustedResponses(
        HttpStatusCode statusCode, bool noStore, string mediaType, string body)
    {
        using var client = new HttpClient(new HealthHandler(body, statusCode, noStore, mediaType));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RuntimeSandboxHttpTransport.VerifyHealthAsync(client, Endpoint(), CancellationToken.None));

        Assert.Equal("runtime_sandbox_health_unavailable", failure.Code);
    }

    [Fact]
    public async Task VerifyHealthRejectsResponsesLargerThanTheBound()
    {
        using var client = new HttpClient(new HealthHandler(new string('x', 4097)));

        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            RuntimeSandboxHttpTransport.VerifyHealthAsync(client, Endpoint(), CancellationToken.None));

        Assert.Equal("runtime_sandbox_health_unavailable", failure.Code);
    }

    private static SandboxRuntimeHttpsEndpoint Endpoint()
    {
        var address = new Uri("https://runtime.svc.cluster.local:8443/");
        return new(address, "10.2.3.4", "runtime-pod-uid", "runtime-service-uid")
        {
            ReceiverConfigureEndpoint = new Uri(address, "runtime/v1/configure")
        };
    }

    private sealed class HealthHandler(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        bool noStore = true,
        string mediaType = "application/json") : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Address { get; private set; }
        public string? Authorization { get; private set; }
        public SandboxRuntimeHttpsEndpoint? EndpointOption { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Address = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            request.Options.TryGetValue(
                new HttpRequestOptionsKey<SandboxRuntimeHttpsEndpoint>("Agentweaver.SandboxRuntimeEndpoint"),
                out var endpointOption);
            EndpointOption = endpointOption;

            var content = new StringContent(body, Encoding.UTF8, mediaType);
            var response = new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = content
            };
            if (noStore)
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingHealthHandler : HttpMessageHandler
    {
        public BlockingReadStream Stream { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(Stream)
            };
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
            response.Content.Headers.ContentType = new("application/json");
            return Task.FromResult(response);
        }
    }

    private sealed class BlockingReadStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
