using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for workflow integration tests. Overrides IAgentRunner
/// with TestFileEditAgentRunner so tests exercise the REAL MAF workflow path
/// (not the direct fallback) with deterministic, real file operations.
/// </summary>
public sealed class WorkflowWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string TestApiKey = "workflow-test-key-12345";
    public const string TestUser = "workflow-test-user";

    private readonly string _dbPath;
    private readonly string _worktreesPath;
    private readonly string _checkpointsPath;
    private readonly string _coordinatorCheckpointsPath;

    public TestFileEditAgentRunner TestAgentRunner { get; } = new();

    public WorkflowWebApplicationFactory()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wf-{Guid.NewGuid():N}.db");
        _worktreesPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wf-wt-{Guid.NewGuid():N}");
        _checkpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wf-cp-{Guid.NewGuid():N}");
        _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"agentweaver-wf-ccp-{Guid.NewGuid():N}");
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
                ["Git:Author:Name"] = "TestAgent",
                ["Git:Author:Email"] = "test@localhost",
                ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                ["RunBounds:MaxSteps"] = "50",
                ["RunBounds:MaxMinutes"] = "10",
                // Mark as test environment so the fallback warning does not fire.
                ["DOTNET_ENVIRONMENT"] = "Test",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove the production IAgentRunner registration and replace with the test runner.
            var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(IAgentRunner));
            if (descriptor is not null)
                services.Remove(descriptor);

            services.AddSingleton<IAgentRunner>(TestAgentRunner);

            // Replace the production workflow agent factory with a fake that funnels the worker
            // agent into TestAgentRunner and short-circuits Rai/Scribe — so the MAF workflow runs
            // end-to-end without hitting the real GitHub Copilot SDK.
            var agentFactoryDescriptor = services.FirstOrDefault(
                d => d.ServiceType == typeof(Agentweaver.AgentRuntime.Workflow.IWorkflowAgentFactory));
            if (agentFactoryDescriptor is not null)
                services.Remove(agentFactoryDescriptor);

            services.AddSingleton<Agentweaver.AgentRuntime.Workflow.IWorkflowAgentFactory>(
                new FakeWorkflowAgentFactory(TestAgentRunner));
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
