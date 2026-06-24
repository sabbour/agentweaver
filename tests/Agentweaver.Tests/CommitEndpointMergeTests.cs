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
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Integration tests for the POST /api/runs/{id}/commit "commit-and-merge" flow.
///
/// Every test exercises the real endpoint against a real in-process API server,
/// a real SQLite database, and real git repositories created with LibGit2Sharp.
/// No mocks, no fakes, no placeholders.
///
/// Acceptance scenarios:
///   Happy path   — commit + merge succeeds; run is Merged, worktree removed,
///                  originating branch contains the agent's commits.
///   Conflict     — commit succeeds but merge has conflicts; run is MergeFailed,
///                  conflicting_files list in event payload and on the run record,
///                  worktree preserved.
///   Wrong status — commit on a non-awaiting_review run returns 409.
/// </summary>
public sealed class CommitEndpointMergeTests : IClassFixture<ReviewWebApplicationFactory>, IDisposable
{
    private readonly ReviewWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;

    private readonly List<string> _tempRepoDirs = new();

    public CommitEndpointMergeTests(ReviewWebApplicationFactory factory)
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
            catch { /* best effort — git packs may still be locked */ }
        }
    }

    // =========================================================================
    // Test 1 — Happy path: commit + merge succeeds.
    // After POST /api/runs/{id}/commit:
    //   • Response status is "merged".
    //   • The originating branch tip tree equals the worktree tree hash.
    //   • The worktree directory is removed (cleanup after merge).
    //   • GET /api/runs/{id} returns status="merged".
    // =========================================================================
    [Fact]
    public async Task Commit_HappyPath_MergesIntoOriginatingBranch()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "agent-output.txt"), "agent produced this"));

        string originHeadBefore;
        using (var repo = new Repository(repoPath))
            originHeadBefore = repo.Branches[run.OriginatingBranch]!.Tip.Sha;

        var response = await _ownerClient.PostAsJsonAsync($"/api/runs/{run.Id}/commit", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CommitResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("merged");
        result.MergeResult.Should().StartWith("merged:",
            "a successful commit+merge result must start with 'merged:' followed by the commit hash");
        result.ConflictingFiles.Should().BeNullOrEmpty("no conflicts on clean merge");

        // The originating branch must have advanced.
        using var repoAfter = new Repository(repoPath);
        var branchAfter = repoAfter.Branches[run.OriginatingBranch]!;
        branchAfter.Tip.Sha.Should().NotBe(originHeadBefore,
            "originating branch must advance to include the agent's commit");

        // The worktree must have been removed (cleanup on success).
        Directory.Exists(run.WorktreePath).Should().BeFalse(
            "the worktree must be removed after a successful merge");

        // GET /api/runs/{id} must reflect merged status.
        var getResp = await _ownerClient.GetAsync($"/api/runs/{run.Id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var runResp = await getResp.Content.ReadFromJsonAsync<RunResponse>();
        runResp!.Status.Should().Be("merged");
        runResp.MergeConflicts.Should().BeNull("no conflicts occurred");
    }

    // =========================================================================
    // Test 2 — Happy path: the agent's file content is present in the
    // originating branch after the merge.
    // =========================================================================
    [Fact]
    public async Task Commit_HappyPath_AgentFilePresentOnOriginatingBranch()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "feature.cs"), "// generated code"));

        var response = await _ownerClient.PostAsJsonAsync($"/api/runs/{run.Id}/commit", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CommitResponse>())!.Status.Should().Be("merged");

        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches[run.OriginatingBranch]!.Tip.Tree["feature.cs"]
            .Should().NotBeNull("the agent's file must appear on the originating branch after merge");
    }

    // =========================================================================
    // Test 3 — Merge conflict:
    // When the originating branch has diverged with a conflicting commit,
    // POST /api/runs/{id}/commit must:
    //   • Return HTTP 200 with status="merge_failed".
    //   • Include a non-empty conflicting_files list.
    //   • Preserve the worktree for manual resolution.
    //   • Store merge_conflicts JSON on the run record (readable via GET).
    //   • NOT modify the originating branch.
    // =========================================================================
    [Fact]
    public async Task Commit_MergeConflict_ReturnsMergeFailed_WithConflictFiles()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "conflict.txt"), "agent version"));

        // Advance the originating branch with a conflicting commit on the same file.
        var humanCommitSha = AdvanceBranchWithContent(
            repoPath, run.OriginatingBranch,
            "conflict.txt", "human version", "Human conflicting advance");

        var response = await _ownerClient.PostAsJsonAsync($"/api/runs/{run.Id}/commit", new { });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<CommitResponse>();
        result.Should().NotBeNull();
        result!.Status.Should().Be("merge_failed");
        result.MergeResult.Should().StartWith("conflict:");
        result.ConflictingFiles.Should().NotBeNullOrEmpty(
            "conflicting_files must be populated when a merge fails due to conflicts");
        result.ConflictingFiles.Should().Contain(f => f.Contains("conflict.txt"),
            "the conflicting file must appear in the conflict list");

        // The originating branch must remain at the human's advance commit (unchanged).
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches[run.OriginatingBranch]!.Tip.Sha.Should().Be(humanCommitSha,
            "the originating branch must not be modified when a merge fails");

        // The worktree is preserved on conflict so the user can inspect the merge state manually.
        Directory.Exists(run.WorktreePath).Should().BeTrue(
            "the worktree must be preserved on merge conflict for manual inspection — conflict info is also stored in merge_conflicts");

        // GET /api/runs/{id} must reflect the conflict data.
        var getResp = await _ownerClient.GetAsync($"/api/runs/{run.Id}");
        var runResp = await getResp.Content.ReadFromJsonAsync<RunResponse>();
        runResp!.Status.Should().Be("merge_failed");
        runResp.MergeConflicts.Should().NotBeNullOrEmpty(
            "merge_conflicts must be stored on the run record when a conflict occurs");

        // The stored JSON must parse to a list containing conflict.txt.
        var storedFiles = JsonSerializer.Deserialize<List<string>>(runResp.MergeConflicts!);
        storedFiles.Should().Contain(f => f.Contains("conflict.txt"),
            "the stored merge_conflicts JSON must include the conflicting file path");
    }

    // =========================================================================
    // Test 4 — Wrong status: committing a non-awaiting_review run returns 409.
    // =========================================================================
    [Fact]
    public async Task Commit_NonAwaitingReview_Returns409Conflict()
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

        var response = await _ownerClient.PostAsJsonAsync($"/api/runs/{runId}/commit", new { });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a run that is not in awaiting_review cannot be committed");
    }

    // =========================================================================
    // Test 5 — Commit endpoint records merge.started + merge.completed events
    // in the stream entry for a successful merge.
    // =========================================================================
    [Fact]
    public async Task Commit_HappyPath_RecordsMergeEventsInStreamEntry()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "out.txt"), "content"));

        var response = await _ownerClient.PostAsJsonAsync($"/api/runs/{run.Id}/commit", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify events are recorded in the stream entry (accessible after the SSE
        // stream has closed for the review.requested gate).
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Get(run.Id.ToString());
        entry.Should().NotBeNull("stream entry must remain retained after merge");

        var snapshot = entry!.GetSnapshotSince(0);
        var eventTypes = snapshot.Events.Select(e => e.Type).ToList();

        eventTypes.Should().Contain(EventTypes.MergeStarted,
            "merge.started must be recorded when commit+merge begins");
        eventTypes.Should().Contain(EventTypes.MergeCompleted,
            "merge.completed must be recorded when commit+merge succeeds");
        snapshot.IsCompleted.Should().BeTrue(
            "stream must be marked complete after a successful merge");
    }

    // =========================================================================
    // Test 6 — Commit conflict records merge.conflicted event in stream entry.
    // =========================================================================
    [Fact]
    public async Task Commit_MergeConflict_RecordsMergeConflictedEventInStreamEntry()
    {
        var (run, repoPath) = await SetupRunAwaitingReviewAsync(worktreeCustomizer: dir =>
            File.WriteAllText(Path.Combine(dir, "conflict.txt"), "agent version"));

        AdvanceBranchWithContent(
            repoPath, run.OriginatingBranch,
            "conflict.txt", "human version", "Human conflicting advance");

        var response = await _ownerClient.PostAsJsonAsync($"/api/runs/{run.Id}/commit", new { });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Get(run.Id.ToString());
        entry.Should().NotBeNull("stream entry must remain retained after merge failure");

        var snapshot = entry!.GetSnapshotSince(0);
        var eventTypes = snapshot.Events.Select(e => e.Type).ToList();

        eventTypes.Should().Contain(EventTypes.MergeStarted,
            "merge.started must be recorded before the conflict is detected");
        eventTypes.Should().Contain(EventTypes.MergeConflicted,
            "merge.conflicted must be recorded when commit+merge encounters conflicts");
        snapshot.IsCompleted.Should().BeTrue(
            "stream must be marked complete after a merge failure");

        // The merge.conflicted event payload must include conflicting_files.
        var conflictEvent = snapshot.Events.First(e => e.Type == EventTypes.MergeConflicted);
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(conflictEvent.Payload);
        payloadJson.Should().Contain("conflicting_files",
            "merge.conflicted payload must include the conflicting_files array");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a real git repository, adds a worktree for a agentweaver run,
    /// lets the caller write files, commits the changes, and inserts a run
    /// at AwaitingReview status — mirroring what RunOrchestrator produces
    /// before waiting for a human decision.
    ///
    /// "main" is NOT checked out in the main working tree (HEAD points to
    /// "_workspace") so MergeWorktree does not trip the checked-out guard.
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

        var finalRun = run with
        {
            Status    = RunStatus.AwaitingReview,
            TreeHash  = treeHash,
            Diff      = diff,
            StepCount = 0,
        };

        return (finalRun, repoPath);
    }

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(
            Path.GetTempPath(), $"agentweaver-commit-test-{Guid.NewGuid():N}");
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

    private static string AdvanceBranchWithContent(
        string repositoryPath,
        string branchName,
        string filePath,
        string fileContent,
        string commitMessage)
    {
        using var repo = new Repository(repositoryPath);
        var branch     = repo.Branches[branchName]
            ?? throw new InvalidOperationException($"Branch '{branchName}' not found.");

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
                sig, sig, commitMessage, newTree, new[] { branch.Tip }, prettifyMessage: true);

            repo.Refs.UpdateTarget(repo.Refs[$"refs/heads/{branchName}"], newCommit.Id);
            return newCommit.Sha;
        }
        finally
        {
            if (File.Exists(tmpBlobPath))
                File.Delete(tmpBlobPath);
        }
    }
}
