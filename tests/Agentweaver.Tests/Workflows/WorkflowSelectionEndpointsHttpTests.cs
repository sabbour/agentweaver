using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Workflows;

/// <summary>
/// HTTP integration tests for the workflow-selection endpoints (Feature 010, FR-041/FR-042): setting a
/// project's default workflow and a per-task workflow override. Runs against a real in-process API host
/// (<see cref="ProjectsWebApplicationFactory"/>) over a real SQLite DB and the real
/// <c>WorkflowRegistry</c> reading each project's materialized <c>.agentweaver/workflows/</c> — no mocks
/// (Principle VII). Owner-authorized like the sibling workflow endpoints.
/// </summary>
public sealed class WorkflowSelectionEndpointsHttpTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WorkflowSelectionEndpointsHttpTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    // ── Default workflow (FR-041) ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetDefault_ToResolvableWorkflow_PersistsAndIsReflected()
    {
        var (projectId, dir) = await CreateProjectAsync();
        await WriteCustomWorkflowAsync(dir, "custom-flow");
        (await _client.PostAsync($"/api/projects/{projectId}/workflows/sync", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/default", new { workflow_id = "custom-flow" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("default_workflow_id").GetString().Should().Be("custom-flow");

        // Persisted: the list endpoint reports the new default on a fresh request.
        var list = await _client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/workflows");
        list.GetProperty("default_workflow_id").GetString().Should().Be("custom-flow");
    }

    [Fact]
    public async Task SetDefault_ToNull_ClearsBackToBuiltInDefault()
    {
        var (projectId, dir) = await CreateProjectAsync();
        await WriteCustomWorkflowAsync(dir, "custom-flow");
        await _client.PostAsync($"/api/projects/{projectId}/workflows/sync", null);
        await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/default", new { workflow_id = "custom-flow" });

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/default", new { workflow_id = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("default_workflow_id").GetString().Should().Be("default");
    }

    [Fact]
    public async Task SetDefault_ToUnknownWorkflow_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/default", new { workflow_id = "does-not-exist" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("unknown_workflow_id");
    }

    [Fact]
    public async Task SetDefault_UnknownProject_Returns404()
    {
        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{ProjectId.New()}/workflows/default", new { workflow_id = "default" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetDefault_NotOwner_Returns403()
    {
        // A project owned by someone else is inserted directly; the authenticated caller is not the owner.
        var store = _factory.Services.GetRequiredService<IProjectStore>();
        var foreign = MakeProject() with { Owner = "someone-else", WorkingDirectory = _factory.NewWorkingDirectory() };
        await store.InsertAsync(foreign);

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{foreign.Id}/workflows/default", new { workflow_id = "default" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SetDefault_Unauthenticated_Returns401()
    {
        var (projectId, _) = await CreateProjectAsync();
        using var anon = _factory.CreateClient();   // no bearer token
        var resp = await anon.PutAsJsonAsync(
            $"/api/projects/{projectId}/workflows/default", new { workflow_id = "default" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Per-task override (FR-042) ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task SetOverride_OnUnclaimedTask_PersistsAndCanBeCleared()
    {
        var (projectId, _) = await CreateProjectAsync();
        var taskId = await CaptureTaskAsync(projectId, "override me");

        var set = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override",
            new { workflow_id = "default" });
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await set.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("task_id").GetString().Should().Be(taskId);
        body.GetProperty("workflow_override_id").GetString().Should().Be("default");

        var clear = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override",
            new { workflow_id = (string?)null });
        clear.StatusCode.Should().Be(HttpStatusCode.OK);
        (await clear.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("workflow_override_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task SetOverride_ToUnknownWorkflow_Returns400()
    {
        var (projectId, _) = await CreateProjectAsync();
        var taskId = await CaptureTaskAsync(projectId, "bad override");

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override",
            new { workflow_id = "does-not-exist" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("unknown_workflow_id");
    }

    [Fact]
    public async Task SetOverride_UnknownTask_Returns404()
    {
        var (projectId, _) = await CreateProjectAsync();

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks/{BacklogTaskId.New()}/workflow-override",
            new { workflow_id = "default" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetOverride_OnClaimedTask_Returns409()
    {
        var (projectId, _) = await CreateProjectAsync();
        var taskId = await CaptureTaskAsync(projectId, "claim me");
        var pid = ProjectId.Parse(projectId);
        var tid = BacklogTaskId.Parse(taskId);

        // Move to Ready, then claim through the real atomic claim+reserve transaction.
        (await _client.PostAsync($"/api/projects/{projectId}/backlog/tasks/{taskId}/ready", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var backlog = _factory.Services.GetRequiredService<IBacklogTaskStore>();
        var claim = await backlog.TryClaimAndReserveCoordinatorRunAsync(
            pid, tid, MakeCoordinatorRun(pid, RunId.New()), DateTimeOffset.UtcNow);
        claim.Should().Be(ClaimReserveResult.Won);

        var resp = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks/{taskId}/workflow-override",
            new { workflow_id = "default" });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("task_claimed");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<(string ProjectId, string WorkingDirectory)> CreateProjectAsync()
    {
        var dir = _factory.NewWorkingDirectory();
        var resp = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Workflow Selection Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "the test project must be created");
        var id = (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
        return (id, dir);
    }

    private async Task<string> CaptureTaskAsync(string projectId, string title)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks", new { title });
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "capturing a task must return 201");
        return (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("task_id").GetString()!;
    }

    /// <summary>Writes a second, valid workflow (with the given id) into the project's workflows dir so
    /// a Sync discovers it. Derived from the real built-in default so it validates identically.</summary>
    private static async Task WriteCustomWorkflowAsync(string workingDir, string workflowId)
    {
        var yaml = Agentweaver.Api.Workflows.DefaultWorkflowTemplate.Yaml
            .Replace("id: default", $"id: {workflowId}")
            .Replace("name: Default Run Workflow", $"name: Custom {workflowId}");
        var dir = Path.Combine(workingDir, ".agentweaver", "workflows");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "custom.yaml"), yaml);
    }
}
