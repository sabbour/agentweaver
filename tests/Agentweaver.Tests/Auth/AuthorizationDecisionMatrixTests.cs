using System.Net;
using System.Net.Http.Headers;
using Agentweaver.AgentRuntime;
using Agentweaver.Api.Auth;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class AuthorizationDecisionMatrixTests : IClassFixture<EntraWebApplicationFactory>
{
    private readonly EntraWebApplicationFactory _factory;

    public AuthorizationDecisionMatrixTests(EntraWebApplicationFactory factory) => _factory = factory;

    public static TheoryData<string, string, string, string, EndpointAuthorizationKind, HttpStatusCode> Cases => new()
    {
        { "anonymous operational", "GET", "/api/version", "/api/version", EndpointAuthorizationKind.OperationalAnonymous, HttpStatusCode.OK },
        { "missing platform credential", "GET", "/api/projects", "/api/projects", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.Unauthorized },
        { "platform role", "GET", "/api/projects", "/api/projects", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.OK },
        { "platform principal without role", "GET", "/api/projects", "/api/projects", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.Forbidden },
        { "authenticated self", "GET", "/api/auth/session", "/api/auth/session", EndpointAuthorizationKind.AuthenticatedSelf, HttpStatusCode.OK },
        { "MCP-shaped forwarded platform credential", "GET", "/api/blueprints", "/api/blueprints", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.OK },
        { "internal service", "GET", "/api/projects", "/api/projects", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.OK },
        { "run capability", "GET", "/api/runs/not-a-run/tool-approval-policies/read_file", "/api/runs/{id}/tool-approval-policies/{toolName}", EndpointAuthorizationKind.RunCapability, HttpStatusCode.BadRequest },
        { "malformed bearer", "GET", "/api/projects", "/api/projects", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.Unauthorized },
        { "wrong audience", "GET", "/api/projects", "/api/projects", EndpointAuthorizationKind.PlatformOrMcp, HttpStatusCode.Unauthorized },
    };

    [Theory]
    [InlineData("/health")]
    [InlineData("/api/health")]
    [InlineData("/api/ping")]
    [InlineData("/healthz/workspace")]
    public async Task HealthProbe_RemainsAnonymous(string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CurrentPipeline_MatchesGoldenDecision(
        string callerShape,
        string method,
        string requestPath,
        string routePattern,
        EndpointAuthorizationKind expectedClassification,
        HttpStatusCode expectedStatus)
    {
        using var client = _factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), requestPath);
        ConfigureCredential(callerShape, request);

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expectedStatus);
        FindEndpoint(method, routePattern).Metadata
            .GetRequiredMetadata<EndpointAuthorizationMetadata>().Kind
            .Should().Be(expectedClassification);
    }

    private void ConfigureCredential(string callerShape, HttpRequestMessage request)
    {
        string? bearer = callerShape switch
        {
            "platform role" or "MCP-shaped forwarded platform credential" =>
                _factory.CreateBearerToken("platform-user", PlatformRoles.Contributor),
            "platform principal without role" or "authenticated self" =>
                _factory.CreateBearerToken("self-user", "UnrecognizedRole"),
            "internal service" => "internal-test-api-key",
            "run capability" => "run-capability-token",
            "malformed bearer" => "not-a-jwt",
            "wrong audience" => _factory.CreateBearerTokenWithOverrides(
                "wrong-audience-user",
                audience: "different-audience",
                PlatformRoles.Contributor),
            _ => null,
        };

        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (callerShape == "run capability")
        {
            request.Headers.Add(RunAuthorshipHeaders.RunId, "not-a-run");
            request.Headers.Add(RunAuthorshipHeaders.RunToken, bearer);
        }
    }

    private RouteEndpoint FindEndpoint(string method, string routePattern)
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ApplicationEndpointMetadata>() is not null)
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(method) == true);

        return endpoints.Single(endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, routePattern, StringComparison.Ordinal));
    }
}
