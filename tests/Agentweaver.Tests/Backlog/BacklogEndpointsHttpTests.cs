using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// HTTP integration tests for the Feature 009 backlog endpoints that exercise behaviour the
/// store-level tests cannot: the capture handler resolving the signed-in GitHub login as CapturedBy,
/// and the bulk "send all to Ready" endpoint. Runs against a real in-process API host
/// (<see cref="ProjectsWebApplicationFactory"/>) with the sanctioned in-memory
/// <see cref="Agentweaver.Api.Auth.InMemoryGitHubTokenStore"/> (a real component, not a mock —
/// Principle VII). The default scope provider is the fixed installation scope, so the caller's token
/// lives under <see cref="GitHubTokenScope.Installation"/>.
/// </summary>
public sealed class BacklogEndpointsHttpTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BacklogEndpointsHttpTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // =========================================================================
    // CAPTURE: persist the signed-in GitHub login as CapturedBy (falls back to caller.User).
    // =========================================================================
    [Fact]
    public async Task Capture_WhenSignedIn_PersistsGitHubLoginAsCapturedBy()
    {
        // Sign the installation scope in as GitHub login "sabbour".
        await _factory.TokenStore.SetAsync(
            GitHubTokenScope.Installation,
            new GitHubToken("access-tok", null, null, "sabbour", null, Array.Empty<string>()));

        var projectId = await CreateProjectAsync();
        var task = await CaptureAsync(projectId, "Signed-in capture");

        task.GetProperty("captured_by").GetString().Should().Be(
            "sabbour", "the signed-in GitHub login must be stored as who captured the task");
    }

    [Fact]
    public async Task Capture_WhenSignedOut_FallsBackToCallerUser()
    {
        // Explicit signed-out tombstone for the installation scope.
        await _factory.TokenStore.SignOutAsync(GitHubTokenScope.Installation);

        var projectId = await CreateProjectAsync();
        var task = await CaptureAsync(projectId, "Signed-out capture");

        task.GetProperty("captured_by").GetString().Should().Be(
            ProjectsWebApplicationFactory.TestUser,
            "with no signed-in GitHub identity, CapturedBy falls back to the API-key Auth:User");
    }

    // =========================================================================
    // READY-ALL: bulk promote Backlog -> Ready.
    // =========================================================================
    [Fact]
    public async Task ReadyAll_PromotesAllBacklogTasks_ReturnsMovedCount()
    {
        var projectId = await CreateProjectAsync();
        await CaptureAsync(projectId, "task one");
        await CaptureAsync(projectId, "task two");
        await CaptureAsync(projectId, "task three");

        var resp = await _client.PostAsync($"/api/projects/{projectId}/backlog/ready-all", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("moved").GetInt32().Should().Be(3);

        // All tasks now sit in the Ready column on the board; the Backlog column is empty.
        var board = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/board");
        CountCardsInColumn(board, "backlog").Should().Be(0);
        CountCardsInColumn(board, "ready").Should().Be(3);
    }

    [Fact]
    public async Task ReadyAll_EmptyBacklog_IsIdempotent_MovedZero()
    {
        var projectId = await CreateProjectAsync();

        var resp = await _client.PostAsync($"/api/projects/{projectId}/backlog/ready-all", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("moved").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task ReadyAll_UnknownProject_Returns404()
    {
        var resp = await _client.PostAsync(
            $"/api/projects/{ProjectId.New()}/backlog/ready-all", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ReadyAll_Unauthenticated_Returns401_LikeSiblingEndpoints()
    {
        var projectId = await CreateProjectAsync();

        using var anon = _factory.CreateClient();   // no bearer token
        var resp = await anon.PostAsync($"/api/projects/{projectId}/backlog/ready-all", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ArchiveTask_RemovesTaskFromBoard()
    {
        var projectId = await CreateProjectAsync();
        var task = await CaptureAsync(projectId, "archive me");
        var taskId = task.GetProperty("task_id").GetString();

        var resp = await _client.PostAsync(
            $"/api/projects/{projectId}/backlog/tasks/{taskId}/archive", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var archived = await resp.Content.ReadFromJsonAsync<JsonElement>();
        archived.GetProperty("archived_at").GetString().Should().NotBeNullOrWhiteSpace();

        var board = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/board");
        CountCardsInColumn(board, "backlog").Should().Be(0);
        CountCardsInColumn(board, "ready").Should().Be(0);
    }

    // =========================================================================
    // Helpers
    // =========================================================================
    private async Task<string> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Backlog Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }

    private async Task<JsonElement> CaptureAsync(string projectId, string title)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks", new { title });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "capturing a task must return 201");
        return await resp.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static int CountCardsInColumn(JsonElement board, string columnId) =>
        board.GetProperty("columns").EnumerateArray()
            .Where(c => c.GetProperty("id").GetString() == columnId)
            .Sum(c => c.GetProperty("cards").GetArrayLength());
}
