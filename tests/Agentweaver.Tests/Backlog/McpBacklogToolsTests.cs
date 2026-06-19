using System.Net.Http.Json;
using FluentAssertions;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Integration tests for the MCP BacklogTools targeting the send_all_backlog_to_ready tool.
/// Uses the same in-process API factory seam as sibling HTTP tests (no mocks — Principle VII).
/// </summary>
public sealed class McpBacklogToolsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;

    public McpBacklogToolsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private BacklogTools CreateTools()
    {
        // The factory client has BaseAddress=http://localhost/ — AgentweaverApiClient
        // will re-set it to the same value (http://localhost/) without conflict.
        var httpClient = _factory.CreateClient();
        var config = new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey);
        var apiClient = new AgentweaverApiClient(httpClient, config);
        return new BacklogTools(apiClient);
    }

    private async Task<string> CreateProjectAsync()
    {
        using var httpClient = _factory.CreateAuthenticatedClient();
        var dir = _factory.NewWorkingDirectory();
        var resp = await httpClient.PostAsJsonAsync("/api/projects", new
        {
            name = $"MCP Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task CaptureAsync(string projectId, string title)
    {
        using var httpClient = _factory.CreateAuthenticatedClient();
        var resp = await httpClient.PostAsJsonAsync(
            $"/api/projects/{projectId}/backlog/tasks", new { title });
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SendAllBacklogToReady_PromotesMultipleTasks_ReturnsCorrectSummary()
    {
        var projectId = await CreateProjectAsync();
        await CaptureAsync(projectId, "alpha");
        await CaptureAsync(projectId, "beta");
        await CaptureAsync(projectId, "gamma");

        var tools = CreateTools();
        var result = await tools.SendAllBacklogToReadyAsync(projectId, CancellationToken.None);

        result.Should().Be("Promoted 3 backlog task(s) to Ready.");
    }

    [Fact]
    public async Task SendAllBacklogToReady_EmptyBacklog_ReturnsNoTasksMessage()
    {
        var projectId = await CreateProjectAsync();

        var tools = CreateTools();
        var result = await tools.SendAllBacklogToReadyAsync(projectId, CancellationToken.None);

        result.Should().Be("No backlog tasks to promote.");
    }

    [Fact]
    public async Task SendAllBacklogToReady_IsIdempotent_SecondCallAlsoReturnsNoTasks()
    {
        var projectId = await CreateProjectAsync();
        await CaptureAsync(projectId, "once");

        var tools = CreateTools();
        var first = await tools.SendAllBacklogToReadyAsync(projectId, CancellationToken.None);
        var second = await tools.SendAllBacklogToReadyAsync(projectId, CancellationToken.None);

        first.Should().Be("Promoted 1 backlog task(s) to Ready.");
        second.Should().Be("No backlog tasks to promote.");
    }

    [Fact]
    public async Task SendAllBacklogToReady_UnknownProject_ThrowsMcpApiException404()
    {
        var tools = CreateTools();
        var act = () => tools.SendAllBacklogToReadyAsync(Guid.NewGuid().ToString("N"), CancellationToken.None);

        await act.Should().ThrowAsync<McpApiException>()
            .Where(ex => ex.StatusCode == 404);
    }
}
