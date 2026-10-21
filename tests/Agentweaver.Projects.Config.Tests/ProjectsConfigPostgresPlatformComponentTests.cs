using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

/// <summary>Authorization tests for the platform components report.</summary>
public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task PlatformComponentReaderRequiresCurrentTenantAdminOrPlatformAdmin()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "components-tenant-" + suffix;
        string[] scopes = ["api.read", "projects.admin"];

        var tenantAdmin = await SeedCallerAsync(db, store, "components-tenant-admin-" + suffix, tenantId,
            scopes, ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        await service.EnsurePlatformComponentReaderAsync(tenantAdmin, CancellationToken.None);

        var platformAdmin = await SeedCallerAsync(db, store, "components-platform-admin-" + suffix,
            "components-other-" + suffix, scopes, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        await service.EnsurePlatformComponentReaderAsync(platformAdmin, CancellationToken.None);

        var project = await service.CreateProjectAsync(tenantAdmin, "Components", CancellationToken.None);
        var ownerMembership = await SeedMembershipAsync(store, "components-owner-" + suffix, tenantId);
        await store.AssignRoleAsync(ownerMembership, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorityRole.Owner, "fixture");
        var projectOwner = await ResolveCallerAsync(db, "components-owner-" + suffix, tenantId, scopes);
        await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.EnsurePlatformComponentReaderAsync(projectOwner, CancellationToken.None));

        var member = await SeedCallerAsync(db, store, "components-member-" + suffix, tenantId, scopes);
        await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.EnsurePlatformComponentReaderAsync(member, CancellationToken.None));

        var revokedMembership = await SeedMembershipAsync(store, "components-revoked-" + suffix, tenantId);
        var assignment = await store.AssignRoleAsync(revokedMembership, ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin, "fixture");
        var revoked = await ResolveCallerAsync(db, "components-revoked-" + suffix, tenantId, scopes);
        await service.EnsurePlatformComponentReaderAsync(revoked, CancellationToken.None);
        await store.RevokeRoleAssignmentAsync(assignment.AssignmentId, assignment.Revision, "fixture");
        await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.EnsurePlatformComponentReaderAsync(revoked, CancellationToken.None));
    }
}
