extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayRunEgressTests
{
    [Fact]
    public async Task UnauthenticatedRunEgressSetsNoStoreBeforeAuthRejects()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                ownerCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

        foreach (var path in new[] { "/api/v1/runs/run-a/egress", "/api/v1/runs/run-a/egress/" })
        {
            using var response = await gateway.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        }
        Assert.Equal(0, ownerCalls);
    }

    [Fact]
    public async Task ValidatedBearerForwardsToOrchestratorWithTenantAndNoStore()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string expectedBody =
            """{"runId":"run-a","coverage":"Unavailable","delayedUntil":null,"items":[{"host":"api.example","address":null,"port":443,"verdict":"allowed","firstSeen":"2026-11-20T10:00:00Z","lastSeen":"2026-11-20T10:00:00Z","count":1}],"nextCursor":null}""";

        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (owner, request, _) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/runs/run-a/egress?limit=25", request.RequestUri!.PathAndQuery);
                Assert.Equal(bearer, request.Headers.Authorization?.Parameter);
                Assert.Equal("tenant-a", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
                var resp = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(expectedBody, Encoding.UTF8, "application/json")
                };
                resp.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return Task.FromResult(resp);
            });

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/run-a/egress?limit=25");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        req.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var response = await gateway.Client.SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OpenApiDescribesReadRunEgressContractAndBounds()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths")
            .GetProperty("/api/v1/runs/{runId}/egress").GetProperty("get");
        Assert.Equal("readRunEgress", operation.GetProperty("operationId").GetString());
        Assert.Equal("Orchestrator", operation.GetProperty("x-agentweaver-owner").GetString());

        var limit = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "limit");
        Assert.Equal(1, limit.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal(50, limit.GetProperty("schema").GetProperty("default").GetInt32());

        var cursor = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            p => p.GetProperty("name").GetString() == "cursor");
        Assert.Equal(2048, cursor.GetProperty("schema").GetProperty("maxLength").GetInt32());

        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "400", "401", "403", "404", "503" })
            Assert.True(responses.TryGetProperty(status, out _), $"Missing status {status}");
        Assert.Contains("Cache-Control", responses.GetProperty("200")
            .GetProperty("headers").EnumerateObject().Select(h => h.Name));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var pageSchema = schemas.GetProperty("RunEgressPage");
        Assert.Equal(["Unavailable"], pageSchema.GetProperty("properties").GetProperty("coverage")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        var recordProps = schemas.GetProperty("RunEgressRecord").GetProperty("properties");
        Assert.Equal(["allowed", "denied"], recordProps.GetProperty("verdict")
            .GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.False(recordProps.TryGetProperty("workloadRef", out _));
        Assert.False(recordProps.TryGetProperty("ownerFingerprint", out _));
        Assert.False(recordProps.TryGetProperty("flowId", out _));
    }

    [Fact]
    public async Task GatewayNormalizesReadRunEgressUpstreamTimeoutAndNetworkFailuresTo503OnlyForThisRoute()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        Exception failureToThrow = new HttpRequestException("upstream connection refused");

        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (owner, request, cancellationToken) =>
            {
                if (failureToThrow is TaskCanceledException)
                    return Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        .ContinueWith(_ => new HttpResponseMessage(HttpStatusCode.OK), cancellationToken);
                return Task.FromException<HttpResponseMessage>(failureToThrow);
            },
            ownerRequestTimeout: TimeSpan.FromMilliseconds(100));

        // 1. Network failure on readRunEgress -> 503 run_egress_unavailable with no-store.
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/run-a/egress"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await gateway.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            Assert.Contains("run_egress_unavailable", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 2. IOException on readRunEgress -> 503 run_egress_unavailable with no-store.
        failureToThrow = new IOException("broken pipe");
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/run-a/egress"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await gateway.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            Assert.Contains("run_egress_unavailable", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 3. Timeout on readRunEgress -> 503 run_egress_unavailable with no-store.
        failureToThrow = new TaskCanceledException("simulated timeout");
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/run-a/egress"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await gateway.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            Assert.Contains("run_egress_unavailable", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 4. Another route keeps 502 owner_unavailable on HttpRequestException (normalization is route-only).
        failureToThrow = new HttpRequestException("upstream connection refused");
        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/proj-a/runs"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await gateway.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
            Assert.Contains("owner_unavailable", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task GatewayNormalizesRejectedUpstreamRedirectOnReadRunEgressTo503OnlyForThisRoute()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);

        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://evil.example/redirect");
                return Task.FromResult(redirect);
            });

        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/runs/run-a/egress"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await gateway.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            Assert.Contains("run_egress_unavailable", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects/proj-a/runs"))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var resp = await gateway.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
            Assert.Contains("owner_redirect_rejected", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    private static string CreateToken(SecurityKey key) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", Guid.NewGuid().ToString("D")), new Claim("scope", "api.read")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
}
