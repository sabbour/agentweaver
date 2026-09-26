using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Tests.Helpers;
using FluentAssertions;

namespace Agentweaver.Tests.Memory;

public sealed class KnowledgeRevisionEndpointsTests : IClassFixture<ProjectsWebApplicationFactory>
{
    private readonly ProjectsWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public KnowledgeRevisionEndpointsTests(ProjectsWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
    }

    [Fact]
    public async Task MemoryUpdate_RejectsStaleRevision_AndRestoreAppendsRedactedHistory()
    {
        var projectId = await CreateProjectAsync();
        const string token = "ghp_0123456789abcdefghijklmnopqrstuvwxyz";
        var created = await CreateMemoryAsync(projectId, $"Original {token}");
        var memoryId = created.GetProperty("id").GetInt32();

        var update = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}",
            new
            {
                expected_revision = 1,
                content = "Updated searchable guidance",
                reason = $"replace leaked value {token}",
            });
        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("revision").GetInt32().Should().Be(2);
        updated.GetProperty("trustState").GetString().Should().Be("pending");

        var stale = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}",
            new { expected_revision = 1, content = "stale overwrite" });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("error").GetString().Should().Be("stale_revision");

        var history = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}/revisions?page=1&page_size=1");
        history.GetProperty("total_count").GetInt32().Should().Be(2);
        history.GetProperty("items").GetArrayLength().Should().Be(1);

        var completeHistory = await _client.GetStringAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}/revisions?page_size=100");
        completeHistory.Should().NotContain(token);
        completeHistory.Should().NotContain("owner@example.test");

        var restore = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}/restore",
            new { expected_revision = 2, revision = 1, reason = "restore safe original" });
        restore.StatusCode.Should().Be(HttpStatusCode.OK, await restore.Content.ReadAsStringAsync());
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>();
        restored.GetProperty("revision").GetInt32().Should().Be(3);
        restored.GetProperty("trustState").GetString().Should().Be("pending");
    }

    [Fact]
    public async Task Search_IsFilteredBoundedDeterministic_AndExcludesInactiveByDefault()
    {
        var projectId = await CreateProjectAsync();
        var first = await CreateMemoryAsync(projectId, "needle alpha");
        await CreateMemoryAsync(projectId, "needle beta");
        var firstId = first.GetProperty("id").GetInt32();

        var archive = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{firstId}",
            new { expected_revision = 1, status = "archived", reason = "obsolete" });
        archive.EnsureSuccessStatusCode();

        var active = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/memory?q=needle&page=1&page_size=1");
        active.GetProperty("total_count").GetInt32().Should().Be(1);
        active.GetProperty("items")[0].GetProperty("content").GetString().Should().Be("needle beta");

        var all = await _client.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/memory?q=needle&status=all&page=1&page_size=1");
        all.GetProperty("total_count").GetInt32().Should().Be(2);
        all.GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task LeavingSupersededState_ClearsReplacementLink()
    {
        var projectId = await CreateProjectAsync();
        var original = await CreateMemoryAsync(projectId, "original");
        var replacement = await CreateMemoryAsync(projectId, "replacement");
        var originalId = original.GetProperty("id").GetInt32();
        var replacementId = replacement.GetProperty("id").GetInt32();

        var superseded = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{originalId}",
            new
            {
                expected_revision = 1,
                status = "superseded",
                replaced_by_id = replacementId,
            });
        superseded.EnsureSuccessStatusCode();

        var archived = await _client.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{originalId}",
            new { expected_revision = 2, status = "archived" });
        archived.EnsureSuccessStatusCode();
        var body = await archived.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("status").GetString().Should().Be("archived");
        body.GetProperty("replaced_by_id").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task RevisionHistory_EnforcesProjectAuthorization()
    {
        var projectId = await CreateProjectAsync();
        var memory = await CreateMemoryAsync(projectId, "private project memory");
        var memoryId = memory.GetProperty("id").GetInt32();
        using var intruder = _factory.CreateClient();
        intruder.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "intruder-user-token-abc123");

        var response = await intruder.GetAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}/revisions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DecisionEditAndRestore_CreatePendingImmutableRevisions()
    {
        var projectId = await CreateProjectAsync();
        var token = "ghp_" + new string('B', 36);
        var create = await _client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", new
        {
            agent_name = "coordinator",
            type = "architectural",
            title = $"Stable boundary {token}",
            content = "Original decision",
        });
        create.EnsureSuccessStatusCode();
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var decisionId = created.GetProperty("id").GetInt32();

        var update = await _client.PutAsJsonAsync($"/api/projects/{projectId}/decisions/{decisionId}", new
        {
            expected_revision = 1,
            content = "Edited decision",
            reason = $"new evidence {token}",
        });
        update.EnsureSuccessStatusCode();
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("revision").GetInt32().Should().Be(2);
        updated.GetProperty("trustState").GetString().Should().Be("pending");
        var history = await _client.GetStringAsync(
            $"/api/projects/{projectId}/decisions/{decisionId}/revisions?page_size=100");
        history.Should().NotContain(token);

        var restore = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/decisions/{decisionId}/restore",
            new { expected_revision = 2, revision = 1 });
        restore.EnsureSuccessStatusCode();
        var restored = await restore.Content.ReadFromJsonAsync<JsonElement>();
        restored.GetProperty("revision").GetInt32().Should().Be(3);
        restored.GetProperty("content").GetString().Should().Be("Original decision");
        restored.GetProperty("trustState").GetString().Should().Be("pending");
    }

    private async Task<string> CreateProjectAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Knowledge Revision Test {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString()!;
    }

    private async Task<JsonElement> CreateMemoryAsync(string projectId, string content)
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory",
            new { type = "learning", importance = "high", content, tags = "searchable" });
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
