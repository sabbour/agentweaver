using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Casting;

/// <summary>
/// Web application factory for casting-related integration tests.
/// Uses LocalFilesystemWorkspaceProvider pointed at an isolated temp directory,
/// and stubs out ProjectGitInitializer to skip real git operations.
/// </summary>
public sealed class CastingWebApplicationFactory : ApiWebApplicationFactory
{
    public const string TestApiKey = "casting-test-api-key-99999";
    public const string TestUser   = "casting-test-user";

    public CastingWebApplicationFactory() : base("agentweaver-cast", createWorkspaceRoot: true)
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
    /// Creates a project working directory under the isolated workspace root.
    /// </summary>
    public string NewProjectWorkingDirectory() => CreateWorkspaceDirectory();

    /// <summary>
    /// Creates a temp directory, initializes it as a real git repository using LibGit2Sharp,
    /// creates an initial commit so HEAD is not unborn, and returns the repo path.
    /// Required for sync tests that need a real git repo.
    /// </summary>
    public string NewGitRepository()
    {
        var dir = CreateWorkspaceDirectory($"repo-{Guid.NewGuid():N}");

        Repository.Init(dir);
        using var repo = new Repository(dir);

        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit(
            "Initial commit",
            sig,
            sig,
            new CommitOptions { AllowEmptyCommit = true });

        return dir;
    }

    protected override void ConfigureTestConfiguration(IDictionary<string, string?> configuration)
    {
        configuration["Auth:ApiKey"] = TestApiKey;
        configuration["Auth:User"] = TestUser;
        configuration["Auth:GitHub:ClientId"] = "test-github-client-id";
        configuration["Auth:GitHub:BaseUrl"] = "https://github.com";
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        RemoveService<ProjectGitInitializer>(services);
        services.AddSingleton<ProjectGitInitializer, NoOpProjectGitInitializerForCasting>();
    }
}

/// <summary>
/// Stub ProjectGitInitializer that skips real git operations for casting tests.
/// </summary>
internal sealed class NoOpProjectGitInitializerForCasting : ProjectGitInitializer
{
    public NoOpProjectGitInitializerForCasting(Microsoft.Extensions.Logging.ILogger<ProjectGitInitializer> logger)
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
}
