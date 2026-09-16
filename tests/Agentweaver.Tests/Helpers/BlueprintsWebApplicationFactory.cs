using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for blueprint integration tests. Mirrors the project test factory
/// (no-op git, isolated temp paths) and additionally replaces the model-backed
/// <see cref="IBlueprintGenerator"/> with <see cref="StubBlueprintGenerator"/> so the generate
/// endpoint can be exercised without the live model.
/// </summary>
public sealed class BlueprintsWebApplicationFactory : ApiWebApplicationFactory
{
    public const string TestApiKey = "blueprints-test-api-key-99887";
    public const string TestUser   = "blueprints-test-user";

    public StubBlueprintGenerator Generator { get; } = new();

    public BlueprintsWebApplicationFactory() : base("agentweaver-bp", createWorkspaceRoot: true)
    {
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    public async Task PrepareAiExecutionAsync(
        HttpClient client,
        string operation,
        string? projectId = null)
    {
        await using (var scope = Services.CreateAsyncScope())
        {
            var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
            var provider = await settings.GetAsync(CancellationToken.None)
                ?? await settings.AddAsync(
                    new ByokProviderConfiguration(
                        string.Empty,
                        "Blueprint test provider",
                        "openai",
                        "https://api.example.test/v1",
                        "gpt-5",
                        "test-key"),
                    CancellationToken.None);
            await settings.SetActiveAsync(provider.Id, CancellationToken.None);
        }

        var response = await client.PostAsJsonAsync(
            "/api/ai/execution-context",
            new { operation, project_id = projectId });
        response.EnsureSuccessStatusCode();
        var context = await response.Content.ReadFromJsonAsync<AiExecutionContextResponse>()
            ?? throw new InvalidOperationException("AI execution context response was empty.");
        var providerKey = context.ExecutionKey
            ?? throw new InvalidOperationException("AI execution context did not return a provider key.");
        client.DefaultRequestHeaders.Remove(AiExecutionPlanHeaders.ProviderKey);
        client.DefaultRequestHeaders.Add(AiExecutionPlanHeaders.ProviderKey, providerKey);
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubTokenAuth"] = "true";
        configuration["Auth:ApiKey"] = TestApiKey;
        configuration["Auth:User"] = TestUser;
        configuration["Auth:GitHub:ClientId"] = "test-github-client-id";
        configuration["Auth:GitHub:BaseUrl"] = "https://github.com";
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        RemoveService<ProjectGitInitializer>(services);
        services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();

        RemoveService<IBlueprintGenerator>(services);
        services.AddSingleton<IBlueprintGenerator>(Generator);
    }

    public string NewWorkingDirectory() => CreateWorkspaceDirectory();
}

/// <summary>
/// Test <see cref="IBlueprintGenerator"/> that returns a preset raw response. Not a mock of the
/// system under test: it stands in for the external model so the generate endpoint's parse/validate
/// pipeline runs against deterministic output.
/// </summary>
public sealed class StubBlueprintGenerator : IBlueprintGenerator
{
    public string Response { get; set; } = "{}";
    public Exception? ExceptionToThrow { get; set; }
    public string? LastTargetRepository { get; private set; }
    public string? LastModelId { get; private set; }

    public Task<string> GenerateRawAsync(
        string description,
        CancellationToken ct,
        string? userId = null,
        string? targetRepository = null,
        string? modelId = null)
    {
        LastTargetRepository = targetRepository;
        LastModelId = modelId;
        var exception = ExceptionToThrow;
        ExceptionToThrow = null;
        if (exception is not null)
            throw exception;
        return Task.FromResult(Response);
    }
}
