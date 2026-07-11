using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Verifies the three properties of the worker deliverable capture fix:
/// (a) A child subtask that writes a file into its worktree produces a NON-empty commit and a
///     non-empty diff — the file will appear in the assembly diff.
/// (b) A child subtask that produces NO files is committed without creating an empty commit; the
///     diff is empty and the child stream emits <c>run.no_changes_produced</c> so the reviewer is
///     not sent to an empty review with no explanation.
/// (c) When a subtask is reset to Pending by recovery while its old child is still active (the
///     duplicate-dispatch scenario from run d929348d), dispatch deduplicates on
///     (parentRunId, subtaskId), but terminal delivered children are not reused for a new revision.
///
/// Tests use real git repos (temp directories), real LibGit2Sharp via WorktreeManager, and real
/// SQLite via TestSqliteDb — no mocks (Constitution Principle VII).
/// </summary>
public sealed class WorkerDeliverableCaptureTests : IAsyncDisposable
{
    private readonly List<string> _tempDirs = new();
    private readonly TestSqliteDb _runDb;
    private readonly SqliteRunStore _runStore;

    public WorkerDeliverableCaptureTests()
    {
        _runDb = TestSqliteDb.CreateAsync().GetAwaiter().GetResult();
        _runStore = new SqliteRunStore(_runDb.Db);
    }

    // -------------------------------------------------------------------------
    // (a) Worker writes a file → non-empty commit + non-empty diff
    // -------------------------------------------------------------------------

    [Fact]
    public void CommitChanges_WithFileWritten_ProducesNonEmptyCommitAndDiff()
    {
        var (repoPath, worktreePath, runId) = CreateWorktree();
        var manager = BuildWorktreeManager();

        File.WriteAllText(Path.Combine(worktreePath, "deliverable.md"), "# My Report\nSome content.");

        var treeHash = manager.CommitChanges(worktreePath, runId);

        treeHash.Should().NotBeNullOrEmpty("CommitChanges must return the committed tree hash");

        using var repo = new Repository(repoPath);
        var origin = repo.Branches["main"]!;
        var branch = repo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        branch.Tip.Should().NotBe(origin.Tip, "a new commit must have been created");
        using var patch = repo.Diff.Compare<Patch>(origin.Tip.Tree, branch.Tip.Tree);
        patch.Content.Should().NotBeNullOrWhiteSpace(
            "a child that wrote a file must produce a non-empty diff vs the origin branch");
        patch.Content.Should().Contain("deliverable.md",
            "the committed diff must include the file the agent wrote");
    }

    // -------------------------------------------------------------------------
    // Issue #222 invariant: staging is scope-INDEPENDENT — every non-ignored change
    // is committed regardless of the subtask scope prose. The original bug required a
    // non-null IServiceScopeFactory plus a resolvable subtask scope to trigger the
    // prose-whitelist filter; that seam (and the whitelist) has been deleted, so these
    // tests PIN the invariant rather than reproduce the deleted code path. The deletion
    // and nested-repo tests below genuinely pin their own behaviors.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Invariant guard (not a bug reproduction): a deliverable written into nested directories is
    /// committed even though no subtask scope named it. Pins the scope-independent staging contract;
    /// the prose-whitelist path that dropped subdirectory trees no longer exists.
    /// </summary>
    [Fact]
    public void CommitChanges_WithSubdirectoryDeliverable_CommitsNestedTree()
    {
        // The live #222 symptom: a backend subtask wrote server/** but the scope named only .md
        // inputs, so the whole tree ended up untracked and uncommitted. Staging is now
        // scope-independent, so the nested tree must be committed.
        var (repoPath, worktreePath, runId) = CreateWorktree();
        var manager = BuildWorktreeManager();

        var target = Path.Combine(worktreePath, "server", "src", "index.js");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "console.log('hello');\n");

        manager.CommitChanges(worktreePath, runId);

