using System.Net;
using System.Text.Json;
using Agentweaver.Api.Auth;
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
        var createProjectSchema = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("CreateProjectRequest").GetProperty("properties");
        createProjectSchema.TryGetProperty("repository_selection_code", out _).Should().BeTrue();
        createProjectSchema.TryGetProperty("source_repository", out _).Should().BeFalse();

        var startOrchestration = paths.GetProperty("/api/projects/{id}/orchestrations").GetProperty("post");
        startOrchestration.GetProperty("operationId").GetString().Should().Be("StartProjectOrchestration");
        startOrchestration.GetProperty("security").GetArrayLength().Should().BeGreaterThan(0);
        startOrchestration.GetProperty("parameters").EnumerateArray()
            .Should().Contain(parameter =>
                parameter.GetProperty("name").GetString() == "If-Model-Provider-Key"
                && parameter.GetProperty("in").GetString() == "header"
                && parameter.GetProperty("required").GetBoolean());

        var executionContext = paths.GetProperty("/api/ai/execution-context").GetProperty("post");
        executionContext.GetProperty("operationId").GetString().Should().Be("ResolveAiExecutionContext");
        executionContext.GetProperty("requestBody").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/AiExecutionContextRequest");
        executionContext.GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/AiExecutionContextResponse");
        executionContext.GetProperty("responses").GetProperty("409").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString()
            .Should().EndWith("/AiExecutionContextErrorResponse");
        executionContext.GetProperty("description").GetString().Should().Contain("If-Model-Provider-Key");
        var executionContextOperation = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("AiExecutionContextRequest").GetProperty("properties").GetProperty("operation");
        executionContextOperation.GetProperty("enum").EnumerateArray().Select(value => value.GetString())
            .Should().BeEquivalentTo(AiOperationCatalog.Names);
        executionContextOperation.GetProperty("description").GetString()
            .Should().Contain("guarded endpoint");
        AiOperationCatalog.Names.Should().Contain("orchestration");

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
