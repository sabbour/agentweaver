using System.Text.Json;
using Agentweaver.Api.Blueprints;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Agentweaver.Tests.Blueprints;

public sealed class McpBlueprintToolsTests : IClassFixture<BlueprintsWebApplicationFactory>
{
    private readonly BlueprintsWebApplicationFactory _factory;

    public McpBlueprintToolsTests(BlueprintsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BlueprintGenerate_ReturnsDurableJob_ThenStatusAndResultToolsReadArtifact()
    {
        _factory.Generator.Response = """
            {
              "id": "mcp-blueprint",
              "name": "MCP Blueprint",
              "description": "Generated through MCP.",
              "roster": ["backend-engineer"],
              "workflows": ["software-delivery"],
              "review_policy": "default",
              "sandbox_profile": "default"
            }
            """;
        using (var setup = _factory.CreateAuthenticatedClient())
            await _factory.PrepareAiExecutionAsync(setup, "blueprint_generation");
        using var http = _factory.CreateClient();
        var tools = new BlueprintTools(new AgentweaverApiClient(
            http,
            new McpConfig("http://localhost", BlueprintsWebApplicationFactory.TestApiKey)));

        var acceptedJson = await tools.BlueprintGenerateAsync(
            "generate through MCP",
            idempotency_key: "mcp-blueprint-job",
            ct: CancellationToken.None);
        using var accepted = JsonDocument.Parse(acceptedJson);
        accepted.RootElement.GetProperty("status").GetString().Should().Be("queued");
        accepted.RootElement.GetProperty("provider_snapshot")
            .GetProperty("provider_kind").GetString().Should().NotBeNullOrWhiteSpace();
        accepted.RootElement.GetProperty("created_at").GetDateTimeOffset()
            .Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(1));
        accepted.RootElement.TryGetProperty("ai_execution_context", out _).Should().BeTrue();
        var jobId = accepted.RootElement.GetProperty("job_id").GetString()!;

        var worker = _factory.Services.GetServices<IHostedService>()
            .OfType<BlueprintGenerationJobWorker>()
            .Single();
        _ = await worker.RunOneAsync(CancellationToken.None);

        using var status = JsonDocument.Parse(
            await tools.BlueprintGenerationStatusAsync(jobId, CancellationToken.None));
        status.RootElement.GetProperty("status").GetString().Should().Be("completed");

        using var result = JsonDocument.Parse(
            await tools.BlueprintGenerationResultAsync(jobId, CancellationToken.None));
        result.RootElement.GetProperty("artifact_id").GetString().Should().NotBeNullOrWhiteSpace();
        result.RootElement.GetProperty("logical_id").GetString().Should().Be("mcp-blueprint");
        result.RootElement.GetProperty("version").GetInt32().Should().Be(1);
    }
}
