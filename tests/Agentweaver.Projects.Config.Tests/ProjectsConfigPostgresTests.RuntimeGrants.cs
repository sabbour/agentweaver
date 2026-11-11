using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task MigrationAppliesTheReviewedRuntimeGrantsAndKeepsOwnerOnlyObjectsPrivate()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT coalesce(array_agg(check_name ORDER BY check_name), ARRAY[]::text[])
            FROM (VALUES
              ('migration schema owner',
               (SELECT pg_catalog.pg_get_userbyid(nspowner) = 'agentweaver-id-projects-config-migration'
                FROM pg_catalog.pg_namespace WHERE nspname = 'projects_config')),
              ('migration database CREATE',
               NOT has_database_privilege(
                 'agentweaver-id-projects-config-migration', current_database(), 'CREATE')),
              ('runtime schema USAGE',
               has_schema_privilege('agentweaver-id-projects-config', 'projects_config', 'USAGE')),
              ('runtime schema CREATE denied',
               NOT has_schema_privilege('agentweaver-id-projects-config', 'projects_config', 'CREATE')),
              ('projects INSERT',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.projects', 'INSERT')),
              ('model providers INSERT',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_providers', 'INSERT')),
              ('model providers SELECT',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_providers', 'SELECT')),
              ('model providers UPDATE',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_providers', 'UPDATE')),
              ('model providers DELETE denied',
               NOT has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_providers', 'DELETE')),
              ('model provider revisions SELECT',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_provider_revisions', 'SELECT')),
              ('model provider revisions INSERT',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_provider_revisions', 'INSERT')),
              ('model provider revisions UPDATE denied',
               NOT has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_provider_revisions', 'UPDATE')),
              ('model provider revisions DELETE denied',
               NOT has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_provider_revisions', 'DELETE')),
              ('model provider revisions TRUNCATE denied',
               NOT has_table_privilege('agentweaver-id-projects-config', 'projects_config.model_provider_revisions', 'TRUNCATE')),
              ('platform runtime heads SELECT and UPDATE',
               has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.platform_runtime_heads', 'SELECT')
               AND has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.platform_runtime_heads', 'UPDATE')),
              ('platform runtime heads INSERT denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.platform_runtime_heads', 'INSERT')),
              ('native ACS heads SELECT and INSERT',
               has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_heads', 'SELECT')
               AND has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_heads', 'INSERT')),
              ('native ACS heads table UPDATE denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_heads', 'UPDATE')),
              ('native ACS head pointer UPDATE',
               has_column_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.native_acs_policy_heads', 'current_revision', 'UPDATE')
               AND has_column_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.native_acs_policy_heads', 'updated_at', 'UPDATE')),
              ('native ACS head identity UPDATE denied',
               NOT has_column_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.native_acs_policy_heads', 'policy_id', 'UPDATE')),
              ('native ACS revision UPDATE and DELETE denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_revisions', 'UPDATE')
               AND NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_revisions', 'DELETE')),
              ('native ACS artifact file UPDATE and DELETE denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_artifact_files', 'UPDATE')
               AND NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_artifact_files', 'DELETE')),
              ('native ACS idempotency UPDATE and DELETE denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_idempotency', 'UPDATE')
               AND NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_idempotency', 'DELETE')),
              ('native ACS audit UPDATE and DELETE denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_change_audit', 'UPDATE')
               AND NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_change_audit', 'DELETE')),
              ('native ACS outbox UPDATE and DELETE denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_outbox', 'UPDATE')
               AND NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.native_acs_policy_outbox', 'DELETE')),
              ('native ACS head trigger EXECUTE denied',
               NOT has_function_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.require_native_acs_policy_head_advance()', 'EXECUTE')),
              ('role assignments INSERT',
               has_table_privilege('agentweaver-id-projects-config', 'projects_config.project_role_assignments', 'INSERT')),
              ('revoked_at UPDATE',
               has_column_privilege(
                 'agentweaver-id-projects-config', 'projects_config.project_role_assignments', 'revoked_at', 'UPDATE')),
              ('assignment_id UPDATE denied',
               NOT has_column_privilege(
                 'agentweaver-id-projects-config', 'projects_config.project_role_assignments', 'assignment_id', 'UPDATE')),
              ('setup receipts SELECT denied',
               NOT has_table_privilege(
                 'agentweaver-id-projects-config', 'projects_config.project_setup_operations', 'SELECT')),
              ('revision sequence USAGE denied',
               NOT has_sequence_privilege(
                 'agentweaver-id-projects-config', 'projects_config.platform_runtime_revisions_revision_seq', 'USAGE')),
              ('issue function EXECUTE',
               has_function_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)', 'EXECUTE')),
              ('read function EXECUTE',
               has_function_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)', 'EXECUTE')),
              ('casting authority function EXECUTE',
               has_function_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)', 'EXECUTE')),
              ('native ACS authority function EXECUTE',
               has_function_privilege(
                 'agentweaver-id-projects-config',
                 'projects_config.lock_native_acs_policy_authority(uuid, text, text, text, bigint, text, text, uuid[], text[], text[], text[], bigint[])',
                 'EXECUTE')),
              ('issue function PUBLIC EXECUTE denied',
               NOT EXISTS (
                 SELECT 1
                 FROM pg_catalog.pg_proc AS p
                 CROSS JOIN LATERAL pg_catalog.aclexplode(
                   coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) AS acl
                 WHERE p.oid = pg_catalog.to_regprocedure(
                   'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)')
                   AND acl.grantee = 0 AND acl.privilege_type = 'EXECUTE'))
            ) AS checks(check_name, passed)
            WHERE passed IS DISTINCT FROM true
            """, connection);
        var failures = (string[])(await command.ExecuteScalarAsync())!;
        Assert.True(failures.Length == 0, $"Runtime grant contract failed: {string.Join(", ", failures)}");

        await using var runtimeConnection = await fixture.DataSource.OpenConnectionAsync();
        await using (var setRole = new NpgsqlCommand(
            """SET ROLE "agentweaver-id-projects-config" """,
            runtimeConnection))
            await setRole.ExecuteNonQueryAsync();
        try
        {
            await using var lockHead = new NpgsqlCommand(
                "SELECT id FROM projects_config.platform_runtime_heads WHERE id = 'default' FOR SHARE",
                runtimeConnection);
            _ = await lockHead.ExecuteScalarAsync();
            await using var updateHead = new NpgsqlCommand(
                "UPDATE projects_config.platform_runtime_heads SET current_revision = current_revision WHERE false",
                runtimeConnection);
            _ = await updateHead.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("RESET ROLE", runtimeConnection);
            await resetRole.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MigrationRefusesToReconcileGrantsFromAnyOtherDatabasePrincipal()
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.DataSource, options));

        Assert.Contains("dedicated schema owner", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"agentweaver-id-projects-config\"", "agentweaver-id-projects-config")]
    [InlineData("PUBLIC", "PUBLIC")]
    public async Task MigrationRejectsColumnGrantsOnOwnerOnlyReceipts(string grantee, string expectedGrantee)
    {
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var grant = new NpgsqlCommand(
            $"GRANT SELECT (result) ON TABLE projects_config.project_setup_operations TO {grantee}", admin))
            await grant.ExecuteNonQueryAsync();

        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, options));
            Assert.Contains("project_setup_operations.result", failure.Message, StringComparison.Ordinal);
            Assert.Contains(expectedGrantee, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            await using var revoke = new NpgsqlCommand(
                $"REVOKE SELECT (result) ON TABLE projects_config.project_setup_operations FROM {grantee}", admin);
            await revoke.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MigrationAllowsOwnerOnlyReceiptColumnGrantToItsOwner()
    {
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var grant = new NpgsqlCommand("""
            GRANT SELECT (result) ON TABLE projects_config.project_setup_operations
                TO "agentweaver-id-projects-config-migration"
            """, admin))
            await grant.ExecuteNonQueryAsync();

        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        try
        {
            await ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, options);
        }
        finally
        {
            await using var revoke = new NpgsqlCommand("""
                REVOKE SELECT (result) ON TABLE projects_config.project_setup_operations
                    FROM "agentweaver-id-projects-config-migration"
                """, admin);
            await revoke.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task MigrationReconcilesAllowListedFunctionGrantsAndRemovesPublicExecute()
    {
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var drift = new NpgsqlCommand("""
            REVOKE EXECUTE ON FUNCTION projects_config.issue_p1_project(
                uuid, text, text, text, uuid, bigint, uuid, bigint)
                FROM "agentweaver-id-projects-config";
            GRANT EXECUTE ON FUNCTION projects_config.issue_p1_project(
                uuid, text, text, text, uuid, bigint, uuid, bigint) TO PUBLIC
            """, connection))
        {
            await drift.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        await ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, options);

        await using var verify = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT has_function_privilege(
                     'agentweaver-id-projects-config',
                     'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
                     'EXECUTE')
               AND NOT EXISTS (
                     SELECT 1
                     FROM pg_catalog.pg_proc AS p
                     CROSS JOIN LATERAL pg_catalog.aclexplode(
                       coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) AS acl
                     WHERE p.oid = pg_catalog.to_regprocedure(
                       'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)')
                       AND acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')
            """, verify);
        Assert.True(await command.ExecuteScalarAsync() is true);
    }
}
