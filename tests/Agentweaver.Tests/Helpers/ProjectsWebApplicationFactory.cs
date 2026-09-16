using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Helpers;

/// <summary>
/// Web application factory for project-related integration tests.
/// Uses LocalFilesystemWorkspaceProvider pointed at an isolated temp directory,
/// and stubs out ProjectGitInitializer to skip real git operations.
/// </summary>
public class ProjectsWebApplicationFactory : ApiWebApplicationFactory
{
    public const string TestApiKey = "projects-test-api-key-54321";
    public const string TestUser   = "projects-test-user";

    public ProjectsWebApplicationFactory() : base("agentweaver-proj", createWorkspaceRoot: true)
    {
    }

    /// <summary>
    /// Creates an authenticated HttpClient using the test API key.
    /// </summary>
    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TestApiKey);
        return client;
    }

    /// <summary>
    /// Hook for subclasses (e.g. a persistent-volume workspace variant) to layer extra
    /// configuration on top of the defaults below, such as selecting a different
    /// <c>Workspace:Provider</c>.
    /// </summary>
    protected virtual IDictionary<string, string?> GetAdditionalConfiguration() =>
        new Dictionary<string, string?>();

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Testing:BypassGitHubTokenAuth"] = "true";
        configuration["Auth:ApiKey"] = TestApiKey;
        configuration["Auth:User"] = TestUser;
        configuration["Auth:GitHub:ClientId"] = "test-github-client-id";
        configuration["Auth:GitHub:BaseUrl"] = "https://github.com";
        foreach (var (key, value) in GetAdditionalConfiguration())
            configuration[key] = value;
    }

    /// <summary>
    /// Creates a project working directory under the isolated workspace root.
    /// </summary>
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        RemoveService<ProjectGitInitializer>(services);
        services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializer>();
    }

    public string NewWorkingDirectory() => CreateWorkspaceDirectory();
}

/// <summary>
/// Variant of <see cref="ProjectsWebApplicationFactory"/> that selects the
/// <see cref="PersistentVolumeWorkspaceProvider"/> (<c>AutoAssignsPath == true</c>) instead of the
/// default local filesystem provider, so tests can verify that <c>POST /api/projects</c> does not
/// require a client-supplied <c>working_directory</c> when the active provider auto-assigns one (#333).
/// </summary>
public class PersistentVolumeProjectsWebApplicationFactory : ProjectsWebApplicationFactory
{
    private readonly string _mountRoot =
        Path.Combine(Path.GetTempPath(), $"agentweaver-proj-pv-{Guid.NewGuid():N}");

    public PersistentVolumeProjectsWebApplicationFactory()
    {
        Directory.CreateDirectory(_mountRoot);
    }

    protected override IDictionary<string, string?> GetAdditionalConfiguration() =>
        new Dictionary<string, string?>
        {
            ["Workspace:Provider"] = "persistent-volume",
            ["Workspace:PersistentVolume:MountRoot"] = _mountRoot,
        };

    protected override void DisposeFixture()
    {
        try { Directory.Delete(_mountRoot, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// Stub ProjectGitInitializer that skips real git operations for tests.
/// InitBlank just creates the directory and returns the branch name; Clone creates the directory and returns "main".
/// </summary>
internal sealed class NoOpProjectGitInitializer : ProjectGitInitializer
{
    public NoOpProjectGitInitializer(Microsoft.Extensions.Logging.ILogger<ProjectGitInitializer> logger)
        : base(logger) { }

    public override string InitBlank(string workingDirectory, string defaultBranch)
    {
        Directory.CreateDirectory(workingDirectory);
        return defaultBranch;
    }

    public override string Clone(
        string workingDirectory,
        string sourceRepository,
        string accessToken,
        GitClonePurpose purpose)
    {
        Directory.CreateDirectory(workingDirectory);
        return "main";
    }

    public override void PushToNewRemote(string workingDirectory, string remoteUrl, string branchName, string accessToken)
    {
        // No-op: tests never need a real remote push.
    }
}
