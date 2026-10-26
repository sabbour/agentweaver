using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
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
    public async Task Migration039BackfillsExistingP1AuditRowsAndRestoresAppendOnlyProtection()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"authority-audit-migration-{Guid.NewGuid():N}";
        var admin = await SeedCallerAsync(
            db, store, $"authority-audit-migration-admin-{Guid.NewGuid():N}", tenantId,
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
        var setupInvocationId = Guid.NewGuid();
        var setupReceipt = Assert.IsType<ProjectSetupReceipt>((await store.ExecuteProjectSetupOperationAsync(
            admin,
            new ProjectSetupOperationRequest(
                1, setupInvocationId, ProjectSetupOperationKind.Setup, tenantId, null, null))).Setup);
        await db.Projects
            .Where(item => item.ProjectId == setupReceipt.ProjectId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.Name,
                $"operation-backfill-{setupInvocationId:N}"));

        var originalEventIds = await db.AuthorityAudit.AsNoTracking()
            .Where(record => record.TenantId == tenantId && record.InvocationId == invocationId)
            .Select(record => record.EventId)
            .ToArrayAsync();
        Assert.Equal(2, originalEventIds.Length);

        var migrator = db.GetService<IMigrator>();
        const string beforeInvocationAudit = "20261023201615_AddHostedModelReasoningCatalogAfterCasting";
        const string invocationAudit = "20261026183946_039_LinkProjectSetupAuthorityAuditInvocation";
        await migrator.MigrateAsync(beforeInvocationAudit);
        try
        {
            await migrator.MigrateAsync(invocationAudit);

            await using var verificationDb = CreateDbContext();
            var invocationRows = await verificationDb.AuthorityAudit.AsNoTracking()
                .Where(record => record.TenantId == tenantId && record.InvocationId == invocationId)
                .Select(record => record.EventId)
                .ToArrayAsync();
            var read = await new ProjectSetupAuthorityAuditService(verificationDb)
                .ReadAsync(admin, invocationId, CancellationToken.None);
            var setupEventIds = new[]
            {
                setupReceipt.AuthorityAuditEventId,
                setupReceipt.OwnerAuthorityAuditEventId!.Value,
            };
            var setupRows = await verificationDb.AuthorityAudit.AsNoTracking()
                .Where(record => setupEventIds.Contains(record.EventId))
                .ToArrayAsync();
            var setupRead = await new ProjectSetupAuthorityAuditService(verificationDb)
                .ReadAsync(admin, setupInvocationId, CancellationToken.None);

            Assert.Equal(originalEventIds.Order(), invocationRows.Order());
            Assert.True(read.Complete);
            Assert.Equal(originalEventIds.Order(), read.Records.Select(record => record.EventId).Order());
            Assert.Contains(read.Records, record =>
                record.AssignmentId == owner.AssignmentId &&
                record.Role == ProjectAuthorityRole.Owner);
            Assert.Contains(read.Records, record =>
                record.AssignmentId == orchestrator.AssignmentId &&
                record.Role == ProjectAuthorityRole.Orchestrator);
            Assert.Equal(setupEventIds.Order(), setupRows.Select(record => record.EventId).Order());
            Assert.All(setupRows, record => Assert.Equal(setupInvocationId, record.InvocationId));
            Assert.True(setupRead.Complete);
            Assert.False(setupRead.Truncated);
            Assert.Equal(setupEventIds.Order(), setupRead.Records.Select(record => record.EventId).Order());
            await AssertImmutableAsync(
                "UPDATE projects_config.authority_audit SET actor = 'migration-test' WHERE event_id = @event_id",
                ("event_id", originalEventIds[0]));
        }
        finally
        {
            await migrator.MigrateAsync(invocationAudit);
        }
    }

    [Fact]
    public async Task Migration039RejectsAmbiguousAuditInvocationLinksAtomically()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"authority-audit-conflict-{Guid.NewGuid():N}";
        var admin = await SeedCallerAsync(
            db, store, $"authority-audit-conflict-admin-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var invocationId = Guid.NewGuid();
        var setupReceipt = Assert.IsType<ProjectSetupReceipt>((await store.ExecuteProjectSetupOperationAsync(
            admin,
            new ProjectSetupOperationRequest(
                1, invocationId, ProjectSetupOperationKind.Setup, tenantId, null, null))).Setup);

        var migrator = db.GetService<IMigrator>();
        const string beforeInvocationAudit = "20261023201615_AddHostedModelReasoningCatalogAfterCasting";
        const string invocationAudit = "20261026183946_039_LinkProjectSetupAuthorityAuditInvocation";
        var conflictingInvocationId = Guid.NewGuid();
        await migrator.MigrateAsync(beforeInvocationAudit);
        try
        {
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                """
                UPDATE projects_config.project_setup_operations
                SET invocation_id = @conflicting_invocation_id
                WHERE invocation_id = @invocation_id AND operation_kind = 'Setup'
                """,
                connection))
            {
                command.Parameters.AddWithValue("conflicting_invocation_id", conflictingInvocationId);
                command.Parameters.AddWithValue("invocation_id", invocationId);
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }

            var migrationError = await Assert.ThrowsAsync<PostgresException>(
                () => migrator.MigrateAsync(invocationAudit));
            Assert.Contains(
                "authority audit event is linked to multiple setup invocations",
                migrationError.MessageText,
                StringComparison.Ordinal);

            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            {
                await using (var history = new NpgsqlCommand(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM projects_config.__ef_migrations_history
                        WHERE "MigrationId" = @migration_id)
                    """,
                    connection))
                {
                    history.Parameters.AddWithValue("migration_id", invocationAudit);
                    Assert.False((bool)(await history.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("Migration history check returned no result.")));
                }

                await using (var column = new NpgsqlCommand(
                    """
                    SELECT EXISTS (
                        SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'projects_config'
                            AND table_name = 'authority_audit'
                            AND column_name = 'invocation_id')
                    """,
                    connection))
                {
                    Assert.False((bool)(await column.ExecuteScalarAsync()
                        ?? throw new InvalidOperationException("Column check returned no result.")));
                }

                await using var trigger = new NpgsqlCommand(
                    """
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_trigger AS trg
                        JOIN pg_catalog.pg_class AS relation ON relation.oid = trg.tgrelid
                        JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
                        WHERE schema.nspname = 'projects_config'
                            AND relation.relname = 'authority_audit'
                            AND trg.tgname = 'authority_audit_immutable'
                            AND NOT trg.tgisinternal)
                    """,
                    connection);
                Assert.True((bool)(await trigger.ExecuteScalarAsync()
                    ?? throw new InvalidOperationException("Trigger check returned no result.")));
            }

            await AssertImmutableAsync(
                "UPDATE projects_config.authority_audit SET actor = 'migration-conflict-test' WHERE event_id = @event_id",
                ("event_id", setupReceipt.AuthorityAuditEventId));
        }
        finally
        {
            await migrator.MigrateAsync(beforeInvocationAudit);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                """
                UPDATE projects_config.project_setup_operations
                SET invocation_id = @invocation_id
                WHERE invocation_id = @conflicting_invocation_id AND operation_kind = 'Setup'
                """,
                connection))
            {
                command.Parameters.AddWithValue("invocation_id", invocationId);
                command.Parameters.AddWithValue("conflicting_invocation_id", conflictingInvocationId);
                await command.ExecuteNonQueryAsync();
            }

            await migrator.MigrateAsync(invocationAudit);
        }
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
