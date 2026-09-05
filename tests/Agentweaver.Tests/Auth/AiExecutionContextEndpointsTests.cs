using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class AiExecutionContextEndpointsTests
{
    [Fact]
    public async Task Assistant_preflight_reports_platform_unavailable_without_inventing_model_source()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var client = AuthedClient(factory);

        var response = await client.PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "assistant_turn" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>();
        body.Should().NotBeNull();
        body!.AiRequired.Should().BeTrue();
        body.Operation.Should().Be("assistant_turn");
        body.Phase.Should().Be("prepared");
        body.EffectiveModelProvider.Should().NotBeNull();
        body.EffectiveModelProvider!.State.Should().Be("unavailable");
        body.EffectiveModelProvider.ProviderKind.Should().Be("unavailable");
        body.EffectiveModelProvider.ResolutionScope.Should().Be("platform");
        body.EffectiveModelProvider.ProviderScope.Should().Be("none");
        body.EffectiveModelProvider.ProviderKey.Should().BeNull();
        body.EffectiveModelProvider.UnavailableReason.Should().Be("no_provider");
    }

    [Fact]
    public async Task Project_generation_preflight_distinguishes_project_resolution_from_platform_byok()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        await SeedByokProviderAsync(factory);
        var client = AuthedClient(factory);
        var workingDirectory = Path.Combine(Path.GetTempPath(), $"provider-context-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var projectResponse = await client.PostAsJsonAsync("/api/projects", new
            {
                name = "Provider context project",
                origin = "blank",
                working_directory = workingDirectory,
            });
            projectResponse.StatusCode.Should().Be(HttpStatusCode.Created);
            var projectId = (await projectResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("project_id").GetString();

            var response = await client.PostAsJsonAsync(
                "/api/ai/execution-context",
                new { operation = "workflow_generation", project_id = projectId });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>();
            var provider = body!.EffectiveModelProvider!;
            provider.State.Should().Be("resolved");
            provider.ProviderKind.Should().Be("byok");
            provider.ResolutionScope.Should().Be("project");
            provider.ProviderScope.Should().Be("platform");
            provider.ProviderType.Should().Be("azure");
            provider.ProviderKey.Should().HaveLength(64);
        }
        finally
        {
            try { Directory.Delete(workingDirectory, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Project_operation_requires_project_id()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var response = await AuthedClient(factory).PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "casting_generation" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_id_required");
    }

    [Fact]
    public async Task Unknown_operation_is_rejected()
    {
        await using var factory = new AgentweaverWebApplicationFactory();
        var response = await AuthedClient(factory).PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation = "make_magic" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("invalid_ai_operation");
    }

    private static async Task SeedByokProviderAsync(AgentweaverWebApplicationFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        var created = await settings.AddAsync(
            new ByokProviderConfiguration(
                Id: string.Empty,
                Name: "Test Azure provider",
                Type: "azure",
                BaseUrl: "https://provider.example.test",
                Model: "gpt-4.1",
                ApiKey: "test-key"),
            CancellationToken.None);
        await settings.SetActiveAsync(created.Id, CancellationToken.None);
    }

    private static HttpClient AuthedClient(AgentweaverWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentweaverWebApplicationFactory.TestApiKey);
        return client;
    }
}
