using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;

namespace Agentweaver.Tests.Helpers;

public abstract class ApiWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    protected ApiWebApplicationFactory(string pathPrefix, bool createWorkspaceRoot = false)
    {
        var unique = Guid.NewGuid().ToString("N");
        _databasePath = Path.Combine(Path.GetTempPath(), $"{pathPrefix}-{unique}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"{pathPrefix}-wt-{unique}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"{pathPrefix}-cp-{unique}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"{pathPrefix}-ccp-{unique}");

        if (createWorkspaceRoot)
        {
            WorkspaceRoot = Path.Combine(Path.GetTempPath(), $"{pathPrefix}-ws-{unique}");
            Directory.CreateDirectory(WorkspaceRoot);
        }
    }

    protected virtual string DatabasePath => _databasePath;
    protected string? WorkspaceRoot { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Database:Path"] = DatabasePath,
                ["Worktrees:BasePath"] = _worktreesPath,
                ["Checkpoints:Path"] = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"] = "50",
                ["RunBounds:MaxMinutes"] = "10",
            };
            ConfigureTestConfiguration(values);
            configuration.AddInMemoryCollection(values);
        });
        builder.ConfigureServices(ConfigureTestServices);
    }

    protected virtual void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
    }

    protected virtual void ConfigureTestServices(IServiceCollection services)
    {
    }

    protected string CreateWorkspaceDirectory(string? name = null)
    {
        var root = WorkspaceRoot
            ?? throw new InvalidOperationException("This factory does not have a workspace root.");
        var directory = Path.Combine(root, name ?? Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    protected static void RemoveService<T>(IServiceCollection services)
    {
        var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(T));
        if (descriptor is not null)
            services.Remove(descriptor);
    }

    protected virtual void DisposeFixture()
    {
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        DisposeFixture();

        var memoryDatabasePath = SqliteMemoryDbPathResolver.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Database:Path"] = DatabasePath })
            .Build());
        foreach (var path in new[]
        {
            DatabasePath,
            DatabasePath + "-wal",
            DatabasePath + "-shm",
            memoryDatabasePath,
            memoryDatabasePath + "-wal",
            memoryDatabasePath + "-shm",
        })
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }

        foreach (var directory in new[]
        {
            WorkspaceRoot,
            _worktreesPath,
            _checkpointsPath,
            _coordinatorCheckpointsPath,
        })
        {
            if (directory is null)
                continue;
            try { Directory.Delete(directory, recursive: true); } catch { /* best effort */ }
        }
    }
}
