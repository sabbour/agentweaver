using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Scaffolder.Tests.Helpers;

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

    public ReviewWebApplicationFactory()
    {
        _dbPath        = Path.Combine(Path.GetTempPath(), $"scaffolder-rv-{Guid.NewGuid():N}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"scaffolder-rv-wt-{Guid.NewGuid():N}");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"]                         = _dbPath,
                ["Worktrees:BasePath"]                    = _worktreesPath,
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
    }
}
