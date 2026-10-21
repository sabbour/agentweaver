extern alias GatewayHost;

using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using System.Net;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayUserNotificationTests
{
    [Theory]
    [InlineData("GET", "/api/v1/notifications?projectId=p&includeDismissed=true&pageSize=1",
        "/api/notifications?projectId=p&includeDismissed=true&pageSize=1", 200)]
    [InlineData("GET", "/api/v1/notifications?unknown=1&pageSize=1&pageSize=2",
        "/api/notifications?unknown=1&pageSize=1&pageSize=2", 400)]
    [InlineData("POST", "/api/v1/notifications/ABC/dismiss", "/api/notifications/ABC/dismiss", 204)]
    public async Task UnboundBrowserRoutesForwardCallerTenantQueryAndOwnerResult(
        string method, string path, string ownerPath, int status)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri, "https://api.test",
            [new Claim("sub", Guid.NewGuid().ToString()), new Claim("scope", "api.read api.write")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        var bearer = new JwtSecurityTokenHandler().WriteToken(token);
        var calls = 0;
        var body = status == 400
            ? """{"code":"query_invalid","status":400}"""
            : """{"items":[],"count":0,"nextCursor":null}""";
        await using var gateway = await GatewayResourceServer.StartAsync(key,
            () => throw new InvalidOperationException("Notifications do not call the Projects owner."),
            (owner, request, _) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                calls++;
                Assert.Equal(ownerPath, request.RequestUri!.PathAndQuery);
                Assert.Equal(method, request.Method.Method);
                Assert.Equal("Bearer " + bearer, request.Headers.Authorization!.ToString());
                Assert.Equal("tenant-a", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
                var result = new HttpResponseMessage((HttpStatusCode)status);
                if (status != 204)
                    result.Content = new StringContent(body, Encoding.UTF8, "application/json");
                result.Headers.CacheControl = new() { NoStore = true };
                return Task.FromResult(result);
            });
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Authorization = new("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal((HttpStatusCode)status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (status != 204) Assert.Equal(body, await response.Content.ReadAsStringAsync());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NotificationsUseExistingOwnerWithStrictPublicContractsAndNoParallelAttentionRoute()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI does not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var list = paths.GetProperty("/api/v1/notifications").GetProperty("get");
        Assert.Equal("listUserNotifications", list.GetProperty("operationId").GetString());
        var query = list.GetProperty("parameters").EnumerateArray()
            .Where(parameter => parameter.GetProperty("in").GetString() == "query")
            .Select(parameter => parameter.GetProperty("name").GetString()!).Order().ToArray();
        Assert.Equal(["cursor", "includeDismissed", "pageSize", "projectId"], query);
        var dismiss = paths.GetProperty("/api/v1/notifications/{notificationId}/dismiss").GetProperty("post");
        Assert.True(dismiss.GetProperty("responses").TryGetProperty("204", out _));
        Assert.Equal(JsonValueKind.Null, dismiss.GetProperty("requestBody").ValueKind);
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var item = schemas.GetProperty("UserNotification");
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["createdAt", "gateKind", "kind", "notificationId", "projectId", "promotionId", "prompt", "requestId",
                "runId", "sessionId", "source", "stateVersion", "taskCount", "updatedAt"],
            item.GetProperty("required").EnumerateArray().Select(value => value.GetString()!).Order());
        var properties = item.GetProperty("properties");
        Assert.Equal(["backlogPromoted", "pendingGate"],
            properties.GetProperty("source").GetProperty("enum").EnumerateArray().Select(value => value.GetString()!).Order());
        var page = schemas.GetProperty("UserNotificationPage");
        Assert.False(page.GetProperty("additionalProperties").GetBoolean());
    }

}
