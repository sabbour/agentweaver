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
