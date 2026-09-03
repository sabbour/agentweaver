using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using Agentweaver.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Agentweaver.Tests.Mcp;

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

    [Fact]
    public async Task Stdio_ForwardsConfiguredBrokerToken()
    {
        var handler = new CapturingHandler();
        var client = new AgentweaverApiClient(
            new HttpClient(handler),
            new McpConfig("http://localhost", "broker-token"));

        await client.GetAsync<object>("/api/ping", CancellationToken.None);

        handler.CapturedAuth?.Parameter.Should().Be("broker-token");
    }

    [Fact]
    public async Task Stdio_WithoutBrokerToken_FailsBeforeApiCall()
    {
        var handler = new CapturingHandler();
        var client = new AgentweaverApiClient(
            new HttpClient(handler),
            new McpConfig("http://localhost"));

        var action = () => client.GetAsync<object>("/api/ping", CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*validated Agentweaver MCP broker token*");
        handler.CapturedAuth.Should().BeNull();
    }

    [Fact]
    public async Task Http_ForwardsOnlyAuthenticatedValidatedBrokerToken()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "subject")],
                "McpBroker")),
        };
        context.Items["mcp.validated_broker_token"] = "validated-broker-token";
        var handler = new CapturingHandler();
        var client = new AgentweaverApiClient(
            new HttpClient(handler),
            new McpConfig("http://localhost", "configured-stdio-token"),
            new HttpContextAccessor { HttpContext = context });

        await client.GetAsync<object>("/api/ping", CancellationToken.None);

        handler.CapturedAuth?.Parameter.Should().Be("validated-broker-token");
    }

    [Fact]
    public async Task Http_DoesNotFallBackToConfiguredOrUnvalidatedTokens()
    {
        var context = new DefaultHttpContext();
        context.Items["mcp.validated_broker_token"] = "unvalidated-token";
        var handler = new CapturingHandler();
        var client = new AgentweaverApiClient(
            new HttpClient(handler),
            new McpConfig("http://localhost", "configured-stdio-token"),
            new HttpContextAccessor { HttpContext = context });

        var action = () => client.GetAsync<object>("/api/ping", CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        handler.CapturedAuth.Should().BeNull();
    }
}
