using System.Net;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.OpenApi;

public sealed class OpenApiEndpointsTests : IDisposable
{
    private readonly AgentweaverWebApplicationFactory _factory = new();
    private readonly HttpClient _client;

    public OpenApiEndpointsTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task OpenApiJson_IncludesNamedTaggedDocumentedOperations_AndBearerSecurity()
    {
        var response = await _client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("openapi").GetString().Should().StartWith("3.1");
        var paths = root.GetProperty("paths");

        var createProject = paths.GetProperty("/api/projects").GetProperty("post");
        createProject.GetProperty("operationId").GetString().Should().Be("CreateProject");
        createProject.GetProperty("tags").EnumerateArray().Select(t => t.GetString()).Should().Contain("Projects");
        createProject.GetProperty("summary").GetString().Should().Contain("Creates a project workspace");
        if (createProject.TryGetProperty("description", out var createProjectDescription))
        {
            createProjectDescription.GetString().Should().NotBeNullOrWhiteSpace();
        }

        var startOrchestration = paths.GetProperty("/api/projects/{id}/orchestrations").GetProperty("post");
        startOrchestration.GetProperty("operationId").GetString().Should().Be("StartProjectOrchestration");
        startOrchestration.GetProperty("security").GetArrayLength().Should().BeGreaterThan(0);

        var outcomeSpec = paths.GetProperty("/api/runs/{id}/outcome-spec").GetProperty("get");
        outcomeSpec.GetProperty("operationId").GetString().Should().Be("GetCoordinatorOutcomeSpec");
        outcomeSpec.GetProperty("summary").GetString().Should().Contain("Returns the coordinator's current drafted outcome spec");

        var securitySchemes = root.GetProperty("components").GetProperty("securitySchemes");
        var bearer = securitySchemes.GetProperty("Bearer");
        bearer.GetProperty("type").GetString().Should().Be("http");
        bearer.GetProperty("scheme").GetString().Should().Be("bearer");
        bearer.GetProperty("description").GetString().Should().Contain("Authorization: Bearer");
    }

    [Fact]
    public async Task OpenApiYaml_UsesYamlRoute_AndExposesSameDocumentSurface()
    {
        var response = await _client.GetAsync("/openapi/v1.yaml");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("openapi:");
        body.Should().Contain("title: Agentweaver API");
        body.Should().Contain("/api/projects:");
        body.Should().Contain("components:");
        body.Should().Contain("securitySchemes:");
    }
}
