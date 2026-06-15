using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Scaffolder.Tests.Casting;

/// <summary>
/// SC-001, SC-002: Scenario-based casting — list available scenarios and
/// propose/confirm or propose/reject a cast.
/// </summary>
public sealed class ScenarioCastingTests : IClassFixture<CastingWebApplicationFactory>
{
    private readonly CastingWebApplicationFactory _factory;

    public ScenarioCastingTests(CastingWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetScenarios_ReturnsAtLeastSoftwareDevelopmentAndContentAuthoring()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/casting/templates");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("quick-software-development", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-authoring",    body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScenarioCast_ProposeConfirm_CreatesValidSquadFiles()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();
        using var client = _factory.CreateAuthenticatedClient();

        var (projectId, _) = await CreateProjectAsync(client, workingDir);

        using var proposeResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals",
            new { mode = "scenario", template_id = "quick-software-development" });

        Assert.Equal(HttpStatusCode.OK, proposeResponse.StatusCode);

        var proposal = await proposeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var proposalId = proposal.GetProperty("proposal_id").GetString()!;

        using var confirmResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals/{proposalId}/confirm",
            new { });

        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);

        Assert.True(Directory.Exists(Path.Combine(workingDir, ".squad")),
            ".squad/ directory was not created after confirm.");
        Assert.True(File.Exists(Path.Combine(workingDir, ".squad", "team.md")),
            "team.md was not created after confirm.");
    }

    [Fact]
    public async Task ScenarioCast_ProposeReject_WritesZeroSquadFiles()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();
        using var client = _factory.CreateAuthenticatedClient();

        var (projectId, _) = await CreateProjectAsync(client, workingDir);

        using var proposeResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals",
            new { mode = "scenario", template_id = "quick-software-development" });

        Assert.Equal(HttpStatusCode.OK, proposeResponse.StatusCode);

        var proposal = await proposeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var proposalId = proposal.GetProperty("proposal_id").GetString()!;

        using var deleteResponse = await client.DeleteAsync(
            $"/api/projects/{projectId}/casting/proposals/{proposalId}");

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.False(Directory.Exists(Path.Combine(workingDir, ".squad")),
            ".squad/ directory should not exist after reject.");
    }

    private async Task<(string ProjectId, string WorkingDirectory)> CreateProjectAsync(HttpClient client, string workingDir)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = "test-project",
            origin = "blank",
            working_directory = workingDir
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("project_id").GetString()!, body.GetProperty("working_directory").GetString()!);
    }
}