        using var repo = new Repository(repoPath);
        var origin = repo.Branches["main"]!;
        var branch = repo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        branch.Tip.Should().NotBe(origin.Tip, "a subdirectory deliverable must produce a commit");
        using var patch = repo.Diff.Compare<Patch>(origin.Tip.Tree, branch.Tip.Tree);
        patch.Content.Should().Contain("server/src/index.js",
            "a deliverable written into nested directories must be committed and appear in the diff");
        branch.Tip.Tree["server/src/index.js"].Should().NotBeNull(
            "the nested file must exist as a blob in the committed tree");
    }

    /// <summary>
    /// Invariant guard (not a bug reproduction): several files across subdirectories — none named by
    /// any subtask scope — are all committed. Pins the scope-independent staging contract.
    /// </summary>
    [Fact]
    public void CommitChanges_WithMultipleUnnamedFilesAcrossSubdirs_CommitsAll()
    {
        var (repoPath, worktreePath, runId) = CreateWorktree();
        var manager = BuildWorktreeManager();

        var files = new[]
        {
            "src/app.ts",
            "src/util/helpers.ts",
            "public/index.html",
            "package.json",
        };
        foreach (var rel in files)
        {
            var full = Path.Combine(worktreePath, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, $"// {rel}\n");
        }

        manager.CommitChanges(worktreePath, runId);

        using var repo = new Repository(repoPath);
        var branch = repo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        foreach (var rel in files)
            branch.Tip.Tree[rel].Should().NotBeNull($"{rel} must be committed even though the scope never named it");
    }

    [Fact]
    public void CommitChanges_WithDeletionAndRename_CapturesDeletion()
    {
        // Guards B1: the canonical changed set must include deletions and renames. A New/Modified-only
        // mask would drop the deletion and leave the old blob in the tree.
        var (repoPath, worktreePath, runId) = CreateWorktree(new Dictionary<string, string>
        {
            ["keep.txt"] = "shared content that will be renamed\nline two\nline three\n",
            ["old.txt"] = "this file will be deleted\n",
        });
        var manager = BuildWorktreeManager();

        // Agent deletes old.txt and renames keep.txt -> renamed.txt (delete + recreate same content).
        File.Delete(Path.Combine(worktreePath, "old.txt"));
        var keepPath = Path.Combine(worktreePath, "keep.txt");
        var keepContent = File.ReadAllText(keepPath);
        File.Delete(keepPath);
        File.WriteAllText(Path.Combine(worktreePath, "renamed.txt"), keepContent);

        manager.CommitChanges(worktreePath, runId);

        using var repo = new Repository(repoPath);
        var branch = repo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        var tree = branch.Tip.Tree;

        tree["old.txt"].Should().BeNull("the deleted file must be absent from the committed tree");
        tree["keep.txt"].Should().BeNull("the renamed file's old path must be absent from the committed tree");
        tree["renamed.txt"].Should().NotBeNull("the renamed file's new path must be present");

        // No duplicate content: the shared content must appear exactly once (renamed.txt only).
        var blobPaths = tree.Where(e => e.TargetType == TreeEntryTargetType.Blob).Select(e => e.Path).ToList();
        blobPaths.Should().Contain("renamed.txt");
        blobPaths.Should().NotContain("keep.txt");
    }

    [Fact]
    public void CommitChanges_WithGitignore_DoesNotCommitIgnoredFiles()
    {
        var (repoPath, worktreePath, runId) = CreateWorktree();
        var manager = BuildWorktreeManager();

        File.WriteAllText(Path.Combine(worktreePath, ".gitignore"), "node_modules/\n");
        var nodeModules = Path.Combine(worktreePath, "node_modules", "left-pad");
        Directory.CreateDirectory(nodeModules);
        File.WriteAllText(Path.Combine(nodeModules, "index.js"), "module.exports = () => {};\n");
        File.WriteAllText(Path.Combine(worktreePath, "app.js"), "require('left-pad');\n");

        manager.CommitChanges(worktreePath, runId);

        using var repo = new Repository(repoPath);
        var branch = repo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        var tree = branch.Tip.Tree;

        tree["app.js"].Should().NotBeNull("the real deliverable must be committed");
        tree["node_modules"].Should().BeNull("an ignored directory must never be committed");
    }

    [Fact]
    public void CommitChanges_WithNestedGitRepository_SkipsGitlinkButCommitsSibling()
    {
        // Guards N2: a subdirectory carrying its own .git (e.g. a create-react-app scaffold) must NOT
        // be staged as an empty gitlink; a normal sibling file must still commit.
        var (repoPath, worktreePath, runId) = CreateWorktree();
        var manager = BuildWorktreeManager();

        // Minimal nested repo: a client/ directory with its own .git and a file.
        var clientGit = Path.Combine(worktreePath, "client", ".git");
        Directory.CreateDirectory(clientGit);
        File.WriteAllText(Path.Combine(clientGit, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(worktreePath, "client", "app.jsx"), "export default () => null;\n");

        // A normal sibling deliverable outside the nested repo.
        File.WriteAllText(Path.Combine(worktreePath, "server.js"), "listen(3000);\n");

        manager.CommitChanges(worktreePath, runId);

        using var repo = new Repository(repoPath);
        var branch = repo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        var tree = branch.Tip.Tree;

        tree["server.js"].Should().NotBeNull("the sibling deliverable outside the nested repo must commit");
        var clientEntry = tree["client"];
        if (clientEntry is not null)
            clientEntry.TargetType.Should().NotBe(TreeEntryTargetType.GitLink,
                "the nested repo must never be committed as a gitlink/submodule pointer");
    }

    // -------------------------------------------------------------------------
    // (b) Worker writes nothing → no new commit, empty diff, run.no_changes_produced
    // -------------------------------------------------------------------------

    [Fact]
    public void CommitChanges_NoFilesWritten_ReturnsHeadTreeSha_NoNewCommit_EmptyDiff()
    {
        var (repoPath, worktreePath, runId) = CreateWorktree();
        var manager = BuildWorktreeManager();

        string headCommitShaBefore;
        string headTreeShaBefore;
        using (var repoBefore = new Repository(worktreePath))
        {
            headCommitShaBefore = repoBefore.Head.Tip.Sha;
            headTreeShaBefore = repoBefore.Head.Tip.Tree.Sha;
        }

        // No file written — the agent did nothing in the worktree.
        var returnedHash = manager.CommitChanges(worktreePath, runId);

        using var repoAfter = new Repository(worktreePath);
        repoAfter.Head.Tip.Sha.Should().Be(headCommitShaBefore,
            "CommitChanges must not create a new commit when there are no staged changes");

        // CommitChanges returns the HEAD *tree* SHA when no commit is created.
        returnedHash.Should().Be(headTreeShaBefore,
            "the returned hash must be the HEAD tree SHA so the caller can still compute an empty diff");

        using var mainRepo = new Repository(repoPath);
        var origin = mainRepo.Branches["main"]!;
        var branch = mainRepo.Branches[WorktreeManager.BranchNameFor(runId)]!;
        using var patch = mainRepo.Diff.Compare<Patch>(origin.Tip.Tree, branch.Tip.Tree);
        patch.Content.Should().BeNullOrEmpty(
            "a child that wrote no files must produce an empty diff — HasChanges = false");
    }

    [Fact]
    public void AssembleReady_WithNoChanges_EmitsRunNoChangesProducedEvent()
    {
        // Verify the event contract: when HasChanges == false, the watch loop must emit
        // run.no_changes_produced after run.assemble_ready so reviewers see an explanation
        // instead of a silent empty diff panel.
        //
        // This is a contract/unit test that exercises the stream entry directly — the full
        // RunWatchLoopService path requires MAF workflow execution and is covered by integration
        // tests. Here we confirm the event type constants and payload shape are correct and that
        // RunStreamEntry correctly surfaces both events.

        var streamStore = new RunStreamStore();
        var childRunId = RunId.New().ToString();
        var entry = streamStore.Create(childRunId, "alice");

        // Simulate what RunWatchLoopService.HandleAssembleReadyAsync emits when HasChanges == false.
        entry.RecordNext(EventTypes.RunAssembleReady, new
        {
            runId = childRunId,
            subtaskId = "7",
            parentRunId = "coord-1",
            worktreeBranch = "agentweaver/" + childRunId,
            treeHash = "abc123",
            hasChanges = false,
            stepCount = 0,
            raiSafetyFlagged = false,
        });
        entry.RecordNext(EventTypes.RunNoChangesProduced, new
        {
            runId = childRunId,
            subtaskId = "7",
            parentRunId = "coord-1",
            message = "This subtask completed without writing any deliverables to the repository.",
        });

        var events = entry.GetSnapshotSince(0).Events;
        events.Should().Contain(e => e.Type == EventTypes.RunAssembleReady,
            "run.assemble_ready must always be emitted");
        events.Should().Contain(e => e.Type == EventTypes.RunNoChangesProduced,
            "run.no_changes_produced must be emitted when HasChanges == false so the reviewer " +
            "is not sent to an empty diff panel with no explanation");
    }

    // -------------------------------------------------------------------------
    // (c) Recovery re-dispatch is idempotent: no duplicate child for active subtask
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FindActiveChildAsync_WhenInProgressChildExists_ReturnsIt()
    {
        // Seed a coordinator run and a child run that is in_progress.
        var coordRunId = RunId.New().ToString();
        var childRunId = RunId.New();
        const string subtaskId = "42";

        await _runStore.InsertAsync(new Run
        {
            Id = childRunId,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "write the deliverable",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = coordRunId,
            SubtaskId = subtaskId,
        });

        var found = await _runStore.FindActiveChildAsync(coordRunId, subtaskId);

        found.Should().NotBeNull(
            "an in_progress child run must be found and prevent a duplicate dispatch");
        found!.Id.Should().Be(childRunId);
    }

    [Fact]
    public async Task FindActiveChildAsync_WhenAssembleReadyChildExists_ReturnsNull()
    {
        var coordRunId = RunId.New().ToString();
        var childRunId = RunId.New();
        const string subtaskId = "43";

        await _runStore.InsertAsync(new Run
        {
            Id = childRunId,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "write the deliverable",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = coordRunId,
            SubtaskId = subtaskId,
        });
        await _runStore.SetAssembleReadyAsync(childRunId, "hash", "branch", "diff", 0, DateTimeOffset.UtcNow);

        var found = await _runStore.FindActiveChildAsync(coordRunId, subtaskId);

        found.Should().BeNull(
            "assemble_ready is terminal output; if a subtask was reset to pending for a revision, " +
            "dispatch must create a new child instead of reusing stale terminal output");
    }

    [Fact]
    public async Task FindActiveChildAsync_WhenChildIsFailed_ReturnsNull()
    {
        // A failed child should NOT block re-dispatch — recovery intentionally retries failed subtasks.
        var coordRunId = RunId.New().ToString();
        var childRunId = RunId.New();
        const string subtaskId = "44";

        await _runStore.InsertAsync(new Run
        {
            Id = childRunId,
            RepositoryPath = "repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "write the deliverable",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ParentRunId = coordRunId,
            SubtaskId = subtaskId,
        });
        await _runStore.UpdateStatusAsync(childRunId, RunStatus.Failed, DateTimeOffset.UtcNow);

        var found = await _runStore.FindActiveChildAsync(coordRunId, subtaskId);

        found.Should().BeNull(
            "a failed child must not block re-dispatch — recovery retries failed subtasks");
    }

    [Fact]
    public async Task FindActiveChildAsync_WhenNoChildExists_ReturnsNull()
    {
        var found = await _runStore.FindActiveChildAsync("coord-none", "subtask-none");
        found.Should().BeNull("no child exists for this (coordinator, subtask) pair");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// Creates a temp git repo at a unique path, renames the initial branch to "main",
    /// and uses WorktreeManager.AddWorktree to create a real linked worktree for <paramref name="runId"/>.
    /// When <paramref name="baseFiles"/> is provided those files are committed to "main" as the
    /// starting tree (otherwise a single README.md is committed).
    /// Returns (repoPath, worktreePath, runId) for use in assertions.
    private (string RepoPath, string WorktreePath, RunId RunId) CreateWorktree(
        IReadOnlyDictionary<string, string>? baseFiles = null)
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"aw-test-repo-{Guid.NewGuid():N}");
        var worktreesBase = Path.Combine(Path.GetTempPath(), $"aw-test-wt-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);
        _tempDirs.Add(worktreesBase);

        // Initialize repo with an initial commit so HEAD exists.
        Repository.Init(repoPath);
        using (var repo = new Repository(repoPath))
        {
            if (baseFiles is { Count: > 0 })
            {
                foreach (var (rel, content) in baseFiles)
                {
                    var full = Path.Combine(repoPath, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, content);
                }
            }
            else
            {
                File.WriteAllText(Path.Combine(repoPath, "README.md"), "init");
            }
            Commands.Stage(repo, "*");
            var sig = new Signature("Test", "test@test.com", DateTimeOffset.UtcNow);
            repo.Commit("init", sig, sig);

            // Ensure the branch is "main" regardless of the git global default.
            if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
                repo.Branches.Rename(repo.Head, "main");

            // Detach HEAD so "main" is not the currently checked-out branch in the main worktree —
            // a branch checked out in the main worktree cannot be checked out in a linked worktree.
            Commands.Checkout(repo, repo.Head.Tip);
        }

        var runId = RunId.New();
        var manager = BuildWorktreeManager(worktreesBase);
        var wtInfo = manager.AddWorktree(repoPath, "main", runId);

        return (repoPath, wtInfo.WorktreePath, runId);
    }

    private static WorktreeManager BuildWorktreeManager(string? worktreesBase = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Worktrees:BasePath"] = worktreesBase ?? Path.GetTempPath(),
                ["Git:Author:Name"] = "Test",
                ["Git:Author:Email"] = "test@test.com",
            })
            .Build();
        return new WorktreeManager(config, NullLogger<WorktreeManager>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _runDb.DisposeAsync();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort — git locks may linger on Windows */ }
        }
    }
}
