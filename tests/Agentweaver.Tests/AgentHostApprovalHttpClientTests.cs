using System.Net;
using System.Text;
using Agentweaver.Api.Sandbox;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests;

public sealed class AgentHostApprovalHttpClientTests
{
    private const string Origin = "http://10.0.0.8:8088";

    [Fact]
    public async Task Grant_TargetsResolvedOrigin_WithInjectedBearer()
    {
        var handler = new CapturingHandler("""{"resolved":true,"state":"approved"}""");
        var client = CreateClient(handler, Origin);

        var result = await client.GrantAsync(
            "child-run", "request-1", "tool", "pod-bearer", CancellationToken.None);

        result.Should().Be(new AgentHostApprovalOutcome(true, "approved", false, 200));
        handler.LastRequest!.RequestUri!.ToString().Should().Be($"{Origin}/tool-approvals");
        handler.Authorization.Should().Be("Bearer pod-bearer");
        handler.Body.Should().Contain("\"runId\":\"child-run\"");
        handler.Body.Should().Contain("\"scope\":\"tool\"");
    }

    [Fact]
    public async Task NullOrigin_ReturnsUnreachable()
    {
        var handler = new CapturingHandler("""{"resolved":true,"state":"approved"}""");
        var client = CreateClient(handler, null);

        var result = await client.DenyAsync("child-run", "request-2", null, CancellationToken.None);

        result.Unreachable.Should().BeTrue();
        result.StatusCode.Should().BeNull();
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task PendingResponse_MapsBodyAndStatus()
    {
        var handler = new CapturingHandler(
            """{"resolved":false,"state":"pending"}""",
            HttpStatusCode.Conflict);
        var client = CreateClient(handler, Origin);

        var result = await client.DenyAsync("child-run", "request-3", "cred", CancellationToken.None);

        result.Should().Be(new AgentHostApprovalOutcome(false, "pending", false, 409));
        handler.LastRequest!.RequestUri!.ToString().Should().Be($"{Origin}/tool-denials");
        handler.Body.Should().NotContain("\"scope\"");
    }

    [Fact]
    public async Task GetPendingContext_TargetsRequestPathWithoutResolvingIt()
    {
        var handler = new CapturingHandler(
            """{"resolved":false,"state":"pending","toolName":"web_fetch","url":"https://example.test"}""");
        var client = CreateClient(handler, Origin);

        var result = await client.GetPendingContextAsync(
            "child-run", "request/1", "pod-bearer", CancellationToken.None);

        result.Should().Be(new AgentHostApprovalOutcome(
            false,
            "pending",
            false,
            200,
            ToolName: "web_fetch",
            Url: "https://example.test"));
        handler.LastRequest!.Method.Should().Be(HttpMethod.Get);
        handler.LastRequest.RequestUri!.ToString().Should().Be($"{Origin}/tool-approvals/request%2F1");
        handler.Body.Should().BeNull();
    }

    [Fact]
    public async Task RollbackScope_TargetsRequestPathWithExactScopeGrantId()
    {
        var handler = new CapturingHandler(
            """{"resolved":false,"state":"rolled_back","rolledBack":true}""");
        var client = CreateClient(handler, Origin);

        var result = await client.RollbackScopeAsync(
            "child-run", "request/1", "provisional-grant", "pod-bearer", CancellationToken.None);

        result.RolledBack.Should().BeTrue();
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.ToString()
            .Should().Be($"{Origin}/tool-approvals/request%2F1/rollback");
        handler.Body.Should().Contain("\"runId\":\"child-run\"");
        handler.Body.Should().Contain("\"requestId\":\"request/1\"");
        handler.Body.Should().Contain("\"scopeGrantId\":\"provisional-grant\"");
    }

    private static AgentHostApprovalHttpClient CreateClient(HttpMessageHandler handler, string? origin) =>
        new(
            new StubHttpClientFactory(handler),
            new StubOriginResolver(origin),
            NullLogger<AgentHostApprovalHttpClient>.Instance);

    private sealed class StubOriginResolver(string? origin) : IAgentHostOriginResolver
    {
        public Task<string?> TryResolveOriginAsync(string runId, CancellationToken ct) =>
            Task.FromResult(origin);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CapturingHandler(
        string responseJson,
        HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
