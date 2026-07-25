using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory that registers two API keys so review-endpoint
/// ownership tests can submit as one user and attempt to review as another,
/// exercising the 403 Forbidden path without mocking identity.
/// </summary>
public sealed class ReviewWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string OwnerApiKey = "review-test-owner-key-12345";
    public const string OwnerUser   = "review-owner-user";
    public const string OtherApiKey = "review-test-other-key-99999";
    public const string OtherUser   = "review-other-user";

    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public ReviewWebApplicationFactory()
    {
        _dbPath        = Path.Combine(Path.GetTempPath(), $"agentweaver-rv-{Guid.NewGuid():N}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-rv-wt-{Guid.NewGuid():N}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-rv-cp-{Guid.NewGuid():N}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-rv-ccp-{Guid.NewGuid():N}");

        // Program.cs computes SandboxAgentOptions.RequireMtls from builder.Configuration
        // *before* builder.Build() runs, at the top level of the minimal-hosting Program.cs.
        // WebApplicationFactory's ConfigureWebHost/ConfigureAppConfiguration additions (see
        // below) are only visible to configuration reads that happen at/after Build() -- they
        // do NOT reach this early read. Environment variables, in contrast, are loaded by
        // WebApplication.CreateBuilder(args) itself, so they ARE visible to that early read.
        // This fixture is the only one whose tests actually resolve the named
        // "a2a-sandbox-pod"/streaming HttpClients (A2ATransportTimeoutTests), which triggers
        // AgentHostMtlsClientHandler.Create(). RequireMtls defaults to true (production-safe
        // default), which would make the handler try to load client cert files that don't exist
        // outside a real cluster. These tests only assert HttpClient timeout wiring, not mTLS
        // behavior (see AgentHostMtlsClientHandlerTests for that), so disable it here via an
        // env var set before the host is built.
        Environment.SetEnvironmentVariable("Sandbox__AgentHost__RequireMtls", "false");
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
                ["Testing:BypassGitHubTokenAuth"]        = "true",
                // Primary owner key (Auth:ApiKey + Auth:User).
                ["Auth:ApiKey"]                           = OwnerApiKey,
                ["Auth:User"]                             = OwnerUser,
                // Second user added via the multi-key list (Auth:Keys[]).
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
