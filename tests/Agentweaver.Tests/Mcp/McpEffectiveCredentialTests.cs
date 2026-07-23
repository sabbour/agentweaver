using System.Net;
using System.Net.Http.Headers;
using Agentweaver.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Mcp;

/// <summary>
/// Regression coverage for #474: MCP stdio clients must forward the caller's OWN per-user token
/// (<c>AGENTWEAVER_TOKEN</c>) to the backend, NOT the shared internal service key
/// (<c>AGENTWEAVER_API_KEY</c>). The shared key maps to the trusted <c>agentweaver-internal</c>
/// identity that bypasses project-ownership checks, so forwarding it from a human/stdio client would
/// grant access to every project. These tests pin the credential-selection precedence used by
/// <see cref="AgentweaverApiClient"/>:
///   inbound per-request token (HTTP mode) &gt; configured user token (stdio) &gt; shared service key.
/// </summary>
public sealed class McpEffectiveCredentialTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public AuthenticationHeaderValue? CapturedAuth { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CapturedAuth = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private static async Task<string?> SendAndCaptureAsync(McpConfig config, IHttpContextAccessor? accessor)
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler);
        var client = new AgentweaverApiClient(http, config, accessor);

        await client.GetAsync<object>("/api/ping", CancellationToken.None);

        return handler.CapturedAuth?.Parameter;
    }

    [Fact]
    public async Task Stdio_WithUserToken_ForwardsUserToken_NotSharedServiceKey()
    {
        // stdio mode: no inbound HttpContext. The user configured their own AGENTWEAVER_TOKEN.
        var config = new McpConfig("http://localhost", ApiKey: "shared-internal-service-key", UserToken: "user-personal-token");

        var forwarded = await SendAndCaptureAsync(config, accessor: null);

        forwarded.Should().Be("user-personal-token",
            "stdio clients must authenticate as the real user, never with the shared internal service key");
        forwarded.Should().NotBe("shared-internal-service-key");
    }

    [Fact]
    public async Task Stdio_WithoutUserToken_FallsBackToSharedKey()
    {
        // Back-compat: with no per-user token, the shared key is still used (in-process/service callers).
        var config = new McpConfig("http://localhost", ApiKey: "shared-internal-service-key");

        var forwarded = await SendAndCaptureAsync(config, accessor: null);

        forwarded.Should().Be("shared-internal-service-key");
    }

    [Fact]
    public async Task Http_InboundBearer_TakesPrecedenceOverConfiguredTokens()
    {
        // HTTP mode: the per-request caller token stashed by McpBearerTokenMiddleware wins.
        var context = new DefaultHttpContext();
        context.Items["mcp.bearer_token"] = "inbound-caller-token";
        var accessor = new HttpContextAccessor { HttpContext = context };

        var config = new McpConfig("http://localhost", ApiKey: "shared-internal-service-key", UserToken: "user-personal-token");

        var forwarded = await SendAndCaptureAsync(config, accessor);

        forwarded.Should().Be("inbound-caller-token");
    }
}
