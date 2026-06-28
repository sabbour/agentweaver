using System.Net;
using System.Net.Sockets;
using Agentweaver.Api.Sandbox;
using FluentAssertions;

namespace Agentweaver.Tests;

/// <summary>
/// Verifies the <c>a2a-sandbox-pod</c> defense-in-depth: <see cref="ConnectRefusedRetryHandler"/>
/// retries ONLY connection-refused (cold-start boot window) and never masks other transport faults.
/// </summary>
public sealed class ConnectRefusedRetryHandlerTests
{
    [Fact]
    public async Task Retries_connection_refused_then_succeeds()
    {
        var refused = new HttpRequestException(
            "refused", new SocketException((int)SocketError.ConnectionRefused));
        var inner = new StepHandler(
            () => throw refused,
            () => throw refused,
            () => new HttpResponseMessage(HttpStatusCode.OK));
        var handler = new ConnectRefusedRetryHandler(maxAttempts: 5, baseDelay: TimeSpan.FromMilliseconds(1))
        {
            InnerHandler = inner,
        };
        var client = new HttpClient(handler);

        var response = await client.GetAsync("http://10.0.0.7:8088/a2a/agent");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.CallCount.Should().Be(3);
    }

    [Fact]
    public async Task Does_not_retry_non_connect_failures()
    {
        // A generic HttpRequestException with no connection-refused signal must NOT be retried.
        var other = new HttpRequestException("some mid-stream error");
        var inner = new StepHandler(() => throw other);
        var handler = new ConnectRefusedRetryHandler(maxAttempts: 5, baseDelay: TimeSpan.FromMilliseconds(1))
        {
            InnerHandler = inner,
        };
        var client = new HttpClient(handler);

        var act = () => client.GetAsync("http://10.0.0.7:8088/a2a/agent");

        await act.Should().ThrowAsync<HttpRequestException>();
        inner.CallCount.Should().Be(1);
    }

    private sealed class StepHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage>[] _steps;
        public int CallCount { get; private set; }
        public StepHandler(params Func<HttpResponseMessage>[] steps) => _steps = steps;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var step = _steps[Math.Min(CallCount, _steps.Length - 1)];
            CallCount++;
            try { return Task.FromResult(step()); }
            catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
        }
    }
}
