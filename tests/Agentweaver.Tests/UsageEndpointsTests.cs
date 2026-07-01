using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Api;

/// <summary>
/// Integration tests for the four token-usage endpoints introduced in Feature 019.
///   GET /api/runs/{id}/usage
///   GET /api/projects/{id}/usage
///   GET /api/usage
/// Each test seeds real rows via <see cref="ITokenUsageStore"/> and asserts the HTTP contract.
/// </summary>
public sealed class UsageEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UsageEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateAuthenticatedClient();
    }

    // =========================================================================
    // UE-01: GET /api/runs/{id}/usage returns 200 with the correct JSON shape.
    // =========================================================================
    [Fact]
    public async Task GetRunUsage_SeededRecord_Returns200WithCorrectShape()
    {
        var usageStore = _factory.Services.GetRequiredService<ITokenUsageStore>();
        var runId = Guid.NewGuid().ToString();

        await usageStore.RecordAsync(new TokenUsageRecord
        {
            Id = $"{runId}:1",
            RunId = runId,
            ModelId = "gpt-4o",
            InputTokens = 500,
            OutputTokens = 200,
            TotalNanoAiu = 4_000_000_000L,
            RecordedAt = DateTimeOffset.UtcNow,
        });

        var response = await _client.GetAsync($"/api/runs/{runId}/usage");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("input_tokens").GetInt64().Should().Be(500);
        body.GetProperty("output_tokens").GetInt64().Should().Be(200);
        body.GetProperty("total_tokens").GetInt64().Should().Be(700);
        body.GetProperty("total_nano_aiu").GetInt64().Should().Be(4_000_000_000L);

        var byModel = body.GetProperty("by_model");
        byModel.GetArrayLength().Should().Be(1);
        byModel[0].GetProperty("model_id").GetString().Should().Be("gpt-4o");
    }

    // =========================================================================
    // UE-02: GET /api/projects/{id}/usage returns 200 with totals and by_model.
    // =========================================================================
    [Fact]
    public async Task GetProjectUsage_SeededRows_ReturnsTotalsAndByModel()
    {
        // Create a project owned by the test user so ownership check passes.
        var createRequest = new
        {
            name = $"Usage Test Project {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        };
        var createResponse = await _client.PostAsJsonAsync("/api/projects", createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project_id").GetString()!;

        var usageStore = _factory.Services.GetRequiredService<ITokenUsageStore>();
        var at = DateTimeOffset.UtcNow;

        await usageStore.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o",
            InputTokens = 300,
            OutputTokens = 120,
            TotalNanoAiu = 2_500_000_000L,
            RecordedAt = at,
        });
        await usageStore.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o-mini",
            InputTokens = 100,
            OutputTokens = 40,
            TotalNanoAiu = 500_000_000L,
            RecordedAt = at,
        });

        var fromTs = at.AddMinutes(-1).ToString("O");
        var toTs   = at.AddMinutes(1).ToString("O");
        var url = $"/api/projects/{projectId}/usage?from={Uri.EscapeDataString(fromTs)}&to={Uri.EscapeDataString(toTs)}";

        var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.GetProperty("total_tokens").GetInt64().Should().Be(560, "300+120+100+40");
        body.GetProperty("input_tokens").GetInt64().Should().Be(400);
        body.GetProperty("output_tokens").GetInt64().Should().Be(160);

        var byModel = body.GetProperty("by_model");
        byModel.GetArrayLength().Should().Be(2, "two distinct models were recorded");
    }

    // =========================================================================
    // UE-03: GET /api/usage returns 200 with the app-level summary shape.
    // =========================================================================
    [Fact]
    public async Task GetAppUsage_Returns200WithCorrectShape()
    {
        var usageStore = _factory.Services.GetRequiredService<ITokenUsageStore>();
        var projectId = Guid.NewGuid().ToString();
        var at = DateTimeOffset.UtcNow;

        await usageStore.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o",
            InputTokens = 150,
            OutputTokens = 60,
            TotalNanoAiu = 1_000_000_000L,
            RecordedAt = at,
        });

        var fromTs = at.AddMinutes(-1).ToString("O");
        var toTs   = at.AddMinutes(1).ToString("O");
        var url = $"/api/usage?from={Uri.EscapeDataString(fromTs)}&to={Uri.EscapeDataString(toTs)}";

        var response = await _client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        body.TryGetProperty("generated_utc", out _).Should().BeTrue("response must include generated_utc");
        body.TryGetProperty("from_utc", out _).Should().BeTrue();
        body.TryGetProperty("to_utc", out _).Should().BeTrue();
        body.TryGetProperty("by_project", out _).Should().BeTrue();
        body.TryGetProperty("by_model", out _).Should().BeTrue();

        // At least the seeded project must appear.
        var byProject = body.GetProperty("by_project");
        byProject.GetArrayLength().Should().BeGreaterThanOrEqualTo(1);

        var seededEntry = byProject.EnumerateArray()
            .FirstOrDefault(p => p.GetProperty("project_id").GetString() == projectId);
        seededEntry.ValueKind.Should().NotBe(JsonValueKind.Undefined,
            "the seeded project must appear in the by_project list");
        seededEntry.GetProperty("total_tokens").GetInt64().Should().Be(210);
    }

    [Fact]
    public async Task GetProjectDashboard_FromToQuery_AppliesToLeaderboardAndTokenUsage()
    {
        var createRequest = new
        {
            name = $"Dashboard Range Project {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        };
        var createResponse = await _client.PostAsJsonAsync("/api/projects", createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<JsonElement>();
        var projectId = created.GetProperty("project_id").GetString()!;

        ProjectId.TryParse(projectId, out var parsedProjectId).Should().BeTrue();

        var runStore = _factory.Services.GetRequiredService<IRunStore>();
        var usageStore = _factory.Services.GetRequiredService<ITokenUsageStore>();
        var now = DateTimeOffset.UtcNow;

        await runStore.InsertAsync(new Run
        {
            Id = RunId.New(),
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Recent run",
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.Merged,
            StartedAt = now.AddDays(-2),
            EndedAt = now.AddDays(-2).AddMinutes(5),
            ProjectId = parsedProjectId,
            AgentName = "neo",
            Origin = RunOrigin.Interactive,
        });
        await runStore.InsertAsync(new Run
        {
            Id = RunId.New(),
            RepositoryPath = _factory.NewWorkingDirectory(),
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "Older run",
            SubmittingUser = ProjectsWebApplicationFactory.TestUser,
            Status = RunStatus.Merged,
            StartedAt = now.AddDays(-20),
            EndedAt = now.AddDays(-20).AddMinutes(5),
            ProjectId = parsedProjectId,
            AgentName = "neo",
            Origin = RunOrigin.Interactive,
        });

        await usageStore.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o",
            InputTokens = 100,
            OutputTokens = 40,
            TotalNanoAiu = 1_000_000_000L,
            RecordedAt = now.AddDays(-2),
        });
        await usageStore.RecordAsync(new TokenUsageRecord
        {
            Id = $"{Guid.NewGuid()}:1",
            RunId = Guid.NewGuid().ToString(),
            ProjectId = projectId,
            ModelId = "gpt-4o-mini",
            InputTokens = 60,
            OutputTokens = 20,
            TotalNanoAiu = 500_000_000L,
            RecordedAt = now.AddDays(-20),
        });

        var defaultResponse = await _client.GetAsync($"/api/projects/{projectId}/dashboard");
        defaultResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var defaultBody = await defaultResponse.Content.ReadFromJsonAsync<JsonElement>();

        var fromTs = now.AddDays(-30).ToString("O");
        var toTs = now.ToString("O");
        var rangedUrl = $"/api/projects/{projectId}/dashboard?from={Uri.EscapeDataString(fromTs)}&to={Uri.EscapeDataString(toTs)}";
        var rangedResponse = await _client.GetAsync(rangedUrl);
        rangedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rangedBody = await rangedResponse.Content.ReadFromJsonAsync<JsonElement>();

        defaultBody.GetProperty("agent_leaderboard")[0].GetProperty("runs_this_week").GetInt32().Should().Be(1);
        defaultBody.GetProperty("token_usage").GetProperty("total_tokens").GetInt64().Should().Be(140);

        rangedBody.GetProperty("agent_leaderboard")[0].GetProperty("runs_this_week").GetInt32().Should().Be(2);
        rangedBody.GetProperty("token_usage").GetProperty("total_tokens").GetInt64().Should().Be(220);
    }
}
