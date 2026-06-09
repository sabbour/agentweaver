using System.Net;
using System.Net.Http.Json;
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
/// Integration tests that exercise the REAL MAF workflow path end-to-end:
/// POST /runs -> agent turn -> workflow review gate -> POST /review -> merge/decline.
/// Uses TestFileEditAgentRunner (real file ops, real git diff) and the full
/// MAF workflow with checkpointing, watch loop, and PendingRequestStore.
/// </summary>
public sealed class WorkflowIntegrationTests : IDisposable
{
    private readonly WorkflowWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly List<string> _tempRepoDirs = new();

    public WorkflowIntegrationTests()
    {
        _factory = new WorkflowWebApplicationFactory();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", WorkflowWebApplicationFactory.TestApiKey);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();

        foreach (var dir in _tempRepoDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort — git locks may linger on Windows */ }
        }
    }

    // =========================================================================
    // Test 1 — Happy path: start -> AwaitingReview -> approve -> Merged
    // Proves Issue 1 fix: the workflow correctly routes through review-adapter
    // and merge-adapter to MergeExecutor.
    // =========================================================================
    [Fact]
    public async Task HappyPath_StartApprove_ReachesMerged()
    {
        var repoPath = CreateTempGitRepo();
        _factory.TestAgentRunner.Mode = TestFileEditAgentRunner.AgentMode.MakesChange;

        // Start the run via POST /api/runs.
        var createResp = await _client.PostAsJsonAsync("/api/runs", new
        {
            repository_path = repoPath,
            originating_branch = "main",
            task = "Add a greeting file",
            model_source = "github-copilot"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await createResp.Content.ReadFromJsonAsync<CreateRunResponse>();
        var runId = created!.RunId;

        // Poll until AwaitingReview (the agent turn completes and review gate triggers).
        var run = await PollUntilStatusAsync(runId, "awaiting_review", TimeSpan.FromSeconds(30));
        run.Should().NotBeNull("run must reach awaiting_review within timeout");
        run!.Diff.Should().NotBeNullOrEmpty("diff must be populated at review");
        run.TreeHash.Should().NotBeNullOrEmpty("tree_hash must be populated at review");

        // Verify PendingRequestStore is populated (proves the watch loop processed the event).
        var pendingStore = _factory.Services.GetRequiredService<PendingRequestStore>();
        var pending = pendingStore.Get(runId);
        pending.Should().NotBeNull("PendingRequestStore must have the review request");

        // Approve the review.
        var reviewResp = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review", new { approved = true });
        reviewResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewResult = await reviewResp.Content.ReadFromJsonAsync<ReviewResponse>();
        reviewResult!.Status.Should().Be("merging");

        // Poll until Merged (the merge executor runs through the watch loop).
        var mergedRun = await PollUntilStatusAsync(runId, "merged", TimeSpan.FromSeconds(30));
        mergedRun.Should().NotBeNull("run must reach merged status within timeout");
        mergedRun!.Result.Should().StartWith("merged:");

        // Verify the originating branch was advanced.
        using var repo = new Repository(repoPath);
        var mainBranch = repo.Branches["main"]!;
        var agentFile = Path.Combine(repoPath, _factory.TestAgentRunner.FileName);
        // The file should NOT exist in the main worktree (it's on _workspace checkout),
        // but the branch tip tree should include it.
        mainBranch.Tip.Tree[_factory.TestAgentRunner.FileName].Should().NotBeNull(
            "the agent's file must be present in the merged branch tree");
    }

    // =========================================================================
    // Test 2 — Decline: start -> AwaitingReview -> decline -> Declined
    // =========================================================================
    [Fact]
    public async Task Decline_ReachesDeclinedTerminal()
    {
        var repoPath = CreateTempGitRepo();
        _factory.TestAgentRunner.Mode = TestFileEditAgentRunner.AgentMode.MakesChange;

        var createResp = await _client.PostAsJsonAsync("/api/runs", new
        {
            repository_path = repoPath,
            originating_branch = "main",
            task = "Add a file that will be declined",
            model_source = "github-copilot"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await createResp.Content.ReadFromJsonAsync<CreateRunResponse>();
        var runId = created!.RunId;

        // Poll until AwaitingReview.
        var run = await PollUntilStatusAsync(runId, "awaiting_review", TimeSpan.FromSeconds(30));
        run.Should().NotBeNull("run must reach awaiting_review");

        // Record the main branch HEAD before decline.
        string headBefore;
        using (var repo = new Repository(repoPath))
            headBefore = repo.Branches["main"]!.Tip.Sha;

        // Decline.
        var reviewResp = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review", new { approved = false });
        reviewResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var reviewResult = await reviewResp.Content.ReadFromJsonAsync<ReviewResponse>();
        reviewResult!.Status.Should().Be("declined");

        // Poll until Declined is the final state.
        var declinedRun = await PollUntilStatusAsync(runId, "declined", TimeSpan.FromSeconds(10));
        declinedRun.Should().NotBeNull("run must reach declined status");

        // Verify main branch is unchanged.
        using var repoAfter = new Repository(repoPath);
        repoAfter.Branches["main"]!.Tip.Sha.Should().Be(headBefore,
            "declining must not advance the originating branch");
    }

    // =========================================================================
    // Test 3 — No changes: agent makes no modifications -> Completed(no_changes)
    // without pausing at review, and worktree is removed (Issue 5).
    // =========================================================================
    [Fact]
    public async Task NoChanges_SkipsReview_CompletesImmediately()
    {
        var repoPath = CreateTempGitRepo();
        _factory.TestAgentRunner.Mode = TestFileEditAgentRunner.AgentMode.NoChange;

        var createResp = await _client.PostAsJsonAsync("/api/runs", new
        {
            repository_path = repoPath,
            originating_branch = "main",
            task = "Do nothing",
            model_source = "github-copilot"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await createResp.Content.ReadFromJsonAsync<CreateRunResponse>();
        var runId = created!.RunId;

        // Poll until completed.
        var run = await PollUntilStatusAsync(runId, "completed", TimeSpan.FromSeconds(30));
        run.Should().NotBeNull("no-change run must reach completed status");
        run!.Result.Should().Be("no_changes");

        // Verify worktree was cleaned up (Issue 5).
        var worktreeBasePath = _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Worktrees:BasePath"];
        if (worktreeBasePath is not null && Directory.Exists(worktreeBasePath))
        {
            var remainingWorktrees = Directory.GetDirectories(worktreeBasePath);
            remainingWorktrees.Should().BeEmpty(
                "no-changes runs must have their worktree removed (Issue 5)");
        }
    }

    // =========================================================================
    // Test 4 — Content safety: agent signals violation -> Failed(content_safety)
    // without ever reaching AwaitingReview. Worktree removed (Issue 5).
    // =========================================================================
    [Fact]
    public async Task ContentSafety_FailsImmediately_NeverReachesReview()
    {
        var repoPath = CreateTempGitRepo();
        _factory.TestAgentRunner.Mode = TestFileEditAgentRunner.AgentMode.ContentSafety;

        var createResp = await _client.PostAsJsonAsync("/api/runs", new
        {
            repository_path = repoPath,
            originating_branch = "main",
            task = "Generate harmful content",
            model_source = "github-copilot"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var created = await createResp.Content.ReadFromJsonAsync<CreateRunResponse>();
        var runId = created!.RunId;

        // Poll until failed.
        var run = await PollUntilStatusAsync(runId, "failed", TimeSpan.FromSeconds(30));
        run.Should().NotBeNull("content-safety-flagged run must reach failed status");
        run!.Result.Should().Be("content_safety");

        // Verify diff is NOT served (FR-026 / SC-009).
        run.Diff.Should().BeNull(
            "diff must not be served for safety-failed runs");

        // Verify worktree was cleaned up (Issue 5).
        var worktreeBasePath = _factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Worktrees:BasePath"];
        if (worktreeBasePath is not null && Directory.Exists(worktreeBasePath))
        {
            var remainingWorktrees = Directory.GetDirectories(worktreeBasePath);
            remainingWorktrees.Should().BeEmpty(
                "content-safety-failed runs must have their worktree removed (Issue 5)");
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private async Task<RunResponse?> PollUntilStatusAsync(
        string runId, string expectedStatus, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var resp = await _client.GetAsync($"/api/runs/{runId}");
            if (resp.StatusCode == HttpStatusCode.OK)
            {
                var run = await resp.Content.ReadFromJsonAsync<RunResponse>();
                if (run is not null && string.Equals(run.Status, expectedStatus, StringComparison.Ordinal))
                    return run;

                // If it hit a terminal state that is NOT the expected one, stop early.
                if (run is not null && IsTerminal(run.Status) && run.Status != expectedStatus)
                    return null;
            }
            await Task.Delay(100);
        }
        return null;
    }

    private static bool IsTerminal(string status) =>
        status is "completed" or "merged" or "merge_failed" or "declined" or "failed";

    /// <summary>
    /// Creates a git repository with an initial commit on "main", then checks out
    /// a "_workspace" branch so "main" is not HEAD (required by WorktreeManager).
    /// </summary>
    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(
            Path.GetTempPath(), $"scaffolder-wf-test-{Guid.NewGuid():N}");
        _tempRepoDirs.Add(repoPath);

        Repository.Init(repoPath);

        using var repo = new Repository(repoPath);
        File.WriteAllText(Path.Combine(repoPath, "readme.txt"), "initial content");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        var initial = repo.Commit("Initial commit", sig, sig);

        if (!string.Equals(repo.Head.FriendlyName, "main", StringComparison.Ordinal))
            repo.Branches.Rename(repo.Head, "main");

        var workspace = repo.CreateBranch("_workspace", initial);
        Commands.Checkout(repo, workspace);

        return repoPath;
    }
}
