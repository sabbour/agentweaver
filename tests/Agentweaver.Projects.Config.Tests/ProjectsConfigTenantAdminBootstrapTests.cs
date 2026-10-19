using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigTenantAdminBootstrapTests(
    ProjectsConfigPostgresFixture fixture) : IClassFixture<ProjectsConfigPostgresFixture>
{
    private const string TestIssuer = "https://projects-config.test/";

    [Fact]
    public async Task CreatesMembershipTenantAdminAndAuditAtomicallyAndIsIdempotent()
    {
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "approved-subject-" + suffix;
        var tenantId = "bootstrap-tenant-" + suffix;

        var concurrentResults = await Task.WhenAll(
            store.BootstrapFirstTenantAdminAsync(TestIssuer, subject, tenantId),
            store.BootstrapFirstTenantAdminAsync(TestIssuer, subject, tenantId));

        Assert.Single(concurrentResults, created => created);
        Assert.Single(concurrentResults, created => !created);

        await using var db = CreateDbContext();
        var membership = await db.TenantMemberships.AsNoTracking()
            .SingleAsync(item =>
                item.Issuer == TestIssuer &&
                item.Subject == subject &&
                item.TenantId == tenantId);
        var assignment = await db.RoleAssignments.AsNoTracking()
            .SingleAsync(item =>
                item.MembershipId == membership.MembershipId &&
                item.ResourceType == ProjectAuthorityResourceType.Tenant &&
                item.ResourceId == tenantId &&
                item.Role == ProjectAuthorityRole.TenantAdmin);
        var audit = await db.AuthorityAudit.AsNoTracking()
            .Where(item => item.MembershipId == membership.MembershipId)
            .ToArrayAsync();

        Assert.Equal(ProjectAuthorityRecordState.Active, membership.State);
        Assert.Equal(1, membership.Revision);
        Assert.Equal(ProjectAuthorityRecordState.Active, assignment.State);
        Assert.Equal(1, assignment.Revision);
        Assert.Equal("first-tenant-admin-bootstrap", membership.GrantedBy);
        var membershipAudit = Assert.Single(audit, item => item.EventType == "membership_granted");
        Assert.Equal(TestIssuer, membershipAudit.Issuer);
        Assert.Equal(subject, membershipAudit.Subject);
        Assert.Equal(tenantId, membershipAudit.TenantId);
        Assert.Equal(1, membershipAudit.Revision);
        Assert.Equal("first-tenant-admin-bootstrap", membershipAudit.Actor);
        Assert.Null(membershipAudit.AssignmentId);
        var roleAudit = Assert.Single(audit, item => item.EventType == "role_assigned");
        Assert.Equal(assignment.AssignmentId, roleAudit.AssignmentId);
        Assert.Equal(TestIssuer, roleAudit.Issuer);
        Assert.Equal(subject, roleAudit.Subject);
        Assert.Equal(tenantId, roleAudit.TenantId);
        Assert.Equal(ProjectAuthorityResourceType.Tenant, roleAudit.ResourceType);
        Assert.Equal(tenantId, roleAudit.ResourceId);
        Assert.Equal(ProjectAuthorityRole.TenantAdmin, roleAudit.Role);
        Assert.Equal(1, roleAudit.Revision);
        Assert.Equal("first-tenant-admin-bootstrap", roleAudit.Actor);

        var membershipCount = await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId);
        var assignmentCount = await db.RoleAssignments.CountAsync(item => item.ResourceId == tenantId);
        var auditCount = await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId);
        Assert.False(await store.BootstrapFirstTenantAdminAsync(TestIssuer, subject, tenantId));
        Assert.Equal(membershipCount, await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId));
        Assert.Equal(assignmentCount, await db.RoleAssignments.CountAsync(item => item.ResourceId == tenantId));
        Assert.Equal(auditCount, await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId));
    }

    [Fact]
    public async Task RefusesBootstrapWhenAnotherTenantAdminExists()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "occupied-bootstrap-tenant-" + suffix;
        var existing = await store.GrantMembershipAsync(
            TestIssuer, "existing-admin-" + suffix, tenantId, "fixture");
        await store.AssignRoleAsync(
            existing.MembershipId,
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin,
            "fixture");

        await using var db = new ProjectsConfigDbContext(options);
        var membershipCount = await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId);
        var assignmentCount = await db.RoleAssignments.CountAsync(item => item.ResourceId == tenantId);
        var auditCount = await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId);

        await Assert.ThrowsAsync<TenantAdminBootstrapConflictException>(() =>
            store.BootstrapFirstTenantAdminAsync(
                TestIssuer, "different-approved-subject-" + suffix, tenantId));

        Assert.Equal(membershipCount, await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId));
        Assert.Equal(assignmentCount, await db.RoleAssignments.CountAsync(item => item.ResourceId == tenantId));
        Assert.Equal(auditCount, await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId));
    }

    private ProjectsConfigDbContext CreateDbContext() => new(CreateDbContextOptions());

    private DbContextOptions<ProjectsConfigDbContext> CreateDbContextOptions() =>
        new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
}
