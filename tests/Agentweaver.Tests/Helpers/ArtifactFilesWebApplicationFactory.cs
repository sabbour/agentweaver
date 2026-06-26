using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for artifact-browser endpoint tests.
/// Registers two API keys so authorization tests can verify that a non-owner
/// receives 404 (not 403) per SC-009 and the run-id non-enumeration requirement.
/// </summary>
public sealed class ArtifactFilesWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string OwnerApiKey = "artifact-test-owner-key-12345";
    public const string OwnerUser   = "artifact-owner-user";
    public const string OtherApiKey = "artifact-test-other-key-99999";
    public const string OtherUser   = "artifact-other-user";

    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public ArtifactFilesWebApplicationFactory()
    {
        _dbPath          = Path.Combine(Path.GetTempPath(), $"agentweaver-af-{Guid.NewGuid():N}.db");
        _worktreesPath   = Path.Combine(Path.GetTempPath(), $"agentweaver-af-wt-{Guid.NewGuid():N}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-af-cp-{Guid.NewGuid():N}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-af-ccp-{Guid.NewGuid():N}");
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
                ["Testing:BypassGitHubOrgAuthorization"] = "true",
                // Primary owner (Auth:ApiKey + Auth:User).
                ["Auth:ApiKey"]                           = OwnerApiKey,
                ["Auth:User"]                             = OwnerUser,
                // Second user registered via Auth:Keys[].
                ["Auth:Keys:0:Token"]                     = OtherApiKey,
                ["Auth:Keys:0:User"]                      = OtherUser,
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
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(p); } catch { /* best effort */ }
        }

        try { Directory.Delete(_worktreesPath, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_checkpointsPath, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_coordinatorCheckpointsPath, recursive: true); } catch { /* best effort */ }
    }
}
