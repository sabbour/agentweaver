using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Workflow;

/// <summary>
/// Unit tests for <see cref="WorktreeManager.EnsureWorktree"/> — the idempotent
/// worktree provisioner that underpins pod-restart recovery. Key scenarios:
/// 1. Directory already exists → no-op, returns correct paths.
/// 2. Directory missing (pod restart wiped ephemeral storage) → worktree is recreated.
/// 3. Directory missing WITH a stale git admin entry (the real restart case) →
///    prunes the stale entry and recreates successfully.
/// </summary>
public sealed class WorktreeRecoveryTests : IDisposable
{
    private readonly List<string> _tempDirs = new();

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // -------------------------------------------------------------------------
    // Test 1: directory already exists → no-op, returns existing paths
    // -------------------------------------------------------------------------
    [Fact]
    public void EnsureWorktree_WhenDirectoryAlreadyExists_ReturnsExistingInfoWithoutRecreating()
    {
        // Arrange
        var (repoPath, basePath, manager) = CreateTestEnvironment();
        var runId = RunId.New();

        // Create worktree normally (simulates original run).
        var original = manager.AddWorktree(repoPath, "main", runId);
        original.WorktreePath.Should().NotBeNullOrEmpty();
        Directory.Exists(original.WorktreePath).Should().BeTrue();

        // Act — EnsureWorktree called while directory is still present.
        var ensured = manager.EnsureWorktree(repoPath, "main", runId);

        // Assert — same paths returned, directory still intact.
        ensured.WorktreePath.Should().Be(original.WorktreePath);
        ensured.BranchName.Should().Be(original.BranchName);
        Directory.Exists(ensured.WorktreePath).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Test 2: directory missing, no prior worktree call → recreates cleanly
    // -------------------------------------------------------------------------
    [Fact]
    public void EnsureWorktree_WhenDirectoryNeverCreated_CreatesWorktree()
    {
        // Arrange
        var (repoPath, basePath, manager) = CreateTestEnvironment();
        var runId = RunId.New();

        // Act — EnsureWorktree called without a prior AddWorktree (no stale admin entry).
        var info = manager.EnsureWorktree(repoPath, "main", runId);

        // Assert — physical directory exists and branch is correct.
        info.WorktreePath.Should().NotBeNullOrEmpty();
        info.BranchName.Should().Be(WorktreeManager.BranchNameFor(runId));
        Directory.Exists(info.WorktreePath).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Test 3: THE RECOVERY CASE — directory missing, stale git admin entry present
    // -------------------------------------------------------------------------
    /// <summary>
    /// Simulates an OOM-restart: AddWorktree creates both the physical directory and a
    /// git admin entry. The pod restarts → ephemeral storage wiped → physical dir gone,
    /// but git admin entry remains in .git/worktrees/{runId}.
    /// Calling AddWorktree directly now would throw "already checked out".
    /// EnsureWorktree must prune the stale entry and recreate the worktree.
    /// </summary>
    [Fact]
    public void EnsureWorktree_WhenDirectoryMissingButAdminEntryStale_PrunesAndRecreates()
    {
        // Arrange
        var (repoPath, basePath, manager) = CreateTestEnvironment();
        var runId = RunId.New();

        // Create worktree (simulates original run).
        var original = manager.AddWorktree(repoPath, "main", runId);
        Directory.Exists(original.WorktreePath).Should().BeTrue("pre-condition: worktree created");

        // Simulate pod restart: delete only the physical worktree directory,
        // leaving the git admin entry (.git/worktrees/<runId>) intact.
        Directory.Delete(original.WorktreePath, recursive: true);
        Directory.Exists(original.WorktreePath).Should().BeFalse("pre-condition: directory wiped");

        // Verify that the stale admin entry blocks a direct AddWorktree call.
        // LibGit2Sharp wraps the "already checked out" git error as a LibGit2SharpException.
        var directRecreateAction = () => manager.AddWorktree(repoPath, "main", runId);
        directRecreateAction.Should().Throw<LibGit2SharpException>(
            "direct AddWorktree should fail when a stale admin entry exists for the branch");

        // Act — EnsureWorktree must handle this case.
        var recovered = manager.EnsureWorktree(repoPath, "main", runId);

        // Assert — worktree recreated successfully.
        recovered.WorktreePath.Should().Be(original.WorktreePath);
        recovered.BranchName.Should().Be(original.BranchName);
        Directory.Exists(recovered.WorktreePath).Should().BeTrue("recovered worktree directory must exist");
    }

    // -------------------------------------------------------------------------
    // Test 4: idempotent across multiple calls when directory is present
    // -------------------------------------------------------------------------
    [Fact]
    public void EnsureWorktree_CalledMultipleTimes_NeverThrows()
    {
        // Arrange
        var (repoPath, basePath, manager) = CreateTestEnvironment();
        var runId = RunId.New();

        // Act — multiple calls should all succeed without error.
        var act = () =>
        {
            var a = manager.EnsureWorktree(repoPath, "main", runId);
            var b = manager.EnsureWorktree(repoPath, "main", runId);
            var c = manager.EnsureWorktree(repoPath, "main", runId);
            b.WorktreePath.Should().Be(a.WorktreePath);
            c.WorktreePath.Should().Be(a.WorktreePath);
        };
        act.Should().NotThrow();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private (string repoPath, string basePath, WorktreeManager manager) CreateTestEnvironment()
    {
        var repoPath = MakeTempDir("repo");
        var basePath = MakeTempDir("worktrees");

        // Initialize a git repo with a single commit on "main".
        Repository.Init(repoPath);
        using (var repo = new Repository(repoPath))
        {
            File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial");
            Commands.Stage(repo, "*");
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repo.Commit("Initial commit", sig, sig);

            if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
                repo.Branches.Rename(repo.Head, "main");
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = basePath,
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@localhost",
            })
            .Build();

        var manager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
        return (repoPath, basePath, manager);
    }

    private string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aw-recovery-test-{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }
}
