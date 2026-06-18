using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Scaffolder.Api.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Scaffolder.Tests.Coordinator;

/// <summary>
/// Tests for the D1 collective integration-branch build
/// (<see cref="WorktreeManager.BuildIntegrationBranch"/>) against a real temp git repository. Eligible
/// child branches must merge into the integration branch in dependency order (happy path); a
/// conflicting pair must stop with NO partial assembly and surface the conflict (D2).
/// </summary>
public sealed class IntegrationBranchBuilderTests : IDisposable
{
    private readonly List<string> _tempRepoDirs = [];
    private readonly WorktreeManager _manager = new(
        new ConfigurationBuilder().Build(), NullLogger<WorktreeManager>.Instance);

    [Fact]
    public void BuildIntegrationBranch_MergesEligibleChildren_InDependencyOrder()
    {
        var repoPath = CreateTempGitRepo();

        // Two children touching DIFFERENT files off main — both merge cleanly into the integration branch.
        CommitOnNewBranch(repoPath, "scaffolder/child-a", "alpha.txt", "alpha contents", "child a");
        CommitOnNewBranch(repoPath, "scaffolder/child-b", "beta.txt", "beta contents", "child b");

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "scaffolder/integration/coord-1",
            new[] { "scaffolder/child-a", "scaffolder/child-b" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        result.HasChanges.Should().BeTrue();
        result.Diff.Should().NotBeNullOrEmpty();

        // The integration branch tree contains BOTH children's files.
        using var repo = new Repository(repoPath);
        var intTip = repo.Branches["scaffolder/integration/coord-1"].Tip;
        intTip["alpha.txt"].Should().NotBeNull();
        intTip["beta.txt"].Should().NotBeNull();
        result.TreeHash.Should().Be(intTip.Tree.Sha);

        // origin (main) is untouched by the build (branch-ref only).
        repo.Branches["main"].Tip["alpha.txt"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_EmptyChildList_YieldsEmptyDiffSuccess()
    {
        var repoPath = CreateTempGitRepo();

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "scaffolder/integration/coord-2", Array.Empty<string>());

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        result.HasChanges.Should().BeFalse();
        result.Diff.Should().BeEmpty();
    }

    [Fact]
    public void BuildIntegrationBranch_ConflictingChildren_StopsWithConflict_NoPartialAssembly()
    {
        var repoPath = CreateTempGitRepo();

        // Both children edit the SAME file with different content => 3-way merge conflict.
        CommitOnNewBranch(repoPath, "scaffolder/child-x", "shared.txt", "from X\n", "child x");
        CommitOnNewBranch(repoPath, "scaffolder/child-y", "shared.txt", "from Y\n", "child y");

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "scaffolder/integration/coord-3",
            new[] { "scaffolder/child-x", "scaffolder/child-y" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Conflict);
        result.ConflictingBranch.Should().Be("scaffolder/child-y");
        result.ConflictingFiles.Should().Contain("shared.txt");
    }

    // ── helpers (mirrors CommitEndpointMergeTests git setup) ──────────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"scaffolder-intbranch-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        // Detach onto a workspace branch so 'main' is never the checked-out branch (mirrors prod).
        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);

        return repoPath;
    }

    /// <summary>Creates a branch off main with a single commit adding/replacing one file (no checkout).</summary>
    private static void CommitOnNewBranch(
        string repositoryPath, string branchName, string filePath, string fileContent, string commitMessage)
    {
        using var repo = new Repository(repositoryPath);
        var main = repo.Branches["main"] ?? throw new InvalidOperationException("main not found");
        var branch = repo.Branches[branchName] ?? repo.CreateBranch(branchName, main.Tip);

        var tmpBlobPath = Path.Combine(repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmpBlobPath, fileContent, Encoding.UTF8);
        try
        {
            var blob = repo.ObjectDatabase.CreateBlob(tmpBlobPath);
            var treeDef = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree = repo.ObjectDatabase.CreateTree(treeDef);
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var newCommit = repo.ObjectDatabase.CreateCommit(
                sig, sig, commitMessage, newTree, new[] { branch.Tip }, prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);
        }
        finally
        {
            if (File.Exists(tmpBlobPath)) File.Delete(tmpBlobPath);
        }
    }

    public void Dispose()
    {
        foreach (var dir in _tempRepoDirs)
        {
            try { DeleteDirectory(dir); }
            catch { /* best effort */ }
        }
    }

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
