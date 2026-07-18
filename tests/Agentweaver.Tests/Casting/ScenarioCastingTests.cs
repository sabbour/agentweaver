using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Casting;

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
    public async Task ScenarioCast_ConfirmAfterTeamRevisionChanges_ReturnsConflict()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();
        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, _) = await CreateProjectAsync(client, workingDir);

        using var proposeResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals",
            new { mode = "scenario", template_id = "quick-software-development" });
        proposeResponse.EnsureSuccessStatusCode();
        var proposal = await proposeResponse.Content.ReadFromJsonAsync<JsonElement>();
        var proposalId = proposal.GetProperty("proposal_id").GetString()!;

        using (var scope = _factory.Services.CreateScope())
        {
            var store = scope.ServiceProvider.GetRequiredService<IProjectStore>();
            var id = ProjectId.Parse(projectId);
            var project = (await store.GetAsync(id))!;
            await using var mutation = await store.TryBeginTeamMutationAsync(id, project.TeamRevision);
            mutation.Should().NotBeNull();
            await mutation!.CompleteAsync();
        }

        using var confirmResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals/{proposalId}/confirm",
            new { });

        confirmResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await confirmResponse.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("code").GetString().Should().Be("team_changed");
        Directory.Exists(Path.Combine(workingDir, ".squad")).Should().BeFalse();
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

    [Fact]
    public async Task GetRoles_ExcludesReservedOrchestrationRoles()
    {
        using var client = _factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/catalog/roles");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("\"id\":\"scribe\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\":\"work-monitor\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\":\"coordinator\"", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"id\":\"rai\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("scribe")]
    [InlineData("work-monitor")]
    [InlineData("coordinator")]
    [InlineData("rai")]
    public async Task ManualCast_WithReservedRoleId_IsRejected(string reservedRoleId)
    {
        // Regression for #311: manual casting must never allow rostering the reserved orchestration
        // roles, even though "scribe"/"work-monitor" resolve as real catalog roles.
        var workingDir = _factory.NewProjectWorkingDirectory();
        using var client = _factory.CreateAuthenticatedClient();

        var (projectId, _) = await CreateProjectAsync(client, workingDir);

        using var proposeResponse = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/casting/proposals",
            new { mode = "manual", role_ids = new[] { "backend-engineer", reservedRoleId } });

        Assert.Equal(HttpStatusCode.BadRequest, proposeResponse.StatusCode);
        var body = await proposeResponse.Content.ReadAsStringAsync();
        Assert.Contains(reservedRoleId, body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reserved", body, StringComparison.OrdinalIgnoreCase);
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
