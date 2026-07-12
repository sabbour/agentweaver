using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Sandbox;
using Agentweaver.Api.Sandbox.Preview;

namespace Agentweaver.Tests.Preview;

/// <summary>
/// Unit coverage for <see cref="PreviewRunnerHttpClient"/> (spec-006 BLOCKER 1/2). Asserts the client
/// targets the AgentHost ORIGIN + root <c>/preview-runner/*</c> path (NO <c>/a2a/</c> segment) and
/// carries the bearer credential, and that a <c>401</c> surfaces as
/// <c>preview_runner_unauthorized</c>.
/// </summary>
public sealed class PreviewRunnerHttpClientTests
{
    private const string Origin = "http://10.0.0.5:8088";

    [Fact]
    public async Task StartProcess_TargetsOriginRootPath_WithBearer_NoA2ASegment()
    {
        var handler = new CapturingHandler(
            """{ "session_id": "sess-1", "pid": 42, "working_directory": "/workspace" }""");
        var client = CreateClient(handler);

        var result = await client.StartProcessAsync(
            "run-1", "turn-token-xyz", "npm run dev", "/workspace/app", 7, "tree-abc", CancellationToken.None);

        result.SessionId.Should().Be("sess-1");
        handler.LastRequest!.RequestUri!.ToString().Should().Be($"{Origin}/preview-runner/processes");
        handler.LastRequest.RequestUri.ToString().Should().NotContain("/a2a/");
        handler.LastAuthorization.Should().Be("Bearer turn-token-xyz");
    }

    [Fact]
    public async Task ObserveBoundPort_TargetsOriginRootPath()
    {
        var handler = new CapturingHandler(
            """{ "session_id": "sess-1", "port": 3000, "healthy": true, "evidence": "stdout" }""");
        var client = CreateClient(handler);

        var result = await client.ObserveBoundPortAsync(
            "run-1", "cred", "sess-1", 60, "/", CancellationToken.None);

        result.Port.Should().Be(3000);
        result.Healthy.Should().BeTrue();
        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be($"{Origin}/preview-runner/processes/sess-1/observe-bound-port");
        handler.LastRequest.RequestUri.ToString().Should().NotContain("/a2a/");
    }

    [Fact]
    public async Task ObserveBoundPort_ParsesPublicPortAppPortAndReason()
    {
        // spec-006 preview-forwarder: observe now returns the public (forwarder) port plus the app's
        // real loopback port and a distinct failure reason. The client must thread all three through.
        var handler = new CapturingHandler(
            """{ "session_id": "sess-1", "port": 45678, "app_port": 3000, "healthy": false, "evidence": "fwd", "reason": "bound_unreachable" }""");
        var client = CreateClient(handler);

        var result = await client.ObserveBoundPortAsync(
            "run-1", "cred", "sess-1", 60, "/", CancellationToken.None);

        result.Port.Should().Be(45678);
        result.AppPort.Should().Be(3000);
        result.Healthy.Should().BeFalse();
        result.Reason.Should().Be("bound_unreachable");
    }

    [Fact]
    public async Task HealthCheckByOrigin_TargetsExplicitOrigin()
    {
        var handler = new CapturingHandler(
            """{ "session_id": "sess-1", "port": 3000, "healthy": true, "status_code": 200 }""");
        var client = CreateClient(handler);

        await client.HealthCheckByOriginAsync(Origin, "cred", "sess-1", 3000, "/", CancellationToken.None);

        handler.LastRequest!.RequestUri!.ToString()
            .Should().Be($"{Origin}/preview-runner/processes/sess-1/health-check");
    }

    [Fact]
    public async Task Unauthorized_SurfacesTypedReason()
    {
        var handler = new CapturingHandler("", HttpStatusCode.Unauthorized);
        var client = CreateClient(handler);

        var act = () => client.StartProcessAsync(
            "run-1", "bad-cred", "npm run dev", "/workspace", null, null, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<PreviewRunnerHttpException>();
        ex.Which.Reason.Should().Be("preview_runner_unauthorized");
    }

    private static PreviewRunnerHttpClient CreateClient(HttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(handler);
        var origin = new StubOriginResolver(Origin);
        return new PreviewRunnerHttpClient(factory, origin, NullLogger<PreviewRunnerHttpClient>.Instance);
    }

    private sealed class StubOriginResolver(string origin) : IAgentHostOriginResolver
    {
        public Task<string?> TryResolveOriginAsync(string runId, CancellationToken ct) =>
            Task.FromResult<string?>(origin);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(string responseJson, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var v)
                ? string.Join(",", v)
                : null;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
