using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Tests for the run-retrigger surface (POST /api/runs/{id}/retry). The user ask: "If something
/// failed, I should be able to retrigger it again." A retry creates a FRESH run (new run_id) from the
/// failed run's persisted inputs, links it back via <see cref="Run.RetriedFrom"/>, and NEVER mutates
/// the source run. Exercised end-to-end against the hermetic <see cref="CoordinatorWebApplicationFactory"/>
/// (FakeCoordinatorSpecDrafter seam — no live model/network): coordinator retries re-draft a fresh
/// outcome spec under the new run_id, pickup retries preserve <see cref="RunOrigin.BacklogPickup"/>
/// without re-claiming a backlog task, regular project retries copy the inputs, and the eligibility,
/// retry-cap, and ownership gates are enforced.
/// </summary>
[Collection("CoordinatorOutcomeSpec")]
public sealed class RunRetryTests : IDisposable
{
    private readonly CoordinatorWebApplicationFactory _factory;
    private readonly HttpClient _owner;
    private readonly List<string> _tempRepos = new();

    public RunRetryTests()
    {
        _factory = new CoordinatorWebApplicationFactory();
        _owner = _factory.CreateOwnerClient();
    }

    public void Dispose()
    {
        _owner.Dispose();
        _factory.Dispose();
        foreach (var d in _tempRepos)
        {
            try { ForceDeleteDirectory(d); } catch { /* best effort */ }
        }
    }

    private SqliteRunStore Runs => _factory.Services.GetRequiredService<SqliteRunStore>();

