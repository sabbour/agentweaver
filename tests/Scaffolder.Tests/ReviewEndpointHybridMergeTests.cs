using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.Api.Contracts;
using Scaffolder.Api.Git;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Runs;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

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
    // HM-2 — Checked-out branch, modified tracked file → 409 retriable;
    // fix the condition and re-approve → merged. Proves retriability of the
    // Blocked outcome and correct Merging → AwaitingReview reversion.
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_ModifiedTrackedFile_Blocks_ThenClean_Merges()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "agent-file.txt"), "agent content"));

        // Dirty the main working tree: modify the tracked readme.txt without staging.
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "locally modified content");

        var firstResponse = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        firstResponse.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a dirty working tree must block the merge with HTTP 409 (retriable)");

        var firstBody = await firstResponse.Content.ReadFromJsonAsync<JsonElement>();
        firstBody.GetProperty("status").GetString().Should().Be("awaiting_review",
            "the response body must carry the current status so the client knows it can retry");
        firstBody.GetProperty("error").GetString().Should().Contain("uncommitted changes",
            "the blocked reason must describe the uncommitted tracked-file condition");

        // The DB must reflect awaiting_review — not merging or any terminal state.
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runAfterBlock = await runStore.GetAsync(run.Id);
        runAfterBlock!.Status.Should().Be(RunStatus.AwaitingReview,
            "RevertMergingAsync must return the run to awaiting_review after a blocked outcome");

        // Restore the tracked file to its committed content and re-approve.
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");

        var secondResponse = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "a clean working tree must allow the merge to proceed on re-approval");
        var secondResult = await secondResponse.Content.ReadFromJsonAsync<ReviewResponse>();
        secondResult!.Status.Should().Be("merged");
    }

    // =========================================================================
    // HM-3 — Checked-out branch, untracked file collides with a path added by
    // the merge → 409 retriable (untracked-overwrite category).
    // =========================================================================
    [Fact]
    public async Task Approve_CheckedOut_UntrackedFileCollides_Blocks()
    {
        // The agent adds "collision.txt" — a path that does not yet exist on main.
        var (run, repoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "collision.txt"), "agent added this"));

        // Place an untracked file at the same path in the main working tree.
        File.WriteAllText(Path.Combine(repoPath, "collision.txt"), "local untracked version");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "an untracked file that would be overwritten must block the merge (retriable 409)");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("awaiting_review",
            "the run must remain awaiting_review so the human can remove the collision and retry");
        body.GetProperty("error").GetString().Should().Contain("untracked files would be overwritten",
            "the blocked reason must identify the untracked-overwrite category");

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var runAfterBlock = await runStore.GetAsync(run.Id);
        runAfterBlock!.Status.Should().Be(RunStatus.AwaitingReview);
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
                "an in-progress merge state must block the scaffolder merge (retriable 409)");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("status").GetString().Should().Be("awaiting_review");
            body.GetProperty("error").GetString().Should().Contain("merge or rebase is already in progress",
                "the blocked reason must identify the in-progress sequencer operation");
        }
        finally
        {
            if (File.Exists(mergeHeadPath))
                File.Delete(mergeHeadPath);
        }
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
    // and directory must both be preserved for human inspection.
    // =========================================================================
    [Fact]
    public async Task Approve_DivergentConflict_MergeFailed_WorktreeBranchAndDirectoryPreserved()
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

        // The worktree BRANCH must still exist in the repository.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches[run.WorktreeBranch].Should().NotBeNull(
            "the worktree branch must be preserved when the merge fails so the human can inspect it");

        // The worktree DIRECTORY must still exist.
        Directory.Exists(run.WorktreePath).Should().BeTrue(
            "the worktree directory must be preserved when the merge fails");
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
    // stream entry is re-created for the recovered run.
    // =========================================================================
    [Fact]
    public async Task RestartRecovery_RevertsInterruptedMerge_AndRecreatesStreamEntry()
    {
        var runStore     = _factory.Services.GetRequiredService<SqliteRunStore>();
        var restartSvc   = _factory.Services.GetRequiredService<WorkflowRestartService>();
        var streamStore  = _factory.Services.GetRequiredService<RunStreamStore>();

        // Simulate a run that was interrupted while in the Merging state
        // (CAS succeeded but the process died before MergeWorktree completed).
        var runId = RunId.New();
        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = "recovery-test-dummy-path",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "recovery test task",
            SubmittingUser    = ReviewWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
        };
        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, "treehash-recovery", "diff", 0);

        var casSucceeded = await runStore.TryStartMergingAsync(runId);
        casSucceeded.Should().BeTrue("setup: run must advance from awaiting_review to merging");

        var runBeforeRecovery = await runStore.GetAsync(runId);
        runBeforeRecovery!.Status.Should().Be(RunStatus.Merging);

        // Trigger the restart-recovery path.
        await restartSvc.RecoverAsync(CancellationToken.None);

        // The run must be reverted to awaiting_review.
        var runAfterRecovery = await runStore.GetAsync(runId);
        runAfterRecovery!.Status.Should().Be(RunStatus.AwaitingReview,
            "restart recovery must revert any run stuck in merging back to awaiting_review");

        // A stream entry must be re-created for the recovered run.
        var entry = streamStore.Get(runId.ToString());
        entry.Should().NotBeNull(
            "restart recovery must re-create an in-memory stream entry for the recovered awaiting_review run");
        entry!.IsAwaitingReview.Should().BeTrue(
            "the re-created stream entry must be marked as awaiting_review so it is not evicted");
    }

    // =========================================================================
    // HM-12 — Safe-string: Blocked 409 error messages and Conflict merge_result
    // strings must never expose absolute filesystem paths or raw file content.
    // =========================================================================
    [Fact]
    public async Task BlockedAndConflict_Responses_NeverExposePathsOrFileContent()
    {
        // --- Part A: blocked 409 from a modified tracked file ---
        var (blockedRun, blockedRepoPath) = await SetupRunAwaitingReviewWithMainCheckedOutAsync(
            dir => File.WriteAllText(Path.Combine(dir, "secret.txt"), "SECRET FILE CONTENT"));

        // Dirty the working tree so the approve is blocked.
        File.WriteAllText(
            Path.Combine(blockedRepoPath, "readme.txt"), "SECRET DIRTY CONTENT");

        var blockedResponse = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{blockedRun.Id}/review", new { approved = true });

        blockedResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var blockedBody = await blockedResponse.Content.ReadAsStringAsync();

        blockedBody.Should().NotContain(blockedRepoPath,
            "the 409 error must not expose absolute repository filesystem paths");
        blockedBody.Should().NotContain("SECRET DIRTY CONTENT",
            "the 409 error must not expose raw tracked-file content");
        blockedBody.Should().NotContain("SECRET FILE CONTENT",
            "the 409 error must not expose raw agent file content");

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
            Path.GetTempPath(), $"scaffolder-test-case-{Guid.NewGuid():N}");
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
            Path.GetTempPath(), $"scaffolder-test-checked-{Guid.NewGuid():N}");
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
            Path.GetTempPath(), $"scaffolder-test-repo-{Guid.NewGuid():N}");
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
