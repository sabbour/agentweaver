using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Notifications;

/// <summary>
/// Integration tests for GET /api/notifications (#247 global notification center MVP).
/// Uses ProjectsWebApplicationFactory so created projects/runs are naturally owned by the
/// authenticated test user, matching the auth conventions used by ProjectEndpointsTests.
/// </summary>
public sealed class NotificationsEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public NotificationsEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    private string NewWorkingDir() => _factory.NewWorkingDirectory();

    private async Task<string> CreateBlankProjectAsync(string? name = null)
    {
        var request = new CreateProjectRequest
        {
            Name = name ?? $"Notif Test Project {Guid.NewGuid():N}",
            Origin = "blank",
            WorkingDirectory = NewWorkingDir(),
        };
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task<Run> InsertAwaitingReviewRunAsync(string projectId, string task, string? agentName = null, string? workflowRunId = null)
    {
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = task,
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ProjectId = ProjectId.Parse(projectId),
            AgentName = agentName,
            WorkflowRunId = workflowRunId,
        };
        await runStore.InsertAsync(run);
        return run;
    }

    [Fact]
    public async Task GetNotifications_SurfacesOwnedAwaitingReviewRun()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project A");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Implement the checkout flow", "Coordinator", "wf-run-1");

        var response = await _client.GetAsync("/api/notifications");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        var match = notifications.Should().ContainSingle(n => n.GetProperty("run_id").GetString() == run.Id.ToString())
            .Subject;
        match.GetProperty("type").GetString().Should().Be("human_review");
        match.GetProperty("project_id").GetString().Should().Be(projectId);
        match.GetProperty("agent_name").GetString().Should().Be("Coordinator");
        match.GetProperty("title").GetString().Should().Be("Implement the checkout flow");
        match.GetProperty("cta_path").GetString().Should().Be($"/projects/{projectId}/orchestrations/wf-run-1");
    }

    [Fact]
    public async Task GetNotifications_FallsBackToRunIdInCtaPath_WhenWorkflowRunIdMissing()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project B");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Legacy run with no workflow id");

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        match.GetProperty("cta_path").GetString().Should().Be($"/projects/{projectId}/orchestrations/{run.Id}");
    }

    [Fact]
    public async Task GetNotifications_ExcludesNonAwaitingReviewRuns()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project C");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var inProgress = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Still working",
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
            ProjectId = ProjectId.Parse(projectId),
        };
        await runStore.InsertAsync(inProgress);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == inProgress.Id.ToString());
    }

    [Fact]
    public async Task GetNotifications_ExcludesArchivedRuns()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project D");
        var run = await InsertAwaitingReviewRunAsync(projectId, "Archived review");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        await runStore.ArchiveAsync(run.Id, DateTimeOffset.UtcNow);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
    }

    [Fact]
    public async Task GetNotifications_ExcludesOtherUsersRuns()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project E");
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var othersProject = ProjectId.New();
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Someone else's review",
            SubmittingUser = "someone-else",
            Status = RunStatus.AwaitingReview,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            EndedAt = DateTimeOffset.UtcNow,
            ProjectId = othersProject,
        };
        // No project row is inserted for othersProject, so it can never be "owned" by the caller —
        // this exercises the project-ownership filter, not just SubmittingUser matching.
        await runStore.InsertAsync(run);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var notifications = body.GetProperty("notifications").EnumerateArray().ToList();

        notifications.Should().NotContain(n => n.GetProperty("run_id").GetString() == run.Id.ToString());
        _ = projectId; // keep the owned project created so the factory/db is exercised consistently
    }

    [Fact]
    public async Task GetNotifications_TruncatesLongTaskIntoTitle()
    {
        var projectId = await CreateBlankProjectAsync("Notif Project F");
        var longTask = string.Concat(Enumerable.Repeat("word ", 40)).Trim();
        var run = await InsertAwaitingReviewRunAsync(projectId, longTask);

        var response = await _client.GetAsync("/api/notifications");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var match = body.GetProperty("notifications").EnumerateArray()
            .Single(n => n.GetProperty("run_id").GetString() == run.Id.ToString());

        var title = match.GetProperty("title").GetString();
        title.Should().NotBeNull();
        title!.Length.Should().BeLessThanOrEqualTo(121, "the title should be truncated to ~120 chars plus the ellipsis");
        title.Should().EndWith("\u2026");
    }
}
