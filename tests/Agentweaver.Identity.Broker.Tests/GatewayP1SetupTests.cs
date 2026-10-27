extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayP1SetupTests
{
    [Fact]
    public async Task SetupIsPurposeLimitedBoundedAndExcludedFromMcp()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call owners."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/p1/invocations/{invocationId}/setup").GetProperty("post");
        Assert.Equal("createP1InvocationSetup", operation.GetProperty("operationId").GetString());
        Assert.Equal("Projects", operation.GetProperty("x-agentweaver-owner").GetString());
        Assert.True(operation.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
        Assert.True(!operation.TryGetProperty("requestBody", out var body) ||
            body.ValueKind == JsonValueKind.Null);
        Assert.True(Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            value => value.GetProperty("name").GetString() == "X-Agentweaver-Tenant")
            .GetProperty("required").GetBoolean());
        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "403", "409", "503" })
            Assert.True(responses.TryGetProperty(status, out _));
        Assert.Equal("no-store", responses.GetProperty("200").GetProperty("headers")
            .GetProperty("Cache-Control").GetProperty("schema").GetProperty("example").GetString());
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("P1ProjectSetupReceipt");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(schema.GetProperty("properties").EnumerateObject().Select(value => value.Name),
            schema.GetProperty("required").EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain("accessToken",
            schema.GetProperty("properties").EnumerateObject().Select(value => value.Name));
        Assert.DoesNotContain("environmentId",
            schema.GetProperty("properties").EnumerateObject().Select(value => value.Name));
        var route = Assert.Single(GatewayRouteCatalog.Routes,
            value => value.OperationId == "createP1InvocationSetup");
        Assert.Equal("/api/p1/invocations/{invocationId:guid}/setup", route.OwnerPath);
        Assert.False(route.HasJsonBody);
        Assert.True(route.RequiresTenantSelector);
        Assert.Equal(16 * 1024L, route.MaximumResponseBytes);
    }

    [Fact]
    public async Task SetupForwardsOnlyExistingBearerAndTenantToProjectsAndPreservesUnavailable()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "setup-actor"),
                new Claim("scope", "api.read projects.admin projects.orchestrator")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var bearer = new JwtSecurityTokenHandler().WriteToken(token);
        var invocationId = Guid.NewGuid();
        var calls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey, () => new SetupOwnerHandler(request =>
            {
                calls++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal($"/api/p1/invocations/{invocationId:D}/setup", request.RequestUri!.AbsolutePath);
                Assert.Equal(bearer, request.Headers.Authorization?.Parameter);
                Assert.Equal("tenant-a", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
                Assert.Null(request.Content);
                var result = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"code":"project_setup_authority_unavailable"}""", Encoding.UTF8, "application/problem+json"),
                };
                result.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return result;
            }),
            (_, _, _) => throw new InvalidOperationException("Setup must call Projects only."));
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/p1/invocations/{invocationId:D}/setup");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, calls);
        Assert.Contains("project_setup_authority_unavailable", await response.Content.ReadAsStringAsync());
        using var withBody = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/p1/invocations/{invocationId:D}/setup");
        withBody.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        withBody.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        withBody.Content = new StringContent("""{"projectId":"existing-project"}""",
            Encoding.UTF8, "application/json");
        using var rejectedBody = await gateway.Client.SendAsync(withBody);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedBody.StatusCode);
        Assert.True(rejectedBody.Headers.CacheControl?.NoStore);
        Assert.Equal(1, calls);
        using var unframedBody = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/p1/invocations/{invocationId:D}/setup")
        {
            Version = HttpVersion.Version20,
            Content = new LengthlessContent(),
        };
        unframedBody.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        unframedBody.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        Assert.Null(unframedBody.Content.Headers.ContentLength);
        Assert.False(unframedBody.Headers.Contains("Transfer-Encoding"));
        using var rejectedUnframedBody = await gateway.Client.SendAsync(unframedBody);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedUnframedBody.StatusCode);
        Assert.True(rejectedUnframedBody.Headers.CacheControl?.NoStore);
        Assert.Equal(1, calls);
        using var withQuery = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/p1/invocations/{invocationId:D}/setup?projectId=existing-project");
        withQuery.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        withQuery.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var rejectedQuery = await gateway.Client.SendAsync(withQuery);
        Assert.Equal(HttpStatusCode.BadRequest, rejectedQuery.StatusCode);
        Assert.True(rejectedQuery.Headers.CacheControl?.NoStore);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SetupBodyProbeHonorsOwnerRequestTimeout()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "setup-actor"),
                new Claim("scope", "api.read projects.admin projects.orchestrator")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var bearer = new JwtSecurityTokenHandler().WriteToken(token);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey, () => new HttpClientHandler(),
            (_, _, _) =>
            {
                ownerCalls++;
                throw new InvalidOperationException("The owner must not be called for a stalled body.");
            },
            ownerRequestTimeout: TimeSpan.FromSeconds(1));
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"/api/v1/p1/invocations/{Guid.NewGuid():D}/setup")
        {
            Content = new StalledContent(),
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal(0, ownerCalls);
    }

    private sealed class LengthlessContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes("{}")).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StalledContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.InfiniteTimeSpan);

        protected override Task SerializeToStreamAsync(
            Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class SetupOwnerHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
