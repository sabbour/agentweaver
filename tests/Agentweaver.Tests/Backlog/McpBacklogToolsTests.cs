using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Auth;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// Integration tests for the MCP BacklogTools targeting the send_all_backlog_to_ready tool.
/// Uses the same in-process API factory seam as sibling HTTP tests (no mocks — Principle VII).
/// </summary>
public sealed class McpBacklogToolsTests : IClassFixture<EntraWebApplicationFactory>
{
    private readonly EntraWebApplicationFactory _factory;

    public McpBacklogToolsTests(EntraWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private BacklogTools CreateTools()
    {
        // The factory client has BaseAddress=http://localhost/ — AgentweaverApiClient
        // will re-set it to the same value (http://localhost/) without conflict.
        var httpClient = _factory.CreateClient();
        var config = new McpConfig("http://localhost",
            _factory.CreateBearerToken("mcp-backlog-owner", PlatformRoles.ProjectCreator, PlatformRoles.Contributor));
        var apiClient = new AgentweaverApiClient(httpClient, config);
        return new BacklogTools(apiClient);
    }

    private async Task<string> CreateProjectAsync()
    {
        using var httpClient = _factory.CreateAuthenticatedClientForObjectId(
            "mcp-backlog-owner", PlatformRoles.ProjectCreator, PlatformRoles.Contributor);
        var dir = _factory.NewWorkingDirectory();
        var resp = await httpClient.PostAsJsonAsync("/api/projects", new
        {
            name = $"MCP Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });
        resp.EnsureSuccessStatusCode();
        var providers = _factory.Services.GetRequiredService<ByokProviderConfigurationService>();
        var provider = await providers.AddAsync(new ByokProviderConfiguration(
            "unused", "MCP backlog test", "azure", "https://backlog.example.test",
            "gpt-4.1", "test-mcp-backlog-key"), CancellationToken.None);
        await providers.SetActiveAsync(provider.Id, CancellationToken.None);
        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    private async Task CaptureAsync(string projectId, string title)
    {
        using var httpClient = _factory.CreateAuthenticatedClientForObjectId(
            "mcp-backlog-owner", PlatformRoles.ProjectCreator, PlatformRoles.Contributor);
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

    [Fact]
    public async Task EditDependencies_PreviewsAndPersistsThroughSameApiContract()
    {
        var project = await CreateProjectAsync();
        var tools = CreateTools();
        var upstream = System.Text.Json.JsonDocument.Parse(
            await tools.BacklogCaptureTaskAsync(project, "upstream")).RootElement.GetProperty("task_id").GetString()!;
        var downstream = System.Text.Json.JsonDocument.Parse(
            await tools.BacklogCaptureTaskAsync(project, "downstream")).RootElement.GetProperty("task_id").GetString()!;

        var preview = System.Text.Json.JsonDocument.Parse(
            await tools.BacklogEditDependenciesAsync(project, downstream, 0, add: [upstream], preview: true)).RootElement;
        preview.GetProperty("revision").GetInt64().Should().Be(1);
        System.Text.Json.JsonDocument.Parse(await tools.BacklogGetDependencyRevisionAsync(project))
            .RootElement.GetProperty("revision").GetInt64().Should().Be(0);

        await tools.BacklogEditDependenciesAsync(project, downstream, 0, add: [upstream]);
        var projection = System.Text.Json.JsonDocument.Parse(await tools.BacklogGetTaskAsync(project, downstream)).RootElement;
        projection.GetProperty("prerequisites")[0].GetProperty("task_id").GetString().Should().Be(upstream);
        projection.GetProperty("is_blocked").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task MissingIntegratedOutputIdentity_HasSameBlockerInRestAndMcp()
    {
        var project = await CreateProjectAsync();
        var tools = CreateTools();
        var upstream = System.Text.Json.JsonDocument.Parse(
            await tools.BacklogCaptureTaskAsync(project, "upstream")).RootElement.GetProperty("task_id").GetString()!;
        var downstream = System.Text.Json.JsonDocument.Parse(
            await tools.BacklogCaptureTaskAsync(project, "downstream")).RootElement.GetProperty("task_id").GetString()!;
        await tools.BacklogEditDependenciesAsync(project, downstream, 0, add: [upstream]);
        using var client = _factory.CreateAuthenticatedClientForObjectId(
            "mcp-backlog-owner", PlatformRoles.ProjectCreator, PlatformRoles.Contributor);
        (await client.PostAsync($"/api/projects/{project}/backlog/tasks/{upstream}/ready", null))
            .EnsureSuccessStatusCode();
        var pid = ProjectId.Parse(project);
        var runs = _factory.Services.GetRequiredService<IRunStore>();
        var source = RunId.New();
        (await _factory.Services.GetRequiredService<IBacklogTaskStore>()
            .TryClaimAndReserveCoordinatorRunWithPolicyAsync(pid, BacklogTaskId.Parse(upstream),
                MakeCoordinatorRun(pid, source), DateTimeOffset.UtcNow, expectedProviderKey:
                    (await _factory.Services.GetRequiredService<IBacklogTaskStore>()
                        .GetAsync(pid, BacklogTaskId.Parse(upstream)))!.AiExecutionProviderKey,
                expectedReadyByUserId: "mcp-backlog-owner")).Result.Should().Be(ClaimReserveResult.Won);
        var run = (await runs.GetAsync(source))!;
        (await runs.TrySetTerminalOutcomeAsync(source,
            TerminalRunOutcome.Create(RunStatus.Completed, "run.completed",
                new { result = "assembly_complete" }, DateTimeOffset.UtcNow, run.LifecycleGeneration),
            "assembly_complete")).Should().BeTrue();

        var rest = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/api/projects/{project}/backlog/tasks/{downstream}");
        var mcp = System.Text.Json.JsonDocument.Parse(
            await tools.BacklogGetTaskAsync(project, downstream)).RootElement;
        foreach (var projection in new[] { rest, mcp })
        {
            projection.GetProperty("prerequisites")[0].GetProperty("reason").GetString()
                .Should().Be("upstream_output_identity_unavailable");
            projection.GetProperty("is_blocked").GetBoolean().Should().BeTrue();
        }
    }
}
