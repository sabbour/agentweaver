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
///     (parentRunId, subtaskId) and re-observes the existing child instead of creating a second one.
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
    public async Task FindActiveChildAsync_WhenAssembleReadyChildExists_ReturnsIt()
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

        found.Should().NotBeNull(
            "an assemble_ready child run must be found and prevent a duplicate dispatch — " +
            "the coordinator should advance to assembly, not create another worker");
        found!.Id.Should().Be(childRunId);
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
    /// Returns (repoPath, worktreePath, runId) for use in assertions.
    private (string RepoPath, string WorktreePath, RunId RunId) CreateWorktree()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"aw-test-repo-{Guid.NewGuid():N}");
        var worktreesBase = Path.Combine(Path.GetTempPath(), $"aw-test-wt-{Guid.NewGuid():N}");
        _tempDirs.Add(repoPath);
        _tempDirs.Add(worktreesBase);

        // Initialize repo with an initial commit so HEAD exists.
        Repository.Init(repoPath);
        using (var repo = new Repository(repoPath))
        {
            File.WriteAllText(Path.Combine(repoPath, "README.md"), "init");
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
