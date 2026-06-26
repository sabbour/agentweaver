using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Projects;

/// <summary>
/// Integration tests for the project CRUD and run endpoints.
/// Uses ProjectsWebApplicationFactory which wires InMemoryGitHubTokenStore and
/// a no-op git initializer so tests do not touch real git or the OS credential store.
/// </summary>
public sealed class ProjectEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ProjectEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private string NewWorkingDir() => _factory.NewWorkingDirectory();

    private async Task<string> CreateBlankProjectAsync(string? name = null, string? dir = null)
    {
        dir ??= NewWorkingDir();
        var request = new CreateProjectRequest
        {
            Name             = name ?? $"Test Project {Guid.NewGuid():N}",
            Origin           = "blank",
            WorkingDirectory = dir,
        };
        var response = await _client.PostAsJsonAsync("/api/projects", request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    // =========================================================================
    // PE-01: POST /api/projects (blank) returns 201 with project fields
    // =========================================================================
    [Fact]
    public async Task PostProject_Blank_Returns201()
    {
        var dir  = NewWorkingDir();
        var body = new CreateProjectRequest
        {
            Name             = "Integration Blank Project",
            Origin           = "blank",
            WorkingDirectory = dir,
        };

        var response = await _client.PostAsJsonAsync("/api/projects", body);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var result = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        result.Should().NotBeNull();
        result!.Name.Should().Be("Integration Blank Project");
        result.Origin.Should().Be("blank");
        result.State.Should().Be("active");
        result.Available.Should().BeTrue();
        response.Headers.Location.Should().NotBeNull();
    }

    // =========================================================================
    // PE-02: GET /api/projects lists created projects
    // =========================================================================
    [Fact]
    public async Task GetProjects_ListsCreatedProjects()
    {
        var name = $"Listed Project {Guid.NewGuid():N}";
        await CreateBlankProjectAsync(name);

        var response = await _client.GetAsync("/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        list.Should().NotBeNull();
        list!.Any(p => p.GetProperty("name").GetString() == name).Should().BeTrue();
    }

    // =========================================================================
    // PE-03: GET /api/projects/{id} returns the project
    // =========================================================================
    [Fact]
    public async Task GetProject_ById_ReturnsProject()
    {
        var id = await CreateBlankProjectAsync("Show Project");

        var response = await _client.GetAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await response.Content.ReadFromJsonAsync<ProjectResponse>();
        result!.ProjectId.Should().Be(id);
        result.Name.Should().Be("Show Project");
    }

    // =========================================================================
    // PE-04: GET /api/projects/{id} returns 404 for unknown id
    // =========================================================================
    [Fact]
    public async Task GetProject_UnknownId_Returns404()
    {
        var response = await _client.GetAsync($"/api/projects/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PE-05: PATCH /api/projects/{id} renames the project
    // =========================================================================
    [Fact]
    public async Task PatchProject_Rename_Returns204()
    {
        var id = await CreateBlankProjectAsync("Original Name");

        var response = await _client.SendAsync(new HttpRequestMessage(
            HttpMethod.Patch, $"/api/projects/{id}")
        {
            Content = JsonContent.Create(new { name = "Renamed Project" }),
        });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await _client.GetAsync($"/api/projects/{id}");
        var result  = await getResp.Content.ReadFromJsonAsync<ProjectResponse>();
        result!.Name.Should().Be("Renamed Project");
    }

    // =========================================================================
    // PE-06: PUT /api/projects/{id}/provider-settings updates provider config
    // =========================================================================
    [Fact]
    public async Task PutProviderSettings_Returns204()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.PutAsJsonAsync(
            $"/api/projects/{id}/provider-settings",
            new UpdateProjectProviderSettingsRequest
            {
                DefaultProvider           = "microsoft-foundry",
                DefaultModelMicrosoftFoundry = "gpt-4o",
            });

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResp = await _client.GetAsync($"/api/projects/{id}");
        var result  = await getResp.Content.ReadFromJsonAsync<ProjectResponse>();
        result!.DefaultProvider.Should().Be("microsoft-foundry");
        result.DefaultModelMicrosoftFoundry.Should().Be("gpt-4o");
    }

    // =========================================================================
    // PE-07: DELETE /api/projects/{id}?confirm=true returns 204
    // =========================================================================
    [Fact]
    public async Task DeleteProject_WithConfirm_Returns204()
    {
        var id = await CreateBlankProjectAsync("To Delete");

        var response = await _client.DeleteAsync($"/api/projects/{id}?confirm=true");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Project should no longer be findable
        var getResp = await _client.GetAsync($"/api/projects/{id}");
        getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // =========================================================================
    // PE-08: DELETE /api/projects/{id} without confirm=true returns 400
    // =========================================================================
    [Fact]
    public async Task DeleteProject_WithoutConfirm_Returns400()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.DeleteAsync($"/api/projects/{id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // PE-09: GET /api/projects/{id}/runs returns empty list for new project
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_EmptyForNewProject()
    {
        var id = await CreateBlankProjectAsync();

        var response = await _client.GetAsync($"/api/projects/{id}/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        runs.Should().NotBeNull();
        runs.Should().BeEmpty();
    }

    // =========================================================================
    // PE-10: GET /api/projects/{id}/runs returns runs for that project
    // =========================================================================
    [Fact]
    public async Task GetProjectRuns_ReturnsRunsForProject()
    {
        var id       = await CreateBlankProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();

        // Insert a run for this project directly
        var projectId = ProjectId.Parse(id);
        var run = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "endpoint test task",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Failed,
            StartedAt         = DateTimeOffset.UtcNow,
            EndedAt           = DateTimeOffset.UtcNow,
            ProjectId         = projectId,
        };
        await runStore.InsertAsync(run);

        var response = await _client.GetAsync($"/api/projects/{id}/runs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        runs.Should().NotBeNull();
        runs!.Any(r => r.GetProperty("execution_id").GetString() == run.Id.ToString())
             .Should().BeTrue();
    }

    [Fact]
    public async Task GetProjectRuns_CanFilterTerminalAgentHistoryIncludingChildren()
    {
        var id       = await CreateBlankProjectAsync();
        var runStore = _factory.Services.GetRequiredService<SqliteRunStore>();
        var projectId = ProjectId.Parse(id);
        var now = DateTimeOffset.UtcNow;
        var parentId = RunId.New();

        await runStore.InsertAsync(new Run
        {
            Id                = parentId,
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Coordinator",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.InProgress,
            StartedAt         = now.AddMinutes(-20),
            ProjectId         = projectId,
            AgentName         = "Coordinator",
        });

        var adaChild = new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Ada terminal work",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.Completed,
            StartedAt         = now.AddMinutes(-10),
            EndedAt           = now.AddMinutes(-5),
            ProjectId         = projectId,
            AgentName         = "Ada",
            ParentRunId       = parentId.ToString(),
            SubtaskId         = "1",
        };
        await runStore.InsertAsync(adaChild);

        await runStore.InsertAsync(new Run
        {
            Id                = RunId.New(),
            RepositoryPath    = NewWorkingDir(),
            OriginatingBranch = "main",
            ModelSource       = ModelSource.GitHubCopilot,
            Task              = "Ada active work",
            SubmittingUser    = ProjectsWebApplicationFactory.TestUser,
            Status            = RunStatus.InProgress,
            StartedAt         = now.AddMinutes(-1),
            ProjectId         = projectId,
            AgentName         = "Ada",
            ParentRunId       = parentId.ToString(),
            SubtaskId         = "2",
        });

        var response = await _client.GetAsync($"/api/projects/{id}/runs?agent=Ada&terminal_only=true&include_children=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runs = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        runs.Should().ContainSingle();
        runs![0].GetProperty("execution_id").GetString().Should().Be(adaChild.Id.ToString());
        runs[0].GetProperty("status").GetString().Should().Be("completed");
        runs[0].GetProperty("agent_name").GetString().Should().Be("Ada");
    }

    // =========================================================================
    // PE-11: POST /api/projects/{id}/runs on a deleting project returns 409
    // =========================================================================
    [Fact]
    public async Task PostProjectRun_OnDeletingProject_Returns409()
    {
        var id       = await CreateBlankProjectAsync();
        var projectId = ProjectId.Parse(id);

        // Force the project into Deleting state directly via the store
        var store = _factory.Services.GetRequiredService<SqliteProjectStore>();
        await store.TryBeginDeleteAsync(projectId);

        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{id}/runs",
            new CreateProjectRunRequest { Task = "should be rejected" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetString().Should().Be("project_deleting");
    }

    // =========================================================================
    // PE-12: POST /api/projects with missing required fields returns 400
    // =========================================================================
    [Fact]
    public async Task PostProject_MissingName_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            origin           = "blank",
            working_directory = NewWorkingDir(),
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // =========================================================================
    // PE-13: GET /api/auth/github returns status
    // =========================================================================
    [Fact]
    public async Task GetAuthGitHub_ReturnsStatus()
    {
        var response = await _client.GetAsync("/api/auth/github");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("status", out _).Should().BeTrue();
    }

    // =========================================================================
    // PE-14: POST /api/auth/github/sign-out returns 204
    // =========================================================================
    [Fact]
    public async Task PostSignOut_Returns204()
    {
        var response = await _client.PostAsync("/api/auth/github/sign-out", null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
