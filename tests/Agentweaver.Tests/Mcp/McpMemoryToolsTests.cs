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
    public async Task DecisionList_UnknownProject_ThrowsMcpApiException()
    {
        var tools = CreateTools();
        var act = () => tools.DecisionListAsync(Guid.NewGuid().ToString("N"), ct: CancellationToken.None);

        var error = await act.Should().ThrowAsync<McpApiException>();
        error.Which.StatusCode.Should().Be(404);
        error.Which.Message.Should().Contain("\"error\"");
        error.Which.Message.Should().Contain("\"hint\"");
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
    public async Task MemoryGet_MissingEntry_ThrowsMcpApiException()
    {
        var projectId = await CreateProjectAsync();
        var tools = CreateTools();
        var act = () => tools.MemoryGetAsync(projectId, "morpheus", "99999", CancellationToken.None);

        var error = await act.Should().ThrowAsync<McpApiException>();
        error.Which.StatusCode.Should().Be(404);
        error.Which.Message.Should().Contain("\"error\"");
        error.Which.Message.Should().Contain("\"hint\"");
    }

    [Fact]
    public async Task MemoryRevisionTools_SearchUpdateCompareRestore_KeepConcurrencyExplicit()
    {
        var projectId = await CreateProjectAsync();
        using var httpClient = _factory.CreateAuthenticatedClient();
        var create = await httpClient.PostAsJsonAsync($"/api/projects/{projectId}/agents/morpheus/memory", new
        {
            type = "learning",
            importance = "high",
            content = "Original searchable guidance",
            tags = ",release,",
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var memoryId = created.GetProperty("id").GetInt32().ToString();
        var tools = CreateTools();

        var updated = await tools.MemoryUpdateAsync(
            projectId, "morpheus", memoryId, expected_revision: 1,
            content: "Updated searchable guidance", reason: "verified correction");
        updated.Should().Contain("\"revision\": 2");

        var stale = () => tools.MemoryUpdateAsync(
            projectId, "morpheus", memoryId, expected_revision: 1,
            content: "stale overwrite");
        var staleError = await stale.Should().ThrowAsync<McpApiException>();
        staleError.Which.StatusCode.Should().Be(409);
        staleError.Which.Message.Should().Contain("current_revision");

        var search = await tools.MemorySearchAsync(
            projectId, query: "Updated", status: "active", page: 1, page_size: 1);
        search.Should().Contain("Updated searchable guidance");
        search.Should().Contain("\"page_size\": 1");

        var history = await tools.MemoryHistoryAsync(
            projectId, "morpheus", memoryId, page: 1, page_size: 1);
        history.Should().Contain("\"total_count\": 2");
        history.Should().Contain("\"revision\": 2");

        var comparison = await tools.MemoryCompareAsync(
            projectId, "morpheus", memoryId, from_revision: 1, to_revision: 2);
        comparison.Should().Contain("Original searchable guidance");
        comparison.Should().Contain("Updated searchable guidance");

        var restored = await tools.MemoryRestoreAsync(
            projectId, "morpheus", memoryId, expected_revision: 2, revision: 1,
            reason: "restore known-good guidance");
        restored.Should().Contain("\"revision\": 3");
        restored.Should().Contain("Original searchable guidance");
    }
}
