using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Git;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for the Feature 008 Phase 1 coordinator outcome-spec flow.
///
/// It wires the API exactly like production (real in-process host, real SQLite database,
/// real <c>CoordinatorRunService</c> + <c>CoordinatorWorkflowFactory</c> + MAF workflow,
/// real request-port suspend/resume) with two seams that keep the suite deterministic and
/// hermetic — both of which are real components, not mocks (Principle VII):
///
/// <list type="bullet">
/// <item>A <see cref="SignedOutGitHubTokenStore"/> so the coordinator's drafting agent turn
/// fails closed immediately (no live model call, no network) and the workflow falls back to
/// the deterministic draft that Morpheus built into <c>CoordinatorWorkflowFactory</c>.</item>
/// <item>A no-op <see cref="ProjectGitInitializer"/> so a blank project can be created without
/// touching real git, mirroring <see cref="ProjectsWebApplicationFactory"/>.</item>
/// </list>
///
/// Two API keys are registered (owner + other) so owner-scoping (403) can be exercised
/// without mocking identity, mirroring <see cref="ReviewWebApplicationFactory"/>.
/// </summary>
public sealed class CoordinatorWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string OwnerApiKey = "coordinator-test-owner-key-12345";
    public const string OwnerUser   = "coordinator-owner-user";
    public const string OtherApiKey = "coordinator-test-other-key-99999";
    public const string OtherUser   = "coordinator-other-user";

    private readonly string _dbPath;
    private readonly string _workspaceRoot;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public CoordinatorWebApplicationFactory()
    {
        var unique = Guid.NewGuid().ToString("N");
        _dbPath                     = Path.Combine(Path.GetTempPath(), $"agentweaver-coord-{unique}.db");
        _workspaceRoot              = Path.Combine(Path.GetTempPath(), $"agentweaver-coord-ws-{unique}");
        _worktreesPath              = Path.Combine(Path.GetTempPath(), $"agentweaver-coord-wt-{unique}");
        _checkpointsPath            = Path.Combine(Path.GetTempPath(), $"agentweaver-coord-cp-{unique}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-coord-ccp-{unique}");

        Directory.CreateDirectory(_workspaceRoot);
    }

    public HttpClient CreateOwnerClient() => CreateClientWithKey(OwnerApiKey);

    public HttpClient CreateOtherClient() => CreateClientWithKey(OtherApiKey);

    private HttpClient CreateClientWithKey(string apiKey)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return client;
    }

    /// <summary>Creates an isolated project working directory under the workspace root.</summary>
    public string NewWorkingDirectory()
    {
        var dir = Path.Combine(_workspaceRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
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
                // Primary owner key (Auth:ApiKey + Auth:User).
                ["Auth:ApiKey"]                           = OwnerApiKey,
                ["Auth:User"]                             = OwnerUser,
                // Second user via the multi-key list (Auth:Keys[]).
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
                // Phase 1 + decompose/persist suite: keep child dispatch off so the confirm/decline
                // lifecycle and the work-plan contract stay deterministic in this hermetic host
                // (non-git workspaces + signed-out tokens cannot spawn real child runs). The
                // dispatch-frontier logic is covered by SubtaskFrontierTests instead.
                ["Coordinator:AutoDispatch"]              = "false",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Force the coordinator's drafting agent turn to fail closed (signed out) so it
            // never makes a live model call and deterministically falls back to the built-in
            // deterministic draft. This is a real IGitHubTokenStore, not a mock.
            RemoveService<IGitHubTokenStore>(services);
            services.AddSingleton<IGitHubTokenStore>(new SignedOutGitHubTokenStore());

            // Skip real git init when creating the project that owns the coordinator run.
            RemoveService<ProjectGitInitializer>(services);
            services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
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
/// Real <see cref="IGitHubTokenStore"/> that reports an explicit signed-out state for every
/// scope. With this state <c>GitHubCopilotClientFactory.CreateClientAsync</c> fails closed and
/// throws before any network call, which is exactly what drives the coordinator's deterministic
/// draft fallback in tests. Distinct from <see cref="NullGitHubTokenStore"/> (NeverSignedIn),
/// which would let the config fallback token through and attempt a real client connection.
/// </summary>
public sealed class SignedOutGitHubTokenStore : IGitHubTokenStore
{
    public Task<GitHubTokenEntry> GetAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult(new GitHubTokenEntry(GitHubTokenStatus.SignedOut, null));

    public Task<GitHubToken?> GetTokenAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubToken?>(null);

    public Task SetAsync(GitHubTokenScope scope, GitHubToken token, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task<GitHubIdentity?> GetIdentityAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.FromResult<GitHubIdentity?>(null);

    public Task SignOutAsync(GitHubTokenScope scope, CancellationToken ct = default) =>
        Task.CompletedTask;
}
