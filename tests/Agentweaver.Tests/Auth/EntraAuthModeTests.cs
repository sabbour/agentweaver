using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Api.Auth;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Auth;

public sealed class EntraAuthModeTests : IClassFixture<EntraWebApplicationFactory>
{
    private readonly EntraWebApplicationFactory _factory;

    public EntraAuthModeTests(EntraWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task AuthConfig_ReturnsEntraModeAndPublicOidcConfig()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/config");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("mode").GetString().Should().Be("Entra");
        json.RootElement.GetProperty("entra").GetProperty("client_id").GetString().Should().Be(EntraWebApplicationFactory.ClientId);
    }

    [Fact]
    public async Task EntraToken_WithRecognizedRole_CanAccessContextAndProjects()
    {
        using var client = _factory.CreateAuthenticatedClient(PlatformRoles.Contributor);

        var contextResponse = await client.GetAsync("/api/auth/context");
        var projectsResponse = await client.GetAsync("/api/projects");

        contextResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        projectsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = JsonDocument.Parse(await contextResponse.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("primary_platform_role").GetString().Should().Be(PlatformRoles.Contributor);
    }

    [Fact]
    public async Task EntraToken_WithoutRecognizedRole_IsForbidden()
    {
        using var client = _factory.CreateAuthenticatedClient("UnknownRole");

        var response = await client.GetAsync("/api/auth/context");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task NonJwtBearer_IsRejectedInEntraMode()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
