using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>Exercises the repository-sync projection through the Projects owner and migrated PostgreSQL.</summary>
public sealed class ProjectTeamSyncProjectionEndpointTests(
    ProjectsConfigPostgresFixture fixture) : IClassFixture<ProjectsConfigPostgresFixture>, IAsyncLifetime
{
    private IHost _host = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _host = await SkillMarketplaceEndpointHost.StartAsync(
            fixture.DataSource,
            _ => throw new InvalidOperationException("Team projection tests must not call GitHub."),
            objectStore: null,
            mapTeamSyncProjection: true);
        _client = _host.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Owner_reads_only_the_cast_and_configured_repository_projection()
    {
        var caller = await SeedProjectAsync();

        using var response = await SendAsync(
            caller, HttpMethod.Get, $"/api/projects/{caller.ProjectId}/team-sync-projection");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(caller.ProjectId, body.RootElement.GetProperty("projectId").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("configurationRevision").GetInt64());
        Assert.Equal("octo/team", body.RootElement.GetProperty("authority")
            .GetProperty("repository").GetProperty("owner").GetString() + "/" +
            body.RootElement.GetProperty("authority").GetProperty("repository").GetProperty("name").GetString());
        var member = Assert.Single(body.RootElement.GetProperty("members").EnumerateArray());
        Assert.Equal("lead", member.GetProperty("agentId").GetString());
        Assert.Equal("Lead", member.GetProperty("name").GetString());
        Assert.Equal("Engineer", member.GetProperty("role").GetString());
        Assert.Equal("Builds the project.", member.GetProperty("charter").GetString());
    }

    [Fact]
    public async Task Non_owner_is_forbidden_even_when_the_caller_can_read_the_project()
    {
        var owner = await SeedProjectAsync();
        var outsider = "viewer-" + Guid.NewGuid().ToString("N");
        var options = CreateDbContextOptions();
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var membership = await authority.GrantMembershipAsync(
            SkillMarketplaceEndpointHost.Issuer, outsider, owner.TenantId, "fixture");
        await authority.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Project,
            owner.ProjectId,
            ProjectAuthorityRole.Viewer,
            "fixture");

        using var response = await SendAsync(
            owner with { Subject = outsider },
            HttpMethod.Get,
            $"/api/projects/{owner.ProjectId}/team-sync-projection");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Stale_configuration_revision_conflicts_without_writing_project_state()
    {
        var caller = await SeedProjectAsync();
        string before;
        await using (var db = new ProjectsConfigDbContext(CreateDbContextOptions()))
        {
            before = (await db.ProjectConfigurationRevisions.SingleAsync(
                item => item.ProjectId == caller.ProjectId)).ConfigurationJson;
        }

        using var response = await SendAsync(
            caller,
            HttpMethod.Get,
            $"/api/projects/{caller.ProjectId}/team-sync-projection?expectedConfigurationRevision=2");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("team_sync_configuration_changed", body.RootElement.GetProperty("error").GetString());
        await using var after = new ProjectsConfigDbContext(CreateDbContextOptions());
        Assert.Equal(1, await after.Projects.Where(item => item.ProjectId == caller.ProjectId)
            .Select(item => item.ConfigurationRevision).SingleAsync());
        Assert.Equal(before, (await after.ProjectConfigurationRevisions.SingleAsync(
            item => item.ProjectId == caller.ProjectId)).ConfigurationJson);
    }

    private async Task<TestCaller> SeedProjectAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var caller = new TestCaller(
            suffix,
            "tenant-" + suffix,
            "owner-" + suffix,
            SkillMarketplaceEndpointHost.DefaultScope);
        var options = CreateDbContextOptions();
        var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var membership = await authority.GrantMembershipAsync(
            SkillMarketplaceEndpointHost.Issuer, caller.Subject, caller.TenantId, "fixture");
        var configuration = new ProjectConfiguration
        {
            AgentCharters = [new ProjectAgentCharter("lead", "Lead", "Engineer", "Builds the project.")],
            Casting = [new ProjectAgentCast("lead", "engineer", 0)],
            SourceControl = new SourceControlProjectSettings(
                new SourceControlRepositoryIdentity("octo", "team"),
                apiSecretReference: null,
                authMode: SourceControlAuthMode.GitHubApp,
                identityConnectionId: "connection-" + suffix),
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        // Seed the immutable current revision directly so the test isolates route authorization and projection reads.
        await using (var db = new ProjectsConfigDbContext(options))
        {
            var now = DateTimeOffset.UtcNow;
            db.Projects.Add(new ProjectRecord
            {
                ProjectId = caller.ProjectId,
                TenantId = caller.TenantId,
                CreatedByActorId = caller.Subject,
                Name = "Team sync project",
                State = ProjectLifecycleState.Active,
                Revision = 1,
                ConfigurationRevision = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
            db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
            {
                ProjectId = caller.ProjectId,
                Revision = 1,
                ConfigurationJson = JsonSerializer.Serialize(configuration, jsonOptions),
                UpdatedByActorId = caller.Subject,
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }
        await authority.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Project,
            caller.ProjectId,
            ProjectAuthorityRole.Owner,
            "fixture");
        return caller;
    }

    private async Task<HttpResponseMessage> SendAsync(TestCaller caller, HttpMethod method, string path)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(SkillMarketplaceEndpointHost.SubjectHeader, caller.Subject);
        request.Headers.Add(SkillMarketplaceEndpointHost.ScopeHeader, caller.Scope);
        request.Headers.Add(ProjectAuthorizationOwner.TenantSelectorHeader, caller.TenantId);
        return await _client.SendAsync(request);
    }

    private DbContextOptions<ProjectsConfigDbContext> CreateDbContextOptions() =>
        new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;

    private sealed record TestCaller(string ProjectId, string TenantId, string Subject, string Scope);
}
