using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Projects;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Tests for ProjectService.RelinkAsync: validates directory, git repo, and origin matching.
/// Uses real LibGit2Sharp to create test repositories (relink requires a real git repo).
/// </summary>
public sealed class ProjectServiceRelinkTests : IAsyncDisposable
{
    private readonly string _testRoot;

    public ProjectServiceRelinkTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), $"agentweaver-relink-{Guid.NewGuid():N}");
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

    private static ProjectService BuildService(IProjectStore store, string? workspaceRoot = null) =>
        new(store, TestWorkspaceProviders.CreateLocal(workspaceRoot),
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
        var root    = NewDir();
        var svc     = BuildService(store, root);

        // Create project (blank, no-op git)
        var project = await svc.CreateBlankAsync("Relink Test", string.Empty, null, null, null, "user");

        // Simulate "move": create a new directory with a real git repo
        var movedDir = Path.Combine(project.WorkingDirectory, "moved-repo");
        Directory.CreateDirectory(movedDir);
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
        var root    = NewDir();
        var svc     = BuildService(store, root);

        var project = await svc.CreateBlankAsync("Relink Test", string.Empty, null, null, null, "user");

        // Target is a plain directory with no .git
        var plainDir = Path.Combine(project.WorkingDirectory, "plain");
        Directory.CreateDirectory(plainDir);
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
        var root       = NewDir();
        var tokenStore = new InMemoryGitHubTokenStore();
        var scope      = GitHubTokenScope.Installation;
        await tokenStore.SetAsync(scope, new GitHubToken("ghp_test", null, null, "user", null, ["repo"]));

        var svc = new ProjectService(
            store, TestWorkspaceProviders.CreateLocal(root),
            new NoOpGitInitializer(), tokenStore,
            new FixedInstallationScopeProvider(),
            NullLogger<ProjectService>.Instance);

        var project = await svc.CreateFromGitHubAsync(
            "GH Project", "https://github.com/owner/my-repo", string.Empty, null, null, null, "user");

        // Create a git repo pointing at a different remote
        var wrongDir = Path.Combine(project.WorkingDirectory, "wrong-repo");
        Directory.CreateDirectory(wrongDir);
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
        var root  = NewDir();
        var svc   = BuildService(store, root);
        var dir   = NewDir();
        InitRealGitRepo(dir);

        var result = await svc.RelinkAsync(ProjectId.New(), dir);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task RelinkAsync_RejectsTraversalOutsideProjectWorkspaceRoot()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var root  = NewDir();
        var svc   = BuildService(store, root);
        var project = await svc.CreateBlankAsync("Relink Test", string.Empty, null, null, null, "user");

        var siblingRepo = Path.Combine(root, "sibling-repo");
        Directory.CreateDirectory(siblingRepo);
        InitRealGitRepo(siblingRepo);

        var traversalPath = Path.Combine(project.WorkingDirectory, "..", "sibling-repo");
        var act = async () => await svc.RelinkAsync(project.Id, traversalPath);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*workspace root*");
    }

    [Fact]
    public async Task RelinkAsync_RejectsAbsolutePathOutsideProjectWorkspaceRoot()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var root  = NewDir();
        var svc   = BuildService(store, root);
        var project = await svc.CreateBlankAsync("Relink Test", string.Empty, null, null, null, "user");

        var serverHomeRepo = NewDir();
        InitRealGitRepo(serverHomeRepo);

        var act = async () => await svc.RelinkAsync(project.Id, serverHomeRepo);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*workspace root*");
    }

    [Fact]
    public async Task RelinkAsync_RejectsCrossProjectWorkspacePath()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var root  = NewDir();
        var svc   = BuildService(store, root);

        var projectA = await svc.CreateBlankAsync("Relink A", string.Empty, null, null, null, "user");
        var projectB = await svc.CreateBlankAsync("Relink B", string.Empty, null, null, null, "user");
        InitRealGitRepo(projectB.WorkingDirectory);

        var act = async () => await svc.RelinkAsync(projectA.Id, projectB.WorkingDirectory);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*workspace root*");
    }

    [Fact]
    public async Task RelinkAsync_RejectsSymlinkEscapingProjectWorkspaceRoot()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var store = new SqliteProjectStore(testDb.Db);
        var root  = NewDir();
        var svc   = BuildService(store, root);
        var project = await svc.CreateBlankAsync("Relink Test", string.Empty, null, null, null, "user");

        var outsideTarget = NewDir();
        InitRealGitRepo(outsideTarget);
        var symlinkPath = Path.Combine(project.WorkingDirectory, "escaped-link");

        try
        {
            Directory.CreateSymbolicLink(symlinkPath, outsideTarget);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (IOException)
        {
            return;
        }

        try
        {
            var act = async () => await svc.RelinkAsync(project.Id, symlinkPath);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*workspace root*");
        }
        finally
        {
            try { Directory.Delete(symlinkPath); } catch { /* best effort */ }
        }
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
