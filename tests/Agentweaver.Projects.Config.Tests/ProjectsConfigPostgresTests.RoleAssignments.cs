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
        var listedOwner = Assert.Single(ownerList.Assignments);
        Assert.Equal(owner.AssignmentId, listedOwner.AssignmentId);
        Assert.Equal(ownerSubject, listedOwner.PrincipalId);
        Assert.Equal(ProjectAuthorityRole.Owner, listedOwner.Role);
        Assert.Equal("role-api-admin-" + suffix, listedOwner.CreatedBy);
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

        var lastOwner = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, project.ProjectId, owner.AssignmentId, owner.Revision, CancellationToken.None));
        Assert.Equal("last_project_owner", lastOwner.Code);

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

        var current = await projects.GetProjectAsync(admin, project.ProjectId, CancellationToken.None);
        await projects.UpdateProjectAsync(admin, project.ProjectId, current.Revision, current.Name,
            ProjectLifecycleState.Archived, CancellationToken.None);

        var archivedAssign = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.AssignAsync(
            admin, project.ProjectId,
            new AssignProjectRoleRequest { PrincipalId = memberSubject, Role = ProjectAuthorityRole.Viewer },
            CancellationToken.None));
        Assert.Equal("project_archived", archivedAssign.Code);

        // After archive, a tenant admin can revoke the last Owner so cleanup leaves no active project roles.
        var revokedOwner = await roles.RevokeAsync(
            admin, project.ProjectId, owner.AssignmentId, owner.Revision, CancellationToken.None);
        Assert.Equal(ProjectAuthorityRecordState.Revoked, revokedOwner.State);
        Assert.Equal(2, revokedOwner.Revision);

        var audit = await db.AuthorityAudit.AsNoTracking()
            .Where(item => item.ResourceId == project.ProjectId)
            .OrderBy(item => item.CreatedAt).ThenBy(item => item.Revision)
            .Select(item => item.EventType + ":" + item.Role + ":" + item.Revision)
            .ToListAsync();
        Assert.Equal(
            [
                "role_assigned:Owner:1",
                "role_assigned:Orchestrator:1",
                "role_revoked:Orchestrator:2",
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
        var adminB = await SeedCallerAsync(db, store, subject, tenantB, scopes,
            ProjectAuthorityResourceType.Tenant, tenantB, ProjectAuthorityRole.TenantAdmin);
        var projectB = await projects.CreateProjectAsync(adminB, "Role display B", CancellationToken.None);

        var projectOwnerAssignment = await store.AssignRoleAsync(
            adminA.MembershipId, ProjectAuthorityResourceType.Project, projectA.ProjectId,
            ProjectAuthorityRole.Owner, "fixture");
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
        Assert.Equal("principal_not_found",
            (await Assign(project.ProjectId, "role-api-foreign-" + suffix, ProjectAuthorityRole.Viewer)).Code);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await Assign("missing" + suffix[..8], "role-api-input-admin-" + suffix, ProjectAuthorityRole.Viewer)).StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await Assign("../bad", "role-api-input-admin-" + suffix, ProjectAuthorityRole.Viewer)).StatusCode);
        var badRevision = await Assert.ThrowsAsync<ProjectConfigException>(() => roles.RevokeAsync(
            admin, project.ProjectId, Guid.NewGuid(), 0, CancellationToken.None));
        Assert.Equal("invalid_expected_revision", badRevision.Code);
        Assert.False(await db.RoleAssignments.AnyAsync(item => item.ResourceId == project.ProjectId));
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
            await OwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(
                    uuid, text, text, text, bigint, text, boolean) TO "{roleName}"
                """);
            await using var runtime = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false,
            }.ConnectionString);

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
                await OwnerSqlAsync($"REVOKE ALL ON SCHEMA projects_config FROM \"{roleName}\"");
                await OwnerSqlAsync($"DROP ROLE \"{roleName}\"");
            }
        }
    }
}
