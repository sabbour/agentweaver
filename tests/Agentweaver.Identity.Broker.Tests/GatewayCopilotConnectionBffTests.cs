extern alias GatewayHost;

using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayCopilotConnectionBffTests
{
    [Fact]
    public async Task CopilotConnectionBffForwardsCurrentBearerAndOnlyLinkCookie()
    {
        using var rsa = RSA.Create(2048);
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        const string audience = "https://api.test";
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            issuer,
            audience,
            [new Claim("sub", "copilot-user-1")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddHours(1),
            new SigningCredentials(new RsaSecurityKey(rsa), SecurityAlgorithms.RsaSha256)));
        var requests = new ConcurrentQueue<OwnerRequest>();
        const string beginSetCookie =
            "__Host-agentweaver-copilot-link=nonce-token; Path=/; Max-Age=300; Secure; HttpOnly; SameSite=Lax";
        const string clearLinkCookie =
            "__Host-agentweaver-copilot-link=; Path=/; Max-Age=0; Secure; HttpOnly; SameSite=Lax";
        const string connectionId = "b390b940-4141-4d5a-a1e5-4db4ec10a487";
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            async (owner, request, cancellationToken) =>
            {
                Assert.Equal(GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker, owner);
                var body = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                requests.Enqueue(new OwnerRequest(
                    request.Method,
                    request.RequestUri!,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant)
                        ? tenant.Single()
                        : null,
                    request.Headers.TryGetValues("Cookie", out var cookie)
                        ? cookie.Single()
                        : null,
                    body));

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { accepted = true }),
                };
                if (request.RequestUri!.AbsolutePath.EndsWith("/begin", StringComparison.Ordinal))
                    response.Headers.TryAddWithoutValidation("Set-Cookie", beginSetCookie);
                else if (request.RequestUri.AbsolutePath.EndsWith("/complete", StringComparison.Ordinal))
                    response.Headers.TryAddWithoutValidation("Set-Cookie", clearLinkCookie);
                return response;
            });

        using (var openApiResponse = await gateway.Client.GetAsync("/openapi/v1.json"))
        using (var openApi = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync()))
        {
            Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
            Assert.False(openApi.RootElement.GetProperty("paths")
                .TryGetProperty("/api/connections/copilot-user/v1/begin", out _));
        }

        using var begin = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/connections/copilot-user/v1/begin")
        {
            Content = JsonContent.Create(new { scope = "project", scopeId = "project-1" }),
        };
        AddCallerHeaders(begin, token);
        using (var beginResponse = await gateway.Client.SendAsync(begin))
        {
            Assert.Equal(HttpStatusCode.OK, beginResponse.StatusCode);
            Assert.Equal(
                beginSetCookie,
                Assert.Single(beginResponse.Headers.GetValues("Set-Cookie")));
            Assert.Equal(
                "{\"accepted\":true}",
                await beginResponse.Content.ReadAsStringAsync());
        }

        using var complete = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/connections/copilot-user/v1/complete")
        {
            Content = JsonContent.Create(new { state = "sealed-state", code = "authorization-code" }),
        };
        complete.Headers.TryAddWithoutValidation(
            "Cookie",
            $"preferences=dark; __Host-agentweaver-copilot-link=nonce-token; session=unrelated");
        AddCallerHeaders(complete, token);
        using (var completeResponse = await gateway.Client.SendAsync(complete))
        {
            Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);
            Assert.Equal(
                clearLinkCookie,
                Assert.Single(completeResponse.Headers.GetValues("Set-Cookie")));
        }

        var expectedMutationBody = JsonSerializer.Serialize(new
        {
            connectionId = Guid.Parse(connectionId),
            expectedRevision = 1,
        });
        foreach (var action in new[] { "refresh", "revoke" })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/connections/copilot-user/v1/{action}")
            {
                Content = JsonContent.Create(new
                {
                    connectionId = Guid.Parse(connectionId),
                    expectedRevision = 1,
                }),
            };
            AddCallerHeaders(request, token);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var get = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/api/connections/copilot-user/v1/{connectionId}"))
        {
            AddCallerHeaders(get, token);
            using var response = await gateway.Client.SendAsync(get);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var forwarded = requests.ToArray();
        Assert.Equal(5, forwarded.Length);
        Assert.Equal(
            new[]
            {
                "/internal/connections/copilot-user/begin",
                "/internal/connections/copilot-user/complete",
                "/internal/connections/copilot-user/refresh",
                "/internal/connections/copilot-user/revoke",
                $"/internal/connections/copilot-user/{connectionId}",
            },
            forwarded.Select(request => request.Uri.AbsolutePath));
        Assert.All(forwarded, request =>
        {
            Assert.Equal(token, AuthenticationHeaderValue.Parse(request.Authorization!).Parameter);
            Assert.Equal("tenant-1", request.Tenant);
        });
        Assert.Null(forwarded[0].Cookie);
        Assert.Equal("__Host-agentweaver-copilot-link=nonce-token", forwarded[1].Cookie);
        Assert.Null(forwarded[2].Cookie);
        Assert.Null(forwarded[3].Cookie);
        Assert.Null(forwarded[4].Cookie);
        Assert.Equal(expectedMutationBody, forwarded[2].Body);
        Assert.Equal(expectedMutationBody, forwarded[3].Body);
    }

    private static void AddCallerHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
    }

    private sealed record OwnerRequest(
        HttpMethod Method,
        Uri Uri,
        string? Authorization,
        string? Tenant,
        string? Cookie,
        string? Body);
}
