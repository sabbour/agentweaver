using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

public static class ProjectsConfigMigrator
{
    private const string LockName = "agentweaver.projects_config.migrate";

    public static async Task VerifyMigrationsAppliedAsync(
        NpgsqlDataSource dataSource,
        DbContextOptions<ProjectsConfigDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var schemaCheck = new NpgsqlCommand(
            """
            SELECT pg_catalog.to_regnamespace('projects_config') IS NOT NULL
               AND pg_catalog.to_regclass('"projects_config"."__ef_migrations_history"') IS NOT NULL
            """,
            connection);
        if (await schemaCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Projects & Config schema or migration history is missing. Run the approved Projects & Config migration Job before starting the service.");

        var builder = new DbContextOptionsBuilder<ProjectsConfigDbContext>(options);
        builder.UseNpgsql(connection, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema));
        await using var context = new ProjectsConfigDbContext(builder.Options);
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToArray();
        if (pending.Length > 0)
            throw new InvalidOperationException(
                $"The Projects & Config database has pending migrations ({string.Join(", ", pending)}). Run the approved Projects & Config migration Job before starting the service.");
    }

    public static async Task VerifyRuntimeAuthorityReadOnlyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT has_schema_privilege(current_user, 'projects_config', 'USAGE')
               AND NOT has_schema_privilege(current_user, 'projects_config', 'CREATE')
               AND has_table_privilege(current_user, 'projects_config.tenant_memberships', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.project_role_assignments', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.authority_audit', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'TRUNCATE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
                   'EXECUTE')
            """,
            connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Projects & Config runtime database principal must have SELECT-only access to authority tables, EXECUTE on projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean), and no schema CREATE privilege.");
    }

    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        DbContextOptions<ProjectsConfigDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext(@lock_name))", connection))
        {
            advisoryLock.Parameters.AddWithValue("lock_name", LockName);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var builder = new DbContextOptionsBuilder<ProjectsConfigDbContext>(options);
            builder.UseNpgsql(connection, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema));
            await using var context = new ProjectsConfigDbContext(builder.Options);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext(@lock_name))", connection);
            release.Parameters.AddWithValue("lock_name", LockName);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
