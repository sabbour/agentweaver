using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Coordinator;

/// <summary>
/// Regression coverage for backlog-pickup coordinator runs whose accountable
/// <c>Run.SubmittingUser</c> is a captured GitHub login. Project-scoped runs authorize from their
/// persisted <c>ProjectId</c>, so the project owner keeps access regardless of whether that audit
/// identity matches the current API principal or linked GitHub login.
///
/// These tests run against the real in-process host (<see cref="ProjectsWebApplicationFactory"/>) with
/// the sanctioned in-memory <see cref="Agentweaver.Api.Auth.InMemoryGitHubTokenStore"/> (a real
/// component, not a mock — Principle VII). A pickup-shaped run is inserted directly via the real
/// <see cref="SqliteRunStore"/> so the test stays fully hermetic while exercising the persisted-project
/// authorization path.
/// </summary>
public sealed class PickupRunOwnershipTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private const string GitHubLogin = "sabbour";

    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PickupRunOwnershipTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task ProjectOwner_CanView_PickupRun_AttributedToLinkedGitHubLogin()
    {
        // Sign the caller's installation scope in as the GitHub login that captured the task.
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, GitHubLogin, null, Array.Empty<string>()));

        // A pickup coordinator run is accountable to the captured GitHub login, NOT the API principal.
        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the persisted project grants access independently of the run's audit identity");
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("failed");
    }

    [Fact]
    public async Task ProjectOwner_CanStream_PickupRun()
    {
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, GitHubLogin, null, Array.Empty<string>()));

        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        // The stream endpoint must authorize from the persisted project before considering the
        // in-memory stream owner's GitHub identity.
        var streamStore = _factory.Services.GetRequiredService<RunStreamStore>();
        var stream = streamStore.Create(runId, GitHubLogin);
        stream.RecordNext(EventTypes.AgentMessage, new
        {
            messageId = "pickup-owner-authorization",
            content = "project owner can read this pickup stream",
        });
        streamStore.Complete(runId);

        var resp = await _client.GetAsync($"/api/runs/{runId}/stream",
            HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the persisted project owner must be allowed to stream the run");
        resp.Content.Headers.ContentType?.MediaType.Should().Be("text/event-stream");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("project owner can read this pickup stream");
        body.Should().Contain("event: done");
    }

    [Fact]
    public async Task ProjectOwner_CanView_RunWithDifferentSubmittingIdentity()
    {
        // SubmittingUser is an audit identity, not the authorization boundary for project-scoped runs.
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, GitHubLogin, null, Array.Empty<string>()));

        var runId = await InsertPickupRunAsync(submittingUser: "a-different-github-login");

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the caller owns the persisted project even though the run has a different submitting identity");
    }

    [Fact]
    public async Task ProjectOwner_CanView_InteractiveRun_WhenSignedOut()
    {
        // No GitHub identity: project authorization still resolves through the API-key principal.
        await _factory.TokenStore.SignOutAsync(GitHubTokenScope.Installation);

        var runId = await InsertPickupRunAsync(
            submittingUser: ProjectsWebApplicationFactory.TestUser, origin: RunOrigin.Interactive);

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "project authorization does not depend on GitHub sign-in state");
    }

    [Fact]
    public async Task ProjectOwner_CanView_PickupRun_WhenSignedOut()
    {
        // The persisted project, not the linked GitHub identity, is authoritative.
        await _factory.TokenStore.SignOutAsync(GitHubTokenScope.Installation);

        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        var resp = await _client.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the project owner remains authorized without a linked GitHub session");
    }

    [Fact]
    public async Task Unauthenticated_Returns401()
    {
        var runId = await InsertPickupRunAsync(submittingUser: GitHubLogin);

        using var anon = _factory.CreateClient();   // no bearer token
        var resp = await anon.GetAsync($"/api/runs/{runId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the API-key gate rejects unauthenticated callers before any ownership logic");
    }

    /// <summary>
    /// Inserts a coordinator pickup-shaped run directly via the real run store and returns its run_id.
    /// Status is Failed (terminal) so no background service or orchestration runs — the test stays
    /// hermetic while faithfully reproducing a picked-up run's identity shape (Origin=BacklogPickup,
    /// SubmittingUser = the captured GitHub login, AgentName="Coordinator", WorkflowRunId=null).
    /// </summary>
    private async Task<string> InsertPickupRunAsync(string submittingUser, RunOrigin origin = RunOrigin.BacklogPickup)
    {
        var projectId = await CreateProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();

        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "pickup ownership regression",
            SubmittingUser    = submittingUser,
            Status            = RunStatus.Failed,
            StartedAt         = DateTimeOffset.UtcNow,
            EndedAt           = DateTimeOffset.UtcNow,
            ProjectId         = ProjectId.Parse(projectId),
            ModelId           = "gpt-4o",
            AgentName         = "Coordinator",
            WorkflowRunId     = null,
            Origin            = origin,
        };
        await runStore.InsertAsync(run);
        return run.Id.ToString();
    }

    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Pickup Owner Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }
}
