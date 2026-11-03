using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task RoleAssignmentApiKeepsTenantAdminOwnerAndRevisionRules()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var roles = new ProjectRoleAssignmentService(
            new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "role-api-tenant-" + suffix;
        string[] adminScopes = ["api.read", "projects.admin"];
        var admin = await SeedCallerAsync(db, store, "role-api-admin-" + suffix, tenantId, adminScopes,
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await projects.CreateProjectAsync(admin, "Role API rules", CancellationToken.None);
        var creatorOwner = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == admin.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Project &&
            item.ResourceId == project.ProjectId &&
            item.Role == ProjectAuthorityRole.Owner &&
            item.State == ProjectAuthorityRecordState.Active);
        var other = await projects.CreateProjectAsync(admin, "Role API other", CancellationToken.None);
        var ownerSubject = "role-api-owner-" + suffix;
        var memberSubject = "role-api-member-" + suffix;
        await SeedMembershipAsync(store, ownerSubject, tenantId);
        await SeedMembershipAsync(store, memberSubject, tenantId);

        var owner = await roles.AssignAsync(admin, project.ProjectId,
            new AssignProjectRoleRequest { PrincipalId = ownerSubject, Role = ProjectAuthorityRole.Owner },
            CancellationToken.None);
        Assert.Equal(ProjectAuthorityRole.Owner, owner.Role);
        Assert.Equal(1, owner.Revision);

        // A project Owner without TenantAdmin cannot change project roles.
        var ownerCaller = await ResolveCallerAsync(db, ownerSubject, tenantId, adminScopes);
        var ownerDenied = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.AssignAsync(
            ownerCaller, project.ProjectId,
            new AssignProjectRoleRequest { PrincipalId = memberSubject, Role = ProjectAuthorityRole.Orchestrator },
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, ownerDenied.StatusCode);

        // A project Owner can read the active assignments; a member with no project role cannot.
        var ownerList = await roles.ListAsync(ownerCaller, project.ProjectId, CancellationToken.None);
        Assert.Equal(project.ProjectId, ownerList.ProjectId);
        Assert.Equal(2, ownerList.Assignments.Count);
        var listedOwner = Assert.Single(ownerList.Assignments, item => item.AssignmentId == owner.AssignmentId);
        Assert.Equal(owner.AssignmentId, listedOwner.AssignmentId);
        Assert.Equal(ownerSubject, listedOwner.PrincipalId);
        Assert.Equal(ProjectAuthorityRole.Owner, listedOwner.Role);
        Assert.Equal("role-api-admin-" + suffix, listedOwner.CreatedBy);
        var listedCreatorOwner = Assert.Single(
            ownerList.Assignments, item => item.AssignmentId == creatorOwner.AssignmentId);
        Assert.Equal("role-api-admin-" + suffix, listedCreatorOwner.PrincipalId);
        var memberCaller = await ResolveCallerAsync(db, memberSubject, tenantId, ["api.read"]);
        Assert.Equal(StatusCodes.Status403Forbidden, (await Assert.ThrowsAsync<ProjectConfigException>(() =>
            roles.ListAsync(memberCaller, project.ProjectId, CancellationToken.None))).StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, (await Assert.ThrowsAsync<ProjectConfigException>(() =>
            roles.ListAsync(ownerCaller, other.ProjectId, CancellationToken.None))).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, (await Assert.ThrowsAsync<ProjectConfigException>(() =>
            roles.ListAsync(admin, "missing" + suffix[..8], CancellationToken.None))).StatusCode);

        // The admin still needs the projects.admin scope.
        var readOnlyAdmin = await ResolveCallerAsync(db, "role-api-admin-" + suffix, tenantId, ["api.read"]);
        var scopeDenied = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.AssignAsync(
            readOnlyAdmin, project.ProjectId,
            new AssignProjectRoleRequest { PrincipalId = memberSubject, Role = ProjectAuthorityRole.Viewer },
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, scopeDenied.StatusCode);

        var foreign = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, other.ProjectId, owner.AssignmentId, owner.Revision, CancellationToken.None));
        Assert.Equal(StatusCodes.Status404NotFound, foreign.StatusCode);

        var orchestrator = await roles.AssignAsync(admin, project.ProjectId,
            new AssignProjectRoleRequest { PrincipalId = memberSubject, Role = ProjectAuthorityRole.Orchestrator },
            CancellationToken.None);
        var stale = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, project.ProjectId, orchestrator.AssignmentId, orchestrator.Revision + 1, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, stale.StatusCode);
        var revokedOrchestrator = await roles.RevokeAsync(
            admin, project.ProjectId, orchestrator.AssignmentId, orchestrator.Revision, CancellationToken.None);
        Assert.Equal(ProjectAuthorityRecordState.Revoked, revokedOrchestrator.State);
        var repeated = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, project.ProjectId, orchestrator.AssignmentId, revokedOrchestrator.Revision, CancellationToken.None));
        Assert.Equal(StatusCodes.Status409Conflict, repeated.StatusCode);

        var revokedAdditionalOwner = await roles.RevokeAsync(
            admin, project.ProjectId, owner.AssignmentId, owner.Revision, CancellationToken.None);
        Assert.Equal(ProjectAuthorityRecordState.Revoked, revokedAdditionalOwner.State);
        var lastOwner = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, project.ProjectId, creatorOwner.AssignmentId, creatorOwner.Revision, CancellationToken.None));
        Assert.Equal("last_project_owner", lastOwner.Code);

        var current = await projects.GetProjectAsync(admin, project.ProjectId, CancellationToken.None);
        await projects.UpdateProjectAsync(admin, project.ProjectId, current.Revision, current.Name,
            ProjectLifecycleState.Archived, CancellationToken.None);

        var archivedAssign = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.AssignAsync(
            admin, project.ProjectId,
            new AssignProjectRoleRequest { PrincipalId = memberSubject, Role = ProjectAuthorityRole.Viewer },
            CancellationToken.None));
        Assert.Equal("project_archived", archivedAssign.Code);

        // After archive, a tenant admin can revoke the creator's last Owner assignment.
        var revokedCreatorOwner = await roles.RevokeAsync(
            admin, project.ProjectId, creatorOwner.AssignmentId, creatorOwner.Revision, CancellationToken.None);
        Assert.Equal(ProjectAuthorityRecordState.Revoked, revokedCreatorOwner.State);
        Assert.Equal(2, revokedCreatorOwner.Revision);

        var audit = await db.AuthorityAudit.AsNoTracking()
            .Where(item => item.ResourceId == project.ProjectId)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Revision)
            .Select(item => item.EventType + ":" + item.Role + ":" + item.Revision)
            .ToListAsync();
        Assert.Equal(
            [
                "role_assigned:Owner:1",
                "role_assigned:Owner:1",
                "role_assigned:Orchestrator:1",
                "role_revoked:Orchestrator:2",
                "role_revoked:Owner:2",
                "role_revoked:Owner:2",
            ],
            audit);
        Assert.False(await db.RoleAssignments.AnyAsync(item =>
            item.ResourceId == project.ProjectId && item.State == ProjectAuthorityRecordState.Active));
        Assert.Empty((await roles.ListAsync(admin, project.ProjectId, CancellationToken.None)).Assignments);
    }

    [Fact]
    public async Task ProjectSummariesReturnOnlyCurrentCallerRolesForTheirProjectAndTenant()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var roles = new ProjectRoleAssignmentService(
            new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "role-display-admin-" + suffix;
        var tenantA = "role-display-tenant-a-" + suffix;
        var tenantB = "role-display-tenant-b-" + suffix;
        string[] scopes = ["api.read", "projects.admin"];
        var adminA = await SeedCallerAsync(db, store, subject, tenantA, scopes,
            ProjectAuthorityResourceType.Tenant, tenantA, ProjectAuthorityRole.TenantAdmin);
        var projectA = await projects.CreateProjectAsync(adminA, "Role display A", CancellationToken.None);
        var projectAOther = await projects.CreateProjectAsync(adminA, "Role display A other", CancellationToken.None);
        var projectOwnerAssignment = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == adminA.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Project &&
            item.ResourceId == projectA.ProjectId &&
            item.Role == ProjectAuthorityRole.Owner &&
            item.State == ProjectAuthorityRecordState.Active);
        var adminB = await SeedCallerAsync(db, store, subject, tenantB, scopes,
            ProjectAuthorityResourceType.Tenant, tenantB, ProjectAuthorityRole.TenantAdmin);
        var projectB = await projects.CreateProjectAsync(adminB, "Role display B", CancellationToken.None);

        await store.AssignRoleAsync(
            adminA.MembershipId, ProjectAuthorityResourceType.Project, projectAOther.ProjectId,
            ProjectAuthorityRole.Orchestrator, "fixture");
        var otherActorMembership = await SeedMembershipAsync(store, "role-display-other-" + suffix, tenantA);
        await store.AssignRoleAsync(
            otherActorMembership, ProjectAuthorityResourceType.Project, projectA.ProjectId,
            ProjectAuthorityRole.Owner, "fixture");
        await store.AssignRoleAsync(
            otherActorMembership, ProjectAuthorityResourceType.Project, projectA.ProjectId,
            ProjectAuthorityRole.Viewer, "fixture");

        // The same actor can belong to two tenants; each summary must stay within the selected membership.
        var callerA = await ResolveCallerAsync(db, subject, tenantA, scopes);
        await store.AssignRoleAsync(
            callerA.MembershipId, ProjectAuthorityResourceType.Project, projectA.ProjectId,
            ProjectAuthorityRole.Contributor, "fixture");
        var summaryA = await projects.GetProjectAsync(callerA, projectA.ProjectId, CancellationToken.None);
        Assert.Contains(summaryA.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Tenant &&
            role.ResourceId == tenantA &&
            role.Role == ProjectAuthorityRole.TenantAdmin);
        Assert.Contains(summaryA.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == projectA.ProjectId &&
            role.Role == ProjectAuthorityRole.Owner);
        Assert.Contains(summaryA.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == projectA.ProjectId &&
            role.Role == ProjectAuthorityRole.Contributor);
        Assert.DoesNotContain(summaryA.EffectiveRoles, role =>
            role.Role == ProjectAuthorityRole.Viewer ||
            role.ResourceId == projectAOther.ProjectId ||
            role.ResourceId == projectB.ProjectId ||
            role.ResourceId == tenantB);
        var listedSummaryA = (await projects.ListProjectsAsync(callerA, CancellationToken.None))
            .Single(summary => summary.ProjectId == projectA.ProjectId);
        Assert.Equal(summaryA.EffectiveRoles, listedSummaryA.EffectiveRoles);

        var summaryAOther = await projects.GetProjectAsync(callerA, projectAOther.ProjectId, CancellationToken.None);
        Assert.Contains(summaryAOther.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == projectAOther.ProjectId &&
            role.Role == ProjectAuthorityRole.Orchestrator);
        Assert.DoesNotContain(summaryAOther.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project && role.ResourceId == projectA.ProjectId);

        var callerB = await ResolveCallerAsync(db, subject, tenantB, scopes);
        var summaryB = await projects.GetProjectAsync(callerB, projectB.ProjectId, CancellationToken.None);
        Assert.Contains(summaryB.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Tenant &&
            role.ResourceId == tenantB &&
            role.Role == ProjectAuthorityRole.TenantAdmin);
        Assert.DoesNotContain(summaryB.EffectiveRoles, role =>
            role.ResourceId == tenantA || role.ResourceId == projectA.ProjectId);

        // Keep a role in the resolved snapshot, then revoke it; the subsequent summary must use current owner state.
        var revoked = await roles.RevokeAsync(
            callerA, projectA.ProjectId, projectOwnerAssignment.AssignmentId, projectOwnerAssignment.Revision,
            CancellationToken.None);
        Assert.Equal(ProjectAuthorityRecordState.Revoked, revoked.State);
        var staleSummary = await projects.GetProjectAsync(callerA, projectA.ProjectId, CancellationToken.None);
        Assert.DoesNotContain(staleSummary.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == projectA.ProjectId &&
            role.Role == ProjectAuthorityRole.Owner);
        Assert.DoesNotContain(staleSummary.EffectiveRoles, role => role.Role == ProjectAuthorityRole.Viewer);

        var refreshedCaller = await ResolveCallerAsync(db, subject, tenantA, scopes);
        var refreshedSummary = await projects.GetProjectAsync(
            refreshedCaller, projectA.ProjectId, CancellationToken.None);
        Assert.DoesNotContain(refreshedSummary.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == projectA.ProjectId &&
            role.Role == ProjectAuthorityRole.Owner);
        Assert.Contains(refreshedSummary.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Tenant &&
            role.ResourceId == tenantA &&
            role.Role == ProjectAuthorityRole.TenantAdmin);
    }

    [Fact]
    public async Task CreatingProjectAtomicallyAssignsOwnerToCreatorAndAuditsTheExactGrant()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var roles = new ProjectRoleAssignmentService(
            new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "project-create-owner-" + suffix;
        var creator = await SeedCallerAsync(db, store, "project-create-creator-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var otherAdmin = await SeedCallerAsync(db, store, "project-create-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var existingProject = await projects.CreateProjectAsync(
            otherAdmin, "Created by another administrator", CancellationToken.None);

        var created = await projects.CreateProjectAsync(creator, "Created by the caller", CancellationToken.None);
        var assignments = await roles.ListAsync(creator, created.ProjectId, CancellationToken.None);
        var owner = Assert.Single(assignments.Assignments);
        Assert.Equal(creator.ActorId, owner.PrincipalId);
        Assert.Equal(ProjectAuthorityRole.Owner, owner.Role);
        Assert.Equal(1, owner.Revision);
        Assert.Equal(creator.ActorId, owner.CreatedBy);

        var summary = await projects.GetProjectAsync(creator, created.ProjectId, CancellationToken.None);
        Assert.Contains(summary.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == created.ProjectId &&
            role.Role == ProjectAuthorityRole.Owner);
        var otherAdminSummary = await projects.GetProjectAsync(otherAdmin, created.ProjectId, CancellationToken.None);
        Assert.DoesNotContain(otherAdminSummary.EffectiveRoles, role =>
            role.ResourceType == ProjectAuthorityResourceType.Project &&
            role.ResourceId == created.ProjectId &&
            role.Role == ProjectAuthorityRole.Owner);
        var existingAssignments = await roles.ListAsync(creator, existingProject.ProjectId, CancellationToken.None);
        Assert.Equal(otherAdmin.ActorId, Assert.Single(existingAssignments.Assignments).PrincipalId);

        var audit = await db.AuthorityAudit.AsNoTracking().SingleAsync(item =>
            item.ResourceId == created.ProjectId &&
            item.ResourceType == ProjectAuthorityResourceType.Project &&
            item.Role == ProjectAuthorityRole.Owner);
        Assert.Equal("role_assigned", audit.EventType);
        Assert.Equal(creator.MembershipId, audit.MembershipId);
        Assert.Equal(owner.AssignmentId, audit.AssignmentId);
        Assert.Equal(1, audit.Revision);
        Assert.Equal(creator.ActorId, audit.Actor);
        Assert.Null(audit.InvocationId);
    }

    [Fact]
    public async Task CreatingProjectRejectsARevokedTenantAdminAndPersistsNothing()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "project-create-revoked-admin-" + suffix;
        var creator = await SeedCallerAsync(db, store, "project-create-revoked-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var grant = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == creator.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Tenant &&
            item.Role == ProjectAuthorityRole.TenantAdmin &&
            item.State == ProjectAuthorityRecordState.Active);
        await store.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, "fixture");

        var before = await CountTenantProjectDataAsync(db, tenantId);
        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            projects.CreateProjectAsync(creator, "Revoked administrator project", CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(before, await CountTenantProjectDataAsync(db, tenantId));
    }

    [Fact]
    public async Task CreatingProjectRejectsAStaleTenantAdminGrantAndPersistsNothing()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "project-create-stale-admin-" + suffix;
        var creator = await SeedCallerAsync(db, store, "project-create-stale-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var grant = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == creator.MembershipId &&
            item.ResourceType == ProjectAuthorityResourceType.Tenant &&
            item.Role == ProjectAuthorityRole.TenantAdmin &&
            item.State == ProjectAuthorityRecordState.Active);
        await store.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, "fixture");
        await store.AssignRoleAsync(
            creator.MembershipId, ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin, "fixture");

        var before = await CountTenantProjectDataAsync(db, tenantId);
        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            projects.CreateProjectAsync(creator, "Stale administrator project", CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(before, await CountTenantProjectDataAsync(db, tenantId));
    }

    private static async Task<(int Projects, int Configurations, int ProjectRoles, int Audits)> CountTenantProjectDataAsync(
        ProjectsConfigDbContext db,
        string tenantId)
    {
        var projectIds = db.Projects.AsNoTracking()
            .Where(item => item.TenantId == tenantId)
            .Select(item => item.ProjectId);
        return (
            await db.Projects.CountAsync(item => item.TenantId == tenantId),
            await db.ProjectConfigurationRevisions.CountAsync(item => projectIds.Contains(item.ProjectId)),
            await db.RoleAssignments.CountAsync(item =>
                item.ResourceType == ProjectAuthorityResourceType.Project && projectIds.Contains(item.ResourceId)),
            await db.AuthorityAudit.CountAsync(item => item.TenantId == tenantId));
    }

    [Fact]
    public async Task RoleAssignmentApiRejectsInvalidInputBeforeWriting()
    {
        await using var db = CreateDbContext();
        var store = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var roles = new ProjectRoleAssignmentService(
            new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "role-api-input-" + suffix;
        var admin = await SeedCallerAsync(db, store, "role-api-input-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var project = await projects.CreateProjectAsync(admin, "Role API input", CancellationToken.None);
        await SeedMembershipAsync(store, "role-api-foreign-" + suffix, "other-" + tenantId);

        async Task<ProjectConfigException> Assign(string projectId, string principalId, ProjectAuthorityRole role) =>
            await Assert.ThrowsAsync<ProjectConfigException>(() => roles.AssignAsync(
                admin, projectId, new AssignProjectRoleRequest { PrincipalId = principalId, Role = role },
                CancellationToken.None));

        Assert.Equal("invalid_project_role",
            (await Assign(project.ProjectId, "role-api-input-admin-" + suffix, ProjectAuthorityRole.TenantAdmin)).Code);
        Assert.Equal("invalid_principal_id", (await Assign(project.ProjectId, " padded", ProjectAuthorityRole.Viewer)).Code);
        Assert.Equal("invalid_principal_id", (await Assign(project.ProjectId, "", ProjectAuthorityRole.Viewer)).Code);
        Assert.Equal("email_principal_not_supported",
            (await Assign(project.ProjectId, "person@example.test", ProjectAuthorityRole.Viewer)).Code);
        Assert.Equal("principal_not_found",
            (await Assign(project.ProjectId, "role-api-foreign-" + suffix, ProjectAuthorityRole.Viewer)).Code);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await Assign("missing" + suffix[..8], "role-api-input-admin-" + suffix, ProjectAuthorityRole.Viewer)).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await Assign("../bad", "role-api-input-admin-" + suffix, ProjectAuthorityRole.Viewer)).StatusCode);
        var badRevision = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, project.ProjectId, Guid.NewGuid(), 0, CancellationToken.None));
        Assert.Equal("invalid_expected_revision", badRevision.Code);
        var creatorOwner = Assert.Single(await db.RoleAssignments.AsNoTracking()
            .Where(item => item.ResourceId == project.ProjectId).ToListAsync());
        Assert.Equal(admin.MembershipId, creatorOwner.MembershipId);
        Assert.Equal(ProjectAuthorityRole.Owner, creatorOwner.Role);
        Assert.Equal(1, creatorOwner.Revision);
    }

    [Fact]
    public async Task RuntimeAuthorityGateAcceptsOnlyTheExactRoleAssignmentGrantSet()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var roleName = "aw_role_api_runtime_" + suffix;
        var password = Guid.NewGuid().ToString("N");
        var roleCreated = false;
        async Task OwnerSqlAsync(string sql)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await OwnerSqlAsync(
                $"CREATE ROLE \"{roleName}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
            roleCreated = true;
            await OwnerSqlAsync($"GRANT USAGE ON SCHEMA projects_config TO \"{roleName}\"");
            await OwnerSqlAsync(
                $"GRANT SELECT ON projects_config.tenant_memberships, projects_config.project_role_assignments, projects_config.authority_audit TO \"{roleName}\"");
            await OwnerSqlAsync(
                $"GRANT SELECT, INSERT, UPDATE ON projects_config.hosted_model_reasoning_catalog TO \"{roleName}\"");
            await OwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(
                    uuid, text, text, text, bigint, text, boolean) TO "{roleName}"
                """);
            await OwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION
                    projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint),
                    projects_config.read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)
                TO "{roleName}"
                """);
            await using var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false,
            }.ConnectionString);

            await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);

            await OwnerSqlAsync($"""
                REVOKE ALL ON FUNCTION projects_config.issue_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) FROM "{roleName}"
                """);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
            await OwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.issue_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) TO "{roleName}"
                """);
            await OwnerSqlAsync($"""
                REVOKE ALL ON FUNCTION projects_config.read_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) FROM "{roleName}"
                """);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
            await OwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.read_p1_project(
                    uuid, text, text, text, uuid, bigint, uuid, bigint) TO "{roleName}"
                """);
            await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);

            await OwnerSqlAsync($"GRANT INSERT ON projects_config.project_role_assignments TO \"{roleName}\"");
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));

            await OwnerSqlAsync(
                $"GRANT UPDATE (state, revision, revoked_by, revoked_at) ON projects_config.project_role_assignments TO \"{roleName}\"");
            await OwnerSqlAsync($"GRANT INSERT ON projects_config.authority_audit TO \"{roleName}\"");
            await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);

            string[] forbidden =
            [
                $"GRANT UPDATE (role) ON projects_config.project_role_assignments TO \"{roleName}\"",
                $"GRANT UPDATE ON projects_config.project_role_assignments TO \"{roleName}\"",
                $"GRANT DELETE ON projects_config.project_role_assignments TO \"{roleName}\"",
                $"GRANT UPDATE (actor) ON projects_config.authority_audit TO \"{roleName}\"",
                $"GRANT DELETE ON projects_config.authority_audit TO \"{roleName}\"",
                $"GRANT INSERT ON projects_config.tenant_memberships TO \"{roleName}\"",
            ];
            foreach (var grant in forbidden)
            {
                await OwnerSqlAsync(grant);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
                await OwnerSqlAsync(grant.Replace("GRANT ", "REVOKE ", StringComparison.Ordinal)
                    .Replace(" TO ", " FROM ", StringComparison.Ordinal));
                // Revoking table UPDATE also removes column UPDATE grants, so restore the exact set.
                await OwnerSqlAsync(
                    $"GRANT UPDATE (state, revision, revoked_by, revoked_at) ON projects_config.project_role_assignments TO \"{roleName}\"");
                await ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime);
            }
        }
        finally
        {
            if (roleCreated)
            {
                await OwnerSqlAsync($"REVOKE ALL ON ALL TABLES IN SCHEMA projects_config FROM \"{roleName}\"");
                await OwnerSqlAsync($"""
                    REVOKE ALL ON FUNCTION projects_config.lock_casting_authority(
                        uuid, text, text, text, bigint, text, boolean) FROM "{roleName}"
                    """);
                await OwnerSqlAsync($"""
                    REVOKE ALL ON FUNCTION projects_config.issue_p1_project(
                        uuid, text, text, text, uuid, bigint, uuid, bigint) FROM "{roleName}"
                    """);
                await OwnerSqlAsync($"""
                    REVOKE ALL ON FUNCTION projects_config.read_p1_project(
                        uuid, text, text, text, uuid, bigint, uuid, bigint) FROM "{roleName}"
                    """);
                await OwnerSqlAsync($"REVOKE ALL ON SCHEMA projects_config FROM \"{roleName}\"");
                await OwnerSqlAsync($"DROP ROLE \"{roleName}\"");
            }
        }
    }
}
