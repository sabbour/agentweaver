using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the D1 collective integration-branch build
/// (<see cref="WorktreeManager.BuildIntegrationBranch"/>) against a real temp git repository. Eligible
/// child branches must merge into the integration branch in dependency order (happy path); a
/// independent conflicting children stop integration without changing the published ref.
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
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha contents", "child a");
        CommitOnNewBranch(repoPath, "agentweaver/child-b", "beta.txt", "beta contents", "child b");

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/coord-1",
            new[] { "agentweaver/child-a", "agentweaver/child-b" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        result.HasChanges.Should().BeTrue();
        result.Diff.Should().NotBeNullOrEmpty();

        // The integration branch tree contains BOTH children's files.
        using var repo = new Repository(repoPath);
        var intTip = repo.Branches["agentweaver/integration/coord-1"].Tip;
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
            repoPath, "main", "agentweaver/integration/coord-2", Array.Empty<string>());

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        result.HasChanges.Should().BeFalse();
        result.Diff.Should().BeEmpty();
    }

    [Fact]
    public void BuildIntegrationBranch_WhenMainRepoIsOnIntegrationBranch_ChecksOutOriginAndResets()
    {
        var repoPath = CreateTempGitRepo();
        const string integrationBranch = "agentweaver/integration/coord-main-checkout";

        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha contents", "child a");
        _manager.BuildIntegrationBranch(repoPath, "main", integrationBranch, new[] { "agentweaver/child-a" })
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);

        using (var repo = new Repository(repoPath))
        {
            Commands.Checkout(repo, repo.Branches[integrationBranch]);
            repo.Head.FriendlyName.Should().Be(integrationBranch);
        }

        CommitOnNewBranch(repoPath, "agentweaver/child-b", "beta.txt", "beta contents", "child b");
        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", integrationBranch, new[] { "agentweaver/child-a", "agentweaver/child-b" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using var reopened = new Repository(repoPath);
        reopened.Head.FriendlyName.Should().Be("main",
            "the main repository must be moved off the generated integration branch before reset");
        var intTip = reopened.Branches[integrationBranch].Tip;
        intTip["alpha.txt"].Should().NotBeNull();
        intTip["beta.txt"].Should().NotBeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_ConflictingChildren_PreservesEarlierIntegrationRevision()
    {
        var repoPath = CreateTempGitRepo();

        // Both children edit the SAME file with different content => 3-way merge conflict.
        CommitOnNewBranch(repoPath, "agentweaver/child-x", "shared.txt", "from X\n", "child x");
        CommitOnNewBranch(repoPath, "agentweaver/child-y", "shared.txt", "from Y\n", "child y");

        const string integration = "agentweaver/integration/coord-3";
        _manager.BuildIntegrationBranch(repoPath, "main", integration, new[] { "agentweaver/child-x" })
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);
        var previousTip = _manager.GetBranchTipCommitSha(repoPath, integration);
        var result = _manager.BuildIntegrationBranch(repoPath, "main", integration,
            new[] { "agentweaver/child-x", "agentweaver/child-y" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Conflict);
        result.ConflictingBranch.Should().Be("agentweaver/child-y");
        result.ConflictingFiles.Should().Contain("shared.txt");
        result.ConflictingInputs.Keys.Should().BeEquivalentTo("agentweaver/child-x", "agentweaver/child-y");
        result.ConflictingInputs.Values.Should().OnlyContain(sha => sha.Length == 40);

        using var repo = new Repository(repoPath);
        repo.Branches[integration].Tip.Sha.Should().Be(previousTip);
        ReadBlob(repo, repo.Branches[integration].Tip["shared.txt"]).Should().Be("from X\n");
    }

    [Fact]
    public void BuildIntegrationBranch_MissingChild_BlocksWithoutPublishingPartialRef()
    {
        var repoPath = CreateTempGitRepo();
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha", "a");
        var result = _manager.BuildIntegrationBranch(repoPath, "main", "agentweaver/integration/missing",
            new[] { "agentweaver/child-a", "agentweaver/missing" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.MissingInput);
        result.ConflictingBranch.Should().Be("agentweaver/missing");
        using var repo = new Repository(repoPath);
        repo.Branches["agentweaver/integration/missing"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_DescendantAmendment_KeepsPriorIndependentFiles()
    {
        var repoPath = CreateTempGitRepo();
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "shared.txt", "first", "a");
        CommitOnNewBranch(repoPath, "agentweaver/child-b", "unrelated.txt", "independent", "b");
        using (var repo = new Repository(repoPath))
            repo.CreateBranch("agentweaver/child-a-amend", repo.Branches["agentweaver/child-a"].Tip);
        CommitOnNewBranch(repoPath, "agentweaver/child-a-amend", "shared.txt", "amended", "amend a");

        var result = _manager.BuildIntegrationBranch(repoPath, "main", "agentweaver/integration/amend",
            new[] { "agentweaver/child-a", "agentweaver/child-b", "agentweaver/child-a-amend" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using var merged = new Repository(repoPath);
        var tree = merged.Branches["agentweaver/integration/amend"].Tip.Tree;
        ReadBlob(merged, tree["shared.txt"]).Should().Be("amended");
        ReadBlob(merged, tree["unrelated.txt"]).Should().Be("independent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildIntegrationBranch_IndependentRenameOrDelete_BlocksExistingEdit(bool delete)
    {
        var repoPath = CreateTempGitRepo();
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "readme.txt", "edited", "edit");
        CommitRemovalOnNewBranch(repoPath, "agentweaver/child-b", "readme.txt",
            delete ? null : "renamed.txt");

        var result = _manager.BuildIntegrationBranch(repoPath, "main", "agentweaver/integration/remove",
            new[] { "agentweaver/child-a", "agentweaver/child-b" });

        result.Outcome.Should().Be(IntegrationBranchOutcome.Conflict);
        result.ConflictingFiles.Should().Contain("readme.txt");
        using var repo = new Repository(repoPath);
        repo.Branches["agentweaver/integration/remove"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_CleanGitMergeOfIndependentSameFileEdits_StillRequiresResolution()
    {
        var repoPath = CreateTempGitRepo();
        var lines = Enumerable.Range(1, 16).Select(i => $"line {i}").ToArray();
        using (var repo = new Repository(repoPath))
        {
            var main = repo.Branches["main"];
            var definition = TreeDefinition.From(main.Tip.Tree);
            var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream(Encoding.UTF8.GetBytes(
                string.Join("\n", lines) + "\n")));
            definition.Add("shared.txt", blob, Mode.NonExecutableFile);
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var commit = repo.ObjectDatabase.CreateCommit(sig, sig, "shared base",
                repo.ObjectDatabase.CreateTree(definition), new[] { main.Tip }, prettifyMessage: true);
            repo.Refs.UpdateTarget(repo.Refs["refs/heads/main"], commit.Id);
        }
        var first = (string[])lines.Clone();
        first[0] = "first edit";
        var last = (string[])lines.Clone();
        last[^1] = "last edit";
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "shared.txt", string.Join("\n", first) + "\n", "first");
        CommitOnNewBranch(repoPath, "agentweaver/child-b", "shared.txt", string.Join("\n", last) + "\n", "last");

        var result = _manager.BuildIntegrationBranch(repoPath, "main", "agentweaver/integration/disjoint-hunks",
            new[] { "agentweaver/child-a", "agentweaver/child-b" });
        result.Outcome.Should().Be(IntegrationBranchOutcome.Conflict);
        result.ConflictingFiles.Should().Contain("shared.txt");
    }

    // ── helpers (mirrors CommitEndpointMergeTests git setup) ──────────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-intbranch-{Guid.NewGuid():N}");
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

    private static string ReadBlob(Repository repo, TreeEntry? entry)
    {
        entry.Should().NotBeNull();
        using var content = ((Blob)entry!.Target).GetContentStream();
        using var reader = new StreamReader(content, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void CommitRemovalOnNewBranch(
        string repositoryPath, string branchName, string oldPath, string? newPath)
    {
        using var repo = new Repository(repositoryPath);
        var branch = repo.CreateBranch(branchName, repo.Branches["main"].Tip);
        var definition = TreeDefinition.From(branch.Tip.Tree);
        var oldEntry = branch.Tip.Tree[oldPath];
        definition.Remove(oldPath);
        if (newPath is not null)
            definition.Add(newPath, (Blob)oldEntry.Target, oldEntry.Mode);
        var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var commit = repo.ObjectDatabase.CreateCommit(signature, signature, "remove or rename",
            repo.ObjectDatabase.CreateTree(definition), new[] { branch.Tip }, prettifyMessage: true);
        repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], commit.Id);
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
