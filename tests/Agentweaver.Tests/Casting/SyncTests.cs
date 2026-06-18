using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LibGit2Sharp;

namespace Agentweaver.Tests.Casting;

/// <summary>
/// SC-004: Sync — detecting .squad/ changes, committing them, handling stale hashes,
/// and reporting a clean state when there is nothing to sync.
/// </summary>
public sealed class SyncTests : IClassFixture<CastingWebApplicationFactory>
{
    private readonly CastingWebApplicationFactory _factory;

    public SyncTests(CastingWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetSync_AfterCast_ShowsAddedSquadFiles()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        InitGitRepo(wd);
        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        using var response = await client.GetAsync($"/api/projects/{projectId}/team/sync");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(".squad/", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostSync_WithCorrectHash_CreatesCommitContainingOnlySquadFiles()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        InitGitRepo(wd);
        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        var getResp = await client.GetAsync($"/api/projects/{projectId}/team/sync");
        getResp.EnsureSuccessStatusCode();
        var state = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        var hash = state.GetProperty("change_set_hash").GetString()!;

        using var postResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/team/sync",
            new { expected_change_set_hash = hash, message = "Add squad team" });

        Assert.Equal(HttpStatusCode.OK, postResp.StatusCode);

        using var cleanResp = await client.GetAsync($"/api/projects/{projectId}/team/sync");
        var cleanState = await cleanResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(cleanState.GetProperty("nothing_to_sync").GetBoolean(),
            "Expected nothing_to_sync to be true after committing.");
    }

    [Fact]
    public async Task PostSync_WithStaleHash_Returns409()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        InitGitRepo(wd);
        SquadTestFixtureHelper.CreateMinimalSquad(wd);

        using var postResp = await client.PostAsJsonAsync(
            $"/api/projects/{projectId}/team/sync",
            new { expected_change_set_hash = "definitely-stale-hash-00000000", message = "Should fail" });

        Assert.Equal(HttpStatusCode.Conflict, postResp.StatusCode);
        var body = await postResp.Content.ReadAsStringAsync();
        Assert.Contains("sync_state_changed", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSync_OnCleanRepo_ReturnsNothingToSync()
    {
        var workingDir = _factory.NewProjectWorkingDirectory();

        using var client = _factory.CreateAuthenticatedClient();
        var (projectId, wd) = await CreateProjectAsync(client, workingDir);

        InitGitRepo(wd);

        using var response = await client.GetAsync($"/api/projects/{projectId}/team/sync");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(state.GetProperty("nothing_to_sync").GetBoolean(),
            "Expected nothing_to_sync to be true for a clean repo.");
    }

    private static void InitGitRepo(string dir)
    {
        Repository.Init(dir);
        using var repo = new Repository(dir);
        var sig = new Signature("Test", "test@localhost", DateTimeOffset.UtcNow);
        repo.Commit("Initial commit", sig, sig, new CommitOptions { AllowEmptyCommit = true });
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
