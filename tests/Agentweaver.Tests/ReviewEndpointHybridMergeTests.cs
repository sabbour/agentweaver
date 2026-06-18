using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Git;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Integration tests for the hybrid working-tree-aware merge behavior (Story 4).
///
/// Exercises the three-outcome trichotomy (Merged / Blocked / Conflict) for both the
/// "checked-out" path (HEAD == originatingBranch, hard-reset applied to working tree)
/// and the "ref-only" path (HEAD on a different branch, only the ref advanced).
/// Also covers retriability, idempotency, safe-string guarantees, and
/// RunOrchestrator restart recovery.
///
/// No mocks — every test uses a real in-process server, a real SQLite database,
/// and real git repositories constructed with LibGit2Sharp.
/// </summary>
public sealed class ReviewEndpointHybridMergeTests : IClassFixture<ReviewWebApplicationFactory>, IDisposable
{
    private readonly ReviewWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;
    private readonly List<string> _tempRepoDirs = new();

    public ReviewEndpointHybridMergeTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;
        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ReviewWebApplicationFactory.OwnerApiKey);
    }

    public void Dispose()
    {
        _ownerClient.Dispose();
        foreach (var dir in _tempRepoDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort; git packs may still be locked */ }
        }
    }

    // =========================================================================
    // HM-1 — Checked-out branch, clean working tree, fast-forward → Merged.
    // The hard reset performed on the checked-out path must update the main
    // working tree files so the committed content is visible on disk.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_CleanFastForward_Merged_WorkingTreeUpdated()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        using (var repo = new Repository(repoPath))
            repo.Head.FriendlyName.Should().Be("main",
                "sanity: main must be checked out for this test to exercise the working-tree path");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:",
            "a successful merge result must begin with 'merged:' followed by the commit SHA");

        // The hard reset must have updated the main working tree on disk.
        File.Exists(Path.Combine(repoPath, "agent-file.txt")).Should().BeTrue(
            "working-tree reset must update the main working tree so the agent's file is visible on disk");

        using var repoAfter = new Repository(repoPath);
        repoAfter.Head.FriendlyName.Should().Be("main",
            "HEAD must remain on main after the fast-forward merge");
        repoAfter.Branches["main"]!.Tip.Tree["agent-file.txt"].Should().NotBeNull(
            "the fast-forwarded main branch must contain the agent's committed file");
    }

    // =========================================================================
    // HM-2 — Checked-out branch, modified tracked file → ref-only fallback;
    // the merge succeeds without touching the working tree. The user's local
    // changes are preserved; the branch ref advances.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_ModifiedTrackedFile_FallsBackToRefOnly_Merges()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        // Dirty the main working tree: modify the tracked readme.txt without staging.
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "locally modified content");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        // With the fix, a dirty working tree falls back to ref-only merge instead of blocking.
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a dirty working tree must fall back to ref-only merge (not 409-block) because MergeRefOnly does not touch the working tree");

        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:");

        // The originating branch ref must have advanced to include the agent's file.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches["main"]!.Tip.Tree["agent-file.txt"].Should().NotBeNull(
            "the ref-only fallback must advance the main branch ref to include the agent's file");

        // The dirty file must NOT have been overwritten — no hard reset occurred.
        File.ReadAllText(Path.Combine(repoPath, "readme.txt")).Should().Be("locally modified content",
            "the ref-only fallback must not touch the main working tree — the user's local changes are preserved");

        // The run must be in merged status.
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runAfterMerge = await runStore.GetAsync(run.Id);
        runAfterMerge!.Status.Should().Be(RunStatus.Merged,
            "the run must transition to merged after the ref-only fallback merge");
    }

    // =========================================================================
    // HM-3 — Checked-out branch, untracked file collides with a path added by
    // the merge → ref-only fallback; merge succeeds, untracked file preserved.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_UntrackedFileCollides_FallsBackToRefOnly_Merges()
    {
        // The agent adds "collision.txt" — a path that does not yet exist on main.
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "collision.txt"), "agent added this"));

        // Place an untracked file at the same path in the main working tree.
        File.WriteAllText(Path.Combine(repoPath, "collision.txt"), "local untracked version");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        // With the fix, untracked collision falls back to ref-only merge (not 409-block).
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an untracked collision must fall back to ref-only merge since MergeRefOnly does not overwrite untracked files");

        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:");

        // The untracked file must be preserved — no hard reset occurred.
        File.ReadAllText(Path.Combine(repoPath, "collision.txt")).Should().Be("local untracked version",
            "the ref-only fallback must not touch the untracked file in the main working tree");

        // The branch ref must have advanced to include the agent's version of the file.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches["main"]!.Tip.Tree["collision.txt"].Should().NotBeNull(
            "the ref-only fallback must advance the main branch ref to include the agent's file");
    }

    // =========================================================================
    // HM-4 — Checked-out branch, untracked file does NOT collide with any path
    // added by the merge → must succeed. Non-colliding untracked files must not
    // block the merge.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_UntrackedNonCollidingFile_Merges()
    {
        // The agent adds "agent-file.txt"; the untracked file is "irrelevant-local.txt".
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        // Untracked file whose name does NOT match any path the agent added.
        File.WriteAllText(Path.Combine(repoPath, "irrelevant-local.txt"), "scratch content");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a non-colliding untracked file must not block the merge");
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");
    }

    // =========================================================================
    // HM-5 — Checked-out branch, in-progress merge (MERGE_HEAD present) → 409
    // retriable. The sequencer-detection guard must fire.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_MergeInProgress_Blocks()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "content"));

        // Simulate an in-progress git merge by creating the MERGE_HEAD sentinel.
        var mergeHeadPath = Path.Combine(repoPath, ".git", "MERGE_HEAD");
        File.WriteAllText(mergeHeadPath, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n");
        try
        {
            var response = await _ownerClient.PostAsJsonAsync(
                $"/api/runs/{run.Id}/review", new { approved = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "an in-progress merge state must block the agentweaver merge (retriable 409)");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("status").GetString().Should().Be("awaiting_review");
            body.GetProperty("error").GetString().Should().Contain("a git operation is in progress",
                "the blocked reason must identify the in-progress sequencer operation");
        }
        finally
        {
            if (File.Exists(mergeHeadPath))
                File.Delete(mergeHeadPath);
        }
    }

    // =========================================================================
    // HM-5b — Checked-out branch, in-progress revert (REVERT_HEAD present) → 409
    // REVERT_HEAD must be treated the same as MERGE_HEAD; it must NOT fall through
    // to the ref-only path.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_RevertInProgress_Blocks()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "content"));

        // Simulate an in-progress git revert by creating the REVERT_HEAD sentinel.
        var revertHeadPath = Path.Combine(repoPath, ".git", "REVERT_HEAD");
        File.WriteAllText(revertHeadPath, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n");
        try
        {
            var response = await _ownerClient.PostAsJsonAsync(
                $"/api/runs/{run.Id}/review", new { approved = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "an in-progress revert (REVERT_HEAD) must block the agentweaver merge (retriable 409)");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("status").GetString().Should().Be("awaiting_review");
            body.GetProperty("error").GetString().Should().Contain("a git operation is in progress",
                "the blocked reason must identify the in-progress revert operation");
        }
        finally
        {
            if (File.Exists(revertHeadPath))
                File.Delete(revertHeadPath);
        }
    }

    // =========================================================================
    // =========================================================================
    // HM-5c — Checked-out branch, conflicted index (no sequencer sentinel) → 409.
    // Conflicted index entries must block outright (not fall through to ref-only)
    // because advancing the branch ref beneath unresolved conflicts is unsafe.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_ConflictedIndex_Blocks()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "content"));

        // Build a conflicted index state in the main repo:
        // 1. Create two divergent branches with conflicting versions of conflict.txt.
        // 2. Merge them — LibGit2Sharp leaves MERGE_HEAD + conflicted index entries.
        // 3. Delete MERGE_HEAD so only the conflicted index entries remain; this
        //    isolates the conflicted-index check from the sequencer-sentinel check.
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        using (var repo = new Repository(repoPath))
        {
            // Create a feature branch and commit a conflicting file on it.
            var feature = repo.CreateBranch("_conflict-feature");
            Commands.Checkout(repo, feature);
            File.WriteAllText(Path.Combine(repoPath, "conflict.txt"), "feature version\n");
            Commands.Stage(repo, "conflict.txt");
            repo.Commit("Feature: add conflict.txt", sig, sig);

            // Back on main, add a different version of the same file.
            Commands.Checkout(repo, repo.Branches["main"]);
            File.WriteAllText(Path.Combine(repoPath, "conflict.txt"), "main version\n");
            Commands.Stage(repo, "conflict.txt");
            repo.Commit("Main: add conflict.txt", sig, sig);

            // Merge feature → main. Because both branches touched conflict.txt,
            // LibGit2Sharp leaves the index in a conflicted state and creates MERGE_HEAD.
            repo.Merge(repo.Branches["_conflict-feature"], sig, new MergeOptions());
        }

        // Delete MERGE_HEAD so only the conflicted index entries remain.
        var mergeHeadPath = Path.Combine(repoPath, ".git", "MERGE_HEAD");
        if (File.Exists(mergeHeadPath))
            File.Delete(mergeHeadPath);

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a conflicted index must block the agentweaver merge (retriable 409), not fall through to ref-only");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("awaiting_review");
        body.GetProperty("error").GetString().Should().Contain("conflicted",
            "the blocked reason must identify the conflicted index entries");
    }

    // =========================================================================
    // HM-6 — Originating branch NOT checked out (HEAD on a different branch) →
    // Merged via the ref-only path. Main working tree files must NOT be updated
    // because no hard reset is performed.
    // =========================================================================
    [Fact]
    public async Task Approve_BranchNotCheckedOut_Merged_RefOnly_WorkingTreeUntouched()
    {
        // SetupRunAwaitingReviewAsync uses CreateTempGitRepo which checks out
        // _workspace, leaving main as a non-HEAD branch.
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        using (var repo = new Repository(repoPath))
            repo.Head.FriendlyName.Should().NotBe("main",
                "sanity: main must NOT be checked out for this test to exercise the ref-only path");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:");

        // The main branch ref must have advanced to include the agent's file.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches["main"]!.Tip.Tree["agent-file.txt"].Should().NotBeNull(
            "the ref-only merge must advance the main branch ref to include the agent's file");

        // The main working tree (checked out on _workspace) must NOT have been updated.
        File.Exists(Path.Combine(repoPath, "agent-file.txt")).Should().BeFalse(
            "the ref-only path must not modify the main working tree — no hard reset is performed");
    }

    // =========================================================================
    // HM-7 — Diverged, conflicting branch → 200 merge_failed; worktree branch
    // and directory are removed (MergeFailed is cleanly terminal; conflict info
    // is stored in merge_conflicts column).
    // =========================================================================
    [Fact]
    public async Task Approve_DivergentConflict_MergeFailed_WorktreeBranchAndDirectoryRemoved()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(
            dir => File.WriteAllText(Path.Combine(dir, "conflict.txt"), "agent version"));

        // Advance the originating branch with a conflicting change on the same file.
        AdvanceBranchWithContent(
            repoPath, "main", "conflict.txt", "human version", "Human advance with conflict");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a terminal merge failure must return HTTP 200 (not 409) with status=merge_failed");
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merge_failed");
        result.MergeResult.Should().StartWith("conflict:");

        // The worktree DIRECTORY must be removed (MergeFailed is terminal; conflict info is in the DB).
        Directory.Exists(run.WorktreePath).Should().BeFalse(
            "the worktree directory must be removed when the merge fails — conflict info is in merge_conflicts");
    }

    // =========================================================================
    // HM-8 — Idempotency: after a successful merge, a second approve returns 200
    // merged without crashing or attempting a double worktree removal.
    // =========================================================================
    [Fact]
    public async Task Approve_AfterMerged_Idempotent_NoDoubleWorktreeRemoval()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "content"));

        // First approve — transitions to merged, removes the worktree branch.
        var firstResponse = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });
        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstResult = await firstResponse.Content.ReadFromJsonAsync<ReviewResponse>();
        firstResult!.Status.Should().Be("merged");

        // After a successful merge the worktree branch must be deleted.
        using (var repo = new Repository(repoPath))
            repo.Branches[run.WorktreeBranch].Should().BeNull(
                "the worktree branch is deleted on a successful merge");

        // Second approve on the same already-merged run must be idempotent.
        var secondResponse = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "approving an already-merged run must return 200 — no crash, no double worktree-remove");
        var secondResult = await secondResponse.Content.ReadFromJsonAsync<ReviewResponse>();
        secondResult!.Status.Should().Be("merged");
        secondResult.MergeResult.Should().StartWith("merged:");
    }

    // =========================================================================
    // HM-12 — RemoveWorktree teardown order: the branch must be deleted without
    // a "Cannot delete branch as it is the current HEAD of a linked repository"
    // error. Exercises the directory-delete-first + separate-handle fix.
    // =========================================================================
    [Fact]
    public async Task RemoveWorktree_DeletesBranchWithoutHeadWarning()
    {
        // Create a run with a real worktree + branch through the full lifecycle.
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-output.txt"), "teardown test"));

        // Sanity: the worktree branch exists before merge.
        using (var repo = new Repository(repoPath))
            repo.Branches[run.WorktreeBranch].Should().NotBeNull(
                "sanity: worktree branch must exist before the merge");

        // Approve → triggers merge + RemoveWorktree (the code under test).
        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");

        // The worktree directory must be gone.
        Directory.Exists(run.WorktreePath!).Should().BeFalse(
            "RemoveWorktree must delete the physical worktree directory");

        // The worktree branch must be deleted — this is the core assertion.
        // Before the fix, this would throw or leave the branch behind.
        using (var repoAfter = new Repository(repoPath))
        {
            repoAfter.Branches[run.WorktreeBranch].Should().BeNull(
                "the worktree branch must be removed without a 'current HEAD of a linked repository' error");

            // The worktree admin entry must be pruned.
            var worktreeName = Path.GetFileName(
                run.WorktreePath!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            repoAfter.Worktrees[worktreeName].Should().BeNull(
                "the worktree admin entry must be pruned after removal");
        }
    }

    // =========================================================================
    // HM-9 — Tree-hash mismatch (run changed after the review was requested) →
    // terminal merge_failed with conflict:... result.
    // =========================================================================
    [Fact]
    public async Task Approve_TreeHashMismatch_TerminalMergeFailed()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(
            dir => File.WriteAllText(Path.Combine(dir, "original.txt"), "approved content"));

        // Make an additional commit in the worktree branch AFTER the tree hash was
        // recorded, simulating a run that changed after the review was requested.
        using (var worktreeRepo = new Repository(run.WorktreePath))
        {
            File.WriteAllText(
                Path.Combine(run.WorktreePath!, "extra-change.txt"), "unsanctioned change");
            Commands.Stage(worktreeRepo, "*");
            var sig = new Signature("Tamper", "tamper@test", DateTimeOffset.UtcNow);
            worktreeRepo.Commit("Extra unsanctioned commit added after review was requested",
                sig, sig, new CommitOptions { AllowEmptyCommit = false });
        }

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        // Issue 3: the pre-merge tree-hash validation now catches the mismatch before
        // attempting the merge and returns 409 (do not attempt merge on tampered worktree).
        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a tree-hash mismatch must be caught before merge and returned as 409");
    }

    // =========================================================================
    // HM-11 — Restart recovery: a run left in Merging is reverted to
    // AwaitingReview by RunOrchestrator.RestartRecoveryAsync, and a fresh
    // stream entry is re-created with a synthetic review.requested event.
    // Updated (B1 FIX 1): the run must have all merge prerequisites (WorktreePath,
    // WorktreeBranch, TreeHash) and a matching worktree tree hash for the
    // synthetic review.requested to be emitted instead of failing the run.
    // =========================================================================
    [Fact]
    public async Task RestartRecovery_RevertsInterruptedMerge_AndRecreatesStreamEntry()
    {
        var runStore     = _factory.Services.GetRequiredService<SqliteRunStore>();
        var restartSvc   = _factory.Services.GetRequiredService<WorkflowRestartService>();
        var streamStore  = _factory.Services.GetRequiredService<RunStreamStore>();
        var worktreeManager = _factory.Services.GetRequiredService<WorktreeManager>();

        // Use a real git repo + worktree so WorktreeExists returns true and
        // GetTreeHash can compute the real tree SHA for validation.
        var repoPath = CreateTempGitRepo();
        var runId    = RunId.New();
        var worktreeInfo = worktreeManager.AddWorktree(repoPath, "main", runId);
        _tempRepoDirs.Add(worktreeInfo.WorktreePath);

        // Commit changes in the worktree so GetTreeHash returns a real SHA.
        File.WriteAllText(Path.Combine(worktreeInfo.WorktreePath, "recovery-file.txt"), "recovery content");
        var treeHash = worktreeManager.CommitChanges(worktreeInfo.WorktreePath, runId);
        var diff     = worktreeManager.GetDiff(repoPath, "main", worktreeInfo.BranchName);

        // Simulate a run that was interrupted while in the Merging state
        // (CAS succeeded but the process died before MergeWorktree completed).
        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = repoPath,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "recovery test task",
            SubmittingUser    = ReviewWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            WorktreePath      = worktreeInfo.WorktreePath,
            WorktreeBranch    = worktreeInfo.BranchName,
        };
        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, 0);

        var casSucceeded = await runStore.TryStartMergingAsync(runId);
        casSucceeded.Should().BeTrue("setup: run must advance from awaiting_review to merging");

        var runBeforeRecovery = await runStore.GetAsync(runId);
        runBeforeRecovery!.Status.Should().Be(RunStatus.Merging);

        // Trigger the restart-recovery path.
        await restartSvc.RecoverAsync(CancellationToken.None);

        // The run must be reverted to awaiting_review (Merging → AwaitingReview revert)
        // and then processed as a no-checkpoint AwaitingReview run.
        var runAfterRecovery = await runStore.GetAsync(runId);
        runAfterRecovery!.Status.Should().Be(RunStatus.AwaitingReview,
            "restart recovery must revert any run stuck in merging back to awaiting_review");

        // A stream entry must be re-created for the recovered run.
        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull(
            "restart recovery must re-create an in-memory stream entry for the recovered awaiting_review run");
        entry!.IsAwaitingReview.Should().BeTrue(
            "the re-created stream entry must be marked as awaiting_review so it is not evicted");

        // A synthetic review.requested must be emitted so SSE clients unblock (B1).
        var snapshot = entry.GetSnapshotSince(0);
        snapshot.Events.Should().Contain(e => e.Type == EventTypes.ReviewRequested,
            "synthetic review.requested must be emitted for the recovered run so SSE clients can show the review UI");
    }

    // =========================================================================
    // HM-12 — Safe-string: Blocked 409 error messages and Conflict merge_result
    // strings must never expose absolute filesystem paths or raw file content.
    // =========================================================================
    [Fact]
    public async Task BlockedAndConflict_Responses_NeverExposePathsOrFileContent()
    {
        // --- Part A: blocked 409 from an in-progress merge (MERGE_HEAD) ---
        // A sequencer in progress is the remaining case that still returns 409.
        var (blockedRun, blockedRepoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        // Simulate an in-progress git merge so the sequencer check fires.
        var mergeHeadPath = Path.Combine(blockedRepoPath, ".git", "MERGE_HEAD");
        File.WriteAllText(mergeHeadPath, "deadbeefdeadbeefdeadbeefdeadbeefdeadbeef\n");
        try
        {
            var blockedResponse = await _ownerClient.PostAsJsonAsync(
                $"/api/runs/{blockedRun.Id}/review", new { approved = true });

            blockedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
            var blockedBody = await blockedResponse.Content.ReadAsStringAsync();

            blockedBody.Should().NotContain(blockedRepoPath,
                "the 409 error must not expose absolute repository filesystem paths");
        }
        finally
        {
            if (File.Exists(mergeHeadPath))
                File.Delete(mergeHeadPath);
        }

        // --- Part B: terminal conflict merge_result ---
        var (conflictRun, conflictRepoPath) = await SetupRunAwaitingReviewAsync(
            dir => File.WriteAllText(Path.Combine(dir, "c.txt"), "AGENT SECRET CONTENT"));

        AdvanceBranchWithContent(
            conflictRepoPath, "main", "c.txt", "HUMAN SECRET CONTENT",
            "Human conflicting advance");

        var conflictResponse = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{conflictRun.Id}/review", new { approved = true });

        conflictResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var conflictBody = await conflictResponse.Content.ReadAsStringAsync();

        conflictBody.Should().NotContain(conflictRepoPath,
            "the merge_result must not expose absolute repository filesystem paths");
        conflictBody.Should().NotContain("AGENT SECRET CONTENT",
            "the merge_result must not expose raw agent file content");
        conflictBody.Should().NotContain("HUMAN SECRET CONTENT",
            "the merge_result must not expose raw human file content");
    }

    // =========================================================================
    // HM-13 — Platform-conditional HEAD compare: on Windows a case-variant
    // checked-out branch (Main vs main) must take the checked-out (hard-reset)
    // path so merged files land in the working tree.
    // =========================================================================
    [Fact]
    public async Task Approve_CaseVariantCheckedOutBranch_Windows_TakesCheckedOutPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            // On Linux/macOS branch names are case-sensitive; this test validates
            // the Windows-specific OrdinalIgnoreCase comparison.
            return;
        }

        // Create a repo where HEAD is on "Main" (capital M). The originating branch
        // is recorded as "main" (lowercase) to exercise a genuine case-variant scenario.
        // With Ordinal comparison these would NOT match → ref-only path → no working tree update.
        // With OrdinalIgnoreCase (the fix) they DO match → checked-out path → hard reset.
        var repoPath = Path.Combine(
            Path.GetTempPath(), $"agentweaver-test-case-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using (var repo = new Repository(repoPath))
        {
            File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
            Commands.Stage(repo, "*");
            var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            repo.Commit("Initial commit", sig, sig);

            // Rename HEAD to "Main" (uppercase M).
            repo.Branches.Rename(repo.Head, "Main");
        }

        // Setup a run targeting originatingBranch = "main" (lowercase) — different case from HEAD.
        var runId = RunId.New();
        var worktreeManager = _factory.Services.GetRequiredService<WorktreeManager>();
        // AddWorktree looks up the branch via repo.Branches which is case-insensitive on Windows,
        // so "main" resolves to the existing "Main" ref.
        var worktreeInfo = worktreeManager.AddWorktree(repoPath, "main", runId);

        File.WriteAllText(Path.Combine(worktreeInfo.WorktreePath, "agent-file.txt"), "agent content");
        var treeHash = worktreeManager.CommitChanges(worktreeInfo.WorktreePath, runId);
        var diff = worktreeManager.GetDiff(repoPath, "main", worktreeInfo.BranchName);

        var run = new Run
        {
            Id = runId,
            RepositoryPath = repoPath,
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "case-variant checked-out test",
            SubmittingUser = ReviewWebApplicationFactory.OwnerUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            WorktreePath = worktreeInfo.WorktreePath,
            WorktreeBranch = worktreeInfo.BranchName,
        };

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();

        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount: 0);

        var entry = streamStore.Create(runId.ToString(), ReviewWebApplicationFactory.OwnerUser);
        entry.MarkAwaitingReview();
        entry.Record(new RunEvent(1, EventTypes.ReviewRequested, new { tree_hash = treeHash }));

        // Approve — should take the checked-out path and hard-reset the working tree.
        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{runId}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");

        // The hard reset must have updated the main working tree on disk.
        // With the old Ordinal comparison, HEAD "Main" != originating "main" →
        // ref-only path → this file would NOT appear in the working tree.
        File.Exists(Path.Combine(repoPath, "agent-file.txt")).Should().BeTrue(
            "case-variant HEAD (Main vs main) on Windows must take the checked-out path " +
            "and update the working tree via hard reset");
        File.ReadAllText(Path.Combine(repoPath, "agent-file.txt")).Should().Be("agent content");
    }

    // =========================================================================
    // HM-10 — GetTreeHash returns null (unreadable / non-git worktree) at
    // approve time → fail closed with 409; run remains AwaitingReview, no
    // merge is attempted. Exercises the security fix for the fail-open bug
    // where a null treeHash previously skipped hash validation entirely.
    // =========================================================================
    [Fact]
    public async Task Approve_NullTreeHash_FailsClosed_Returns409_NoMerge()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        // Corrupt the worktree's git link so GetTreeHash returns null.
        // The worktree directory still exists (WorktreeExists passes), but the
        // .git file is removed so new Repository(worktreePath) throws and returns null.
        var gitFile = Path.Combine(run.WorktreePath!, ".git");
        File.Delete(gitFile);

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "an unreadable worktree (GetTreeHash=null) must fail closed with 409, not proceed to merge");

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runAfterApprove = await runStore.GetAsync(run.Id);
        runAfterApprove!.Status.Should().Be(RunStatus.AwaitingReview,
            "the run must remain in awaiting_review when the worktree tree hash cannot be verified");
    }

    // =========================================================================
    // HM-14 — Dirty working tree fallback emits merge.completed with
    // merge_mode = "ref-only" in the SSE event payload so the UI can hint
    // the user to run `git pull`.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_DirtyWorkingTree_MergeCompletedEvent_HasRefOnlyMergeMode()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        // Dirty the working tree to trigger the ref-only fallback.
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "locally modified content");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");

        // Inspect the SSE stream entry for a merge.completed event with merge_mode = "ref-only".
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var streamEntry = streamStore.Get(run.Id.ToString());
        streamEntry.Should().NotBeNull();

        var snapshot = streamEntry!.GetSnapshotSince(0);
        var mergeCompletedEvent = snapshot.Events.FirstOrDefault(e => e.Type == EventTypes.MergeCompleted);
        mergeCompletedEvent.Should().NotBeNull("a merge.completed event must be emitted after the ref-only fallback merge");

        var payloadJson = JsonSerializer.Serialize(mergeCompletedEvent!.Payload);
        payloadJson.Should().Contain("\"merge_mode\"",
            "the merge.completed payload must include a merge_mode field");
        payloadJson.Should().Contain("ref-only",
            "merge_mode must be 'ref-only' when the dirty working tree fallback was used");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a run in AwaitingReview status with a real git repo where the
    /// originating branch ("main") IS the currently checked-out HEAD branch.
    /// This exercises the <c>MergeCheckedOut</c> code path in WorktreeManager.
    /// </summary>
    private async Task<(Run Run, string RepoPath)> SetupRunAwaitingReviewWithMainCheckedOutAsync(
        Action<string>? worktreeCustomizer = null)
    {
        var repoPath = CreateTempGitRepoWithMainCheckedOut();
        var runId    = RunId.New();

        var worktreeManager = _factory.Services.GetRequiredService<WorktreeManager>();
        var worktreeInfo    = worktreeManager.AddWorktree(repoPath, "main", runId);

        worktreeCustomizer?.Invoke(worktreeInfo.WorktreePath);

        var treeHash = worktreeManager.CommitChanges(worktreeInfo.WorktreePath, runId);
        var diff     = worktreeManager.GetDiff(repoPath, "main", worktreeInfo.BranchName);

        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = repoPath,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "hybrid merge test task",
            SubmittingUser    = ReviewWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            WorktreePath      = worktreeInfo.WorktreePath,
            WorktreeBranch    = worktreeInfo.BranchName,
        };

        var runStore    = _factory.Services.GetRequiredService<SqliteRunStore>();
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();

        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount: 0);

        var entry = streamStore.Create(runId.ToString(), ReviewWebApplicationFactory.OwnerUser);
        entry.MarkAwaitingReview();
        entry.Record(new RunEvent(1, EventTypes.ReviewRequested, new { tree_hash = treeHash }));

        return (run with
        {
            Status    = RunStatus.AwaitingReview,
            TreeHash  = treeHash,
            Diff      = diff,
            StepCount = 0,
        }, repoPath);
    }

    /// <summary>
    /// Creates a run in AwaitingReview status with a real git repo where the
    /// originating branch ("main") is NOT the currently checked-out branch
    /// (HEAD is on "_workspace"). This exercises the <c>MergeRefOnly</c> path.
    /// </summary>
    private async Task<(Run Run, string RepoPath)> SetupRunAwaitingReviewAsync(
        Action<string>? worktreeCustomizer = null)
    {
        var repoPath = CreateTempGitRepo();
        var runId    = RunId.New();

        var worktreeManager = _factory.Services.GetRequiredService<WorktreeManager>();
        var worktreeInfo    = worktreeManager.AddWorktree(repoPath, "main", runId);

        worktreeCustomizer?.Invoke(worktreeInfo.WorktreePath);

        var treeHash = worktreeManager.CommitChanges(worktreeInfo.WorktreePath, runId);
        var diff     = worktreeManager.GetDiff(repoPath, "main", worktreeInfo.BranchName);

        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = repoPath,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "test task",
            SubmittingUser    = ReviewWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            WorktreePath      = worktreeInfo.WorktreePath,
            WorktreeBranch    = worktreeInfo.BranchName,
        };

        var runStore    = _factory.Services.GetRequiredService<SqliteRunStore>();
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();

        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount: 0);

        var entry = streamStore.Create(runId.ToString(), ReviewWebApplicationFactory.OwnerUser);
        entry.MarkAwaitingReview();
        entry.Record(new RunEvent(1, EventTypes.ReviewRequested, new { tree_hash = treeHash }));

        return (run with
        {
            Status    = RunStatus.AwaitingReview,
            TreeHash  = treeHash,
            Diff      = diff,
            StepCount = 0,
        }, repoPath);
    }

    /// <summary>
    /// Creates a repository where the originating branch ("main") is the currently
    /// checked-out HEAD branch. No _workspace branch is created.
    /// HEAD stays on "main" so WorktreeManager.MergeWorktree takes the checked-out path.
    /// </summary>
    private string CreateTempGitRepoWithMainCheckedOut()
    {
        var repoPath = Path.Combine(
            Path.GetTempPath(), $"agentweaver-test-checked-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        // Intentionally do NOT check out a _workspace branch — HEAD stays on "main".
        return repoPath;
    }

    /// <summary>
    /// Creates a repository where the originating branch ("main") is NOT the currently
    /// checked-out branch (HEAD is on "_workspace").
    /// WorktreeManager.MergeWorktree will take the ref-only path.
    /// </summary>
    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(
            Path.GetTempPath(), $"agentweaver-test-repo-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);
        using var repo = new Repository(repoPath);

        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig     = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);

        return repoPath;
    }

    /// <summary>
    /// Advances <paramref name="branchName"/> with a new commit that adds or
    /// replaces <paramref name="filePath"/> with <paramref name="fileContent"/>
    /// without touching the main working tree.
    /// </summary>
    private static string AdvanceBranchWithContent(
        string repositoryPath,
        string branchName,
        string filePath,
        string fileContent,
        string commitMessage)
    {
        using var repo = new Repository(repositoryPath);
        var branch     = repo.Branches[branchName]
            ?? throw new InvalidOperationException($"Branch '{branchName}' not found in '{repositoryPath}'.");

        var tmpBlobPath = Path.Combine(
            repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmpBlobPath, fileContent, Encoding.UTF8);

        try
        {
            var blob      = repo.ObjectDatabase.CreateBlob(tmpBlobPath);
            var treeDef   = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree   = repo.ObjectDatabase.CreateTree(treeDef);

            var sig       = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var newCommit = repo.ObjectDatabase.CreateCommit(
                sig, sig, commitMessage, newTree,
                new[] { branch.Tip }, prettifyMessage: true);

            repo.Refs.UpdateTarget(
                repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);

            return newCommit.Sha;
        }
        finally
        {
            if (File.Exists(tmpBlobPath))
                File.Delete(tmpBlobPath);
        }
    }
}
