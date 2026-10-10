using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ListsPersistedUnassignedRevisionsAndCurrentAssignmentsAcrossBoundedPages()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateSkillInventoryProjectAsync(db, authorityStore);
        var configurations = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var projectConfiguration = await configurations.UpdateProjectConfigurationAsync(
            setup.Owner,
            setup.Project.ProjectId,
            setup.Project.ConfigurationRevision,
            ProjectSettings() with
            {
                AgentCharters =
                [
                    new ProjectAgentCharter("agent-one", "Agent One", "assistant", "Inventory test agent."),
                ],
                Casting = [new ProjectAgentCast("agent-one", "assistant", 0)],
            },
            CancellationToken.None);
        var writer = new SkillContentService(
            db,
            new SkillContentObjectStore(new SkillTestObjectStore()),
            configurations,
            TimeProvider.System);

        var firstCandidate = SkillCandidate("Initial instructions.", "# Initial reference\n");
        var firstPreview = writer.Preview(firstCandidate);
        var first = await writer.ImportAsync(
            setup.Owner,
            setup.Project.ProjectId,
            new SkillContentImportRequest(
                "inventory-first-" + setup.Suffix,
                firstPreview.ContentDigest,
                "skill-alpha",
                null,
                new SkillContentSourceSelection(
                    "source-alpha", "source-revision-1", "main", new string('a', 40), "skills/alpha"),
                firstCandidate),
            CancellationToken.None);

        var secondCandidate = SkillCandidate("Updated instructions.", "# Updated reference\n");
        var secondPreview = writer.Preview(secondCandidate);
        var second = await writer.ImportAsync(
            setup.Owner,
            setup.Project.ProjectId,
            new SkillContentImportRequest(
                "inventory-second-" + setup.Suffix,
                secondPreview.ContentDigest,
                first.SkillId,
                first.Revision,
                new SkillContentSourceSelection(
                    "source-alpha", "source-revision-2", "main", new string('b', 40), "skills/alpha"),
                secondCandidate),
            CancellationToken.None);

        var otherCandidate = SkillCandidate("Other instructions.", "# Other reference\n");
        var otherPreview = writer.Preview(otherCandidate);
        var other = await writer.ImportAsync(
            setup.Owner,
            setup.Project.ProjectId,
            new SkillContentImportRequest(
                "inventory-other-" + setup.Suffix,
                otherPreview.ContentDigest,
                "skill-bravo",
                null,
                null,
                otherCandidate),
            CancellationToken.None);

        await using var reloadedDb = CreateDbContext();
        var reloadedConfigurations = new ProjectsConfigService(
            reloadedDb, CreateProviderCatalog(), TimeProvider.System);
        var inventoryService = new SkillContentService(
            reloadedDb, null, reloadedConfigurations, TimeProvider.System);
        var unassignedFirstPage = await inventoryService.ListInventoryAsync(
            setup.Owner, setup.Project.ProjectId, 1, 2, CancellationToken.None);
        var unassignedSecondPage = await inventoryService.ListInventoryAsync(
            setup.Owner, setup.Project.ProjectId, 2, 2, CancellationToken.None);

        Assert.Equal(projectConfiguration.Revision, unassignedFirstPage.ConfigurationRevision);
        Assert.Empty(unassignedFirstPage.Assignments);
        Assert.Equal(["skill-alpha:2", "skill-alpha:1"], unassignedFirstPage.Revisions
            .Select(item => $"{item.SkillId}:{item.Revision}"));
        Assert.True(unassignedFirstPage.HasMore);
        Assert.Equal("source-alpha", unassignedFirstPage.Revisions[0].SourceId);
        Assert.Equal("source-revision-2", unassignedFirstPage.Revisions[0].SourceRevision);
        Assert.Equal("skills/alpha", unassignedFirstPage.Revisions[0].SelectedPath);
        Assert.Equal(projectConfiguration.Revision, unassignedSecondPage.ConfigurationRevision);
        Assert.Equal(["skill-bravo:1"], unassignedSecondPage.Revisions
            .Select(item => $"{item.SkillId}:{item.Revision}"));
        Assert.False(unassignedSecondPage.HasMore);
        Assert.Equal(other.ContentDigest, Assert.Single(unassignedSecondPage.Revisions).ContentDigest);

        var assignedConfiguration = await inventoryService.UpdateAsync(
            setup.Owner,
            setup.Project.ProjectId,
            new SkillAssignmentUpdateRequest(
                projectConfiguration.Revision,
                first.SkillId,
                second.Revision,
                second.ContentDigest,
                Enabled: true,
                Order: 3,
                ["agent-one"]),
            CancellationToken.None);
        var assignedFirstPage = await inventoryService.ListInventoryAsync(
            setup.Owner, setup.Project.ProjectId, 1, 2, CancellationToken.None);
        var assignedSecondPage = await inventoryService.ListInventoryAsync(
            setup.Owner, setup.Project.ProjectId, 2, 2, CancellationToken.None);

        Assert.Equal(assignedConfiguration.Revision, assignedFirstPage.ConfigurationRevision);
        Assert.Equal(assignedConfiguration.Revision, assignedSecondPage.ConfigurationRevision);
        var assignment = Assert.Single(assignedFirstPage.Assignments);
        Assert.Equal(first.SkillId, assignment.SkillId);
        Assert.True(assignment.Enabled);
        Assert.Equal(3, assignment.Order);
        Assert.Equal(second.Revision, assignment.Revision);
        Assert.Equal(second.ContentDigest, assignment.ContentDigest);
        Assert.Equal(["agent-one"], assignment.AgentIds!.Value);

        var invalidPage = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            inventoryService.ListInventoryAsync(
                setup.Owner, setup.Project.ProjectId, 0, 2, CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, invalidPage.StatusCode);
        var oversizedPage = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            inventoryService.ListInventoryAsync(
                setup.Owner,
                setup.Project.ProjectId,
                int.MaxValue,
                SkillContentService.MaxInventoryPageSize,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status400BadRequest, oversizedPage.StatusCode);
    }

    [Fact]
    public async Task SkillInventoryRequiresCurrentProjectAdministratorAuthority()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateSkillInventoryProjectAsync(db, authorityStore);
        var service = new SkillContentService(
            db,
            null,
            new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System),
            TimeProvider.System);

        var sameTenantReader = await SeedCallerAsync(
            db,
            authorityStore,
            "inventory-reader-" + setup.Suffix,
            setup.TenantId,
            ["api.read", "projects.admin"]);
        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ListInventoryAsync(
                sameTenantReader, setup.Project.ProjectId, 1, 10, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);

        var foreignTenant = "inventory-foreign-tenant-" + setup.Suffix;
        var foreignAdmin = await SeedCallerAsync(
            db,
            authorityStore,
            "inventory-foreign-admin-" + setup.Suffix,
            foreignTenant,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            foreignTenant,
            ProjectAuthorityRole.TenantAdmin);
        var hiddenProject = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ListInventoryAsync(
                foreignAdmin, setup.Project.ProjectId, 1, 10, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, hiddenProject.StatusCode);
    }

    [Fact]
    public async Task RevokedRevisionRemainsVisibleAsMetadataButIsDeniedForAcceptedRunExecution()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
            CreateDbContextOptions(), TimeProvider.System);
        var setup = await CreateSkillInventoryProjectAsync(db, authorityStore);
        var configurations = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var projectConfiguration = await configurations.UpdateProjectConfigurationAsync(
            setup.Owner,
            setup.Project.ProjectId,
            setup.Project.ConfigurationRevision,
            ProjectSettings() with
            {
                AgentCharters =
                [
                    new ProjectAgentCharter("agent-one", "Agent One", "assistant", "Inventory test agent."),
                ],
                Casting = [new ProjectAgentCast("agent-one", "assistant", 0)],
            },
            CancellationToken.None);
        var service = new SkillContentService(
            db,
            new SkillContentObjectStore(new SkillTestObjectStore()),
            configurations,
            TimeProvider.System);
        var candidate = SkillCandidate("Run only while current.", "# Runtime reference\n");
        var preview = service.Preview(candidate);
        var imported = await service.ImportAsync(
            setup.Owner,
            setup.Project.ProjectId,
            new SkillContentImportRequest(
                "inventory-revoked-" + setup.Suffix,
                preview.ContentDigest,
                "skill-revoked",
                null,
                null,
                candidate),
            CancellationToken.None);
        var assigned = await service.UpdateAsync(
            setup.Owner,
            setup.Project.ProjectId,
            new SkillAssignmentUpdateRequest(
                projectConfiguration.Revision,
                imported.SkillId,
                imported.Revision,
                imported.ContentDigest,
                Enabled: true,
                Order: 0,
                ["agent-one"]),
            CancellationToken.None);

        var orchestratorSubject = "inventory-orchestrator-" + setup.Suffix;
        var membershipId = await SeedMembershipAsync(
            authorityStore, orchestratorSubject, setup.TenantId);
        await authorityStore.AssignRoleAsync(
            membershipId,
            ProjectAuthorityResourceType.Project,
            setup.Project.ProjectId,
            ProjectAuthorityRole.Orchestrator,
            "fixture");
        var runId = "inventory-run-" + setup.Suffix;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", orchestratorSubject),
            new Claim("scope", "api.read projects.orchestrator"),
            new Claim("project_id", setup.Project.ProjectId),
            new Claim("run_id", runId),
        ], "test"));
        var runCaller = await new ProjectAuthorizationOwner(
                db, new ProjectsConfigIdentityOptions(TestIssuer))
            .ResolveAsync(principal, [setup.TenantId], CancellationToken.None);
        await configurations.AcceptRunSelectionAsync(
            runCaller,
            setup.Project.ProjectId,
            runId,
            RunRequest(assigned.Revision, setup.PlatformDefaultsRevision),
            CancellationToken.None);

        await service.RevokeAsync(
            setup.Owner,
            setup.Project.ProjectId,
            imported.SkillId,
            imported.Revision,
            "Withdrawn for this integration test.",
            CancellationToken.None);

        var inventory = await service.ListInventoryAsync(
            setup.Owner, setup.Project.ProjectId, 1, 10, CancellationToken.None);
        var revision = Assert.Single(inventory.Revisions);
        Assert.True(revision.IsRevoked);
        Assert.Equal(setup.Owner.ActorId, revision.RevokedByActorId);
        Assert.NotNull(revision.RevokedAt);
        Assert.Equal(assigned.Revision, inventory.ConfigurationRevision);
        var currentAssignment = Assert.Single(inventory.Assignments);
        Assert.Equal(imported.Revision, currentAssignment.Revision);
        Assert.Equal(imported.ContentDigest, currentAssignment.ContentDigest);

        var denied = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
            service.ReadAcceptedRunSkillsAsync(
                runCaller,
                setup.Project.ProjectId,
                runId,
                "agent-one",
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, denied.StatusCode);
    }

    private async Task<SkillInventoryTestProject> CreateSkillInventoryProjectAsync(
        ProjectsConfigDbContext db,
        ProjectsConfigPrivilegedAuthorityStore authorityStore)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "inventory-tenant-" + suffix;
        var configurations = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var owner = await SeedCallerAsync(
            db,
            authorityStore,
            "inventory-owner-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var platformAdmin = await SeedCallerAsync(
            db,
            authorityStore,
            "inventory-platform-admin-" + suffix,
            tenantId,
            ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var defaultsHead = await configurations.GetPlatformRuntimeDefaultsAsync(
            platformAdmin, CancellationToken.None);
        var defaults = await configurations.UpdatePlatformRuntimeDefaultsAsync(
            platformAdmin, defaultsHead.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await configurations.CreateProjectAsync(
            owner, "Skill inventory " + suffix, CancellationToken.None);
        return new SkillInventoryTestProject(owner, project, tenantId, suffix, defaults.Revision);
    }

    private sealed record SkillInventoryTestProject(
        ProjectAuthorizationContext Owner,
        ProjectSummary Project,
        string TenantId,
        string Suffix,
        long PlatformDefaultsRevision);
}
