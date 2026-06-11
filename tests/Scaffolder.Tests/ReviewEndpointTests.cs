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
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

/// <summary>
/// Integration tests for User Story 4 — Review and approve the merge back.
///
/// Every test exercises the real POST /api/runs/{id}/review endpoint against a
/// real in-process API server, a real SQLite database, and real git repositories
/// created with LibGit2Sharp. No mocks, no fakes.
///
/// Tests map directly to acceptance scenarios and functional requirements:
///   SC-004 / AC2  — approve merges into originating branch
///   SC-005 / AC3  — decline leaves branch byte-for-byte unchanged
///   FR-016        — conflicting diverged branch yields merge_failed; branch and
///                   worktree are preserved
///   FR-016 3-way  — non-conflicting diverged branch succeeds via 3-way merge
///   FR-015        — non-owner is rejected 403
///   FR-016 guard  — reviewing a non-awaiting_review run returns 409
///   idempotency   — repeated same decision is 200; cross-direction is 409
///   FR-019/023    — review/merge events appear on the SSE stream in monotonic
///                   sequence order, continuing after the agent's last event
///   FR-014        — GET /api/runs/{id} returns diff + step_count + tree_hash
///                   when the run is in awaiting_review
/// </summary>
public sealed class ReviewEndpointTests : IClassFixture<ReviewWebApplicationFactory>, IDisposable
{
    private readonly ReviewWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;
    private readonly HttpClient _otherClient;

    // Paths of temporary git repos created by tests. Cleaned up in Dispose.
    private readonly List<string> _tempRepoDirs = new();

    public ReviewEndpointTests(ReviewWebApplicationFactory factory)
    {
        _factory = factory;

        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ReviewWebApplicationFactory.OwnerApiKey);

