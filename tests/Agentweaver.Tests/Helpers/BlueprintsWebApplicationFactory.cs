using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Blueprints;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for blueprint integration tests. Mirrors the project test factory
/// (in-memory token store, no-op git, isolated temp paths) and additionally replaces the model-backed
/// <see cref="IBlueprintGenerator"/> with <see cref="StubBlueprintGenerator"/> so the generate
/// endpoint can be exercised without the live model.
/// </summary>
public sealed class BlueprintsWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "blueprints-test-api-key-99887";
    public const string TestUser   = "blueprints-test-user";

    private readonly string _dbPath;
    private readonly string _workspaceRoot;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public InMemoryGitHubTokenStore TokenStore { get; } = new();
    public StubBlueprintGenerator Generator { get; } = new();

    public BlueprintsWebApplicationFactory()
    {
        var unique = Guid.NewGuid().ToString("N");
        _dbPath          = Path.Combine(Path.GetTempPath(), $"agentweaver-bp-{unique}.db");
        _workspaceRoot   = Path.Combine(Path.GetTempPath(), $"agentweaver-bp-ws-{unique}");
        _worktreesPath   = Path.Combine(Path.GetTempPath(), $"agentweaver-bp-wt-{unique}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-bp-cp-{unique}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-bp-ccp-{unique}");

        Directory.CreateDirectory(_workspaceRoot);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"]                         = _dbPath,
                ["Worktrees:BasePath"]                    = _worktreesPath,
                ["Checkpoints:Path"]                      = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"]          = _coordinatorCheckpointsPath,
                ["Auth:ApiKey"]                           = TestApiKey,
                ["Auth:User"]                             = TestUser,
                ["Auth:GitHub:ClientId"]                  = "test-github-client-id",
                ["Auth:GitHub:BaseUrl"]                   = "https://github.com",
                ["Git:Author:Name"]                       = "Test",
                ["Git:Author:Email"]                      = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"]        = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"]      = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"]         = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"]     = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"]   = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"]                    = "50",
                ["RunBounds:MaxMinutes"]                  = "10",
            });
        });

        builder.ConfigureServices(services =>
        {
            RemoveService<IGitHubTokenStore>(services);
            services.AddSingleton<IGitHubTokenStore>(TokenStore);

            RemoveService<ProjectGitInitializer>(services);
            services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();

            RemoveService<IBlueprintGenerator>(services);
            services.AddSingleton<IBlueprintGenerator>(Generator);
        });
    }

    public string NewWorkingDirectory()
    {
        var dir = Path.Combine(_workspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }
        foreach (var dir in new[] { _workspaceRoot, _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null) services.Remove(descriptor);
    }
}

/// <summary>
/// Test <see cref="IBlueprintGenerator"/> that returns a preset raw response. Not a mock of the
/// system under test: it stands in for the external model so the generate endpoint's parse/validate
/// pipeline runs against deterministic output.
/// </summary>
public sealed class StubBlueprintGenerator : IBlueprintGenerator
{
    public string Response { get; set; } = "{}";

    public Task<string> GenerateRawAsync(string description, CancellationToken ct) => Task.FromResult(Response);
}
