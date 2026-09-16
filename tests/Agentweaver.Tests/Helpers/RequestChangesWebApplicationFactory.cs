using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for request-changes endpoint tests (B3).
/// Registers two API keys (owner + other) so ownership/IDOR tests can exercise
/// the 403 path. Overrides IAgentRunner with TestFileEditAgentRunner so the
/// revision workflow that request-changes triggers does not make real HTTP calls.
/// </summary>
public sealed class RequestChangesWebApplicationFactory : ApiWebApplicationFactory
{
    public const string OwnerApiKey = "rc-test-owner-key-12345";
    public const string OwnerUser   = "rc-owner-user";
    public const string OtherApiKey = "rc-test-other-key-99999";
    public const string OtherUser   = "rc-other-user";

    /// <summary>Exposed so tests can configure the agent's behavior per test.</summary>
    public TestFileEditAgentRunner TestAgentRunner { get; } = new();

    public RequestChangesWebApplicationFactory() : base("agentweaver-rc")
    {
    }

    public async Task<ProjectId> CreateBlankProjectAsync(string workingDirectory)
    {
        var projectId = ProjectId.New();
        var now = DateTimeOffset.UtcNow;
        await Services.GetRequiredService<IProjectStore>().InsertAsync(new Project
        {
            Id = projectId,
            Name = $"Request changes test {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = workingDirectory,
            DefaultBranch = "main",
            Owner = OwnerUser,
            ProviderSettings = new ProjectProviderSettings
            {
                DefaultProvider = ModelSource.GitHubCopilot,
            },
            State = ProjectState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await EnsureByokProviderAsync();

        return projectId;
    }

    private async Task EnsureByokProviderAsync()
    {
        await using var scope = Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ByokProviderConfigurationService>();
        if (await settings.GetAsync(CancellationToken.None) is not null)
            return;
        var provider = await settings.AddAsync(
            new ByokProviderConfiguration(
                string.Empty,
                "Request changes test provider",
                "openai",
                "https://provider.example.test",
                "test-model",
                "test-key"),
            CancellationToken.None);
        await settings.SetActiveAsync(provider.Id, CancellationToken.None);
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubOrgAuthorization"] = "true";
        configuration["Testing:BypassGitHubTokenAuth"] = "true";
        configuration["Auth:Mode"] = "GitHubLegacy";
        configuration["Auth:ApiKey"] = OwnerApiKey;
        configuration["Auth:User"] = OwnerUser;
        configuration["Auth:Keys:0:Token"] = OtherApiKey;
        configuration["Auth:Keys:0:User"] = OtherUser;
        configuration["Auth:Keys:0:PlatformRoles"] = PlatformRoles.Viewer;
        configuration["Runs:MaxRevisions"] = "3";
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
