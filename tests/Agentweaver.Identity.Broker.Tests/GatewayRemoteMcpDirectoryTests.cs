extern alias GatewayHost;

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayOptions = GatewayHost::Agentweaver.Gateway.GatewayOptions;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using GatewayRouteCatalog = GatewayHost::Agentweaver.Gateway.GatewayRouteCatalog;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayRemoteMcpDirectoryTests
{
    private const string DirectoryBody =
        """{"connections":[{"connectionId":"6f1b8f37-6a43-4d6e-9d55-0e0c40a0a4f1","displayName":"Docs","endpointUri":"https://mcp.example.com/","resourceUri":null,"authenticationMode":"none","transportProfile":"streamableHttp20250618","state":"enabled","rowRevision":3,"configurationRevision":1,"configurationSha256":"abc","identityBound":false,"registryServerName":null,"registryExactVersion":null,"discoveryRevision":1,"catalogSha256":"def","toolCount":2}],"nextConnectionId":null}""";

    [Fact]
    public void DirectoryRouteIsEnvironmentOwnedAndForwardsTheTenantSelector()
    {
        var route = Assert.Single(
            GatewayRouteCatalog.Routes,
            candidate => candidate.OperationId == "listRemoteMcpConnections");

        Assert.Equal("GET", route.Method);
        Assert.Equal(GatewayOwner.Environment, route.Owner);
        Assert.Equal("/api/v1/projects/{projectId}/remote-mcp/connections", route.PublicPath);
        Assert.Equal("/api/projects/{projectId}/remote-mcp/directory", route.OwnerPath);
        Assert.Equal(["pageSize", "after"], route.QueryParameters.ToArray());
        Assert.True(route.ForwardTenantSelector);
        Assert.False(route.HasJsonBody);
    }

    [Fact]
    public void EnvironmentOwnerIsOptionalAndMustBeAnHttpsRoot()
    {
        Assert.Null(GatewayOptions.Read(Configuration(null)).EnvironmentOwnerBaseAddress);
        Assert.Equal(
            new Uri("https://environment.test/"),
            GatewayOptions.Read(Configuration("https://ENVIRONMENT.test")).EnvironmentOwnerBaseAddress);
        Assert.Throws<InvalidOperationException>(() => GatewayOptions.Read(Configuration("http://environment.test")));
        Assert.Throws<InvalidOperationException>(() => GatewayOptions.Read(Configuration("https://environment.test/api")));
    }

    [Fact]
    public async Task DirectoryProxiesToTheEnvironmentOwnerWithBearerQueryAndTenant()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var forwarded = new List<(GatewayOwner Owner, Uri? Uri, string? Authorization, string? Tenant)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (owner, request, _) =>
            {
                forwarded.Add((
                    owner,
                    request.RequestUri,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant) ? tenant.Single() : null));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(DirectoryBody, Encoding.UTF8, "application/json"),
                });
            },
            environmentOwner: new Uri("https://environment.test/"));

        var token = CreateToken(key);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/projects/project-a/remote-mcp/connections?pageSize=10&after=6f1b8f37-6a43-4d6e-9d55-0e0c40a0a4f1");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-a");
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "abc",
            body.RootElement.GetProperty("connections")[0].GetProperty("configurationSha256").GetString());
        var call = Assert.Single(forwarded);
        Assert.Equal(GatewayOwner.Environment, call.Owner);
        Assert.Equal(
            "https://environment.test/api/projects/project-a/remote-mcp/directory" +
            "?pageSize=10&after=6f1b8f37-6a43-4d6e-9d55-0e0c40a0a4f1",
            call.Uri?.AbsoluteUri);
        Assert.Equal("Bearer " + token, call.Authorization);
        Assert.Equal("tenant-a", call.Tenant);
    }

    [Fact]
    public async Task DirectoryReturnsServiceUnavailableWhenTheEnvironmentOwnerIsNotConfigured()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                Interlocked.Increment(ref ownerCalls);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/projects/project-a/remote-mcp/connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("owner_not_configured", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, ownerCalls);
    }

    private static string CreateToken(SecurityKey key)
    {
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "gateway-remote-mcp-directory")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private static IConfiguration Configuration(string? environmentOwner) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Issuer"] = "https://identity.test/",
                ["Identity:Audience"] = "https://gateway.test",
                ["Gateway:Owners:Projects"] = "https://projects.test",
                ["Gateway:Owners:Orchestrator"] = "https://orchestrator.test",
                ["Gateway:Owners:Knowledge"] = "https://knowledge.test",
                ["Gateway:Owners:Events"] = "https://events.test",
                ["Gateway:Owners:Environment"] = environmentOwner,
                ["Gateway:Owners:IdentityBrokerAddress"] = "https://identity-broker.test",
                ["Gateway:WebOrigin"] = "https://web.test",
            })
            .Build();
}
