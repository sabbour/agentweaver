using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigMigrationInventoryTests(
    ProjectsConfigPostgresFixture fixture) : IClassFixture<ProjectsConfigPostgresFixture>
{
    [Fact]
    public async Task EfHistoryUsesQuotedMigrationIdAndIncludesOwnerOnlySetupOperation()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();

        await using (var incorrect = new NpgsqlCommand(
            """SELECT migration_id FROM "projects_config"."__ef_migrations_history" """,
            connection))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(
                async () => _ = await incorrect.ExecuteScalarAsync());
            Assert.Equal("42703", error.SqlState);
        }

        await using (var owned = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1
              FROM pg_catalog.pg_class AS relation
              JOIN pg_catalog.pg_namespace AS schema ON schema.oid = relation.relnamespace
              WHERE schema.nspname = 'projects_config'
                AND relation.relname = 'project_setup_operations'
                AND relation.relkind = 'r'
                AND relation.relowner = schema.nspowner)
            """,
            connection))
        {
            Assert.True((bool)(await owned.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Owned-table check returned no result.")));
        }

        var migrationIds = new List<string>();
        await using (var corrected = new NpgsqlCommand(
            """SELECT "MigrationId" FROM "projects_config"."__ef_migrations_history" ORDER BY "MigrationId" """,
            connection))
        await using (var reader = await corrected.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                migrationIds.Add(reader.GetString(0));
        }

        Assert.Contains("20261013095816_AddProjectSetupOperations", migrationIds);
        Assert.Contains("20261013194803_AddProjectSetupOwnerGrantReceipts", migrationIds);
        Assert.All(migrationIds, migrationId => Assert.Contains('_', migrationId));
    }
}
