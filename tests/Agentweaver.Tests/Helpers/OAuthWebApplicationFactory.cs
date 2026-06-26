using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for MCP OAuth 2.1 integration tests (Feature 009 — MCP OAuth).
///
/// Configures a test API and AS under a synthetic issuer/audience so the metadata, PKCE,
/// and backward-compat tests can run against the in-process server without real GitHub credentials
/// or real Key Vault signing keys.
///
/// When Tank lands T1-T3 (metadata + token endpoints), tests here will become live.
/// Until then, tests that depend on those endpoints are marked Skip with a clear TODO.
/// </summary>
public sealed class OAuthWebApplicationFactory : WebApplicationFactory<Program>
{
    // Static API key preserved for the S4 backward-compat path.
    public const string TestApiKey  = "oauth-test-api-key-11111";
    public const string TestUser    = "oauth-test-user";
    public const string TestIssuer  = "http://localhost";
    public const string TestAudience = "http://localhost/mcp";

    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public OAuthWebApplicationFactory()
    {
        var unique = Guid.NewGuid().ToString("N");
        _dbPath          = Path.Combine(Path.GetTempPath(), $"agentweaver-oauth-{unique}.db");
        _worktreesPath   = Path.Combine(Path.GetTempPath(), $"agentweaver-oauth-wt-{unique}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-oauth-cp-{unique}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-oauth-ccp-{unique}");
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    public HttpClient CreateUnauthenticatedClient() => CreateClient();

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
                // Auth bypass (test-only)
                ["Testing:BypassGitHubOrgAuthorization"] = "true",
                ["Testing:BypassGitHubTokenAuth"]        = "true",
                // Static API key for S4 backward-compat tests
                ["Auth:ApiKey"]                           = TestApiKey,
                ["Auth:User"]                             = TestUser,
                // GitHub OAuth app stubs (required at startup)
                ["Auth:GitHub:ClientId"]                  = "test-oauth-client-id",
                ["Auth:GitHub:BaseUrl"]                   = "https://github.com",
                // OAuth AS config (Tank T1 uses Auth:OAuth:Issuer / Auth:OAuth:Audience)
                ["Auth:OAuth:Issuer"]                    = TestIssuer,
                ["Auth:OAuth:Audience"]                  = TestAudience,
                ["Auth:Mcp:AllowGitHubPassthrough"]      = "true",
                // Misc required config
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
            services.AddSingleton<IGitHubTokenStore, InMemoryGitHubTokenStore>();

            RemoveService<ProjectGitInitializer>(services);
            services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            try { File.Delete(p); } catch { /* best effort */ }

        foreach (var dir in new[] { _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }

    private static void RemoveService<T>(IServiceCollection services)
    {
        var d = services.FirstOrDefault(s => s.ServiceType == typeof(T));
        if (d is not null) services.Remove(d);
    }
}
