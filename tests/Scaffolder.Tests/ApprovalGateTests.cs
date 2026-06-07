using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Api;

/// <summary>
/// Verifies FR-015/FR-016: the human-approval gate state machine. Uses a real
/// in-process API via WebApplicationFactory. Database state is seeded directly
/// through the service layer to place runs in the needed states.
/// </summary>
public sealed class ApprovalGateTests : IClassFixture<ScaffolderWebApplicationFactory>
{
    private readonly ScaffolderWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ApprovalGateTests(ScaffolderWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ScaffolderWebApplicationFactory.TestApiKey);
    }

    /// <summary>
    /// Seeds the database with a run in the given status and optionally appends
    /// the review.requested event, returning the run id.
    /// </summary>
    private async Task<RunId> SeedRunAsync(
        RunStatus status,
        bool addReviewRequestedEvent = false,
        string? treeHash = null,
        string? worktreeBranch = null,
        string? repoPath = null,
        string? originatingBranch = null)
    {
        using var scope = _factory.Services.CreateScope();
        var runStore = scope.ServiceProvider.GetRequiredService<SqliteRunStore>();
        var eventStore = scope.ServiceProvider.GetRequiredService<SqliteEventStore>();

        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = repoPath ?? "placeholder-path",
            OriginatingBranch = originatingBranch ?? "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test task",
            SubmittingUser = ScaffolderWebApplicationFactory.TestUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            CommittedTreeHash = treeHash,
            WorktreeBranch = worktreeBranch
        };

        await runStore.InsertAsync(run);

        if (addReviewRequestedEvent)
        {
            await eventStore.AppendNewAsync(
                runId,
                EventType.ReviewRequested,
                JsonSerializer.Serialize(new { treeHash = treeHash ?? "abc123" }),
                callId: null);
        }

        return runId;
    }

    private async Task AppendReviewDeclinedAsync(RunId runId)
    {
        using var scope = _factory.Services.CreateScope();
        var eventStore = scope.ServiceProvider.GetRequiredService<SqliteEventStore>();
        await eventStore.AppendNewAsync(
            runId,
            EventType.ReviewDeclined,
            JsonSerializer.Serialize(new { declinedBy = "test-user" }),
            callId: null);
    }

    [Fact]
    public async Task Review_BeforeRunComplete_Returns409()
    {
        var runId = await SeedRunAsync(RunStatus.InProgress);

        var response = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review",
            new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "a review cannot be submitted until the run has completed");
    }

    [Fact]
    public async Task Decline_AfterComplete_Returns200()
    {
        var runId = await SeedRunAsync(RunStatus.Completed, addReviewRequestedEvent: true);

        var response = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review",
            new { approved = false });

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "a decline on a completed run must succeed");

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("declined");
    }

    [Fact]
    public async Task Approve_AfterComplete_Returns200()
    {
        // Set up a real bare git repository and worktree branch so the merge
        // path in RunOrchestrator.ApproveAsync can complete successfully.
        var repoDir = Path.Combine(Path.GetTempPath(), $"approval-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);

        string worktreeBranch;
        string treeHash;
        string originatingBranch;

        try
        {
            Repository.Init(repoDir);
            using var repo = new Repository(repoDir);

            // Configure identity so git operations succeed.
            repo.Config.Set("user.name", "Test");
            repo.Config.Set("user.email", "test@localhost");

            // Create an initial commit on the default branch.
            var signature = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
            var readmePath = Path.Combine(repoDir, "README.md");
            File.WriteAllText(readmePath, "test");
            Commands.Stage(repo, "*");
            var initialCommit = repo.Commit("Initial commit", signature, signature);

            // Discover the actual default branch name (main or master depending on config).
            originatingBranch = repo.Head.FriendlyName;

            // Create the scaffolder worktree branch from the default branch's tip.
            var runId = RunId.New();
            worktreeBranch = $"scaffolder/{runId}";
            var branch = repo.CreateBranch(worktreeBranch, initialCommit);

            // Add a commit to the worktree branch.
            repo.Refs.UpdateTarget(repo.Refs.Head.CanonicalName, branch.CanonicalName);
            var filePath = Path.Combine(repoDir, "output.txt");
            File.WriteAllText(filePath, "agent output");
            Commands.Stage(repo, "*");
            var worktreeCommit = repo.Commit("Agent changes", signature, signature);
            treeHash = worktreeCommit.Tree.Sha;

            // Switch HEAD back to the originating branch so the merge target is correct.
            repo.Refs.UpdateTarget(
                repo.Refs.Head.CanonicalName,
                $"refs/heads/{originatingBranch}");

            // Seed the run with completed status and the real repo path/branch/treeHash.
            var seededRunId = await SeedRunAsync(
                RunStatus.Completed,
                addReviewRequestedEvent: true,
                treeHash: treeHash,
                worktreeBranch: worktreeBranch,
                repoPath: repoDir,
                originatingBranch: originatingBranch);

            var response = await _client.PostAsJsonAsync(
                $"/api/runs/{seededRunId}/review",
                new { approved = true });

            response.StatusCode.Should().Be(HttpStatusCode.OK,
                because: "approving a completed run with a valid git repo must succeed");

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("status").GetString().Should().Be("approved");
        }
        finally
        {
            try { Directory.Delete(repoDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task DoubleApprove_SecondReturns409()
    {
        var runId = await SeedRunAsync(RunStatus.Completed, addReviewRequestedEvent: true);

        // First decline (no git repo needed for decline).
        var first = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review",
            new { approved = false });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second attempt after decision already recorded.
        var second = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review",
            new { approved = true });

        second.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "a review decision cannot be submitted twice for the same run");
    }

    [Fact]
    public async Task ApproveDecline_AfterDeclined_Returns409()
    {
        var runId = await SeedRunAsync(RunStatus.Completed, addReviewRequestedEvent: true);

        // Seed a review.declined event directly so the run is already decided.
        await AppendReviewDeclinedAsync(runId);

        var response = await _client.PostAsJsonAsync(
            $"/api/runs/{runId}/review",
            new { approved = true });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            because: "a run that was already declined must reject further review decisions");
    }
}
