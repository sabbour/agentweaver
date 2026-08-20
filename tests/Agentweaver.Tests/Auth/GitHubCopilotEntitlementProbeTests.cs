using System.Net;
using Agentweaver.Api.Auth;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Auth;

/// <summary>
/// The probe must ask the SAME Copilot surface the agent runtime uses (<c>api.githubcopilot.com/models</c>).
/// The previous <c>copilot_internal/v2/token</c> URL 404s on that host (and is restricted to allow-listed
/// editor OAuth apps on api.github.com), so every entitled account was reported as un-entitled.
/// </summary>
public sealed class GitHubCopilotEntitlementProbeTests
{
    [Fact]
    public async Task Probe_calls_copilot_models_endpoint_with_bearer_token()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "{\"data\":[]}");
        var probe = CreateProbe(handler);

        var result = await probe.ProbeAsync("gho_test-token");

        result.Should().BeTrue();
        handler.LastRequest!.RequestUri!.AbsoluteUri.Should().Be("https://api.githubcopilot.com/models");
        handler.LastRequest.Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.LastRequest.Headers.Authorization.Parameter.Should().Be("gho_test-token");
        handler.LastRequest.Headers.Contains("Copilot-Integration-Id").Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Probe_is_inconclusive_for_auth_rejections(HttpStatusCode statusCode)
    {
        var probe = CreateProbe(new CapturingHandler(statusCode, "{}"));

        (await probe.ProbeAsync("gho_test-token")).Should().BeNull();
    }

    [Fact]
    public async Task Probe_is_inconclusive_for_server_errors()
    {
        var probe = CreateProbe(new CapturingHandler(HttpStatusCode.InternalServerError, "{}"));

        (await probe.ProbeAsync("gho_test-token")).Should().BeNull();
    }

    [Fact]
    public async Task Probe_is_inconclusive_without_a_token()
    {
        var probe = CreateProbe(new CapturingHandler(HttpStatusCode.OK, "{}"));

        (await probe.ProbeAsync("  ")).Should().BeNull();
    }

    private static GitHubCopilotEntitlementProbe CreateProbe(HttpMessageHandler handler) =>
        new(new StubHttpClientFactory(handler), NullLogger<GitHubCopilotEntitlementProbe>.Instance);

    private sealed class CapturingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
