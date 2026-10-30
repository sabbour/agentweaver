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
            SELECT
              (SELECT pg_catalog.pg_get_userbyid(nspowner) = 'agentweaver-id-projects-config-migration'
               FROM pg_catalog.pg_namespace WHERE nspname = 'projects_config')
              AND NOT has_database_privilege(
                'agentweaver-id-projects-config-migration', current_database(), 'CREATE')
              AND has_schema_privilege('agentweaver-id-projects-config', 'projects_config', 'USAGE')
              AND NOT has_schema_privilege('agentweaver-id-projects-config', 'projects_config', 'CREATE')
              AND has_table_privilege('agentweaver-id-projects-config', 'projects_config.projects', 'INSERT')
              AND has_table_privilege('agentweaver-id-projects-config', 'projects_config.project_role_assignments', 'INSERT')
              AND has_column_privilege(
                'agentweaver-id-projects-config', 'projects_config.project_role_assignments', 'revoked_at', 'UPDATE')
              AND NOT has_column_privilege(
                'agentweaver-id-projects-config', 'projects_config.project_role_assignments', 'assignment_id', 'UPDATE')
              AND NOT has_table_privilege(
                'agentweaver-id-projects-config', 'projects_config.project_setup_operations', 'SELECT')
              AND NOT has_sequence_privilege(
                'agentweaver-id-projects-config', 'projects_config.platform_runtime_revisions_revision_seq', 'USAGE')
              AND has_function_privilege(
                'agentweaver-id-projects-config',
                'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)', 'EXECUTE')
              AND NOT EXISTS (
                SELECT 1
                FROM pg_catalog.pg_proc AS p
                CROSS JOIN LATERAL pg_catalog.aclexplode(
                  coalesce(p.proacl, pg_catalog.acldefault('f', p.proowner))) AS acl
                WHERE p.oid = pg_catalog.to_regprocedure(
                  'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)')
                  AND acl.grantee = 0 AND acl.privilege_type = 'EXECUTE')
            """, connection);
        Assert.Equal(true, await command.ExecuteScalarAsync());
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
}
