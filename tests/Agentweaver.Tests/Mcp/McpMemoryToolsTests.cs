using System.Net.Http.Json;
using Agentweaver.Mcp;
using Agentweaver.Mcp.Tools;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Mcp;

public sealed class McpMemoryToolsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;

    public McpMemoryToolsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private MemoryTools CreateTools()
    {
        var httpClient = _factory.CreateClient();
        var config = new McpConfig("http://localhost", ProjectsWebApplicationFactory.TestApiKey);
        var apiClient = new AgentweaverApiClient(httpClient, config);
        return new MemoryTools(apiClient);
    }

    private async Task<string> CreateProjectAsync()
    {
        using var httpClient = _factory.CreateAuthenticatedClient();
        var dir = _factory.NewWorkingDirectory();
        var response = await httpClient.PostAsJsonAsync("/api/projects", new
        {
            name = $"MCP Memory Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = dir,
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("project_id").GetString()!;
    }

    [Fact]
    public async Task DecisionList_OnSuccess_ReturnsJson()
    {
        var projectId = await CreateProjectAsync();
        using var httpClient = _factory.CreateAuthenticatedClient();
        var create = await httpClient.PostAsJsonAsync($"/api/projects/{projectId}/decisions", new
        {
            agent_name = "morpheus",
            type = "architectural",
            title = "Use memory ledger",
            content = "Persist decisions in the team memory ledger.",
        });
        create.EnsureSuccessStatusCode();

        var tools = CreateTools();
        var result = await tools.DecisionListAsync(projectId, ct: CancellationToken.None);

        result.Should().Contain("Use memory ledger");
        result.Should().NotContain("failed:");
    }

    [Fact]
    public async Task DecisionList_UnknownProject_ReturnsErrorStringWithoutThrowing()
    {
        var tools = CreateTools();
        var act = () => tools.DecisionListAsync(Guid.NewGuid().ToString("N"), ct: CancellationToken.None);

        await act.Should().NotThrowAsync();
        var result = await act();
        result.Should().Contain("decision_list failed:");
        result.Should().Contain("404");
    }

    [Fact]
    public async Task MemoryGet_OnSuccess_ReturnsJson()
    {
        var projectId = await CreateProjectAsync();
        using var httpClient = _factory.CreateAuthenticatedClient();
        var create = await httpClient.PostAsJsonAsync($"/api/projects/{projectId}/agents/morpheus/memory", new
        {
            type = "learning",
            importance = "high",
            content = "Use stateless MCP transport for per-request auth propagation.",
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var memoryId = created.GetProperty("id").GetInt32().ToString();

        var tools = CreateTools();
        var result = await tools.MemoryGetAsync(projectId, "morpheus", memoryId, CancellationToken.None);

        result.Should().Contain("stateless MCP transport");
        result.Should().NotContain("failed:");
    }

    [Fact]
    public async Task MemoryGet_MissingEntry_ReturnsErrorStringWithoutThrowing()
    {
        var projectId = await CreateProjectAsync();
        var tools = CreateTools();
        var act = () => tools.MemoryGetAsync(projectId, "morpheus", "99999", CancellationToken.None);

        await act.Should().NotThrowAsync();
        var result = await act();
        result.Should().Contain("memory_get failed:");
        result.Should().Contain("404");
    }
}
