using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Configures the API for integration tests: injects a unique temp SQLite
/// database per factory instance and a known test API key so HTTP tests can
/// authenticate without any real secrets.
/// </summary>
public sealed class AgentweaverWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "test-api-key-12345";
    public const string TestUser = "test-user";

    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public AgentweaverWebApplicationFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"agentweaver-waf-{Guid.NewGuid():N}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wt-{Guid.NewGuid():N}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-cp-{Guid.NewGuid():N}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-ccp-{Guid.NewGuid():N}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = _dbPath,
                ["Worktrees:BasePath"] = _worktreesPath,
                ["Checkpoints:Path"] = _checkpointsPath,
                ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                ["Testing:BypassGitHubOrgAuthorization"] = "true",
                ["Testing:BypassGitHubTokenAuth"]        = "true",
                ["Auth:ApiKey"] = TestApiKey,
                ["Auth:User"] = TestUser,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
                // Provider keys are required by AddAgentRuntime at startup.
                // These tests never execute a run, so the values are never used
                // to make a real model call.
                ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"] = "50",
                ["RunBounds:MaxMinutes"] = "10"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); }
            catch { /* best effort */ }
        }

        try { Directory.Delete(_worktreesPath, recursive: true); }
        catch { /* best effort */ }

        try { Directory.Delete(_checkpointsPath, recursive: true); }
        catch { /* best effort */ }

        try { Directory.Delete(_coordinatorCheckpointsPath, recursive: true); }
        catch { /* best effort */ }
    }
}
