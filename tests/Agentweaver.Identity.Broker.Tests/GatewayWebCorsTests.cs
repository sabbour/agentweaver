extern alias GatewayHost;

using System.Net;
using System.Security.Cryptography;
using GatewayOptions = GatewayHost::Agentweaver.Gateway.GatewayOptions;
using GatewayResourceServer = Agentweaver.Identity.Broker.Tests.GatewayResourceServer;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayWebCorsTests
{
    [Fact]
    public async Task BrowserCorsIsExactBearerOnlyAndPreflightsBeforeAuthentication()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/v1/projects");
        preflight.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        preflight.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,x-agentweaver-tenant");

        using var preflightResponse = await gateway.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, preflightResponse.StatusCode);
        Assert.Equal(
            "https://web.test",
            Assert.Single(preflightResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains("GET", preflightResponse.Headers.GetValues("Access-Control-Allow-Methods"));
        var allowedHeaders = string.Join(",", preflightResponse.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-agentweaver-tenant", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.False(preflightResponse.Headers.Contains("Access-Control-Allow-Credentials"));

        using var copilotPreflight = new HttpRequestMessage(
            HttpMethod.Options, "/api/connections/copilot-user/v1/begin");
        copilotPreflight.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        copilotPreflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        copilotPreflight.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers",
            "authorization,content-type,x-agentweaver-tenant");
        using var copilotPreflightResponse = await gateway.Client.SendAsync(copilotPreflight);
        Assert.Equal(HttpStatusCode.NoContent, copilotPreflightResponse.StatusCode);
        Assert.Equal(
            "https://web.test",
            Assert.Single(copilotPreflightResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Contains(
            "POST",
            string.Join(",", copilotPreflightResponse.Headers.GetValues("Access-Control-Allow-Methods")).Split(','));
        var copilotAllowedHeaders = string.Join(
            ",", copilotPreflightResponse.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("authorization", copilotAllowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", copilotAllowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("x-agentweaver-tenant", copilotAllowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "true",
            Assert.Single(copilotPreflightResponse.Headers.GetValues("Access-Control-Allow-Credentials")));

        using var copilotRequest = new HttpRequestMessage(
            HttpMethod.Get, "/api/connections/copilot-user/v1/00000000-0000-0000-0000-000000000001");
        copilotRequest.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        using var copilotResponse = await gateway.Client.SendAsync(copilotRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, copilotResponse.StatusCode);
        Assert.Equal(
            "true",
            Assert.Single(copilotResponse.Headers.GetValues("Access-Control-Allow-Credentials")));

        using var unauthenticatedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/projects");
        unauthenticatedRequest.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        using var unauthenticatedResponse = await gateway.Client.SendAsync(unauthenticatedRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticatedResponse.StatusCode);
        Assert.Equal(
            "https://web.test",
            Assert.Single(unauthenticatedResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(unauthenticatedResponse.Headers.Contains("Access-Control-Allow-Credentials"));

        using var disallowedOrigin = new HttpRequestMessage(HttpMethod.Options, "/api/v1/projects");
        disallowedOrigin.Headers.TryAddWithoutValidation("Origin", "https://attacker.test");
        disallowedOrigin.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
        using var disallowedResponse = await gateway.Client.SendAsync(disallowedOrigin);
        Assert.False(disallowedResponse.Headers.Contains("Access-Control-Allow-Origin"));

        using var openApiRequest = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        openApiRequest.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        using var openApiResponse = await gateway.Client.SendAsync(openApiRequest);
        Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
        Assert.Equal(
            "https://web.test",
            Assert.Single(openApiResponse.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(openApiResponse.Headers.Contains("Access-Control-Allow-Credentials"));

        using var healthRequest = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        healthRequest.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        using var healthResponse = await gateway.Client.SendAsync(healthRequest);
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        Assert.False(healthResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task KnowledgeImportAllowsOneMiBRequestBodies()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var importEndpoint = Assert.Single(gateway.Endpoints, endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ==
            "importKnowledgeRecords");
        var requestSizeLimit = importEndpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>();

        Assert.NotNull(requestSizeLimit);
        Assert.Equal(1024 * 1024, requestSizeLimit.MaxRequestBodySize);
    }

    [Fact]
    public void WebOriginMustBeAnExactHttpsOriginAndIsNormalized()
    {
        var options = GatewayOptions.Read(Configuration("https://WEB.test:443"));

        Assert.Equal(new Uri("https://web.test/"), options.WebOrigin);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("http://web.test")]
    [InlineData("https://web.test/path")]
    [InlineData("https://user@web.test")]
    [InlineData("https://web.test/?query=value")]
    [InlineData("https://web.test/#fragment")]
    public void WebOriginRejectsMissingOrNonOriginValues(string? webOrigin)
    {
        Assert.Throws<InvalidOperationException>(() => GatewayOptions.Read(Configuration(webOrigin)));
    }

    private static IConfiguration Configuration(string? webOrigin) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Issuer"] = "https://identity.test/",
                ["Identity:Audience"] = "https://gateway.test",
                ["Gateway:Owners:Projects"] = "https://projects.test",
                ["Gateway:Owners:Orchestrator"] = "https://orchestrator.test",
                ["Gateway:Owners:Knowledge"] = "https://knowledge.test",
                ["Gateway:Owners:Events"] = "https://events.test",
                ["Gateway:Owners:IdentityBrokerAddress"] = "https://identity-broker.test",
                ["Gateway:WebOrigin"] = webOrigin,
            })
            .Build();
}