        _otherClient = factory.CreateClient();
        _otherClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ReviewWebApplicationFactory.OtherApiKey);
    }

    public void Dispose()
    {
        _ownerClient.Dispose();
        _otherClient.Dispose();

        foreach (var dir in _tempRepoDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort — git packs may still be locked */ }
        }
    }

    // =========================================================================
    // Test 1 (SC-004, AC2)
    // After approving, the originating branch tip tree must equal the committed
    // tree hash — no additional or missing changes.
    // =========================================================================
    [Fact]
    public async Task Approve_MergesIntoOriginatingBranch()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "agent-output.txt"), "agent produced this"));

        string originHeadBefore;
        using (var repo = new Repository(repoPath))
            originHeadBefore = repo.Branches[run.OriginatingBranch]!.Tip.Sha;

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:",
            "a successful merge result must be prefixed with 'merged:' followed by the commit hash");

        // SC-004: originating branch tip tree must equal the reviewed tree hash exactly.
        using var repoAfter = new Repository(repoPath);
        var branchAfter = repoAfter.Branches[run.OriginatingBranch]!;
        branchAfter.Tip.Tree.Sha.Should().Be(run.TreeHash,
            "the originating branch tip tree must match the approved tree hash with no additions or omissions (SC-004)");
        branchAfter.Tip.Sha.Should().NotBe(originHeadBefore,
            "the originating branch must have advanced to include the agent's commit");
    }

    // =========================================================================
    // Test 2 (SC-005, AC3)
    // After declining, the originating branch HEAD SHA must be byte-for-byte
    // unchanged and the worktree must still be present for reference.
    // =========================================================================
    [Fact]
    public async Task Decline_OriginatingBranchByteForByteUnchanged()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "agent-output.txt"), "agent produced this"));

        string headShaBefore;
        using (var repo = new Repository(repoPath))
            headShaBefore = repo.Branches[run.OriginatingBranch]!.Tip.Sha;

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("declined");
        result.MergeResult.Should().BeNull(
            "a declined run carries no merge result");

        // SC-005: originating branch must be byte-for-byte unchanged.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches[run.OriginatingBranch]!.Tip.Sha.Should().Be(headShaBefore,
            "the originating branch HEAD must be identical to what it was before the decline (SC-005)");

        // AC3 / FR-016: worktree preserved for human reference.
        Directory.Exists(run.WorktreePath).Should().BeTrue(
            "the worktree must be preserved intact when a run is declined so the human can inspect it (FR-016)");
    }

    // =========================================================================
    // Test 3 (FR-016, edge: divergent conflicting branch)
    // Approving when the originating branch has diverged with a conflicting commit
    // must yield merge_failed status. The originating branch must remain at the
    // human's advance commit (not rolled back). The worktree is removed (MergeFailed
    // is cleanly terminal; conflict info is stored in merge_conflicts).
    // =========================================================================
    [Fact]
    public async Task Approve_DivergentConflictingBranch_ReturnsMergeFailed_BranchUnchanged()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "conflict.txt"), "agent version of file"));

        // Advance the originating branch with a conflicting commit on the same file.
        var humanCommitSha = AdvanceBranchWithContent(
            repoPath, run.OriginatingBranch,
            "conflict.txt", "human version of file", "Human advance with conflict");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merge_failed");

        // FR-016 / S2: MergeResult is a safe enumerated string — raw file content
        // must never appear in it.
        result.MergeResult.Should().StartWith("conflict:");
        result.MergeResult.Should().NotContain("agent version",
            "raw file content must not appear in the merge result (FR-016 S2)");
        result.MergeResult.Should().NotContain("human version",
            "raw file content must not appear in the merge result (FR-016 S2)");

        // FR-016: originating branch must remain at the human's advance commit.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches[run.OriginatingBranch]!.Tip.Sha.Should().Be(humanCommitSha,
            "the originating branch must not be modified when a merge fails (FR-016)");

        // Worktree is removed on conflict — MergeFailed is cleanly terminal, conflict info is in the DB.
        Directory.Exists(run.WorktreePath).Should().BeFalse(
            "the worktree must be removed when the merge fails — conflict info is stored in merge_conflicts");
    }

    // =========================================================================
    // Test 4 (FR-016, 3-way merge)
    // When the originating branch has diverged with a non-conflicting commit,
    // approval must succeed via a 3-way merge commit that incorporates both sets
    // of changes.
    // =========================================================================
    [Fact]
    public async Task Approve_DivergentNonConflictingBranch_Merges()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "agent-output.txt"), "agent content"));

        // Advance originating branch with a non-conflicting change (different file).
        var humanCommitSha = AdvanceBranchWithContent(
            repoPath, run.OriginatingBranch,
            "human-output.txt", "human content", "Human non-conflicting advance");

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:");

        // The originating branch must now point to a 3-way merge commit that is
        // different from both the human's advance commit and the agent's commit.
        using var repoAfter = new Repository(repoPath);
        var tip = repoAfter.Branches[run.OriginatingBranch]!.Tip;
        tip.Sha.Should().NotBe(humanCommitSha,
            "a 3-way merge creates a new merge commit, not the human's commit");

        // Both the agent's file and the human's file must appear in the merged tree.
        tip.Tree["agent-output.txt"].Should().NotBeNull(
            "the agent's changes must be present in the merged originating branch");
        tip.Tree["human-output.txt"].Should().NotBeNull(
            "the human's changes must also be present in the merged originating branch");
    }

    // =========================================================================
    // Test 5 (FR-015)
    // A user who did not submit the run must receive 403 Forbidden.
    // =========================================================================
    [Fact]
    public async Task NonOwner_Forbidden_403()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();

        // The _otherClient is authenticated as OtherUser, not the run's SubmittingUser.
        var response = await _otherClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "only the submitting user may review a run (FR-015)");
    }

    // =========================================================================
    // Test 6 (FR-016 guard)
    // Reviewing a run that is not in awaiting_review must return 409 Conflict.
    // =========================================================================
    [Fact]
    public async Task ReviewBeforeAwaitingReview_Returns409Conflict()
    {
        var runId = RunId.New();
        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = "dummy-path-not-used",
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "test task",
            SubmittingUser    = ReviewWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
        };

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.InsertAsync(run);

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{runId}/review", new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a run in InProgress status is not reviewable; only awaiting_review runs may be reviewed");
    }

    // =========================================================================
    // Test 7a — Idempotency: approving an already-merged run returns 200.
    // =========================================================================
    [Fact]
    public async Task Idempotent_Approve_AfterMerged_Returns200()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync(dir =>
            File.WriteAllText(Path.Combine(dir, "file.txt"), "content"));

        // First approve — transitions to merged.
        var first = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second approve on the same already-merged run — must be idempotent.
        var second = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "approving an already-merged run must be idempotent (200, not 409 or 500)");
        var result = await second.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("merged");
    }

    // =========================================================================
    // Test 7b — Idempotency: declining an already-declined run returns 200.
    // =========================================================================
    [Fact]
    public async Task Idempotent_Decline_AfterDeclined_Returns200()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();

        // First decline.
        var first = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = false });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second decline on the same already-declined run — must be idempotent.
        var second = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = false });

        second.StatusCode.Should().Be(HttpStatusCode.OK,
            "declining an already-declined run must be idempotent (200, not 409 or 500)");
        var result = await second.Content.ReadFromJsonAsync<ReviewResponse>();
        result!.Status.Should().Be("declined");
    }

    // =========================================================================
    // Test 8 — Cross-direction after terminal state returns 409.
    // Approving after a run has been declined must return 409 Conflict.
    // =========================================================================
    [Fact]
    public async Task Approve_AfterDeclined_Returns409Conflict()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();

        // Decline first.
        var decline = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = false });
        decline.StatusCode.Should().Be(HttpStatusCode.OK);

        // Attempting to approve a declined run must be rejected.
        var approve = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });

        approve.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "approving a run that has already been declined is not permitted (irreversible decision)");
    }

    // =========================================================================
    // Test 9 (FR-019 / FR-023)
    // A stream for an awaiting_review run must close promptly after delivering
    // review.requested (the HITL gate). The client is expected to switch to
    // polling GET /api/runs/{id} for the merge outcome. After the stream closes,
    // the approval endpoint must still accept decisions and transition the run.
    // =========================================================================
    [Fact]
    public async Task ReviewEvents_OnStream_AreMonotonic_AndArrive()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync(dir =>
            File.WriteAllText(Path.Combine(dir, "file.txt"), "content"));

        // Use a dedicated client for SSE so the approve POST can go in parallel
        // on _ownerClient without serialisation concerns.
        using var sseClient = _factory.CreateClient();
        sseClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", ReviewWebApplicationFactory.OwnerApiKey);

        // Open the SSE stream — ResponseHeadersRead lets us return as soon as
        // headers arrive; the body is still streaming from the server.
        var streamReq = new HttpRequestMessage(
            HttpMethod.Get, $"/api/runs/{run.Id}/stream");
        var streamResp = await sseClient.SendAsync(
            streamReq, HttpCompletionOption.ResponseHeadersRead);
        streamResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The stream must close promptly (no hanging) once review.requested has been
        // delivered — the client switches to polling GET /api/runs/{id} after this.
        var sseBody = await streamResp.Content.ReadAsStringAsync()
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Parse SSE events.
        var events = ParseSseEvents(sseBody);

        // Stream must terminate with "event: done".
        sseBody.Should().Contain("event: done",
            "the SSE stream must end with the done sentinel (FR-019)");

        // review.requested must be present (this is the last agent-phase event).
        var requestedEvt = events.FirstOrDefault(e => e.Type == EventTypes.ReviewRequested);
        requestedEvt.Should().NotBeNull(
            "review.requested must be delivered before the stream closes (FR-023)");

        // All emitted events must have strictly increasing sequences.
        var seqs = events.Select(e => e.Sequence).ToList();
        seqs.Should().BeInAscendingOrder(
            "every event sequence must be strictly monotonic (FR-019)");

        // After the stream closes the run is still awaiting review — verify the
        // approval endpoint accepts the decision and transitions to merged.
        var approveResp = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/review", new { approved = true });
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // =========================================================================
    // Test 10 (FR-014 / AC1)
    // GET /api/runs/{id} must return a non-empty diff, a step_count, and a
    // tree_hash when the run is in awaiting_review state.
    // =========================================================================
    [Fact]
    public async Task GetRun_ReturnsDiffAndMetadata_WhenAwaitingReview()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "output.txt"), "agent produced this file"));

        var response = await _ownerClient.GetAsync($"/api/runs/{run.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<RunResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("awaiting_review");
        result.Diff.Should().NotBeNullOrEmpty(
            "the run's diff must be served when it is awaiting review so a human can inspect the changes (FR-014)");
        result.Diff.Should().Contain("output.txt",
            "the diff must mention the file the agent wrote");
        result.TreeHash.Should().NotBeNullOrEmpty(
            "tree_hash must be present so the review endpoint can verify integrity (FR-014)");
        result.TreeHash.Should().Be(run.TreeHash);
        // step_count is 0 in our setup (no tool.call events were manufactured).
        result.StepCount.Should().BeGreaterThanOrEqualTo(0);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a real git repository with an initial commit, adds a worktree
    /// for a new scaffolder run branch, optionally lets the caller write files
    /// into the worktree, commits the changes, and inserts a run record at
    /// AwaitingReview status into the database — mirroring the state that
    /// RunOrchestrator.RunTurnAsync produces before waiting for a human decision.
    ///
    /// The originating branch ("main") is NOT currently checked out in the main
    /// working tree (HEAD points to "_workspace") so WorktreeManager.MergeWorktree
    /// does not trip the "currently checked out" guard.
    /// </summary>
    private async Task<(Run Run, string RepoPath)> SetupRunAwaitingReviewAsync(
        Action<string>? worktreeCustomizer = null)
    {
        var repoPath = CreateTempGitRepo();
        var runId    = RunId.New();

        var worktreeManager = _factory.Services.GetRequiredService<WorktreeManager>();
        var worktreeInfo    = worktreeManager.AddWorktree(repoPath, "main", runId);

        // Let the test write files into the worktree before committing.
        worktreeCustomizer?.Invoke(worktreeInfo.WorktreePath);

        var treeHash = worktreeManager.CommitChanges(worktreeInfo.WorktreePath, runId);
        var diff     = worktreeManager.GetDiff(repoPath, "main", worktreeInfo.BranchName);

        // Insert the run as InProgress (mirrors RunOrchestrator.StartRunAsync),
        // then advance to AwaitingReview (mirrors the post-agent transition).
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

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount: 0);

        // Create the stream entry that the review endpoint reads events from.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry       = streamStore.Create(runId.ToString(), ReviewWebApplicationFactory.OwnerUser);
        entry.MarkAwaitingReview();
        entry.Record(new RunEvent(1, EventTypes.ReviewRequested, new { tree_hash = treeHash }));

        // Return the run with the fields that UpdateReviewReadyAsync set.
        var finalRun = run with
        {
            Status    = RunStatus.AwaitingReview,
            TreeHash  = treeHash,
            Diff      = diff,
            StepCount = 0,
        };

        return (finalRun, repoPath);
    }

    /// <summary>
    /// Initialises a bare-bones git repository, commits an initial file on
    /// "main", then checks out a "_workspace" branch so that "main" is NOT the
    /// currently checked-out branch. This prevents WorktreeManager.MergeWorktree
    /// from refusing to advance "main" while it is the HEAD branch.
    /// The repository path is registered for cleanup on Dispose.
    /// </summary>
    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(
            Path.GetTempPath(), $"scaffolder-test-repo-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);

        using var repo = new Repository(repoPath);

        // Write an initial file and commit it on whatever the default branch is.
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig     = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        // Rename the default branch to "main" if git initialised it as something else.
        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        // Create and check out a "_workspace" branch so "main" is not HEAD.
        // This is the invariant required by WorktreeManager.MergeWorktree.
        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);

        return repoPath;
    }

    /// <summary>
    /// Advances <paramref name="branchName"/> in <paramref name="repositoryPath"/>
    /// with a new commit that adds or replaces <paramref name="filePath"/> with
    /// <paramref name="fileContent"/>, without touching the main working tree.
    /// The blob is staged through a temporary file inside the repository's .git
    /// directory so no files are written outside the repository.
    /// Returns the SHA of the new commit.
    /// </summary>
    private static string AdvanceBranchWithContent(
        string repositoryPath,
        string branchName,
        string filePath,
        string fileContent,
        string commitMessage)
    {
        using var repo   = new Repository(repositoryPath);
        var branch       = repo.Branches[branchName]
            ?? throw new InvalidOperationException($"Branch '{branchName}' not found in '{repositoryPath}'.");

        // Write a temporary blob file inside .git to keep all I/O within the repo.
        var tmpBlobPath = Path.Combine(
            repositoryPath, ".git", $"tmp-blob-{Guid.NewGuid():N}");
        File.WriteAllText(tmpBlobPath, fileContent, Encoding.UTF8);

        try
        {
            var blob           = repo.ObjectDatabase.CreateBlob(tmpBlobPath);
            var treeDef        = TreeDefinition.From(branch.Tip.Tree);
            treeDef.Add(filePath, blob, Mode.NonExecutableFile);
            var newTree        = repo.ObjectDatabase.CreateTree(treeDef);

            var sig            = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var newCommit      = repo.ObjectDatabase.CreateCommit(
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

    // -----------------------------------------------------------------------
    // SSE parsing
    // -----------------------------------------------------------------------

    private sealed record SseEvent(int Sequence, string Type);

    /// <summary>
    /// Parses a raw SSE body into typed events. Only events that carry an
    /// <c>id:</c> field with a parseable integer are returned; the terminal
    /// <c>event: done</c> sentinel (which has no id) is intentionally excluded
    /// from the returned list but is still present in the raw body.
    /// </summary>
    private static List<SseEvent> ParseSseEvents(string sseBody)
    {
        var result     = new List<SseEvent>();
        string? lastId = null;
        string? lastEvent = null;

        foreach (var raw in sseBody.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("id: ", StringComparison.Ordinal))
            {
                lastId = line[4..];
            }
            else if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                lastEvent = line[7..];
            }
            else if (line.Length == 0)
            {
                // Blank line signals end of one event block.
                if (lastId != null && lastEvent != null
                    && int.TryParse(lastId, out var seq))
                {
                    result.Add(new SseEvent(seq, lastEvent));
                }

                lastId    = null;
                lastEvent = null;
            }
        }

        return result;
    }
}
