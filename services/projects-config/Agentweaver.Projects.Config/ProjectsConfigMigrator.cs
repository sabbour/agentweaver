using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>Applies explicit EF migrations and verifies runtime schema and authority permissions before serving traffic.</summary>
public static class ProjectsConfigMigrator
{
    private const string LockName = "agentweaver.projects_config.migrate";

    /// <summary>
    /// Fails startup unless the service schema and migration history exist and no packaged migration is pending.
    /// </summary>
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

    /// <summary>
    /// Verifies the runtime authority boundary: SELECT on authority tables, no membership writes, no deletes,
    /// no access to setup-operation receipts, and either no role-assignment writes or exactly the
    /// role-assignment API grants (INSERT on assignments and audit, and UPDATE only on the four revoke columns).
    /// The hosted reasoning catalog also requires SELECT, INSERT, and UPDATE, with no DELETE or TRUNCATE.
    /// </summary>
    /// <exception cref="InvalidOperationException">The database principal does not match the required grants.</exception>
    public static async Task VerifyRuntimeAuthorityReadOnlyAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH role_assignment_api AS (
                SELECT ARRAY[
                    has_table_privilege(current_user, 'projects_config.project_role_assignments', 'INSERT'),
                    has_table_privilege(current_user, 'projects_config.authority_audit', 'INSERT'),
                    has_column_privilege(current_user, 'projects_config.project_role_assignments', 'state', 'UPDATE'),
                    has_column_privilege(current_user, 'projects_config.project_role_assignments', 'revision', 'UPDATE'),
                    has_column_privilege(current_user, 'projects_config.project_role_assignments', 'revoked_by', 'UPDATE'),
                    has_column_privilege(current_user, 'projects_config.project_role_assignments', 'revoked_at', 'UPDATE')
                ] AS grants
            )
            SELECT has_schema_privilege(current_user, 'projects_config', 'USAGE')
               AND NOT has_schema_privilege(current_user, 'projects_config', 'CREATE')
               AND has_table_privilege(current_user, 'projects_config.tenant_memberships', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.tenant_memberships', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.project_role_assignments', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_role_assignments', 'TRUNCATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'assignment_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'membership_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'resource_type', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'resource_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'role', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'granted_by', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.project_role_assignments', 'granted_at', 'UPDATE')
               AND has_table_privilege(current_user, 'projects_config.authority_audit', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'UPDATE')
               AND NOT has_any_column_privilege(current_user, 'projects_config.authority_audit', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.authority_audit', 'TRUNCATE')
               AND (SELECT true = ALL(grants) OR false = ALL(grants) FROM role_assignment_api)
               AND has_table_privilege(current_user, 'projects_config.hosted_model_reasoning_catalog', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.hosted_model_reasoning_catalog', 'INSERT')
               AND has_table_privilege(current_user, 'projects_config.hosted_model_reasoning_catalog', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.hosted_model_reasoning_catalog', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.hosted_model_reasoning_catalog', 'TRUNCATE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'TRUNCATE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
                   'EXECUTE')
            """,
            connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Projects & Config runtime database principal must have SELECT on authority tables, no membership writes, no DELETE or TRUNCATE on authority tables, SELECT/INSERT/UPDATE but no DELETE/TRUNCATE on hosted_model_reasoning_catalog, no access to project_setup_operations, EXECUTE on projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean), and no schema CREATE privilege. Role-assignment writes are optional, but when present they must be exactly INSERT on project_role_assignments and authority_audit plus UPDATE on project_role_assignments (state, revision, revoked_by, revoked_at).");
    }

    /// <summary>
    /// Serializes schema migration with a PostgreSQL advisory lock and always attempts to release that lock.
    /// </summary>
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
