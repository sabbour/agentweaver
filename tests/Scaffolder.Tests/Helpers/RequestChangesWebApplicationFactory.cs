using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.Domain;

namespace Scaffolder.Tests.Helpers;

/// <summary>
/// Web application factory for request-changes endpoint tests (B3).
/// Registers two API keys (owner + other) so ownership/IDOR tests can exercise
/// the 403 path. Overrides IAgentRunner with TestFileEditAgentRunner so the
/// revision workflow that request-changes triggers does not make real HTTP calls.
/// </summary>
public sealed class RequestChangesWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string OwnerApiKey = "rc-test-owner-key-12345";
    public const string OwnerUser   = "rc-owner-user";
    public const string OtherApiKey = "rc-test-other-key-99999";
    public const string OtherUser   = "rc-other-user";

    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;

    /// <summary>Exposed so tests can configure the agent's behavior per test.</summary>
    public TestFileEditAgentRunner TestAgentRunner { get; } = new();

    public RequestChangesWebApplicationFactory()
    {
        _dbPath        = Path.Combine(Path.GetTempPath(), $"scaffolder-rc-{Guid.NewGuid():N}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"scaffolder-rc-wt-{Guid.NewGuid():N}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"scaffolder-rc-cp-{Guid.NewGuid():N}");
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
                ["Auth:ApiKey"]                           = OwnerApiKey,
                ["Auth:User"]                             = OwnerUser,
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
                ["Runs:MaxRevisions"]                     = "3",
                ["DOTNET_ENVIRONMENT"]                    = "Test",
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAgentRunner));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddSingleton<IAgentRunner>(TestAgentRunner);
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
    }
}
