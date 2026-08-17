using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Unit tests for ProjectService.CreateAndConnectRepositoryAsync / ListRepositoryOwnersAsync — the
/// post-creation GitHub connection flow for a currently-unconnected (Blank-origin) project (issue:
/// allow creating a GitHub repository for a project that has none connected).
/// </summary>
public sealed class ProjectServiceCreateRepositoryTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public ProjectServiceCreateRepositoryTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-svc-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    private static ProjectService BuildService(
        IProjectStore store, ProjectGitInitializer gitInit, IGitHubRepositoryClient repoClient, InMemoryGitHubTokenStore tokenStore) =>
        new(
            store,
            TestWorkspaceProviders.CreateLocal(),
            gitInit,
            tokenStore,
            new FixedInstallationScopeStub(),
            NullLogger<ProjectService>.Instance,
            repositoryClient: repoClient);

    [Fact]
    public async Task CreateAndConnectRepositoryAsync_PushesHistoryAndPersistsOrigin()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var tokenStore = new InMemoryGitHubTokenStore();
        await tokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("test-token", null, null, "octo", null, ["repo"]));
        var gitInit = new RecordingGitInitializer();
        var repoClient = new FakeGitHubRepositoryClient();
        var service = BuildService(store, gitInit, repoClient, tokenStore);

        var dir = Path.Combine(_testRoot, "proj");
        Directory.CreateDirectory(dir);
        var project = await service.CreateBlankAsync("My Project", dir, null, null, null, "test-user");

        var connected = await service.CreateAndConnectRepositoryAsync(
            project.Id, "octo", null, isPrivate: true, "test-user");

        connected.Origin.Kind.Should().Be(ProjectOriginKind.FromGitHub);
        connected.Origin.SourceRepository.Should().Be("octo/my-project");
        repoClient.CreatedRepositories.Should().ContainSingle();
        repoClient.CreatedRepositories[0].Should().Be(("octo", "my-project", true));
        gitInit.PushedRemotes.Should().ContainSingle();

        var persisted = await store.GetAsync(project.Id);
        persisted!.Origin.Kind.Should().Be(ProjectOriginKind.FromGitHub);
        persisted.Origin.SourceRepository.Should().Be("octo/my-project");
    }

    [Fact]
    public async Task CreateAndConnectRepositoryAsync_Throws_WhenProjectAlreadyHasRepository()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var tokenStore = new InMemoryGitHubTokenStore();
        await tokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("test-token", null, null, "octo", null, ["repo"]));
        var gitInit = new RecordingGitInitializer();
        var repoClient = new FakeGitHubRepositoryClient();
        var service = BuildService(store, gitInit, repoClient, tokenStore);

        var dir = Path.Combine(_testRoot, "proj2");
        Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
        var project = await service.CreateFromGitHubAsync(
            "Existing", "https://github.com/octo/existing", dir, null, null, null, "test-user");

        var act = () => service.CreateAndConnectRepositoryAsync(project.Id, "octo", null, true, "test-user");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has a connected repository*");
        repoClient.CreatedRepositories.Should().BeEmpty();
    }

    [Fact]
    public async Task ListRepositoryOwnersAsync_DelegatesToClient()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var tokenStore = new InMemoryGitHubTokenStore();
        await tokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("test-token", null, null, "octo", null, ["repo"]));
        var repoClient = new FakeGitHubRepositoryClient();
        var service = BuildService(store, new RecordingGitInitializer(), repoClient, tokenStore);

        var owners = await service.ListRepositoryOwnersAsync("test-user");

        owners.Should().HaveCount(2);
        owners[0].Should().Be(new GitHubRepositoryOwner("octo", true));
    }

    private sealed class RecordingGitInitializer : ProjectGitInitializer
    {
        public RecordingGitInitializer() : base(NullLogger<ProjectGitInitializer>.Instance) { }

        public List<(string WorkingDirectory, string RemoteUrl, string Branch)> PushedRemotes { get; } = [];

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
            PushedRemotes.Add((workingDirectory, remoteUrl, branchName));
        }
    }

    private sealed class FakeGitHubRepositoryClient : IGitHubRepositoryClient
    {
        public List<(string Owner, string Name, bool Private)> CreatedRepositories { get; } = [];

        public Task<IReadOnlyList<GitHubRepositoryOwner>> ListRepositoryOwnersAsync(string accessToken, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitHubRepositoryOwner>>(
                [new GitHubRepositoryOwner("octo", true), new GitHubRepositoryOwner("octo-org", false)]);

        public Task<GitHubRepositoryResult> CreateRepositoryAsync(
            string owner, string name, bool isPrivate, string accessToken, CancellationToken ct = default)
        {
            CreatedRepositories.Add((owner, name, isPrivate));
            var fullName = $"{owner}/{name}";
            return Task.FromResult(GitHubRepositoryResult.Ok(
                fullName, $"https://github.com/{fullName}", $"https://github.com/{fullName}.git", "main"));
        }
    }
}
