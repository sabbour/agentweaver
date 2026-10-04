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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildIntegrationBranch_IdentityDivergence_PreservesOriginAndChildApplication(bool originAdvances)
    {
        var repoPath = CreateTempGitRepo();
        const string identity = ".squad/identity/now.md";
        CommitOnNewBranch(repoPath, "main", identity, "base\n", "seed identity");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", identity, "child\n", "child identity");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "app.txt", "child application\n", "child application");
        if (originAdvances)
            CommitOnNewBranch(repoPath, "main", identity, "origin\n", "consolidate identity");

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/identity", ["agentweaver/child-a"]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using var repo = new Repository(repoPath);
        var tree = repo.Branches["agentweaver/integration/identity"].Tip.Tree;
        ReadBlob(repo, tree[identity]).Should().Be(originAdvances ? "origin\n" : "base\n");
        ReadBlob(repo, tree["app.txt"]).Should().Be("child application\n");
        ReadBlob(repo, repo.Branches["main"].Tip[identity])
            .Should().Be(originAdvances ? "origin\n" : "base\n");
        repo.Branches["main"].Tip["app.txt"].Should().BeNull();
    }

    [Theory]
    [InlineData(".squad/identity/now.md")]
    [InlineData(".squad/decisions.md")]
    [InlineData(".squad/agents/scribe/history.md")]
    public void BuildIntegrationBranch_IndependentBookkeepingEdits_DoNotBlockApplicationAssembly(string ledger)
    {
        var repoPath = CreateTempGitRepo();
        CommitOnNewBranch(repoPath, "main", ledger, "base\n", "seed ledger");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", ledger, "first child\n", "first ledger");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha\n", "first application");
        CommitOnNewBranch(repoPath, "agentweaver/child-b", ledger, "second child\n", "second ledger");
        CommitOnNewBranch(repoPath, "agentweaver/child-b", "beta.txt", "beta\n", "second application");
        CommitOnNewBranch(repoPath, "main", ledger, "origin\n", "consolidate ledger");

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/siblings",
            ["agentweaver/child-a", "agentweaver/child-b"]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using var repo = new Repository(repoPath);
        var tree = repo.Branches["agentweaver/integration/siblings"].Tip.Tree;
        ReadBlob(repo, tree[ledger]).Should().Be("origin\n");
        ReadBlob(repo, tree["alpha.txt"]).Should().Be("alpha\n");
        ReadBlob(repo, tree["beta.txt"]).Should().Be("beta\n");
    }

    [Fact]
    public void BuildIntegrationBranch_ApplicationConflictAlongsideBookkeeping_StillRequiresResolution()
    {
        var repoPath = CreateTempGitRepo();
        const string identity = ".squad/identity/now.md";
        CommitOnNewBranch(repoPath, "main", identity, "base\n", "seed identity");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", identity, "child\n", "child identity");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "app.txt", "child\n", "child application");
        CommitOnNewBranch(repoPath, "main", identity, "origin\n", "consolidate identity");
        CommitOnNewBranch(repoPath, "main", "app.txt", "origin\n", "origin application");

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/app-conflict", ["agentweaver/child-a"]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.Conflict);
        result.ConflictingFiles.Should().Contain("app.txt");
        result.ConflictingFiles.Should().NotContain(identity);
        using var repo = new Repository(repoPath);
        repo.Branches["agentweaver/integration/app-conflict"].Should().BeNull();
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
    public void BuildIntegrationBranch_LegacyFlatRef_AllowsAttemptBranchWithoutTouchingOtherRefs()
    {
        var repoPath = CreateTempGitRepo();
        const string legacy = "agentweaver/integration/coord-legacy";
        const string attempt = legacy + "/attempt-2";
        const string unrelated = "agentweaver/integration/another-run";
        CommitOnNewBranch(repoPath, legacy, "previous.txt", "previous", "old assembly");
        CommitOnNewBranch(repoPath, unrelated, "unrelated.txt", "unrelated", "other assembly");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha", "child");
        var legacyTip = _manager.GetBranchTipCommitSha(repoPath, legacy);
        var unrelatedTip = _manager.GetBranchTipCommitSha(repoPath, unrelated);

        var result = _manager.BuildIntegrationBranch(repoPath, "main", attempt, ["agentweaver/child-a"]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using var repo = new Repository(repoPath);
        repo.Branches[legacy].Should().BeNull();
        repo.Branches["agentweaver/legacy-integration/coord-legacy"].Tip.Sha.Should().Be(legacyTip);
        repo.Branches[attempt].Tip["alpha.txt"].Should().NotBeNull();
        repo.Branches[unrelated].Tip.Sha.Should().Be(unrelatedTip);
        repo.Branches["main"].Tip["alpha.txt"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_RetryAndNextAttempt_KeepMigratedRefAndResetOnlyRequestedAttempt()
    {
        var repoPath = CreateTempGitRepo();
        const string legacy = "agentweaver/integration/coord-retry";
        const string attempt = legacy + "/attempt-2";
        CommitOnNewBranch(repoPath, legacy, "previous.txt", "previous", "old assembly");
        CommitOnNewBranch(repoPath, "agentweaver/child-a", "alpha.txt", "alpha", "child a");
        CommitOnNewBranch(repoPath, "agentweaver/child-b", "beta.txt", "beta", "child b");
        var legacyTip = _manager.GetBranchTipCommitSha(repoPath, legacy);

        _manager.BuildIntegrationBranch(repoPath, "main", attempt, ["agentweaver/child-a"])
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);
        _manager.BuildIntegrationBranch(repoPath, "main", attempt, ["agentweaver/child-a", "agentweaver/child-b"])
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);
        _manager.BuildIntegrationBranch(repoPath, "main", legacy + "/attempt-3", ["agentweaver/child-b"])
            .Outcome.Should().Be(IntegrationBranchOutcome.Built);

        using var repo = new Repository(repoPath);
        repo.Branches["agentweaver/legacy-integration/coord-retry"].Tip.Sha.Should().Be(legacyTip);
        repo.Branches[attempt].Tip["alpha.txt"].Should().NotBeNull();
        repo.Branches[attempt].Tip["beta.txt"].Should().NotBeNull();
        repo.Branches[legacy + "/attempt-3"].Tip["alpha.txt"].Should().BeNull();
        repo.Branches[legacy + "/attempt-3"].Tip["beta.txt"].Should().NotBeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_MissingInput_DoesNotMigrateLegacyRefOrPublishAttempt()
    {
        var repoPath = CreateTempGitRepo();
        const string legacy = "agentweaver/integration/coord-missing";
        CommitOnNewBranch(repoPath, legacy, "previous.txt", "previous", "old assembly");
        var legacyTip = _manager.GetBranchTipCommitSha(repoPath, legacy);

        _manager.BuildIntegrationBranch(repoPath, "main", legacy + "/attempt-2", ["agentweaver/missing"])
            .Outcome.Should().Be(IntegrationBranchOutcome.MissingInput);

        using var repo = new Repository(repoPath);
        repo.Branches[legacy].Tip.Sha.Should().Be(legacyTip);
        repo.Branches[legacy + "/attempt-2"].Should().BeNull();
        repo.Branches["agentweaver/legacy-integration/coord-missing"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_ArchiveOccupied_RefusesToOverwriteEitherRef()
    {
        var repoPath = CreateTempGitRepo();
        const string legacy = "agentweaver/integration/coord-occupied";
        const string archive = "agentweaver/legacy-integration/coord-occupied";
        CommitOnNewBranch(repoPath, legacy, "previous.txt", "previous", "old assembly");
        CommitOnNewBranch(repoPath, archive, "archived.txt", "archived", "unrelated archive");
        var legacyTip = _manager.GetBranchTipCommitSha(repoPath, legacy);
        var archiveTip = _manager.GetBranchTipCommitSha(repoPath, archive);

        Action build = () => _manager.BuildIntegrationBranch(
            repoPath, "main", legacy + "/attempt-2", Array.Empty<string>());
        build.Should().Throw<InvalidOperationException>().WithMessage("*archive*already exists*");

        using var repo = new Repository(repoPath);
        repo.Branches[legacy].Tip.Sha.Should().Be(legacyTip);
        repo.Branches[archive].Tip.Sha.Should().Be(archiveTip);
        repo.Branches[legacy + "/attempt-2"].Should().BeNull();
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
    public void BuildIntegrationBranch_BranchMovesAfterVerification_UsesOnlyVerifiedCommit()
    {
        var repoPath = CreateTempGitRepo();
        const string branch = "agentweaver/child-a";
        CommitOnNewBranch(repoPath, branch, "verified.txt", "verified bytes", "verified");
        var expectedTree = _manager.GetBranchTipTreeSha(repoPath, branch)!;
        var verified = _manager.GetVerifiedChildInput(repoPath, branch, expectedTree);
        verified.Should().NotBeNull();

        CommitOnNewBranch(repoPath, branch, "unverified.txt", "unverified bytes", "moved after verification");
        _manager.GetVerifiedChildInput(repoPath, branch, expectedTree).Should().BeNull();

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/pinned", [verified!]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);
        using var repo = new Repository(repoPath);
        var tip = repo.Branches["agentweaver/integration/pinned"].Tip;
        tip.Sha.Should().Be(verified!.CommitSha);
        tip.Tree.Sha.Should().Be(expectedTree);
        ReadBlob(repo, tip["verified.txt"]).Should().Be("verified bytes");
        tip["unverified.txt"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_VerifiedCommitUnavailable_BlocksWithoutPublishing()
    {
        var repoPath = CreateTempGitRepo();
        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/unavailable",
            [new("agentweaver/child", new string('a', 40))]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.MissingInput);
        result.ConflictingBranch.Should().Be("agentweaver/child");
        using var repo = new Repository(repoPath);
        repo.Branches["agentweaver/integration/unavailable"].Should().BeNull();
    }

    [Fact]
    public void BuildIntegrationBranch_DirectoryFileConflict_RecordsBothImmutableContributors()
    {
        var repoPath = CreateTempGitRepo();
        const string first = "agentweaver/child-file";
        const string second = "agentweaver/child-directory";
        CommitOnNewBranch(repoPath, first, "docs", "file", "file child");
        CommitOnNewBranch(repoPath, second, "docs/readme.md", "nested", "directory child");
        var firstSha = _manager.GetBranchTipCommitSha(repoPath, first)!;
        var secondSha = _manager.GetBranchTipCommitSha(repoPath, second)!;

        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", "agentweaver/integration/directory-file",
            [new(first, firstSha), new(second, secondSha)]);

        result.Outcome.Should().Be(IntegrationBranchOutcome.Conflict);
        result.ConflictingFiles.Should().Contain(["docs", "docs/readme.md"]);
        result.ConflictingInputs.Should().ContainKey(first).WhoseValue.Should().Be(firstSha);
        result.ConflictingInputs.Should().ContainKey(second).WhoseValue.Should().Be(secondSha);
        using var repo = new Repository(repoPath);
        repo.Branches["agentweaver/integration/directory-file"].Should().BeNull();
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

internal static class IntegrationBranchTestExtensions
{
    internal static IntegrationBranchResult BuildIntegrationBranch(
        this WorktreeManager manager,
        string repositoryPath,
        string originatingBranch,
        string integrationBranch,
        IReadOnlyList<string> childBranchesInOrder,
        bool publish = true)
    {
        var inputs = childBranchesInOrder
            .Select(branch => new IntegrationChildInput(
                branch, manager.GetBranchTipCommitSha(repositoryPath, branch) ?? string.Empty))
            .ToArray();
        return manager.BuildIntegrationBranch(
            repositoryPath, originatingBranch, integrationBranch, inputs, publish);
    }
}
