using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Contracts;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.Auth;

public sealed class ProjectRoleAssignmentTests : IClassFixture<EntraWebApplicationFactory>
{
    private const string OwnerOid = "11111111-1111-1111-1111-111111111111";
    private const string ContributorOid = "22222222-2222-2222-2222-222222222222";
    private const string ViewerOid = "33333333-3333-3333-3333-333333333333";
    private const string AnotherOid = "44444444-4444-4444-4444-444444444444";

    private readonly EntraWebApplicationFactory _factory;

    public ProjectRoleAssignmentTests(EntraWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("Owner")]
    [InlineData("Contributor")]
    [InlineData("Viewer")]
    public void ProjectRole_Names_AreCapturedInTests(string role)
    {
        role.Should().BeOneOf("Owner", "Contributor", "Viewer");
    }

    [Fact]
    public async Task CreateRoleAssignment_Persists_ProjectScopedMembership()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        var projectId = await CreateProjectAsync(owner);

        var create = await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Contributor",
        });

        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>();
        var stored = await store.GetAsync(ProjectId.Parse(projectId), ContributorOid);
        stored.Should().NotBeNull();
        stored!.Role.Should().Be(ProjectRole.Contributor);
        stored.Scope.Should().Be($"Project:{projectId}");

        var members = await owner.GetFromJsonAsync<JsonElement[]>($"/api/projects/{projectId}/role-assignments");
        members.Should().NotBeNull();
        members!.Select(m => (PrincipalId: m.GetProperty("principal_id").GetString(), Role: m.GetProperty("role").GetString()))
            .Should().Contain(new (string? PrincipalId, string? Role)[] { (OwnerOid, "Owner"), (ContributorOid, "Contributor") });
    }

    [Fact]
    public async Task RevokeRoleAssignment_RemovesAccess_OnSubsequentRequest()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var viewer = CreateClient(ViewerOid, PlatformRoles.Viewer);
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ViewerOid,
            role = "Viewer",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await viewer.GetAsync($"/api/projects/{projectId}/memory")).StatusCode.Should().Be(HttpStatusCode.OK);

        var revoke = await owner.DeleteAsync($"/api/projects/{projectId}/role-assignments/{ViewerOid}");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await viewer.GetAsync($"/api/projects/{projectId}/memory")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ProjectContributor_CannotPromoteSelfToOwner()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var contributor = CreateClient(ContributorOid, PlatformRoles.Contributor);
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var promote = await contributor.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Owner",
        });

        promote.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var project = await contributor.GetFromJsonAsync<ProjectResponse>($"/api/projects/{projectId}");
        project!.EffectiveRole.Should().Be("Contributor");
    }

    [Fact]
    public async Task ProjectContributor_CannotGrantOtherUsersAccess()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var contributor = CreateClient(ContributorOid, PlatformRoles.Contributor);
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var grant = await contributor.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = AnotherOid,
            role = "Viewer",
        });

        grant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await owner.GetFromJsonAsync<JsonElement[]>($"/api/projects/{projectId}/role-assignments"))!
            .Should().NotContain(member => member.GetProperty("principal_id").GetString() == AnotherOid);
    }

    [Fact]
    public async Task RemovingLastOwner_Follows_FinalizedRecoveryPolicy()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        var projectId = await CreateProjectAsync(owner);

        var rejectLastOwnerRemoval = await owner.DeleteAsync($"/api/projects/{projectId}/role-assignments/{OwnerOid}");
        rejectLastOwnerRemoval.StatusCode.Should().Be(HttpStatusCode.Conflict);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = AnotherOid,
            role = "Owner",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await owner.DeleteAsync($"/api/projects/{projectId}/role-assignments/{OwnerOid}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Viewer_CanReadButNotMutate_ProjectScopedMemory()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var viewer = CreateClient(ViewerOid, PlatformRoles.Viewer);
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ViewerOid,
            role = "Viewer",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await viewer.GetAsync($"/api/projects/{projectId}/memory")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await viewer.PostAsJsonAsync($"/api/projects/{projectId}/agents/smith/memory", new
        {
            type = "learning",
            content = "should fail",
        })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Contributor_CanWrite_ProjectScopedMemory()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var contributor = CreateClient(ContributorOid, PlatformRoles.Contributor);
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        (await contributor.PostAsJsonAsync($"/api/projects/{projectId}/agents/smith/memory", new
        {
            type = "learning",
            content = "allowed",
        })).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Contributor_CanUpdateAgentMemory_WithDurableReadback_AndReapprovalRequired()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var contributor = CreateClient(ContributorOid, PlatformRoles.Contributor);
        var projectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var create = await contributor.PostAsJsonAsync($"/api/projects/{projectId}/agents/smith/memory", new
        {
            type = "learning",
            importance = "high",
            content = "Before update",
            tags = "api",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var memoryId = created.GetProperty("id").GetInt32();
        var sourceIdentity = created.GetProperty("sourceIdentity").GetString();

        (await owner.PostAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}/promote",
            new { })).StatusCode.Should().Be(HttpStatusCode.OK);

        var update = await contributor.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}",
            new
            {
                type = "pattern",
                importance = "low",
                content = "After update",
                tags = "api, durable, API",
            });

        update.StatusCode.Should().Be(HttpStatusCode.OK, await update.Content.ReadAsStringAsync());
        var updated = await update.Content.ReadFromJsonAsync<JsonElement>();
        updated.GetProperty("content").GetString().Should().Be("After update");
        updated.GetProperty("type").GetString().Should().Be("pattern");
        updated.GetProperty("importance").GetString().Should().Be("low");
        updated.GetProperty("tags").GetString().Should().Be(",api,durable,");
        updated.GetProperty("trustState").GetString().Should().Be("pending");
        updated.GetProperty("approvedBy").ValueKind.Should().Be(JsonValueKind.Null);
        updated.GetProperty("approvedAt").ValueKind.Should().Be(JsonValueKind.Null);
        updated.GetProperty("sourceIdentity").GetString().Should().Be(sourceIdentity);

        var readback = await contributor.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}");
        readback.GetProperty("content").GetString().Should().Be("After update");
        readback.GetProperty("updated_at").GetDateTimeOffset()
            .Should().BeAfter(readback.GetProperty("created_at").GetDateTimeOffset());

        (await contributor.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}",
            new { type = "decision" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await contributor.GetFromJsonAsync<JsonElement>(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}"))
            .GetProperty("type").GetString().Should().Be("pattern");
    }

    [Fact]
    public async Task AgentMemoryUpdate_RejectsViewerAndCrossProjectOrAgentRecords()
    {
        using var owner = CreateClient(OwnerOid, PlatformRoles.ProjectCreator);
        using var contributor = CreateClient(ContributorOid, PlatformRoles.Contributor);
        using var viewer = CreateClient(ViewerOid, PlatformRoles.Viewer);
        var projectId = await CreateProjectAsync(owner);
        var otherProjectId = await CreateProjectAsync(owner);

        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ContributorOid,
            role = "Contributor",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = ViewerOid,
            role = "Viewer",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var create = await owner.PostAsJsonAsync($"/api/projects/{otherProjectId}/agents/smith/memory", new
        {
            type = "learning",
            content = "Other project memory",
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var memoryId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        (await viewer.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}",
            new { content = "Viewer edit" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await contributor.PutAsJsonAsync(
            $"/api/projects/{projectId}/agents/smith/memory/{memoryId}",
            new { content = "Cross-project id" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await owner.PutAsJsonAsync(
            $"/api/projects/{otherProjectId}/agents/trinity/memory/{memoryId}",
            new { content = "Cross-agent id" })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await contributor.PutAsJsonAsync(
            $"/api/projects/{otherProjectId}/agents/smith/memory/{memoryId}",
            new { content = "Unauthorized project" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task LegacyProject_WithoutLinkedGitHubOwner_FailsClosed_WithClaimGuidance()
    {
        var projectId = await CreateLegacyProjectAsync("legacy-owner");

        using var viewer = CreateClient(ViewerOid, PlatformRoles.Viewer);
        var response = await viewer.GetAsync($"/api/projects/{projectId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("project_unclaimed_in_entra_mode");
        body.Should().Contain("platform admin must claim it");

        using var scope = _factory.Services.CreateScope();
        var assignments = scope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>();
        (await assignments.ListByProjectAsync(ProjectId.Parse(projectId))).Should().BeEmpty();
    }

    [Fact]
    public async Task PlatformAdmin_CanAccessAndClaim_UnassignedLegacyProject()
    {
        var projectId = await CreateLegacyProjectAsync("legacy-owner");

        using var admin = CreateClient(AnotherOid, PlatformRoles.PlatformAdmin);
        (await admin.GetAsync($"/api/projects/{projectId}")).StatusCode.Should().Be(HttpStatusCode.OK);

        using (var scope = _factory.Services.CreateScope())
        {
            var assignments = scope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>();
            (await assignments.ListByProjectAsync(ProjectId.Parse(projectId))).Should().BeEmpty();
        }

        var claim = await admin.PostAsJsonAsync($"/api/projects/{projectId}/role-assignments", new
        {
            principal_id = AnotherOid,
            role = "Owner",
        });

        claim.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyAssignments = verifyScope.ServiceProvider.GetRequiredService<IProjectRoleAssignmentStore>();
        var assignment = await verifyAssignments.GetAsync(ProjectId.Parse(projectId), AnotherOid);
        assignment.Should().NotBeNull();
        assignment!.Role.Should().Be(ProjectRole.Owner);
    }

    private HttpClient CreateClient(string objectId, params string[] roles) =>
        _factory.CreateAuthenticatedClientForObjectId(objectId, roles);

    private async Task<string> CreateLegacyProjectAsync(string legacyOwner)
    {
        var projectId = ProjectId.New();
        using var scope = _factory.Services.CreateScope();
        var projectStore = scope.ServiceProvider.GetRequiredService<IProjectStore>();
        await projectStore.InsertAsync(new Project
        {
            Id = projectId,
            Name = $"Legacy RBAC {Guid.NewGuid():N}",
            Origin = ProjectOrigin.Blank(),
            WorkingDirectory = _factory.NewWorkingDirectory(),
            DefaultBranch = "main",
            Owner = legacyOwner,
            ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
            State = ProjectState.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return projectId.ToString();
    }

    private async Task<string> CreateProjectAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/projects", new
        {
            name = $"Tier2 RBAC {Guid.NewGuid():N}",
            origin = "blank",
            working_directory = _factory.NewWorkingDirectory(),
        });
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("project_id").GetString()!;
    }
}
