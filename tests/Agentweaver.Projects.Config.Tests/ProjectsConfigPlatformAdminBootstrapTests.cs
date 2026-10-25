using System.Security.Claims;
using Agentweaver.Abstractions;
using Agentweaver.Projects.Config;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigPlatformAdminBootstrapTests(
    ProjectsConfigPostgresFixture fixture) : IClassFixture<ProjectsConfigPostgresFixture>
{
    private const string TestIssuer = "https://projects-config.test/";
    private const string SyntheticIssuer = "https://agentweaver.synthetic.example/";
    private const string SyntheticTenantId = "11111111-2222-4333-8444-555555555555";

    [Fact]
    public async Task CreatesMembershipPlatformAdminAndAuditAtomicallyAndIsIdempotent()
    {
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "approved-platform-subject-" + suffix;
        var tenantId = "platform-bootstrap-tenant-" + suffix;

        var concurrentResults = await Task.WhenAll(
            store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId),
            store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId));

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
                item.ResourceType == ProjectAuthorityResourceType.Platform &&
                item.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.Role == ProjectAuthorityRole.PlatformAdmin);
        var audit = await db.AuthorityAudit.AsNoTracking()
            .Where(item => item.MembershipId == membership.MembershipId)
            .ToArrayAsync();

        Assert.Equal(ProjectAuthorityRecordState.Active, membership.State);
        Assert.Equal(1, membership.Revision);
        Assert.Equal(ProjectAuthorityRecordState.Active, assignment.State);
        Assert.Equal(1, assignment.Revision);
        Assert.Equal("first-platform-admin-bootstrap", membership.GrantedBy);
        var membershipAudit = Assert.Single(audit, item => item.EventType == "membership_granted");
        Assert.Equal(TestIssuer, membershipAudit.Issuer);
        Assert.Equal(subject, membershipAudit.Subject);
        Assert.Equal(tenantId, membershipAudit.TenantId);
        Assert.Equal(1, membershipAudit.Revision);
        Assert.Equal("first-platform-admin-bootstrap", membershipAudit.Actor);
        Assert.Null(membershipAudit.AssignmentId);
        var roleAudit = Assert.Single(audit, item => item.EventType == "role_assigned");
        Assert.Equal(assignment.AssignmentId, roleAudit.AssignmentId);
        Assert.Equal(TestIssuer, roleAudit.Issuer);
        Assert.Equal(subject, roleAudit.Subject);
        Assert.Equal(tenantId, roleAudit.TenantId);
        Assert.Equal(ProjectAuthorityResourceType.Platform, roleAudit.ResourceType);
        Assert.Equal(ProjectAuthorizationOwner.PlatformResourceId, roleAudit.ResourceId);
        Assert.Equal(ProjectAuthorityRole.PlatformAdmin, roleAudit.Role);
        Assert.Equal(1, roleAudit.Revision);
        Assert.Equal("first-platform-admin-bootstrap", roleAudit.Actor);

        var membershipCount = await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId);
        var assignmentCount = await db.RoleAssignments.CountAsync(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.ResourceId == ProjectAuthorizationOwner.PlatformResourceId);
        var auditCount = await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId);
        Assert.False(await store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId));
        Assert.Equal(membershipCount, await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId));
        Assert.Equal(assignmentCount, await db.RoleAssignments.CountAsync(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.ResourceId == ProjectAuthorizationOwner.PlatformResourceId));
        Assert.Equal(auditCount, await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId));
        await CleanupPlatformAdminAsync(subject, tenantId);
    }

    [Fact]
    public async Task RefusesBootstrapWhenAnotherPlatformAdminExists()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "occupied-platform-bootstrap-tenant-" + suffix;
        var existing = await store.GrantMembershipAsync(
            TestIssuer, "existing-platform-admin-" + suffix, tenantId, "fixture");
        var existingAssignment = await store.AssignRoleAsync(
            existing.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin,
            "fixture");

        await using var db = new ProjectsConfigDbContext(options);
        var membershipCount = await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId);
        var assignmentCount = await db.RoleAssignments.CountAsync(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.ResourceId == ProjectAuthorizationOwner.PlatformResourceId);
        var auditCount = await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId);

        await Assert.ThrowsAsync<PlatformAdminBootstrapConflictException>(() =>
            store.BootstrapFirstPlatformAdminAsync(
                TestIssuer, "different-platform-subject-" + suffix, tenantId));

        Assert.Equal(membershipCount, await db.TenantMemberships.CountAsync(item => item.TenantId == tenantId));
        Assert.Equal(assignmentCount, await db.RoleAssignments.CountAsync(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.ResourceId == ProjectAuthorizationOwner.PlatformResourceId));
        Assert.Equal(auditCount, await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId));
        await store.RevokeRoleAssignmentAsync(
            existingAssignment.AssignmentId, existingAssignment.Revision, "platform-bootstrap-test-cleanup");
    }

    [Fact]
    public async Task RefusesIdempotentBootstrapWhenApprovedIdentityUsesAnotherTenant()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "cross-tenant-platform-admin-" + suffix;
        var tenantId = "approved-platform-tenant-" + suffix;

        Assert.True(await store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId));
        try
        {
            // The PlatformAdmin is global, but its approved membership still must match the configured tenant.
            await Assert.ThrowsAsync<PlatformAdminBootstrapConflictException>(() =>
                store.BootstrapFirstPlatformAdminAsync(
                    TestIssuer, subject, "different-approved-tenant-" + suffix));

            await using var db = new ProjectsConfigDbContext(options);
            Assert.Equal(1, await db.TenantMemberships.CountAsync(item =>
                item.Issuer == TestIssuer && item.Subject == subject));
        }
        finally
        {
            await CleanupPlatformAdminAsync(subject, tenantId);
        }
    }

    [Fact]
    public async Task ReusesApprovedActiveMembershipWithoutDuplicatingMembershipAudit()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "existing-membership-subject-" + suffix;
        var tenantId = "existing-membership-tenant-" + suffix;
        var membership = await store.GrantMembershipAsync(TestIssuer, subject, tenantId, "fixture");

        Assert.True(await store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId));

        await using var db = new ProjectsConfigDbContext(options);
        Assert.Equal(1, await db.TenantMemberships.CountAsync(item =>
            item.Issuer == TestIssuer && item.Subject == subject && item.TenantId == tenantId));
        var audits = await db.AuthorityAudit.AsNoTracking()
            .Where(item => item.MembershipId == membership.MembershipId)
            .ToArrayAsync();
        Assert.Single(audits, item => item.EventType == "membership_granted");
        var roleAudit = Assert.Single(audits, item => item.EventType == "role_assigned");
        Assert.Equal("first-platform-admin-bootstrap", roleAudit.Actor);
        Assert.Equal(ProjectAuthorityResourceType.Platform, roleAudit.ResourceType);
        Assert.Equal(ProjectAuthorizationOwner.PlatformResourceId, roleAudit.ResourceId);
        await CleanupPlatformAdminAsync(subject, tenantId);
    }

    [Fact]
    public async Task PlatformAdminBootstrapSucceedsAfterTenantAdminBootstrapForSyntheticIdentity()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var subject = Guid.NewGuid().ToString();

        Assert.True(await store.BootstrapFirstTenantAdminAsync(
            SyntheticIssuer, subject, SyntheticTenantId));

        try
        {
            Assert.True(await store.BootstrapFirstPlatformAdminAsync(
                SyntheticIssuer, subject, SyntheticTenantId));
            Assert.False(await store.BootstrapFirstPlatformAdminAsync(
                SyntheticIssuer, subject, SyntheticTenantId));

            await using var db = new ProjectsConfigDbContext(options);
            var membership = await db.TenantMemberships.AsNoTracking().SingleAsync(item =>
                item.Issuer == SyntheticIssuer &&
                item.Subject == subject &&
                item.TenantId == SyntheticTenantId);
            var assignments = await db.RoleAssignments.AsNoTracking()
                .Where(item => item.MembershipId == membership.MembershipId)
                .ToArrayAsync();
            var audit = await db.AuthorityAudit.AsNoTracking()
                .Where(item => item.MembershipId == membership.MembershipId)
                .ToArrayAsync();

            Assert.Equal(2, assignments.Length);
            Assert.Contains(assignments, item =>
                item.ResourceType == ProjectAuthorityResourceType.Tenant &&
                item.ResourceId == SyntheticTenantId &&
                item.Role == ProjectAuthorityRole.TenantAdmin);
            Assert.Contains(assignments, item =>
                item.ResourceType == ProjectAuthorityResourceType.Platform &&
                item.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.Role == ProjectAuthorityRole.PlatformAdmin);
            Assert.Single(audit, item => item.EventType == "membership_granted");
            Assert.Single(audit, item =>
                item.EventType == "role_assigned" &&
                item.ResourceType == ProjectAuthorityResourceType.Tenant);
            Assert.Single(audit, item =>
                item.EventType == "role_assigned" &&
                item.ResourceType == ProjectAuthorityResourceType.Platform);
        }
        finally
        {
            await using var db = new ProjectsConfigDbContext(options);
            var membershipId = await db.TenantMemberships.AsNoTracking()
                .Where(item =>
                    item.Issuer == SyntheticIssuer &&
                    item.Subject == subject &&
                    item.TenantId == SyntheticTenantId)
                .Select(item => (Guid?)item.MembershipId)
                .SingleOrDefaultAsync();
            if (membershipId is not null)
            {
                var activeAssignments = await db.RoleAssignments.AsNoTracking()
                    .Where(item =>
                        item.MembershipId == membershipId &&
                        item.State == ProjectAuthorityRecordState.Active)
                    .Select(item => new { item.AssignmentId, item.Revision })
                    .ToArrayAsync();
                foreach (var assignment in activeAssignments)
                {
                    await store.RevokeRoleAssignmentAsync(
                        assignment.AssignmentId, assignment.Revision, "platform-bootstrap-test-cleanup");
                }
            }
        }
    }

    [Fact]
    public async Task BootstrappedPlatformAdminCanReadAndUpdateRuntimeDefaults()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "authorized-platform-admin-" + suffix;
        var tenantId = "authorized-platform-tenant-" + suffix;
        await store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId);

        await using var db = new ProjectsConfigDbContext(options);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", subject),
            new Claim("scope", $"{ProjectAuthorizationOwner.ApiReadScope} {ProjectAuthorizationOwner.ProjectAdminScope}"),
        ], "test"));
        var caller = await new ProjectAuthorizationOwner(
            db, new ProjectsConfigIdentityOptions(TestIssuer))
            .ResolveAsync(principal, [tenantId], CancellationToken.None);
        var service = new ProjectsConfigService(db, null!, TimeProvider.System);

        var initial = await service.GetPlatformRuntimeDefaultsAsync(caller, CancellationToken.None);
        Assert.Equal(0, initial.Revision);
        Assert.Null(initial.Defaults);

        var updated = await service.UpdatePlatformRuntimeDefaultsAsync(
            caller, initial.Revision, new PlatformRuntimeDefaults
            {
                EgressBaseline = [],
                RunLimits = new CopilotRunLimits
                {
                    MaxModelTurns = 12,
                    MaxToolCalls = 100,
                    MaxChildren = 4,
                    MaxConcurrentChildren = 2,
                    MaxWallTimeSeconds = 3600,
                    MaxPromptTokens = 20000,
                },
            }, CancellationToken.None);
        var readback = await service.GetPlatformRuntimeDefaultsAsync(caller, CancellationToken.None);

        Assert.Equal(1, updated.Revision);
        Assert.Equal(updated.Revision, readback.Revision);
        Assert.NotNull(readback.Defaults);
        Assert.Equal(updated.Defaults!.RunLimits, readback.Defaults.RunLimits);
        Assert.Equal(8, readback.Defaults.ModelTaskDefaults.Length);
        await CleanupPlatformAdminAsync(subject, tenantId);
    }

    [Fact]
    public async Task InspectionReportsApprovedActiveMembershipWithoutPlatformAssignment()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "inspection-without-assignment-" + suffix;
        var tenantId = "inspection-tenant-" + suffix;
        var membership = await store.GrantMembershipAsync(TestIssuer, subject, tenantId, "fixture");

        try
        {
            var snapshot = await store.InspectPlatformAdminAsync(TestIssuer, subject, tenantId);

            Assert.Equal(new PlatformAdminMembershipSnapshot(membership.MembershipId, membership.Revision),
                snapshot.Membership);
            Assert.Empty(snapshot.Assignments);
            Assert.Single(snapshot.MembershipAudits);
            Assert.Equal(membership.MembershipId, snapshot.MembershipAudits[0].MembershipId);
            Assert.Empty(snapshot.RoleAssignmentAudits);
            Assert.Equal(0, snapshot.OtherActivePlatformAdminCount);
        }
        finally
        {
            await store.RevokeMembershipAsync(
                membership.MembershipId, membership.Revision, "platform-inspection-test-cleanup");
        }
    }

    [Fact]
    public async Task InspectionReportsAssignmentAndBothMatchingAuditRows()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "inspection-complete-" + suffix;
        var tenantId = "inspection-tenant-" + suffix;

        Assert.True(await store.BootstrapFirstPlatformAdminAsync(TestIssuer, subject, tenantId));
        try
        {
            var snapshot = await store.InspectPlatformAdminAsync(TestIssuer, subject, tenantId);

            var assignment = Assert.Single(snapshot.Assignments);
            Assert.Equal(snapshot.Membership!.MembershipId, assignment.MembershipId);
            Assert.True(assignment.Revision > 0);
            Assert.Contains(PlatformAdminInspectionCommand.Format(snapshot), line =>
                line == $"ASSIGNMENT|{assignment.AssignmentId:D}|{assignment.Revision}|Active|Platform|default|PlatformAdmin|{assignment.MembershipId:D}");
            var membershipAudit = Assert.Single(snapshot.MembershipAudits);
            Assert.Equal(snapshot.Membership.MembershipId, membershipAudit.MembershipId);
            Assert.Null(membershipAudit.AssignmentId);
            var roleAudit = Assert.Single(snapshot.RoleAssignmentAudits);
            Assert.Equal(assignment.MembershipId, roleAudit.MembershipId);
            Assert.Equal(assignment.AssignmentId, roleAudit.AssignmentId);
            Assert.Equal(0, snapshot.OtherActivePlatformAdminCount);
        }
        finally
        {
            await CleanupPlatformAdminAsync(subject, tenantId);
        }
    }

    [Fact]
    public async Task InspectionReportsMissingRoleAssignmentAuditWithoutChangingTheAssignment()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "inspection-missing-audit-" + suffix;
        var tenantId = "inspection-tenant-" + suffix;
        var membership = await store.GrantMembershipAsync(TestIssuer, subject, tenantId, "fixture");
        var assignment = new ProjectRoleAssignmentRecord
        {
            AssignmentId = Guid.NewGuid(),
            MembershipId = membership.MembershipId,
            ResourceType = ProjectAuthorityResourceType.Platform,
            ResourceId = ProjectAuthorizationOwner.PlatformResourceId,
            Role = ProjectAuthorityRole.PlatformAdmin,
            State = ProjectAuthorityRecordState.Active,
            Revision = 1,
            GrantedBy = "test-only-setup",
            GrantedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await using (var setup = new ProjectsConfigDbContext(options))
            {
                // Insert only the active assignment so the test can model a partial prior bootstrap safely.
                setup.RoleAssignments.Add(assignment);
                await setup.SaveChangesAsync();
            }

            var snapshot = await store.InspectPlatformAdminAsync(TestIssuer, subject, tenantId);

            Assert.Single(snapshot.Assignments);
            Assert.Single(snapshot.MembershipAudits);
            Assert.Empty(snapshot.RoleAssignmentAudits);
        }
        finally
        {
            await CleanupPlatformAdminAsync(subject, tenantId);
            await store.RevokeMembershipAsync(
                membership.MembershipId, membership.Revision, "platform-inspection-test-cleanup");
        }
    }

    [Fact]
    public async Task InspectionCountsOtherActivePlatformAdminsWithoutReturningTheirIdentity()
    {
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var otherSubject = "inspection-other-admin-" + suffix;
        var otherTenant = "inspection-other-tenant-" + suffix;
        var approvedSubject = "inspection-approved-without-admin-" + suffix;
        var approvedTenant = "inspection-approved-tenant-" + suffix;
        var membership = await store.GrantMembershipAsync(TestIssuer, otherSubject, otherTenant, "fixture");
        var assignment = await store.AssignRoleAsync(
            membership.MembershipId,
            ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin,
            "fixture");

        try
        {
            var snapshot = await store.InspectPlatformAdminAsync(
                TestIssuer, approvedSubject, approvedTenant);

            Assert.Null(snapshot.Membership);
            Assert.Empty(snapshot.Assignments);
            Assert.Equal(1, snapshot.OtherActivePlatformAdminCount);
        }
        finally
        {
            await store.RevokeRoleAssignmentAsync(
                assignment.AssignmentId, assignment.Revision, "platform-inspection-test-cleanup");
            await store.RevokeMembershipAsync(
                membership.MembershipId, membership.Revision, "platform-inspection-test-cleanup");
        }
    }

    [Fact]
    public async Task InspectionTransactionRejectsWritesAtTheDatabaseBoundary()
    {
        await using var db = CreateDbContext();
        await using var transaction =
            await ProjectsConfigPrivilegedAuthorityStore.BeginReadOnlyTransactionAsync(db, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{ProjectsConfigDbContext.Schema}\".\"tenant_memberships\" SET revision = revision WHERE false"));

        Assert.Equal("25006", exception.SqlState);
        await transaction.RollbackAsync();
    }

    private async Task CleanupPlatformAdminAsync(string subject, string tenantId)
    {
        await using var db = CreateDbContext();
        var assignment = await (
            from role in db.RoleAssignments.AsNoTracking()
            join membership in db.TenantMemberships.AsNoTracking()
                on role.MembershipId equals membership.MembershipId
            where membership.Issuer == TestIssuer &&
                  membership.Subject == subject &&
                  membership.TenantId == tenantId &&
                  membership.State == ProjectAuthorityRecordState.Active &&
                  role.ResourceType == ProjectAuthorityResourceType.Platform &&
                  role.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
                  role.Role == ProjectAuthorityRole.PlatformAdmin &&
                  role.State == ProjectAuthorityRecordState.Active
            select new { role.AssignmentId, role.Revision })
            .SingleOrDefaultAsync();
        if (assignment is not null)
        {
            // Revoke each test's singleton grant so fixture reuse cannot leak platform authority across tests.
            var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            await store.RevokeRoleAssignmentAsync(
                assignment.AssignmentId, assignment.Revision, "platform-bootstrap-test-cleanup");
        }
    }

    private ProjectsConfigDbContext CreateDbContext() => new(CreateDbContextOptions());

    private DbContextOptions<ProjectsConfigDbContext> CreateDbContextOptions() =>
        new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
}
