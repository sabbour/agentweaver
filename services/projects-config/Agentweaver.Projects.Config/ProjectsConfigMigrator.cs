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

    /// <summary>Requires the HTTP setup capability to have exactly its setup-only authority inventory.</summary>
    /// <exception cref="InvalidOperationException">The authority principal has missing or excess privilege.</exception>
    public static async Task VerifyP1SetupAuthorityAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            WITH expected_tables(table_name, can_select, can_insert) AS (
                VALUES
                    ('__ef_migrations_history', true, false),
                    ('tenant_memberships', true, false),
                    ('projects', true, true),
                    ('project_configuration_revisions', true, true),
                    ('project_role_assignments', true, true),
                    ('authority_audit', true, true),
                    ('project_setup_operations', true, true)
            ),
            role_state AS (
                SELECT oid, rolsuper, rolcreatedb, rolcreaterole, rolreplication,
                       rolbypassrls, rolinherit
                FROM pg_catalog.pg_roles
                WHERE rolname = current_user
            ),
            setup_lock AS (
                SELECT pg_catalog.to_regprocedure(
                    'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)') AS oid
            )
            SELECT EXISTS (SELECT 1 FROM role_state)
               AND current_user <> 'postgres'
               AND current_user !~* 'migration'
               AND NOT (SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication
                        OR rolbypassrls OR rolinherit FROM role_state)
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_auth_members AS membership
                   WHERE membership.member = (SELECT oid FROM role_state))
               AND has_schema_privilege(current_user, 'projects_config', 'USAGE')
               AND NOT has_schema_privilege(current_user, 'projects_config', 'CREATE')
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_database AS db
                   WHERE db.datname = current_database()
                     AND db.datdba = (SELECT oid FROM role_state))
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_namespace AS ns
                   WHERE ns.nspname = 'projects_config'
                     AND ns.nspowner = (SELECT oid FROM role_state))
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_class AS rel
                   JOIN pg_catalog.pg_namespace AS ns ON ns.oid = rel.relnamespace
                   WHERE ns.nspname = 'projects_config'
                     AND rel.relowner = (SELECT oid FROM role_state))
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   JOIN pg_catalog.pg_namespace AS ns ON ns.oid = routine.pronamespace
                   WHERE ns.nspname = 'projects_config'
                     AND routine.proowner = (SELECT oid FROM role_state))
               AND (SELECT oid IS NOT NULL FROM setup_lock)
               AND has_function_privilege(current_user, (SELECT oid FROM setup_lock), 'EXECUTE')
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   CROSS JOIN LATERAL pg_catalog.aclexplode(
                       COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) AS acl
                   WHERE routine.oid = (SELECT oid FROM setup_lock)
                     AND acl.grantee = (SELECT oid FROM role_state)
                     AND acl.privilege_type = 'EXECUTE')
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   CROSS JOIN LATERAL pg_catalog.aclexplode(
                       COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) AS acl
                   WHERE routine.oid = (SELECT oid FROM setup_lock)
                     AND acl.grantee = 0
                     AND acl.privilege_type = 'EXECUTE')
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   JOIN pg_catalog.pg_namespace AS ns ON ns.oid = routine.pronamespace
                   CROSS JOIN LATERAL pg_catalog.aclexplode(
                       COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) AS acl
                   WHERE ns.nspname = 'projects_config'
                     AND routine.oid <> (SELECT oid FROM setup_lock)
                     AND acl.grantee = (SELECT oid FROM role_state))
               AND EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   WHERE routine.oid = (SELECT oid FROM setup_lock)
                     AND routine.prosecdef)
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   JOIN pg_catalog.pg_namespace AS ns ON ns.oid = routine.pronamespace
                   WHERE ns.nspname = 'projects_config'
                     AND routine.prosecdef
                     AND routine.oid <> (SELECT oid FROM setup_lock)
                     AND has_function_privilege(current_user, routine.oid, 'EXECUTE'))
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_class AS rel
                   JOIN pg_catalog.pg_namespace AS ns ON ns.oid = rel.relnamespace
                   LEFT JOIN expected_tables AS expected ON expected.table_name = rel.relname
                   WHERE ns.nspname = 'projects_config'
                     AND rel.relkind IN ('r', 'p')
                     AND (
                         has_table_privilege(current_user, rel.oid, 'SELECT')
                             IS DISTINCT FROM COALESCE(expected.can_select, false)
                         OR has_table_privilege(current_user, rel.oid, 'INSERT')
                             IS DISTINCT FROM COALESCE(expected.can_insert, false)
                         OR (NOT COALESCE(expected.can_select, false)
                             AND has_any_column_privilege(current_user, rel.oid, 'SELECT'))
                         OR (NOT COALESCE(expected.can_insert, false)
                             AND has_any_column_privilege(current_user, rel.oid, 'INSERT'))
                         OR has_table_privilege(current_user, rel.oid, 'UPDATE')
                         OR has_any_column_privilege(current_user, rel.oid, 'UPDATE')
                         OR has_table_privilege(current_user, rel.oid, 'DELETE')
                         OR has_table_privilege(current_user, rel.oid, 'TRUNCATE')
                         OR has_table_privilege(current_user, rel.oid, 'REFERENCES')
                         OR has_any_column_privilege(current_user, rel.oid, 'REFERENCES')
                         OR has_table_privilege(current_user, rel.oid, 'TRIGGER')))
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_class AS rel
                   JOIN pg_catalog.pg_namespace AS ns ON ns.oid = rel.relnamespace
                   WHERE ns.nspname = 'projects_config'
                     AND rel.relkind = 'S'
                     AND (
                         has_sequence_privilege(current_user, rel.oid, 'USAGE')
                         OR has_sequence_privilege(current_user, rel.oid, 'SELECT')
                         OR has_sequence_privilege(current_user, rel.oid, 'UPDATE')))
            """,
            connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Projects & Config P1 setup authority principal must be a non-inheriting, non-owner, non-migration role with exactly the declared setup schema/table/function privileges, no extra table or column rights, no sequence rights, and no privileged role attributes or memberships.");
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
