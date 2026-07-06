using System.Text;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;
using Agentweaver.Domain;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression tests for issue #197 — a coordinator child subtask's file output must reach dependent
/// subtasks, the coordinator, and the UI. These exercise the API-managed propagation path (the shared
/// repository's integration + worktree branches — no git remote / push is involved):
/// <list type="number">
/// <item>(a) an upstream subtask's committed file appears in a dependent subtask's base worktree.</item>
/// <item>(b) a file a child committed is retrievable after its worktree directory is gone.</item>
/// <item>(c) the changed-file set is exactly the modified files — no phantom "+0 -0" rows.</item>
/// </list>
/// </summary>
public sealed class ChildOutputPropagationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly WorktreeManager _manager;
    private readonly string _worktreeBase;

    public ChildOutputPropagationTests()
    {
        _worktreeBase = Path.Combine(Path.GetTempPath(), $"aw-197-wt-{Guid.NewGuid():N}");
        _tempDirs.Add(_worktreeBase);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Worktrees:BasePath"] = _worktreeBase })
            .Build();
        _manager = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
    }

    // (a) Upstream subtask creates a file → dependent subtask's base contains it.
    [Fact]
    public void UpstreamFile_IsPresentInDependentSubtaskBaseWorktree()
    {
        var repoPath = CreateTempGitRepo();

        // Upstream subtask committed research-domain.md to its worktree branch.
        CommitOnNewBranch(repoPath, "agentweaver/child-upstream", "research-domain.md",
            "coffee roasting SaaS domain research", "upstream produces artifact");

        // Coordinator assembles it onto the shared integration branch.
        var integrationBranch = "agentweaver/integration/coord-197a";
        var result = _manager.BuildIntegrationBranch(
            repoPath, "main", integrationBranch, new[] { "agentweaver/child-upstream" });
        result.Outcome.Should().Be(IntegrationBranchOutcome.Built);

        // A dependent subtask's base branch is the integration branch (ResolveChildBaseBranchAsync);
        // provisioning its worktree from that base must materialize the upstream artifact on disk.
        var dependentRunId = RunId.New();
        var info = _manager.AddWorktree(repoPath, integrationBranch, dependentRunId);

        var artifactPath = Path.Combine(info.WorktreePath, "research-domain.md");
        File.Exists(artifactPath).Should().BeTrue(
            "the dependent subtask must start with the upstream subtask's committed file present");
        File.ReadAllText(artifactPath).Should().Be("coffee roasting SaaS domain research");
    }

    // (b) A file created by a child is retrievable AFTER the child worktree directory is gone.
    [Fact]
    public void ChildFile_IsRetrievableAfterWorktreeIsGone()
    {
        var repoPath = CreateTempGitRepo();

        CommitOnNewBranch(repoPath, "agentweaver/child-gone", "research-domain.md",
            "durable content survives sandbox teardown", "child produces artifact");

        // Simulate the child's ephemeral worktree/sandbox having been torn down: there is NO worktree
        // directory, only the committed branch in the shared repository.
        var content = _manager.TryReadCommittedFileContent(
            repoPath, "agentweaver/child-gone", commitHash: null, "research-domain.md", out var isBinary);

        content.Should().NotBeNull("the file must be readable from the durable git branch, not a torn-down worktree");
        isBinary.Should().BeFalse();
        content!.Content.Should().Be("durable content survives sandbox teardown");
        content.Path.Should().Be("research-domain.md");
    }

    [Fact]
    public void TryReadCommittedFileContent_ReturnsNull_ForUnknownFile()
    {
        var repoPath = CreateTempGitRepo();
        CommitOnNewBranch(repoPath, "agentweaver/child-x", "a.txt", "a", "c");

        _manager.TryReadCommittedFileContent(repoPath, "agentweaver/child-x", null, "missing.txt", out _)
            .Should().BeNull();
    }

    // (c) Changed-files reports exactly the modified set — no phantom "+0 -0" rows.
    [Fact]
    public void GetCommittedFileEntries_ReportsOnlyGenuinelyChangedFiles()
    {
        var repoPath = CreateTempGitRepo();

        // main already contains readme.txt + untouched.txt (see CreateTempGitRepo). The child branch
        // modifies ONLY readme.txt; untouched.txt must NOT appear in the changed-file set.
        CommitOnNewBranch(repoPath, "agentweaver/child-c", "readme.txt", "changed content\n", "modify one file");

        var entries = _manager.GetCommittedFileEntries(repoPath, "main", "agentweaver/child-c");

        entries.Should().ContainSingle();
        entries[0].Path.Should().Be("readme.txt");
        entries.Should().NotContain(e => e.Path == "untouched.txt");
    }

    // (c) A mode-only diff section (no line changes) must be dropped as a phantom "+0 -0" row, while
    // a genuinely modified file and an added empty file are preserved.
    [Fact]
    public void ParseUnifiedDiffEntries_DropsPhantomModeOnlyRows()
    {
        // Two sections: a real content modification (real.txt) and a mode-only change (mode.sh) that
        // carries no +/- hunk lines — exactly the "+0 -0" phantom rows from issue #197 symptom C.
        var diff =
            "diff --git a/real.txt b/real.txt\n" +
            "index 1111111..2222222 100644\n" +
            "--- a/real.txt\n" +
            "+++ b/real.txt\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old line\n" +
            "+new line\n" +
            "diff --git a/mode.sh b/mode.sh\n" +
            "old mode 100644\n" +
            "new mode 100755\n" +
            "index 3333333..3333333\n" +
            "--- a/mode.sh\n" +
            "+++ b/mode.sh\n";

        var entries = WorkspaceFileEntryParser.ParseUnifiedDiffEntries(diff);

        entries.Should().ContainSingle(e => e.Path == "real.txt");
        entries.Should().NotContain(e => e.Path == "mode.sh");
        entries.Single().AddedLines.Should().Be(1);
        entries.Single().RemovedLines.Should().Be(1);
    }

    // ── helpers (mirror IntegrationBranchBuilderTests git setup) ──────────────────────────────

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"aw-197-repo-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content\n");
        File.WriteAllText(Path.Combine(repoPath, "untouched.txt"), "never changes\n");
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
        foreach (var dir in _tempDirs)
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
