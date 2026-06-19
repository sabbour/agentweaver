using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for project-related integration tests.
/// Replaces OsCredentialStoreGitHubTokenStore with InMemoryGitHubTokenStore,
/// uses LocalFilesystemWorkspaceProvider pointed at an isolated temp directory,
/// and stubs out ProjectGitInitializer to skip real git operations.
/// </summary>
public sealed class ProjectsWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "projects-test-api-key-54321";
    public const string TestUser   = "projects-test-user";

    private readonly string _dbPath;
    private readonly string _workspaceRoot;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public InMemoryGitHubTokenStore TokenStore { get; } = new();

    public ProjectsWebApplicationFactory()
    {
        var unique = Guid.NewGuid().ToString("N");
        _dbPath          = Path.Combine(Path.GetTempPath(), $"agentweaver-proj-{unique}.db");
        _workspaceRoot   = Path.Combine(Path.GetTempPath(), $"agentweaver-proj-ws-{unique}");
        _worktreesPath   = Path.Combine(Path.GetTempPath(), $"agentweaver-proj-wt-{unique}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-proj-cp-{unique}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-proj-ccp-{unique}");

        Directory.CreateDirectory(_workspaceRoot);
    }

    /// <summary>
    /// Creates an authenticated HttpClient using the test API key.
    /// </summary>
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
            // Replace OS credential store with in-memory store for tests.
            RemoveService<IGitHubTokenStore>(services);
            services.AddSingleton<IGitHubTokenStore>(TokenStore);

            // Replace ProjectGitInitializer with a no-op stub.
            RemoveService<ProjectGitInitializer>(services);
            services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
        });
    }

    /// <summary>
    /// Creates a project working directory under the isolated workspace root.
    /// </summary>
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
/// Stub ProjectGitInitializer that skips real git operations for tests.
/// InitBlank just creates the directory and returns the branch name; Clone creates the directory and returns "main".
/// </summary>
internal sealed class NoOpProjectGitInitializer : ProjectGitInitializer
{
    public NoOpProjectGitInitializer(Microsoft.Extensions.Logging.ILogger<ProjectGitInitializer> logger)
        : base(logger) { }

    public override string InitBlank(string workingDirectory, string defaultBranch)
    {
        Directory.CreateDirectory(workingDirectory);
        return defaultBranch;
    }

    public override string Clone(string workingDirectory, string sourceRepository, string accessToken)
    {
        Directory.CreateDirectory(workingDirectory);
        return "main";
    }
}
