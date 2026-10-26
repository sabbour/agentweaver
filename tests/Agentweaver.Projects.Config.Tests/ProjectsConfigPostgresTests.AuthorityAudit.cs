using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task PublicP1FixtureRoleWritesLinkAuditEventsToTheirInvocation()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"authority-audit-public-{Guid.NewGuid():N}";
        var admin = await SeedCallerAsync(
            db, store, $"authority-audit-admin-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var roles = new ProjectRoleAssignmentService(
            new PooledDbContextFactory<ProjectsConfigDbContext>(options), TimeProvider.System);
        var invocationId = Guid.NewGuid();
        var project = await projects.CreateProjectAsync(
            admin, $"p1-{invocationId:D}", CancellationToken.None);

        var owner = await roles.AssignAsync(
            admin,
            project.ProjectId,
            new AssignProjectRoleRequest
            {
                PrincipalId = admin.ActorId,
                Role = ProjectAuthorityRole.Owner,
            },
            CancellationToken.None);
        var orchestrator = await roles.AssignAsync(
            admin,
            project.ProjectId,
            new AssignProjectRoleRequest
            {
                PrincipalId = admin.ActorId,
                Role = ProjectAuthorityRole.Orchestrator,
            },
            CancellationToken.None);

        var read = await new ProjectSetupAuthorityAuditService(db)
            .ReadAsync(admin, invocationId, CancellationToken.None);

        Assert.True(read.Complete);
        Assert.Equal(2, read.Records.Count);
        Assert.Contains(read.Records, record =>
            record.AssignmentId == owner.AssignmentId &&
            record.EventType == "role_assigned" &&
            record.Role == ProjectAuthorityRole.Owner);
        Assert.Contains(read.Records, record =>
            record.AssignmentId == orchestrator.AssignmentId &&
            record.EventType == "role_assigned" &&
            record.Role == ProjectAuthorityRole.Orchestrator);
        Assert.All(
            await db.AuthorityAudit.AsNoTracking()
                .Where(record => record.ResourceId == project.ProjectId)
                .ToArrayAsync(),
            record => Assert.Equal(invocationId, record.InvocationId));
    }

    [Fact]
    public async Task SetupAuthorityAuditReadIsInvocationScopedAndRequiresCurrentVisibility()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var tenantId = $"authority-audit-tenant-{Guid.NewGuid():N}";
        var admin = await SeedCallerAsync(
            db, store, $"authority-audit-admin-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var firstInvocation = Guid.NewGuid();
        var secondInvocation = Guid.NewGuid();
        var firstSetup = Assert.IsType<ProjectSetupReceipt>((await store.ExecuteProjectSetupOperationAsync(
            admin,
            new ProjectSetupOperationRequest(
                1, firstInvocation, ProjectSetupOperationKind.Setup, tenantId, null, null))).Setup);
        var secondSetup = Assert.IsType<ProjectSetupReceipt>((await store.ExecuteProjectSetupOperationAsync(
            admin,
            new ProjectSetupOperationRequest(
                1, secondInvocation, ProjectSetupOperationKind.Setup, tenantId, null, null))).Setup);
        var service = new ProjectSetupAuthorityAuditService(db);

        var firstRead = await service.ReadAsync(admin, firstInvocation, CancellationToken.None);
        var firstEventIds = firstRead.Records.Select(record => record.EventId).ToHashSet();
        Assert.True(firstRead.Complete);
        Assert.False(firstRead.Truncated);
        Assert.Equal(2, firstRead.Records.Count);
        Assert.Equal(
            new[] { firstSetup.AuthorityAuditEventId, firstSetup.OwnerAuthorityAuditEventId!.Value }.Order(),
            firstEventIds.Order());

        var secondRead = await service.ReadAsync(admin, secondInvocation, CancellationToken.None);
        Assert.Equal(
            new[] { secondSetup.AuthorityAuditEventId, secondSetup.OwnerAuthorityAuditEventId!.Value }.Order(),
            secondRead.Records.Select(record => record.EventId).Order());

        var ownerSubject = $"authority-audit-owner-{Guid.NewGuid():N}";
        var ownerMembership = await SeedMembershipAsync(store, ownerSubject, tenantId);
        await store.AssignRoleAsync(
            ownerMembership,
            ProjectAuthorityResourceType.Project,
            firstSetup.ProjectId,
            ProjectAuthorityRole.Owner,
            "audit-test");
        var owner = await ResolveCallerAsync(db, ownerSubject, tenantId, ["api.read"]);
        var ownerRead = await service.ReadAsync(owner, firstInvocation, CancellationToken.None);
        Assert.Equal(2, ownerRead.Records.Count);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            (await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ReadAsync(owner, secondInvocation, CancellationToken.None))).StatusCode);

        var unrelatedSubject = $"authority-audit-unrelated-{Guid.NewGuid():N}";
        await SeedMembershipAsync(store, unrelatedSubject, tenantId);
        var unrelated = await ResolveCallerAsync(db, unrelatedSubject, tenantId, ["api.read"]);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            (await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ReadAsync(unrelated, firstInvocation, CancellationToken.None))).StatusCode);
        var noReadScope = await ResolveCallerAsync(
            db, admin.ActorId, tenantId, ["projects.admin", "projects.orchestrator"]);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            (await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ReadAsync(noReadScope, firstInvocation, CancellationToken.None))).StatusCode);

        var serialized = JsonSerializer.Serialize(firstRead);
        Assert.DoesNotContain("accessToken", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetupAuthorityAuditReadReportsTruncationAsIncomplete()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var tenantId = $"authority-audit-truncated-{Guid.NewGuid():N}";
        var admin = await SeedCallerAsync(
            db, store, $"authority-audit-admin-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var invocationId = Guid.NewGuid();
        await store.ExecuteProjectSetupOperationAsync(
            admin,
            new ProjectSetupOperationRequest(
                1, invocationId, ProjectSetupOperationKind.Setup, tenantId, null, null));

        db.AuthorityAudit.AddRange(Enumerable.Range(0, 31).Select(index =>
            new ProjectAuthorityAuditRecord
            {
                EventId = Guid.NewGuid(),
                InvocationId = invocationId,
                EventType = "membership_granted",
                MembershipId = admin.MembershipId,
                Issuer = admin.Issuer,
                Subject = admin.ActorId,
                TenantId = tenantId,
                ResourceType = ProjectAuthorityResourceType.Tenant,
                ResourceId = tenantId,
                Role = ProjectAuthorityRole.TenantAdmin,
                Revision = index + 1,
                Actor = admin.ActorId,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(index + 1),
            }));
        await db.SaveChangesAsync();

        var result = await new ProjectSetupAuthorityAuditService(db)
            .ReadAsync(admin, invocationId, CancellationToken.None);

        Assert.False(result.Complete);
        Assert.True(result.Truncated);
        Assert.Equal(ProjectSetupAuthorityAuditService.MaximumRecords, result.Records.Count);
    }
}