    // =========================================================================
    // (a) Coordinator Failed -> retry -> fresh resolvable run; source stays Failed.
    // =========================================================================
    [Fact]
    public async Task CoordinatorFailedRun_Retry_CreatesFreshResolvableRun_AndLeavesSourceFailed()
    {
        var projectId = await CreateProjectAsync();
        var source = await SeedRunAsync(
            RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser,
            agentName: "Coordinator", origin: RunOrigin.Interactive, projectId: ProjectId.Parse(projectId));

        var resp = await _owner.PostAsync($"/api/runs/{source.Id}/retry", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var newId = body.GetProperty("run_id").GetString()!;
        newId.Should().NotBe(source.Id.ToString(), "a retry mints a fresh run id");
        body.GetProperty("retried_from").GetString().Should().Be(source.Id.ToString());
        body.GetProperty("status").GetString().Should().Be("in_progress");

        // The fresh coordinator run is resolvable by run_id (200), and exposes its provenance.
        var newRunResp = await _owner.GetAsync($"/api/runs/{newId}");
        newRunResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await newRunResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("retried_from").GetString().Should().Be(source.Id.ToString());

        // A FRESH outcome spec is drafted under the NEW run_id (not copied from the old run).
        var specOk = await PollUntilAsync(async () =>
            (await _owner.GetAsync($"/api/runs/{newId}/outcome-spec")).StatusCode == HttpStatusCode.OK);
        specOk.Should().BeTrue("the retried coordinator run re-drafts its own outcome spec under the new run_id");

        // The source run is untouched — still Failed.
        var sourceResp = await _owner.GetAsync($"/api/runs/{source.Id}");
        sourceResp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await sourceResp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().Should().Be("failed", "a retry never mutates the failed source run");
    }

    // =========================================================================
    // (b) Pickup-origin retry preserves origin + accountable user, no new backlog task.
    // =========================================================================
    [Fact]
    public async Task PickupOriginFailedRun_Retry_PreservesOriginAndUser_WithoutClaimingBacklogTask()
    {
        var projectId = await CreateProjectAsync();
        var pid = ProjectId.Parse(projectId);
        var backlogStore = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        (await backlogStore.ListByProjectAsync(pid)).Should().BeEmpty("precondition: no backlog tasks");

        var source = await SeedRunAsync(
            RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser,
            agentName: "Coordinator", origin: RunOrigin.BacklogPickup, projectId: pid);

        var resp = await _owner.PostAsync($"/api/runs/{source.Id}/retry", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var newId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var newRun = await Runs.GetAsync(RunId.Parse(newId));
        newRun.Should().NotBeNull();
        newRun!.Origin.Should().Be(RunOrigin.BacklogPickup, "a pickup retry preserves the durable origin marker");
        newRun.SubmittingUser.Should().Be(CoordinatorWebApplicationFactory.OwnerUser,
            "the accountable human (original CapturedBy) carries through (Principle IX)");
        newRun.WorkflowRunId.Should().BeNull("identity parity: the pickup retry resolves by run_id");
        newRun.RetriedFrom.Should().Be(source.Id.ToString());

        // No new backlog task was created or claimed by the retry.
        (await backlogStore.ListByProjectAsync(pid)).Should().BeEmpty("a pickup retry must not re-claim or create a backlog task");
    }

    // =========================================================================
    // (c) Regular project Failed -> retry via RunOrchestrator copies the inputs.
    // =========================================================================
    [Fact]
    public async Task RegularProjectFailedRun_Retry_CreatesNewRun_CopyingInputs()
    {
        var repo = CreateTempGitRepo();
        var pid = ProjectId.New();
        var source = await SeedRunAsync(
            RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser,
            agentName: null, origin: RunOrigin.Interactive, projectId: pid,
            repoPath: repo, branch: "main", task: "implement feature X", modelId: "gpt-4o");

        var resp = await _owner.PostAsync($"/api/runs/{source.Id}/retry", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var newId = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("run_id").GetString()!;

        var newRun = await Runs.GetAsync(RunId.Parse(newId));
        newRun.Should().NotBeNull();
        newRun!.Task.Should().Be("implement feature X");
        newRun.ProjectId.Should().Be(pid);
        newRun.RepositoryPath.Should().Be(repo);
        newRun.OriginatingBranch.Should().Be("main");
        newRun.ModelSource.Should().Be(ModelSource.GitHubCopilot);
        newRun.ModelId.Should().Be("gpt-4o");
        newRun.AgentName.Should().BeNull();
        newRun.RetriedFrom.Should().Be(source.Id.ToString());
    }

    // =========================================================================
    // (d) Eligibility gating: non-failure states and child runs are rejected 409.
    // =========================================================================
    [Theory]
    [InlineData(RunStatus.AwaitingReview)]
    [InlineData(RunStatus.Merged)]
    [InlineData(RunStatus.InProgress)]
    [InlineData(RunStatus.Declined)]
    public async Task IneligibleStatus_Retry_Returns409_RunNotRetryable(RunStatus status)
    {
        var source = await SeedRunAsync(status, CoordinatorWebApplicationFactory.OwnerUser);

        var resp = await _owner.PostAsync($"/api/runs/{source.Id}/retry", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("run_not_retryable");
        body.GetProperty("status").GetString().Should().Be(status.ToApiString());
    }

    [Fact]
    public async Task ChildRun_Retry_Returns409_RunNotRetryable()
    {
        var parent = await SeedRunAsync(RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser, agentName: "Coordinator");
        var child = await SeedRunAsync(RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser, parentRunId: parent.Id.ToString());

        var resp = await _owner.PostAsync($"/api/runs/{child.Id}/retry", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("run_not_retryable");
    }

    // =========================================================================
    // (e) Soft cap: a retried_from chain of depth 3 is rejected.
    // =========================================================================
    [Fact]
    public async Task RetryChainDepth3_Retry_Returns409_RetryLimitReached()
    {
        var u = CoordinatorWebApplicationFactory.OwnerUser;
        var a = await SeedRunAsync(RunStatus.Failed, u);
        var b = await SeedRunAsync(RunStatus.Failed, u, retriedFrom: a.Id.ToString());
        var c = await SeedRunAsync(RunStatus.Failed, u, retriedFrom: b.Id.ToString());
        var d = await SeedRunAsync(RunStatus.Failed, u, retriedFrom: c.Id.ToString());

        var resp = await _owner.PostAsync($"/api/runs/{d.Id}/retry", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("retry_limit_reached");
    }

    // =========================================================================
    // (f) Ownership / auth.
    // =========================================================================
    [Fact]
    public async Task NonOwner_Retry_Returns403()
    {
        var source = await SeedRunAsync(RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser);
        using var other = _factory.CreateOtherClient();
        (await other.PostAsync($"/api/runs/{source.Id}/retry", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unauthenticated_Retry_Returns401()
    {
        var source = await SeedRunAsync(RunStatus.Failed, CoordinatorWebApplicationFactory.OwnerUser);
        using var anon = _factory.CreateClient();   // no bearer token
        (await anon.PostAsync($"/api/runs/{source.Id}/retry", content: null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UnknownRun_Retry_Returns404()
    {
        (await _owner.PostAsync($"/api/runs/{RunId.New()}/retry", content: null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    private async Task<Run> SeedRunAsync(
        RunStatus status,
        string submittingUser,
        string? agentName = null,
        string? parentRunId = null,
        RunOrigin origin = RunOrigin.Interactive,
        string? retriedFrom = null,
        ProjectId? projectId = null,
        string? repoPath = null,
        string? branch = null,
        string task = "do the thing",
        string? modelId = "gpt-4o")
    {
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = repoPath ?? Path.Combine(Path.GetTempPath(), "agentweaver-retry-norepo"),
            OriginatingBranch = branch ?? "main",
            ModelSource = ModelSource.GitHubCopilot,
            ModelId = modelId,
            Task = task,
            SubmittingUser = submittingUser,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = status is RunStatus.Failed or RunStatus.MergeFailed or RunStatus.Declined or RunStatus.Merged
                ? DateTimeOffset.UtcNow : null,
            ProjectId = projectId,
            AgentName = agentName,
            ParentRunId = parentRunId,
            Origin = origin,
            RetriedFrom = retriedFrom,
        };
        await Runs.InsertAsync(run);
        return run;
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _owner.PostAsJsonAsync("/api/projects", new
        {
            name = $"Retry Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }

    private string CreateTempGitRepo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), $"agentweaver-retry-repo-{Guid.NewGuid():N}");
        _tempRepos.Add(repoPath);

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

    private static async Task<bool> PollUntilAsync(Func<Task<bool>> predicate, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return true;
            await Task.Delay(50);
        }
        return false;
    }

    private static void ForceDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
        }
        Directory.Delete(path, recursive: true);
    }
}
