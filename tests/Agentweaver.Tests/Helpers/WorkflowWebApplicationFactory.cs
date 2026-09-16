using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for workflow integration tests. Overrides IAgentRunner
/// with TestFileEditAgentRunner so tests exercise the REAL MAF workflow path
/// (not the direct fallback) with deterministic, real file operations.
/// </summary>
public sealed class WorkflowWebApplicationFactory : ApiWebApplicationFactory
{
    public const string TestApiKey = "workflow-test-key-12345";
    public const string TestUser = "workflow-test-user";

    public TestFileEditAgentRunner TestAgentRunner { get; } = new();

    public WorkflowWebApplicationFactory() : base("agentweaver-wf")
    {
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubOrgAuthorization"] = "true";
        configuration["Testing:BypassGitHubTokenAuth"] = "true";
        configuration["Auth:Mode"] = "GitHubLegacy";
        configuration["Auth:ApiKey"] = TestApiKey;
        configuration["Auth:User"] = TestUser;
        configuration["Git:Author:Name"] = "TestAgent";
        configuration["DOTNET_ENVIRONMENT"] = "Test";
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        RemoveService<IAgentRunner>(services);
        services.AddSingleton<IAgentRunner>(TestAgentRunner);

        RemoveService<Agentweaver.AgentRuntime.Workflow.IWorkflowAgentFactory>(services);
        services.AddSingleton<Agentweaver.AgentRuntime.Workflow.IWorkflowAgentFactory>(
            new FakeWorkflowAgentFactory(TestAgentRunner));
    }
}
