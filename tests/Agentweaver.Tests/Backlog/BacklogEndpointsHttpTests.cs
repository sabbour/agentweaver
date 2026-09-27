using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// HTTP integration tests for the backlog endpoints that exercise behavior the store-level tests
/// cannot, including protected automation-invocation handoffs. Runs against a real in-process API
/// host (<see cref="ProjectsWebApplicationFactory"/>).
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

    [Theory]
    [InlineData("external_id", "automation-invocation:forged")]
    [InlineData("client_request_id", "workflow-event-trigger:forged")]
    public async Task Capture_ReservedAutomationExternalIds_AreRejected(string propertyName, string externalId)
    {
        var projectId = await CreateProjectAsync();
        var body = propertyName == "external_id"
            ? new Dictionary<string, string> { ["title"] = "forged automation", ["external_id"] = externalId }
            : new Dictionary<string, string> { ["title"] = "forged automation", ["client_request_id"] = externalId };

        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/backlog/tasks", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "only the server-side trusted trigger path may associate a task with automation");
    }

    [Fact]
    public async Task Capture_IgnoresClientSuppliedProvisionalAutomationMarker()
    {
        var projectId = await CreateProjectAsync();
        var response = await _client.PostAsJsonAsync($"/api/projects/{projectId}/backlog/tasks", new
        {
            title = "ordinary task",
            is_automation_invocation_pending = true,
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var taskId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("task_id").GetString()!;
        var task = await _factory.Services.GetRequiredService<IBacklogTaskStore>()
            .GetAsync(ProjectId.Parse(projectId), BacklogTaskId.Parse(taskId));
        task!.IsAutomationInvocationPending.Should().BeFalse(
            "the provisional marker is never accepted from a contributor request");
        (await _client.PostAsync($"/api/projects/{projectId}/backlog/tasks/{taskId}/ready", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK,
                "ordinary contributor backlog work must remain promotable");
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
    public async Task Dependencies_PreviewEditAndReadProjection_UseOneRevisionedContract()
    {
        var project = await CreateProjectAsync();
        var prerequisite = (await CaptureAsync(project, "First story")).GetProperty("task_id").GetString()!;
        var dependent = (await CaptureAsync(project, "Second story")).GetProperty("task_id").GetString()!;
        var path = $"/api/projects/{project}/backlog/tasks/{dependent}/dependencies";
        var revision = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{project}/backlog/dependencies/revision");
        revision.GetProperty("revision").GetInt64().Should().Be(0);

        var preview = await _client.PostAsJsonAsync($"{path}?preview=true",
            new { expected_revision = 0, add = new[] { prerequisite } });
        preview.StatusCode.Should().Be(HttpStatusCode.OK);
        (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("affected_task_ids").EnumerateArray().Select(id => id.GetString())
            .Should().Contain(dependent);
        var before = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/backlog/tasks/{dependent}");
        before.GetProperty("depends_on_task_ids").GetArrayLength().Should().Be(0);

        var edited = await _client.PostAsJsonAsync(path,
            new { expected_revision = 0, add = new[] { prerequisite } });
        edited.StatusCode.Should().Be(HttpStatusCode.OK);
        (await edited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64().Should().Be(1);
        (await _client.PostAsJsonAsync(path, new { expected_revision = 0, remove = new[] { prerequisite } }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        var task = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/backlog/tasks/{dependent}");
        task.GetProperty("prerequisites")[0].GetProperty("reason").GetString().Should().Be("pending");
        task.GetProperty("graph_revision").GetInt64().Should().Be(1);
        var upstream = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{project}/backlog/tasks/{prerequisite}");
        upstream.GetProperty("dependents_task_ids")[0].GetString().Should().Be(dependent);
    }

    [Fact]
    public async Task Ready_ProvisionalAutomationTask_IsNotPromotedByContributorEndpoint()
    {
        var projectId = await CreateProjectAsync();
        var provisional = await CreateProvisionalAutomationTaskAsync(projectId);

        var response = await _client.PostAsync(
            $"/api/projects/{projectId}/backlog/tasks/{provisional.Id}/ready", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var persisted = await _factory.Services.GetRequiredService<IBacklogTaskStore>()
            .GetAsync(ProjectId.Parse(projectId), provisional.Id);
        persisted.Should().NotBeNull();
        persisted!.State.Should().Be(BacklogTaskState.Backlog);
        persisted.IsAutomationInvocationPending.Should().BeTrue(
            "only the trusted trigger publication path may release its provisional task");
    }

    [Fact]
    public async Task ReadyAll_LeavesProvisionalAutomationTaskInBacklog()
    {
        var projectId = await CreateProjectAsync();
        var provisional = await CreateProvisionalAutomationTaskAsync(projectId);
        await CaptureAsync(projectId, "ordinary contributor task");

        var response = await _client.PostAsync($"/api/projects/{projectId}/backlog/ready-all", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("moved").GetInt32().Should().Be(1);
        var tasks = await _factory.Services.GetRequiredService<IBacklogTaskStore>()
            .ListByProjectAsync(ProjectId.Parse(projectId));
        tasks.Should().ContainSingle(t => t.Id == provisional.Id
            && t.State == BacklogTaskState.Backlog
            && t.IsAutomationInvocationPending);
        tasks.Should().ContainSingle(t => t.Title == "ordinary contributor task"
            && t.State == BacklogTaskState.Ready);
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

    private async Task<BacklogTask> CreateProvisionalAutomationTaskAsync(string projectId)
    {
        var task = new BacklogTask
        {
            Id = BacklogTaskId.New(),
            ProjectId = ProjectId.Parse(projectId),
            Title = "trusted automation invocation",
            State = BacklogTaskState.Backlog,
            OrderKey = "n",
            CapturedBy = "automation:test",
            CreatedAt = DateTimeOffset.UtcNow,
            IsAutomationInvocationPending = true,
        };
        await _factory.Services.GetRequiredService<IBacklogTaskStore>().InsertAsync(task);
        return task;
    }

    private static int CountCardsInColumn(JsonElement board, string columnId) =>
        board.GetProperty("columns").EnumerateArray()
            .Where(c => c.GetProperty("id").GetString() == columnId)
            .Sum(c => c.GetProperty("cards").GetArrayLength());
}
