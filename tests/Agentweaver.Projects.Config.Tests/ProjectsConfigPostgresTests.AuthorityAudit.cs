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
    public async Task Migration039BackfillsPreExistingSetupAndP1AuditRowsWithoutChangingThem()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var store = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"authority-audit-migration-{Guid.NewGuid():N}";
        var admin = await SeedCallerAsync(
            db, store, $"authority-audit-migration-admin-{Guid.NewGuid():N}", tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
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

        var migrator = db.GetService<IMigrator>();
        const string beforeInvocationAudit = "20261023201615_AddHostedModelReasoningCatalogAfterCasting";
        const string invocationAudit = "20261026183946_039_LinkProjectSetupAuthorityAuditInvocation";
        Migration039AuditFixture? legacy = null;
        try
        {
            await migrator.MigrateAsync(beforeInvocationAudit);
            legacy = await SeedMigration039LegacyRowsAsync("valid");
            var eventIds = legacy.EventIds
                .Concat([
                    setupReceipt.AuthorityAuditEventId,
                    setupReceipt.OwnerAuthorityAuditEventId!.Value,
                ])
                .Distinct()
                .ToArray();
            var beforeColumns = await ReadAuditColumnSnapshotsAsync(eventIds);

            await migrator.MigrateAsync(invocationAudit);

            await using var verificationDb = CreateDbContext();
            var linkedInvocationIds = await verificationDb.AuthorityAudit.AsNoTracking()
                .Where(record => eventIds.Contains(record.EventId))
                .ToDictionaryAsync(record => record.EventId, record => record.InvocationId);
            var expectedInvocationIds = legacy.OperationEventIds
                .ToDictionary(eventId => eventId, _ => (Guid?)legacy.InvocationId);
            foreach (var eventId in legacy.P1EventIds)
                expectedInvocationIds.Add(eventId, legacy.P1InvocationId);
            expectedInvocationIds.Add(legacy.NonP1EventId, null);
            expectedInvocationIds.Add(setupReceipt.AuthorityAuditEventId, setupInvocationId);
            expectedInvocationIds.Add(setupReceipt.OwnerAuthorityAuditEventId!.Value, setupInvocationId);

            Assert.Equal(
                expectedInvocationIds.OrderBy(pair => pair.Key),
                linkedInvocationIds.OrderBy(pair => pair.Key));

            var setupRead = await new ProjectSetupAuthorityAuditService(verificationDb)
                .ReadAsync(admin, setupInvocationId, CancellationToken.None);
            var rawOperationRead = await new ProjectSetupAuthorityAuditService(verificationDb)
                .ReadAsync(admin, legacy.InvocationId, CancellationToken.None);
            var p1Read = await new ProjectSetupAuthorityAuditService(verificationDb)
                .ReadAsync(admin, legacy.P1InvocationId, CancellationToken.None);
            var afterColumns = await ReadAuditColumnSnapshotsAsync(eventIds);

            Assert.True(setupRead.Complete);
            Assert.False(setupRead.Truncated);
            Assert.Equal(
                new[]
                {
                    setupReceipt.AuthorityAuditEventId,
                    setupReceipt.OwnerAuthorityAuditEventId!.Value,
                }.Order(),
                setupRead.Records.Select(record => record.EventId).Order());
            Assert.True(rawOperationRead.Complete);
            Assert.False(rawOperationRead.Truncated);
            Assert.Equal(legacy.OperationEventIds.Order(), rawOperationRead.Records.Select(record => record.EventId).Order());
            Assert.True(p1Read.Complete);
            Assert.False(p1Read.Truncated);
            Assert.Equal(legacy.P1EventIds.Order(), p1Read.Records.Select(record => record.EventId).Order());
            Assert.Equal(
                beforeColumns.OrderBy(pair => pair.Key),
                afterColumns.OrderBy(pair => pair.Key));

            await AssertAuthorityAuditTriggerRestoredAsync();
            await AssertImmutableAsync(
                "UPDATE projects_config.authority_audit SET actor = actor WHERE event_id = @event_id",
                ("event_id", legacy.OperationEventIds[0]));
            await AssertAuthorityAuditDeleteIsImmutableAsync(legacy.OperationEventIds[1]);
        }
        finally
        {
            await migrator.MigrateAsync(invocationAudit);
        }
    }

    [Theory]
    [InlineData("cross-invocation", "authority audit event is linked to multiple setup invocations")]
    [InlineData("setup-tenant-mismatch", "setup operation references an authority audit event outside its tenant")]
    [InlineData("setup-assignment-mismatch", "setup operation references an authority audit event outside its tenant")]
    [InlineData("setup-event-type-mismatch", "setup operation references an authority audit event outside its tenant")]
    [InlineData("p1-tenant-mismatch", "P1 authority audit event does not match its project tenant and resource")]
    [InlineData("p1-resource-type-mismatch", "P1 authority audit event does not match its project tenant and resource")]
    public async Task Migration039RejectsConflictingLegacyLinksAtomically(
        string scenario,
        string expectedError)
    {
        await using var db = CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        const string beforeInvocationAudit = "20261023201615_AddHostedModelReasoningCatalogAfterCasting";
        const string invocationAudit = "20261026183946_039_LinkProjectSetupAuthorityAuditInvocation";
        await migrator.MigrateAsync(beforeInvocationAudit);
        Migration039AuditFixture? legacy = null;
        try
        {
            legacy = await SeedMigration039LegacyRowsAsync(scenario);
            var beforeColumns = await ReadAuditColumnSnapshotsAsync(legacy.EventIds);

            var migrationError = await Assert.ThrowsAsync<PostgresException>(
                () => migrator.MigrateAsync(invocationAudit));
            Assert.Contains(expectedError, migrationError.MessageText, StringComparison.Ordinal);
            await AssertMigration039WasRolledBackAsync(invocationAudit);
            Assert.Equal(
                beforeColumns.OrderBy(pair => pair.Key),
                (await ReadAuditColumnSnapshotsAsync(legacy.EventIds)).OrderBy(pair => pair.Key));
            await AssertAuthorityAuditTriggerRestoredAsync();
            await AssertImmutableAsync(
                "UPDATE projects_config.authority_audit SET actor = actor WHERE event_id = @event_id",
                ("event_id", legacy.OperationEventIds[0]));
            await AssertAuthorityAuditDeleteIsImmutableAsync(legacy.OperationEventIds[1]);
        }
        finally
        {
            await migrator.MigrateAsync(beforeInvocationAudit);
            if (legacy is not null)
                await RepairMigration039LegacyRowsAsync(legacy, scenario);
            await migrator.MigrateAsync(invocationAudit);
        }
    }

    private async Task<Migration039AuditFixture> SeedMigration039LegacyRowsAsync(string scenario)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        var now = DateTimeOffset.UtcNow;
        var legacy = new Migration039AuditFixture
        {
            TenantId = $"migration039-{Guid.NewGuid():N}",
            Issuer = TestIssuer,
            Subject = $"migration039-actor-{Guid.NewGuid():N}",
            Actor = $"migration039-actor-{Guid.NewGuid():N}",
            CleanupActor = $"migration039-cleanup-{Guid.NewGuid():N}",
            MembershipId = Guid.NewGuid(),
            InvocationId = Guid.NewGuid(),
            ConflictingInvocationId = Guid.NewGuid(),
            OperationProjectId = Guid.NewGuid().ToString("N"),
            OrchestratorAssignmentId = Guid.NewGuid(),
            OwnerAssignmentId = Guid.NewGuid(),
            ViewerAssignmentId = Guid.NewGuid(),
            SetupAuthorityEventId = Guid.NewGuid(),
            OwnerGrantEventId = Guid.NewGuid(),
            CleanupAuthorityEventId = Guid.NewGuid(),
            OwnerRevocationEventId = Guid.NewGuid(),
            P1ProjectId = Guid.NewGuid().ToString("N"),
            P1InvocationId = Guid.NewGuid(),
            P1OwnerAssignmentId = Guid.NewGuid(),
            P1OrchestratorAssignmentId = Guid.NewGuid(),
            P1EventIds = [Guid.NewGuid(), Guid.NewGuid()],
            NonP1ProjectId = Guid.NewGuid().ToString("N"),
            NonP1AssignmentId = Guid.NewGuid(),
            NonP1EventId = Guid.NewGuid(),
            EventIds = [],
        };
        legacy = legacy with { EventIds = legacy.AllEventIds };

        await ExecuteMigration039SqlAsync(
            connection,
            transaction,
            """
            INSERT INTO projects_config.tenant_memberships
                (membership_id, issuer, subject, tenant_id, state, revision, granted_by, granted_at)
            VALUES (@membership_id, @issuer, @subject, @tenant_id, 'Active', 1, @actor, @created_at)
            """,
            ("membership_id", legacy.MembershipId),
            ("issuer", legacy.Issuer),
            ("subject", legacy.Subject),
            ("tenant_id", legacy.TenantId),
            ("actor", legacy.Actor),
            ("created_at", now));

        await InsertMigration039ProjectAsync(
            connection, transaction, legacy.OperationProjectId, legacy.TenantId,
            $"legacy-operation-{legacy.InvocationId:N}", "Archived", 2, legacy.Actor, now);
        await InsertMigration039ProjectAsync(
            connection, transaction, legacy.P1ProjectId, legacy.TenantId,
            $"p1-{legacy.P1InvocationId:D}", "Active", 1, legacy.Actor, now);
        await InsertMigration039ProjectAsync(
            connection, transaction, legacy.NonP1ProjectId, legacy.TenantId,
            $"ordinary-{Guid.NewGuid():N}", "Active", 1, legacy.Actor, now);

        await InsertMigration039AssignmentAsync(
            connection, transaction, legacy, legacy.OrchestratorAssignmentId,
            legacy.OperationProjectId, "Orchestrator", "Revoked", now);
        await InsertMigration039AssignmentAsync(
            connection, transaction, legacy, legacy.OwnerAssignmentId,
            legacy.OperationProjectId, "Owner", "Revoked", now);
        await InsertMigration039AssignmentAsync(
            connection, transaction, legacy, legacy.ViewerAssignmentId,
            legacy.OperationProjectId, "Viewer", "Active", now);
        await InsertMigration039AssignmentAsync(
            connection, transaction, legacy, legacy.P1OwnerAssignmentId,
            legacy.P1ProjectId, "Owner", "Active", now);
        await InsertMigration039AssignmentAsync(
            connection, transaction, legacy, legacy.P1OrchestratorAssignmentId,
            legacy.P1ProjectId, "Orchestrator", "Active", now);
        await InsertMigration039AssignmentAsync(
            connection, transaction, legacy, legacy.NonP1AssignmentId,
            legacy.NonP1ProjectId, "Owner", "Active", now);

        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.SetupAuthorityEventId,
            legacy.OrchestratorAssignmentId, "role_assigned", "Project",
            legacy.OperationProjectId, "Orchestrator", 1, legacy.TenantId, legacy.Actor, now);
        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.OwnerGrantEventId,
            legacy.OwnerAssignmentId, "role_assigned", "Project",
            legacy.OperationProjectId, "Owner", 1, legacy.TenantId, legacy.Actor, now.AddSeconds(1));
        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.CleanupAuthorityEventId,
            legacy.OrchestratorAssignmentId, "role_revoked", "Project",
            legacy.OperationProjectId, "Orchestrator", 2, legacy.TenantId, legacy.CleanupActor, now.AddSeconds(2));
        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.OwnerRevocationEventId,
            legacy.OwnerAssignmentId, "role_revoked", "Project",
            legacy.OperationProjectId, "Owner", 2, legacy.TenantId, legacy.CleanupActor, now.AddSeconds(3));
        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.P1EventIds[0],
            legacy.P1OwnerAssignmentId, "role_assigned",
            scenario == "p1-resource-type-mismatch" ? "Tenant" : "Project",
            legacy.P1ProjectId, "Owner", 1,
            scenario == "p1-tenant-mismatch" ? $"{legacy.TenantId}-other" : legacy.TenantId,
            legacy.Actor, now.AddSeconds(4));
        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.P1EventIds[1],
            legacy.P1OrchestratorAssignmentId, "role_assigned", "Project",
            legacy.P1ProjectId, "Orchestrator", 1, legacy.TenantId, legacy.Actor, now.AddSeconds(5));
        await InsertMigration039AuditAsync(
            connection, transaction, legacy, legacy.NonP1EventId,
            legacy.NonP1AssignmentId, "role_assigned", "Project",
            legacy.NonP1ProjectId, "Owner", 1, legacy.TenantId, legacy.Actor, now.AddSeconds(6));

        var setupAssignmentId = scenario == "setup-assignment-mismatch"
            ? legacy.ViewerAssignmentId
            : legacy.OrchestratorAssignmentId;
        var setupAuditEventId = scenario == "setup-event-type-mismatch"
            ? legacy.CleanupAuthorityEventId
            : legacy.SetupAuthorityEventId;
        var setupTenantId = scenario == "setup-tenant-mismatch"
            ? $"{legacy.TenantId}-other"
            : legacy.TenantId;
        await InsertMigration039OperationAsync(
            connection, transaction, legacy, legacy.InvocationId, "Setup",
            setupTenantId, setupAssignmentId, setupAuditEventId, now);
        await InsertMigration039OperationAsync(
            connection, transaction, legacy, legacy.InvocationId, "Cleanup",
            legacy.TenantId, legacy.OrchestratorAssignmentId, legacy.CleanupAuthorityEventId, now.AddSeconds(7));

        if (scenario == "cross-invocation")
        {
            await InsertMigration039OperationAsync(
                connection, transaction, legacy, legacy.ConflictingInvocationId, "Setup",
                legacy.TenantId, legacy.OrchestratorAssignmentId, legacy.SetupAuthorityEventId, now.AddSeconds(8));
        }

        await transaction.CommitAsync();
        return legacy;
    }

    private static async Task InsertMigration039ProjectAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string projectId,
        string tenantId,
        string name,
        string state,
        long revision,
        string actor,
        DateTimeOffset createdAt)
    {
        await ExecuteMigration039SqlAsync(
            connection, transaction,
            """
            INSERT INTO projects_config.projects
                (project_id, tenant_id, created_by_actor_id, name, state, revision,
                    configuration_revision, created_at, updated_at)
            VALUES (@project_id, @tenant_id, @actor, @name, @state, @revision, 1, @created_at, @created_at)
            """,
            ("project_id", projectId),
            ("tenant_id", tenantId),
            ("actor", actor),
            ("name", name),
            ("state", state),
            ("revision", revision),
            ("created_at", createdAt));
    }

    private static async Task InsertMigration039AssignmentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Migration039AuditFixture fixture,
        Guid assignmentId,
        string projectId,
        string role,
        string state,
        DateTimeOffset createdAt)
    {
        await ExecuteMigration039SqlAsync(
            connection, transaction,
            """
            INSERT INTO projects_config.project_role_assignments
                (assignment_id, membership_id, resource_type, resource_id, role, state,
                    revision, granted_by, granted_at, revoked_by, revoked_at)
            VALUES (@assignment_id, @membership_id, 'Project', @project_id, @role, @state,
                CASE WHEN @state = 'Revoked' THEN 2 ELSE 1 END, @actor, @created_at,
                CASE WHEN @state = 'Revoked' THEN @cleanup_actor ELSE NULL END,
                CASE WHEN @state = 'Revoked' THEN @created_at + INTERVAL '1 second' ELSE NULL END)
            """,
            ("assignment_id", assignmentId),
            ("membership_id", fixture.MembershipId),
            ("project_id", projectId),
            ("role", role),
            ("state", state),
            ("actor", fixture.Actor),
            ("cleanup_actor", fixture.CleanupActor),
            ("created_at", createdAt));
    }

    private static async Task InsertMigration039AuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Migration039AuditFixture fixture,
        Guid eventId,
        Guid assignmentId,
        string eventType,
        string resourceType,
        string resourceId,
        string role,
        long revision,
        string tenantId,
        string actor,
        DateTimeOffset createdAt)
    {
        await ExecuteMigration039SqlAsync(
            connection, transaction,
            """
            INSERT INTO projects_config.authority_audit
                (event_id, event_type, membership_id, assignment_id, issuer, subject, tenant_id,
                    resource_type, resource_id, role, revision, actor, created_at)
            VALUES (@event_id, @event_type, @membership_id, @assignment_id, @issuer, @subject, @tenant_id,
                @resource_type, @resource_id, @role, @revision, @actor, @created_at)
            """,
            ("event_id", eventId),
            ("event_type", eventType),
            ("membership_id", fixture.MembershipId),
            ("assignment_id", assignmentId),
            ("issuer", fixture.Issuer),
            ("subject", fixture.Subject),
            ("tenant_id", tenantId),
            ("resource_type", resourceType),
            ("resource_id", resourceId),
            ("role", role),
            ("revision", revision),
            ("actor", actor),
            ("created_at", createdAt));
    }

    private static async Task InsertMigration039OperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Migration039AuditFixture fixture,
        Guid invocationId,
        string operationKind,
        string tenantId,
        Guid assignmentId,
        Guid authorityAuditEventId,
        DateTimeOffset createdAt)
    {
        var isSetup = operationKind == "Setup";
        var expectedRevisions = isSetup
            ? "NULL::bigint, NULL::bigint"
            : "@expected_project_revision, @expected_assignment_revision";
        var ownerRevocation = isSetup ? "NULL::uuid" : "@owner_revocation_event_id";
        var parameters = new List<(string Name, object Value)>
        {
            ("invocation_id", invocationId),
            ("operation_kind", operationKind),
            ("request_hash", isSetup ? new string('a', 64) : new string('b', 64)),
            ("issuer", fixture.Issuer),
            ("subject", fixture.Subject),
            ("tenant_id", tenantId),
            ("membership_id", fixture.MembershipId),
            ("project_id", fixture.OperationProjectId),
            ("assignment_id", assignmentId),
            ("authority_event_id", authorityAuditEventId),
            ("owner_assignment_id", fixture.OwnerAssignmentId),
            ("owner_authority_event_id", fixture.OwnerGrantEventId),
            ("owner_revocation_event_id", fixture.OwnerRevocationEventId),
            ("created_at", createdAt),
        };
        if (!isSetup)
        {
            parameters.Add(("expected_project_revision", 2L));
            parameters.Add(("expected_assignment_revision", 1L));
        }

        await ExecuteMigration039SqlAsync(
            connection, transaction,
            $"""
            INSERT INTO projects_config.project_setup_operations
                (invocation_id, operation_kind, request_hash, issuer, subject, tenant_id, membership_id,
                    membership_revision, project_id, project_revision, project_configuration_revision,
                    assignment_id, assignment_revision, expected_project_revision, expected_assignment_revision,
                    authority_audit_event_id, result_state, result, created_at, owner_assignment_id,
                    owner_assignment_revision, owner_authority_audit_event_id, owner_revocation_audit_event_id)
            VALUES (@invocation_id, @operation_kind, @request_hash, @issuer, @subject, @tenant_id, @membership_id,
                1, @project_id, CASE WHEN @operation_kind = 'Setup' THEN 1 ELSE 2 END, 1,
                @assignment_id, CASE WHEN @operation_kind = 'Setup' THEN 1 ELSE 2 END,
                {expectedRevisions}, @authority_event_id,
                CASE WHEN @operation_kind = 'Setup' THEN 'Created' ELSE 'Cleaned' END,
                jsonb_build_object(), @created_at, @owner_assignment_id,
                CASE WHEN @operation_kind = 'Setup' THEN 1 ELSE 2 END,
                @owner_authority_event_id, {ownerRevocation})
            """,
            parameters.ToArray());
    }

    private static async Task ExecuteMigration039SqlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<Dictionary<Guid, string>> ReadAuditColumnSnapshotsAsync(IEnumerable<Guid> eventIds)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT event_id, jsonb_build_object(
                'event_type', event_type,
                'membership_id', membership_id,
                'assignment_id', assignment_id,
                'issuer', issuer,
                'subject', subject,
                'tenant_id', tenant_id,
                'resource_type', resource_type,
                'resource_id', resource_id,
                'role', role,
                'revision', revision,
                'actor', actor,
                'created_at', created_at)::text
            FROM projects_config.authority_audit
            WHERE event_id = ANY(@event_ids)
            """,
            connection);
        command.Parameters.AddWithValue("event_ids", eventIds.ToArray());
        var snapshots = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            snapshots.Add(reader.GetGuid(0), reader.GetString(1));
        return snapshots;
    }

    private async Task AssertMigration039WasRolledBackAsync(string migrationId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                EXISTS (
                    SELECT 1 FROM projects_config.__ef_migrations_history
                    WHERE "MigrationId" = @migration_id),
                EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = 'projects_config'
                        AND table_name = 'authority_audit'
                        AND column_name = 'invocation_id')
            """,
            connection);
        command.Parameters.AddWithValue("migration_id", migrationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
    }

    private async Task AssertAuthorityAuditTriggerRestoredAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
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
        Assert.True((bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Authority audit trigger check returned no result.")));
    }

    private async Task AssertAuthorityAuditDeleteIsImmutableAsync(Guid eventId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            "DELETE FROM projects_config.authority_audit WHERE event_id = @event_id",
            connection,
            transaction);
        command.Parameters.AddWithValue("event_id", eventId);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal("55000", exception.SqlState);
        await transaction.RollbackAsync();
    }

    private async Task RepairMigration039LegacyRowsAsync(
        Migration039AuditFixture legacy,
        string scenario)
    {
        if (scenario.StartsWith("p1-", StringComparison.Ordinal))
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var rename = new NpgsqlCommand(
                """
                UPDATE projects_config.projects
                SET name = @name
                WHERE project_id = @project_id
                """,
                connection);
            rename.Parameters.AddWithValue("name", $"legacy-migration039-{legacy.P1InvocationId:N}");
            rename.Parameters.AddWithValue("project_id", legacy.P1ProjectId);
            await rename.ExecuteNonQueryAsync();
            return;
        }

        var invocationId = scenario == "cross-invocation"
            ? legacy.ConflictingInvocationId
            : legacy.InvocationId;
        await using var deleteConnection = await fixture.DataSource.OpenConnectionAsync();
        await using (var disable = new NpgsqlCommand(
            """
            ALTER TABLE projects_config.project_setup_operations
            DISABLE TRIGGER project_setup_operations_immutable
            """,
            deleteConnection))
        {
            await disable.ExecuteNonQueryAsync();
        }

        try
        {
            await using var delete = new NpgsqlCommand(
                """
                DELETE FROM projects_config.project_setup_operations
                WHERE invocation_id = @invocation_id AND operation_kind = 'Setup'
                """,
                deleteConnection);
            delete.Parameters.AddWithValue("invocation_id", invocationId);
            await delete.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var enable = new NpgsqlCommand(
                """
                ALTER TABLE projects_config.project_setup_operations
                ENABLE TRIGGER project_setup_operations_immutable
                """,
                deleteConnection);
            await enable.ExecuteNonQueryAsync();
        }
    }

    private sealed record Migration039AuditFixture
    {
        public required string TenantId { get; init; }
        public required string Issuer { get; init; }
        public required string Subject { get; init; }
        public required string Actor { get; init; }
        public required string CleanupActor { get; init; }
        public required Guid MembershipId { get; init; }
        public required Guid InvocationId { get; init; }
        public required Guid ConflictingInvocationId { get; init; }
        public required string OperationProjectId { get; init; }
        public required Guid OrchestratorAssignmentId { get; init; }
        public required Guid OwnerAssignmentId { get; init; }
        public required Guid ViewerAssignmentId { get; init; }
        public required Guid SetupAuthorityEventId { get; init; }
        public required Guid OwnerGrantEventId { get; init; }
        public required Guid CleanupAuthorityEventId { get; init; }
        public required Guid OwnerRevocationEventId { get; init; }
        public required string P1ProjectId { get; init; }
        public required Guid P1InvocationId { get; init; }
        public required Guid P1OwnerAssignmentId { get; init; }
        public required Guid P1OrchestratorAssignmentId { get; init; }
        public required Guid[] P1EventIds { get; init; }
        public required string NonP1ProjectId { get; init; }
        public required Guid NonP1AssignmentId { get; init; }
        public required Guid NonP1EventId { get; init; }
        public required Guid[] EventIds { get; init; }

        public Guid[] OperationEventIds =>
        [
            SetupAuthorityEventId,
            OwnerGrantEventId,
            CleanupAuthorityEventId,
            OwnerRevocationEventId,
        ];

        public Guid[] AllEventIds => [.. OperationEventIds, .. P1EventIds, NonP1EventId];
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
