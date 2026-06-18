using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Agentweaver.Tests.Casting;

/// <summary>
/// SC-003: Team management — view roster, read/write charters, add and remove members.
/// </summary>
public sealed class TeamManagementTests : IClassFixture<CastingWebApplicationFactory>
{
    private readonly CastingWebApplicationFactory _factory;

    public TeamManagementTests(CastingWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetTeam_ReturnsRoster()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        using var response = await client.GetAsync($"/api/projects/{projectId}/team");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Alpha", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Lead Architect", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMemberCharter_ReturnsMarkdown()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        using var response = await client.GetAsync($"/api/projects/{projectId}/team/members/alpha/charter");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Alpha", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PutCharter_DoesNotAffectOtherMembersFiles()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        SquadTestFixtureHelper.CreateMinimalSquad(wd, "multi-member-project");

        var beforeSnapshots = SnapshotSquadFiles(wd, except: "alpha/charter.md");

        using var putResp = await client.PutAsJsonAsync(
            $"/api/projects/{projectId}/team/members/alpha/charter",
            new { content = "# Alpha\n\nUpdated.\n" });

        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var afterSnapshots = SnapshotSquadFiles(wd, except: "alpha/charter.md");

        Assert.Equal(beforeSnapshots.Count, afterSnapshots.Count);
        foreach (var (path, before) in beforeSnapshots)
            Assert.Equal(before, afterSnapshots[path]);
    }

    [Fact]
    public async Task AddMember_TeamGrowsByOne()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        using var addResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/team/members",
            new { role_id = "backend-engineer" });

        Assert.Equal(HttpStatusCode.OK, addResp.StatusCode);

        using var teamResp = await client.GetAsync($"/api/projects/{projectId}/team");
        var body = await teamResp.Content.ReadAsStringAsync();

        Assert.Contains("Lead Architect",   body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Backend Engineer", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoveMember_StatusBecomesRetiredAndCharterMovedToAlumni()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        using var removeResp = await client.DeleteAsync($"/api/projects/{projectId}/team/members/alpha");

        Assert.Equal(HttpStatusCode.NoContent, removeResp.StatusCode);

        using var teamResp = await client.GetAsync($"/api/projects/{projectId}/team");
        Assert.Equal(HttpStatusCode.OK, teamResp.StatusCode);
        var body = await teamResp.Content.ReadAsStringAsync();

        Assert.Contains("retired", body, StringComparison.OrdinalIgnoreCase);

        var alumniCharterPath = Path.Combine(wd, ".squad", "agents", "_alumni", "alpha", "charter.md");
        Assert.True(File.Exists(alumniCharterPath), "Charter was not moved to _alumni directory.");
    }

    private static Dictionary<string, byte[]> SnapshotSquadFiles(string projectDir, string except)
    {
        var squadDir = Path.Combine(projectDir, ".squad");
        if (!Directory.Exists(squadDir)) return [];

        return Directory
            .GetFiles(squadDir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Replace('\\', '/').Contains(except, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(f => f, File.ReadAllBytes);
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
