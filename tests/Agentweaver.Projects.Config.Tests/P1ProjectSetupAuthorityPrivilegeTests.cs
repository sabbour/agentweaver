using System.Net;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task P1SetupEndpointWorksWithOnlyDeclaredAuthorityGrantsAndCannotCleanupOrWriteMembership()
    {
        await using var db = CreateDbContext();
        var ownerOptions = CreateDbContextOptions();
        var ownerStore = new ProjectsConfigPrivilegedAuthorityStore(ownerOptions, TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = $"p1-limited-tenant-{suffix}";
        var subject = $"p1-limited-admin-{suffix}";
        var caller = await SeedCallerAsync(
            db,
            ownerStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);

        var roleName = $"aw_p1_setup_{suffix}";
        var password = Guid.NewGuid().ToString("N");
        var roleCreated = false;
        async Task ExecuteOwnerSqlAsync(string sql)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await ExecuteOwnerSqlAsync(
                $"CREATE ROLE \"{roleName}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
            roleCreated = true;
            await ExecuteOwnerSqlAsync($"GRANT USAGE ON SCHEMA projects_config TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync(
                $"GRANT SELECT ON TABLE projects_config.__ef_migrations_history, projects_config.tenant_memberships TO \"{roleName}\"");
            await ExecuteOwnerSqlAsync($"""
                GRANT SELECT, INSERT ON TABLE
                    projects_config.projects,
                    projects_config.project_configuration_revisions,
                    projects_config.project_role_assignments,
                    projects_config.authority_audit,
                    projects_config.project_setup_operations
                TO "{roleName}"
                """);
            await ExecuteOwnerSqlAsync($"""
                GRANT EXECUTE ON FUNCTION projects_config.lock_casting_authority(
                    uuid, text, text, text, bigint, text, boolean) TO "{roleName}"
                """);

            var authorityConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = roleName,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            await using (var authorityDataSource = NpgsqlDataSource.Create(authorityConnectionString))
            {
                var authorityOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                    .UseNpgsql(
                        authorityDataSource,
                        npgsql => npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history", ProjectsConfigDbContext.Schema))
                    .Options;

                await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(authorityDataSource, authorityOptions);
                await ProjectsConfigMigrator.VerifyP1SetupAuthorityAsync(authorityDataSource);
                await AssertDeclaredAuthorityPrivilegeMatrixAsync(roleName);

                var excessRoleName = $"aw_p1_setup_extra_{suffix}";
                await ExecuteOwnerSqlAsync(
                    $"CREATE ROLE \"{excessRoleName}\" NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT");
                try
                {
                    await ExecuteOwnerSqlAsync(
                        $"GRANT UPDATE (revision) ON TABLE projects_config.projects TO \"{excessRoleName}\"");
                    await ExecuteOwnerSqlAsync($"GRANT \"{excessRoleName}\" TO \"{roleName}\"");
                    await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        ProjectsConfigMigrator.VerifyP1SetupAuthorityAsync(authorityDataSource));
                }
                finally
                {
                    await ExecuteOwnerSqlAsync($"REVOKE \"{excessRoleName}\" FROM \"{roleName}\"");
                    await ExecuteOwnerSqlAsync(
                        $"REVOKE UPDATE (revision) ON TABLE projects_config.projects FROM \"{excessRoleName}\"");
                    await ExecuteOwnerSqlAsync($"DROP ROLE IF EXISTS \"{excessRoleName}\"");
                }

                await ExecuteOwnerSqlAsync(
                    $"GRANT UPDATE (revision) ON TABLE projects_config.projects TO \"{roleName}\"");
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    ProjectsConfigMigrator.VerifyP1SetupAuthorityAsync(authorityDataSource));
                await ExecuteOwnerSqlAsync(
                    $"REVOKE UPDATE (revision) ON TABLE projects_config.projects FROM \"{roleName}\"");
                await ProjectsConfigMigrator.VerifyP1SetupAuthorityAsync(authorityDataSource);

                var capability = new P1ProjectSetupAuthorityCapability(
                    authorityOptions,
                    new PrivilegedProjectSetupApproval(
                        new Uri(TestIssuer),
                        "projects-config-tests",
                        subject,
                        tenantId));
                await using var app = await StartP1SetupServerAsync(capability);
                using var client = app.GetTestClient();
                var invocationId = Guid.NewGuid();

                using var firstRequest = SetupRequest(invocationId, subject, tenantId);
                using var firstResponse = await client.SendAsync(firstRequest);
                Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
                var receipt = await firstResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>();
                Assert.NotNull(receipt);

                using var replayRequest = SetupRequest(invocationId, subject, tenantId);
                using var replayResponse = await client.SendAsync(replayRequest);
                Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
                Assert.Equal(receipt, await replayResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>());

                var restrictedStore = new ProjectsConfigPrivilegedAuthorityStore(
                    authorityOptions, TimeProvider.System);
                var cleanupFailure = await Record.ExceptionAsync(() =>
                    restrictedStore.ExecuteProjectSetupOperationAsync(
                        caller,
                        new ProjectSetupOperationRequest(
                            1,
                            invocationId,
                            ProjectSetupOperationKind.Cleanup,
                            tenantId,
                            receipt.ProjectRevision,
                            receipt.AssignmentRevision)));
                AssertInsufficientPrivilege(cleanupFailure);

                await AssertRestrictedStatementDeniedAsync(
                    authorityDataSource,
                    """
                    UPDATE projects_config.tenant_memberships
                    SET revision = revision
                    WHERE false
                    """);
            }
        }
        finally
        {
            if (roleCreated)
            {
                await ExecuteOwnerSqlAsync($"DROP OWNED BY \"{roleName}\"");
                await ExecuteOwnerSqlAsync($"DROP ROLE IF EXISTS \"{roleName}\"");
            }
        }
    }

    [Fact]
    public async Task P1SetupEndpointRejectsReplayAfterTenantAdminAssignmentRevocation()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"p1-revoked-admin-tenant-{Guid.NewGuid():N}";
        var subject = $"p1-revoked-admin-{Guid.NewGuid():N}";
        var caller = await SeedCallerAsync(
            db,
            authorityStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var tenantAdmin = Assert.Single(caller.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Tenant &&
            grant.ResourceId == tenantId &&
            grant.Role == ProjectAuthorityRole.TenantAdmin));
        var capability = new P1ProjectSetupAuthorityCapability(
            options,
            new PrivilegedProjectSetupApproval(new Uri(TestIssuer), "projects-config-tests", subject, tenantId));
        await using var app = await StartP1SetupServerAsync(capability);
        using var client = app.GetTestClient();
        var invocationId = Guid.NewGuid();

        using (var setupRequest = SetupRequest(invocationId, subject, tenantId))
        using (var setupResponse = await client.SendAsync(setupRequest))
            Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);

        await authorityStore.RevokeRoleAssignmentAsync(
            tenantAdmin.AssignmentId,
            tenantAdmin.Revision,
            subject);
        using var replayRequest = SetupRequest(invocationId, subject, tenantId);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Forbidden, replayResponse.StatusCode);
    }

    [Fact]
    public async Task P1SetupEndpointRejectsReplayWhenOwnerReceiptAssignmentRevisionChanges()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"p1-stale-owner-tenant-{Guid.NewGuid():N}";
        var subject = $"p1-stale-owner-admin-{Guid.NewGuid():N}";
        await SeedCallerAsync(
            db,
            authorityStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var capability = new P1ProjectSetupAuthorityCapability(
            options,
            new PrivilegedProjectSetupApproval(new Uri(TestIssuer), "projects-config-tests", subject, tenantId));
        await using var app = await StartP1SetupServerAsync(capability);
        using var client = app.GetTestClient();
        var invocationId = Guid.NewGuid();

        using var setupRequest = SetupRequest(invocationId, subject, tenantId);
        using var setupResponse = await client.SendAsync(setupRequest);
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        var receipt = await setupResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>();
        Assert.NotNull(receipt);
        Assert.NotNull(receipt.OwnerAssignmentId);

        await db.RoleAssignments
            .Where(item => item.AssignmentId == receipt.OwnerAssignmentId!.Value)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.Revision,
                item => item.Revision + 1));
        using var replayRequest = SetupRequest(invocationId, subject, tenantId);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Conflict, replayResponse.StatusCode);
        Assert.Equal(
            "project_setup_receipt_stale",
            await ReadProblemCodeAsync(replayResponse));
    }

    [Fact]
    public async Task P1SetupEndpointRejectsReplayWhenOrchestratorReceiptAssignmentIsRevoked()
    {
        await using var db = CreateDbContext();
        var options = CreateDbContextOptions();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var tenantId = $"p1-stale-orchestrator-tenant-{Guid.NewGuid():N}";
        var subject = $"p1-stale-orchestrator-admin-{Guid.NewGuid():N}";
        await SeedCallerAsync(
            db,
            authorityStore,
            subject,
            tenantId,
            ["api.read", "projects.admin", "projects.orchestrator"],
            ProjectAuthorityResourceType.Tenant,
            tenantId,
            ProjectAuthorityRole.TenantAdmin);
        var capability = new P1ProjectSetupAuthorityCapability(
            options,
            new PrivilegedProjectSetupApproval(new Uri(TestIssuer), "projects-config-tests", subject, tenantId));
        await using var app = await StartP1SetupServerAsync(capability);
        using var client = app.GetTestClient();
        var invocationId = Guid.NewGuid();

        using var setupRequest = SetupRequest(invocationId, subject, tenantId);
        using var setupResponse = await client.SendAsync(setupRequest);
        Assert.Equal(HttpStatusCode.OK, setupResponse.StatusCode);
        var receipt = await setupResponse.Content.ReadFromJsonAsync<ProjectSetupReceipt>();
        Assert.NotNull(receipt);

        await db.RoleAssignments
            .Where(item => item.AssignmentId == receipt.AssignmentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.State, ProjectAuthorityRecordState.Revoked)
                .SetProperty(item => item.Revision, item => item.Revision + 1));
        using var replayRequest = SetupRequest(invocationId, subject, tenantId);
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Conflict, replayResponse.StatusCode);
        Assert.Equal(
            "project_setup_receipt_stale",
            await ReadProblemCodeAsync(replayResponse));
    }

    private async Task AssertDeclaredAuthorityPrivilegeMatrixAsync(string roleName)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var roleCommand = new NpgsqlCommand(
            """
            SELECT NOT role.rolsuper
               AND NOT role.rolcreatedb
               AND NOT role.rolcreaterole
               AND NOT role.rolinherit
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_auth_members AS membership
                   WHERE membership.member = role.oid OR membership.roleid = role.oid)
            FROM pg_catalog.pg_roles AS role
            WHERE role.rolname = @role
            """,
            connection))
        {
            roleCommand.Parameters.AddWithValue("role", roleName);
            Assert.True((bool)(await roleCommand.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The restricted authority role was not found.")));
        }

        await using (var schemaCommand = new NpgsqlCommand(
            """
            SELECT has_schema_privilege(@role, 'projects_config', 'USAGE')
               AND NOT has_schema_privilege(@role, 'projects_config', 'CREATE')
               AND has_function_privilege(
                   @role,
                   'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
                   'EXECUTE')
            """,
            connection))
        {
            schemaCommand.Parameters.AddWithValue("role", roleName);
            Assert.True((bool)(await schemaCommand.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The restricted authority privilege check returned no result.")));
        }

        var expected = new Dictionary<string, (bool Select, bool Insert)>(StringComparer.Ordinal)
        {
            ["__ef_migrations_history"] = (true, false),
            ["tenant_memberships"] = (true, false),
            ["projects"] = (true, true),
            ["project_configuration_revisions"] = (true, true),
            ["project_role_assignments"] = (true, true),
            ["authority_audit"] = (true, true),
            ["project_setup_operations"] = (true, true),
        };
        await using (var tableCommand = new NpgsqlCommand(
            """
            SELECT relation.relname,
                   has_table_privilege(@role, relation.oid, 'SELECT'),
                   has_table_privilege(@role, relation.oid, 'INSERT'),
                   has_table_privilege(@role, relation.oid, 'UPDATE'),
                   has_table_privilege(@role, relation.oid, 'DELETE'),
                   has_table_privilege(@role, relation.oid, 'TRUNCATE')
            FROM pg_catalog.pg_class AS relation
            JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
            WHERE schema.nspname = 'projects_config'
              AND relation.relkind IN ('r', 'p')
            ORDER BY relation.relname
            """,
            connection))
        {
            tableCommand.Parameters.AddWithValue("role", roleName);
            await using var reader = await tableCommand.ExecuteReaderAsync();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                var tableName = reader.GetString(0);
                seen.Add(tableName);
                var declared = expected.TryGetValue(tableName, out var grants);
                Assert.Equal(declared && grants.Select, reader.GetBoolean(1));
                Assert.Equal(declared && grants.Insert, reader.GetBoolean(2));
                Assert.False(reader.GetBoolean(3));
                Assert.False(reader.GetBoolean(4));
                Assert.False(reader.GetBoolean(5));
            }
            Assert.All(expected.Keys, tableName => Assert.Contains(tableName, seen));
        }

        await using (var sequenceCommand = new NpgsqlCommand(
            """
            SELECT NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_class AS relation
                JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
                WHERE schema.nspname = 'projects_config'
                  AND relation.relkind = 'S'
                  AND (
                      has_sequence_privilege(@role, relation.oid, 'USAGE')
                      OR has_sequence_privilege(@role, relation.oid, 'SELECT')
                      OR has_sequence_privilege(@role, relation.oid, 'UPDATE')))
            """,
            connection))
        {
            sequenceCommand.Parameters.AddWithValue("role", roleName);
            Assert.True((bool)(await sequenceCommand.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("The sequence privilege check returned no result.")));
        }
    }

    private static async Task AssertRestrictedStatementDeniedAsync(
        NpgsqlDataSource dataSource,
        string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, exception.SqlState);
    }

    private static void AssertInsufficientPrivilege(Exception? exception)
    {
        Assert.NotNull(exception);
        var postgres = exception;
        while (postgres is not null && postgres is not PostgresException)
            postgres = postgres.InnerException;
        Assert.IsType<PostgresException>(postgres);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ((PostgresException)postgres).SqlState);
    }

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var problem = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync());
        return problem.RootElement.GetProperty("code").GetString();
    }
}
