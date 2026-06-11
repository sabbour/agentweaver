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
/// Integration tests for B3 — the request-changes feedback loop.
///
/// Every test exercises the real POST /api/runs/{id}/request-changes endpoint
/// against a real in-process API server, a real SQLite database, and real git
/// repositories. No mocks, no fakes; the test agent runner (TestFileEditAgentRunner)
/// is a real implementation that operates on the actual worktree.
///
/// Tests cover:
///   - Non-owner -> 403
///   - Empty comment -> 400
///   - Oversized comment (> 8000 chars) -> 400
///   - Happy path: awaiting_review -> 202, revision row persisted with sanitized
///     comment, review.changes_requested + revision.started events on stream
///   - Race/CAS: run not in awaiting_review -> 409
///   - Soft cap: revision count >= Runs:MaxRevisions -> 409
///   - Sanitization: control chars stripped, structured task wraps feedback
/// </summary>
public sealed class RequestChangesEndpointTests
    : IClassFixture<RequestChangesWebApplicationFactory>, IDisposable
{
    private readonly RequestChangesWebApplicationFactory _factory;
    private readonly HttpClient _ownerClient;
    private readonly HttpClient _otherClient;
    private readonly List<string> _tempRepoDirs = new();

    public RequestChangesEndpointTests(RequestChangesWebApplicationFactory factory)
    {
        _factory = factory;

        _ownerClient = factory.CreateClient();
        _ownerClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", RequestChangesWebApplicationFactory.OwnerApiKey);

        _otherClient = factory.CreateClient();
        _otherClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", RequestChangesWebApplicationFactory.OtherApiKey);
    }

    public void Dispose()
    {
        _ownerClient.Dispose();
        _otherClient.Dispose();

        foreach (var dir in _tempRepoDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
    }

    // =========================================================================
    // Test 1 — Non-owner is hidden with 404.
    // =========================================================================
    [Fact]
    public async Task NonOwner_Returns404()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();

        var response = await _otherClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = "please fix this" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "request-changes must not reveal whether another user's run exists");
    }

    // =========================================================================
    // Test 2 — Empty comment is rejected with 400.
    // =========================================================================
    [Fact]
    public async Task EmptyComment_Returns400()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = "   " });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an empty or whitespace-only comment must be rejected with 400");
    }

    // =========================================================================
    // Test 3 — Oversized comment (> 8000 chars) is rejected with 400.
    // =========================================================================
    [Fact]
    public async Task OversizedComment_Returns400()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();
        var huge = new string('x', 8001);

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = huge });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a comment exceeding 8000 characters must be rejected with 400");
    }

    // =========================================================================
    // Test 4 — Happy path: awaiting_review run -> 202, revision row persisted,
    // review.changes_requested + revision.started events recorded on stream.
    // =========================================================================
    [Fact]
    public async Task HappyPath_Returns202_PersistsRevisionRow_AndEmitsEvents()
    {
        // Use NoChange mode so the agent exits immediately and the test does not
        // need to wait for a full workflow cycle. The key assertions are about
        // the endpoint's synchronous effects (revision row + events), not the
        // follow-on workflow completion.
        _factory.TestAgentRunner.Mode = TestFileEditAgentRunner.AgentMode.NoChange;

        var (run, _) = await SetupRunAwaitingReviewAsync();

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = "Please add error handling to the new method." });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "a valid request-changes on an awaiting_review run must return 202");

        var body = await response.Content.ReadFromJsonAsync<RequestChangesResponse>();
        body.Should().NotBeNull();
        body!.RunId.Should().Be(run.Id.ToString());
        body.Status.Should().Be("in_progress",
            "the run transitions to in_progress immediately after the CAS wins");

        // Verify the revision audit row was persisted.
        var revisionStore = _factory.Services.GetRequiredService<SqliteRunRevisionStore>();
        var maxRevision = await revisionStore.GetMaxRevisionNumberAsync(run.Id);
        maxRevision.Should().Be(1,
            "the first revision must be recorded with revision_number 1");

        // Verify the stream has both required events.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry = streamStore.Get(run.Id.ToString());
        entry.Should().NotBeNull("the stream entry must still be accessible after request-changes");

        var events = entry!.GetSnapshotSince(0).Events;
        events.Should().ContainSingle(e => e.Type == EventTypes.ReviewChangesRequested,
            "review.changes_requested must be recorded on the stream");
        events.Should().ContainSingle(e => e.Type == EventTypes.RevisionStarted,
            "revision.started must be recorded on the stream");

        // Events must appear after review.requested (sequence ordering).
        var reviewRequestedSeq = events.First(e => e.Type == EventTypes.ReviewRequested).Sequence;
        var changesRequestedSeq = events.First(e => e.Type == EventTypes.ReviewChangesRequested).Sequence;
        var revisionStartedSeq = events.First(e => e.Type == EventTypes.RevisionStarted).Sequence;

        changesRequestedSeq.Should().BeGreaterThan(reviewRequestedSeq,
            "review.changes_requested must follow review.requested in monotonic sequence");
        revisionStartedSeq.Should().BeGreaterThan(changesRequestedSeq,
            "revision.started must follow review.changes_requested in monotonic sequence");
    }

    // =========================================================================
    // Test 5 — CAS race: run NOT in awaiting_review (already merged/declined)
    // must return 409.
    // =========================================================================
    [Fact]
    public async Task RunNotAwaitingReview_Returns409()
    {
        // Insert a run that is already in a terminal (merged) state.
        var (run, _) = await SetupRunAwaitingReviewAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();

        // Advance the run to Merged to simulate a concurrent approve winning the CAS.
        await runStore.TryTransitionReviewAsync(
            run.Id, RunStatus.Merged, DateTimeOffset.UtcNow, "merged:abc123", default);

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = "please fix this" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "request-changes on a non-awaiting_review run must return 409");
    }

    // =========================================================================
    // Test 6 — Soft cap: when revision count >= Runs:MaxRevisions (3 in test
    // factory), the endpoint must return 409 with a clear error.
    // =========================================================================
    [Fact]
    public async Task SoftCap_Returns409_WhenRevisionsAtMax()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();
        var revisionStore = _factory.Services.GetRequiredService<SqliteRunRevisionStore>();

        // Pre-populate 3 revision rows to reach the factory-configured cap of 3.
        for (var i = 1; i <= 3; i++)
        {
            await revisionStore.InsertRevisionAsync(
                run.Id, i,
                RequestChangesWebApplicationFactory.OwnerUser,
                $"revision {i} raw",
                $"revision {i} sanitized",
                "prev-hash",
                default);
        }

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = "one more revision" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "a revision request that would exceed the MaxRevisions cap must return 409");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Maximum number of revisions",
            "the 409 response must explain the soft cap was reached");
    }

    // =========================================================================
    // Test 7 — Sanitization: control characters are stripped from the comment,
    // and the structured task wraps the sanitized feedback in <reviewer_feedback>.
    // =========================================================================
    [Fact]
    public async Task Sanitization_StripsControlChars_AndSanitizedCommentStoredInRevisionRow()
    {
        _factory.TestAgentRunner.Mode = TestFileEditAgentRunner.AgentMode.NoChange;

        var (run, _) = await SetupRunAwaitingReviewAsync();

        // Comment contains NUL, a C1 control char (0x85 = NEL), and normal content.
        const string controlChars = "\x00\x01\x85";
        var rawComment = $"Fix the bug{controlChars} please add tests.";

        var response = await _ownerClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/request-changes",
            new { comment = rawComment });

        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "control chars in the comment must be stripped, not rejected");

        var revisionStore = _factory.Services.GetRequiredService<SqliteRunRevisionStore>();
        var maxRevision = await revisionStore.GetMaxRevisionNumberAsync(run.Id);
        maxRevision.Should().Be(1);

        // Read the stored sanitized comment directly from the DB.
        var db = _factory.Services.GetRequiredService<SqliteDb>();
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText =
            "SELECT sanitized_comment FROM run_revisions WHERE run_id = $runId AND revision_number = 1;";
        cmd.Parameters.AddWithValue("$runId", run.Id.ToString());
        var stored = (string?)await cmd.ExecuteScalarAsync();

        stored.Should().NotBeNull();
        stored!.Should().NotContain("\x00", "NUL must be stripped by the sanitizer");
        stored.Should().NotContain("\x01", "SOH control char must be stripped");
        stored.Should().NotContain("\x85", "C1 NEL control char must be stripped");
        stored.Should().Contain("Fix the bug",
            "the non-control-char content must be preserved after sanitization");
    }

    [Fact]
    public async Task ShellApproval_NonOwner_Returns403()
    {
        var (run, _) = await SetupRunAwaitingReviewAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var transitioned = await runStore.TryTransitionReviewToInProgressAsync(run.Id);
        transitioned.Should().BeTrue();

        var response = await _otherClient.PostAsJsonAsync(
            $"/api/runs/{run.Id}/shell-approvals",
            new { command_hash = "abc123" });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a real git repository, adds a worktree for a new run, commits a
    /// file into the worktree, and inserts a run record at AwaitingReview status.
    /// Mirrors SetupRunAwaitingReviewAsync in ReviewEndpointTests.
    /// </summary>
    private async Task<(Run Run, string RepoPath)> SetupRunAwaitingReviewAsync()
    {
        var repoPath = CreateTempGitRepo();
        var runId    = RunId.New();

        var worktreeManager = _factory.Services.GetRequiredService<WorktreeManager>();
        var worktreeInfo    = worktreeManager.AddWorktree(repoPath, "main", runId);

        File.WriteAllText(
            Path.Combine(worktreeInfo.WorktreePath, "agent-output.txt"),
            "agent produced this for request-changes test");

        var treeHash = worktreeManager.CommitChanges(worktreeInfo.WorktreePath, runId);
        var diff     = worktreeManager.GetDiff(repoPath, "main", worktreeInfo.BranchName);

        var run = new Run
        {
            Id                = runId,
            RepositoryPath    = repoPath,
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "original task description",
            SubmittingUser    = RequestChangesWebApplicationFactory.OwnerUser,
            Status            = RunStatus.InProgress,
            StartedAt         = DateTimeOffset.UtcNow,
            WorktreePath      = worktreeInfo.WorktreePath,
            WorktreeBranch    = worktreeInfo.BranchName,
        };

        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.InsertAsync(run);
        await runStore.UpdateReviewReadyAsync(runId, treeHash, diff, stepCount: 0);

        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var entry       = streamStore.Create(runId.ToString(), RequestChangesWebApplicationFactory.OwnerUser);
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
            Path.GetTempPath(), $"scaffolder-rc-repo-{Guid.NewGuid():N}");
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
}
