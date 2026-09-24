using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Memory;

public sealed class MemoryWriteDeduplicationTests
{
    [Fact]
    public async Task RepeatedAndConcurrentExactWritesShareIdentityAndExportOnce()
    {
        using var factory = new ProjectsWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var (projectId, workingDirectory) = await CreateProjectAsync(factory, client);

        var memoryWrites = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync($"/api/projects/{projectId}/agents/morpheus/memory", new
            {
                type = "learning",
                importance = "high",
                content = "Exact duplicate memory canary.",
                tags = "deduplication,concurrency",
            })));
        memoryWrites.Should().OnlyContain(response =>
            response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK);
        var memoryIds = await ReadIdsAsync(memoryWrites);
        memoryIds.Should().OnlyHaveUniqueItems().And.HaveCount(1);

        var decisionWrites = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", new
            {
                agent_name = "morpheus",
                type = "technical",
                title = "Exact duplicate decision",
                content = "Use one durable decision identity.",
                rationale = "Concurrent retry canary.",
                tags = "deduplication,concurrency",
            })));
        decisionWrites.Should().OnlyContain(response =>
            response.StatusCode == HttpStatusCode.Created || response.StatusCode == HttpStatusCode.OK);
        var decisionIds = await ReadIdsAsync(decisionWrites);
        decisionIds.Should().OnlyHaveUniqueItems().And.HaveCount(1);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await MemoryLedgerExporter.ExportAsync(
                projectId, workingDirectory, db, CancellationToken.None);
        }
        File.ReadAllText(Path.Combine(
                workingDirectory, ".squad", "agents", "morpheus", "history.md"))
            .Split("Exact duplicate memory canary.", StringSplitOptions.None)
            .Should().HaveCount(2);
        File.ReadAllText(Path.Combine(workingDirectory, ".squad", "decisions.md"))
            .Split("Exact duplicate decision", StringSplitOptions.None)
            .Should().HaveCount(2);
    }

    [Fact]
    public async Task RepeatedSupersessionAllowsNewActiveVersionsWithoutHistoricalIdentityCollisions()
    {
        using var factory = new ProjectsWebApplicationFactory();
        using var client = factory.CreateAuthenticatedClient();
        var (projectId, _) = await CreateProjectAsync(factory, client);
        var original = new
        {
            agent_name = "morpheus",
            type = "technical",
            title = "Versioned decision",
            content = "Original active content.",
        };

        var first = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", original);
        var replacement = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", new
        {
            agent_name = "morpheus",
            type = "technical",
            title = "Versioned decision",
            content = "Replacement active content.",
        });
        first.EnsureSuccessStatusCode();
        replacement.EnsureSuccessStatusCode();
        var firstId = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var replacementId = (await replacement.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var supersede = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/decisions/{firstId}",
            new { superseded_by_id = replacementId });
        supersede.EnsureSuccessStatusCode();

        var nextVersion = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", original);
        nextVersion.StatusCode.Should().Be(HttpStatusCode.Created);
        var nextVersionId = (await nextVersion.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32();
        nextVersionId.Should().NotBe(firstId);

        var supersedeNextVersion = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/decisions/{nextVersionId}",
            new { superseded_by_id = replacementId });
        supersedeNextVersion.EnsureSuccessStatusCode();

        var latestVersion = await client.PostAsJsonAsync($"/api/projects/{projectId}/decisions", original);
        latestVersion.StatusCode.Should().Be(HttpStatusCode.Created);
        (await latestVersion.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt32().Should().NotBe(nextVersionId);
    }

    private static async Task<int[]> ReadIdsAsync(IEnumerable<HttpResponseMessage> responses)
    {
        var ids = new List<int>();
        foreach (var response in responses)
            ids.Add((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32());
        return ids.Distinct().ToArray();
    }

    private static async Task<(string ProjectId, string WorkingDirectory)> CreateProjectAsync(
        ProjectsWebApplicationFactory factory,
        HttpClient client)
    {
        var workingDirectory = factory.NewWorkingDirectory();
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Memory Deduplication {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = workingDirectory,
        });
        response.EnsureSuccessStatusCode();
        var projectId = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("project_id").GetString()!;
        return (projectId, workingDirectory);
    }
}
