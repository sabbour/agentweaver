using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Scaffolder.Api.Auth;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Projects;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Projects;

/// <summary>
/// Tests for ProjectService.RelinkAsync: validates directory, git repo, and origin matching.
/// Uses real LibGit2Sharp to create test repositories (relink requires a real git repo).
/// </summary>
public sealed class ProjectServiceRelinkTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public ProjectServiceRelinkTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"scaffolder-relink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testRoot);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(50);
        try { Directory.Delete(_testRoot, recursive: true); } catch { /* best effort */ }
    }

    private string NewDir(bool create = true)
    {
        var path = Path.Combine(_testRoot, Guid.NewGuid().ToString("N"));
        if (create) Directory.CreateDirectory(path);
        return path;
    }

    private static ProjectService BuildService(IProjectStore store) =>
        new(store, new LocalFilesystemWorkspaceProvider(),
            new NoOpGitInitializer(),
            new InMemoryGitHubTokenStore(), new FixedInstallationScopeProvider(),
            NullLogger<ProjectService>.Instance);

    /// <summary>Creates a real bare git repository (init + empty commit) at the given path.</summary>
    private static void InitRealGitRepo(string path, string? remoteUrl = null)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("Initial commit", sig, sig, new CommitOptions { AllowEmptyCommit = true });

        if (remoteUrl is not null)
            repo.Network.Remotes.Add("origin", remoteUrl);
    }

    // =========================================================================
    // RL-01: RelinkAsync accepts a moved non-empty git repository
    // =========================================================================
    [Fact]
    public async Task RelinkAsync_AcceptsMovedGitRepo()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var svc     = BuildService(store);
        var origDir = NewDir();

        // Create project (blank, no-op git)
        var project = await svc.CreateBlankAsync("Relink Test", origDir, null, null, null, "user");

        // Simulate "move": create a new directory with a real git repo
        var movedDir = NewDir();
        InitRealGitRepo(movedDir);

        var result = await svc.RelinkAsync(project.Id, movedDir);

        result.Should().BeTrue();
        var retrieved = await store.GetAsync(project.Id);
        retrieved!.WorkingDirectory.Should().Be(Path.GetFullPath(movedDir));
    }

    // =========================================================================
    // RL-02: RelinkAsync rejects a directory that is not a git repo
    // =========================================================================
    [Fact]
    public async Task RelinkAsync_RejectsNonGitDirectory()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store   = new SqliteProjectStore(testDb.Db);
        var svc     = BuildService(store);
        var origDir = NewDir();

        var project = await svc.CreateBlankAsync("Relink Test", origDir, null, null, null, "user");

        // Target is a plain directory with no .git
        var plainDir = NewDir();
        File.WriteAllText(Path.Combine(plainDir, "readme.txt"), "not a git repo");

        var act = async () => await svc.RelinkAsync(project.Id, plainDir);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not a valid git repository*");
    }

    // =========================================================================
    // RL-03: RelinkAsync rejects a directory whose origin doesn't match
    // =========================================================================
    [Fact]
    public async Task RelinkAsync_RejectsMismatchedOrigin()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store      = new SqliteProjectStore(testDb.Db);
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;
        await tokenStore.SetAsync(scope, new GitHubToken("ghp_test", null, null, "user", null, ["repo"]));

        var svc = new ProjectService(
            store, new LocalFilesystemWorkspaceProvider(),
            new NoOpGitInitializer(), tokenStore,
            new FixedInstallationScopeProvider(),
            NullLogger<ProjectService>.Instance);

        var origDir = NewDir();
        var project = await svc.CreateFromGitHubAsync(
            "GH Project", "owner/my-repo", origDir, null, null, null, "user");

        // Create a git repo pointing at a different remote
        var wrongDir = NewDir();
        InitRealGitRepo(wrongDir, "https://github.com/owner/DIFFERENT-repo.git");

        var act = async () => await svc.RelinkAsync(project.Id, wrongDir);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*does not match*");
    }

    // =========================================================================
    // RL-04: RelinkAsync returns false for unknown project id
    // =========================================================================
    [Fact]
    public async Task RelinkAsync_ReturnsFalse_ForUnknownProject()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var svc   = BuildService(store);
        var dir   = NewDir();
        InitRealGitRepo(dir);

        var result = await svc.RelinkAsync(ProjectId.New(), dir);

        result.Should().BeFalse();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private sealed class NoOpGitInitializer : ProjectGitInitializer
    {
        public NoOpGitInitializer()
            : base(NullLogger<ProjectGitInitializer>.Instance) { }

        public override string InitBlank(string workingDirectory, string defaultBranch)
        {
            Directory.CreateDirectory(workingDirectory);
            return defaultBranch;
        }

        public override string Clone(string workingDirectory, string sourceRepository, string accessToken)
        {
            Directory.CreateDirectory(workingDirectory);
            return "main";
        }
    }
}
