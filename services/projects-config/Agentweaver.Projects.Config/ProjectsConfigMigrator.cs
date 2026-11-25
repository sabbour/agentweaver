using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agentweaver.Projects.Config;

/// <summary>Applies EF migrations, reconciles the reviewed runtime grants, and verifies permissions before serving traffic.</summary>
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
    /// Verifies the runtime authority boundary, including the exact draft-provider table grants.
    /// Role-assignment writes are optional, but when present they must match the role-assignment API grants.
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
               AND has_table_privilege(current_user, 'projects_config.model_providers', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.model_providers', 'INSERT')
               AND has_table_privilege(current_user, 'projects_config.model_providers', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_providers', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_providers', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.model_provider_revisions', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.model_provider_revisions', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_revisions', 'UPDATE')
               AND NOT has_any_column_privilege(
                   current_user, 'projects_config.model_provider_revisions', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_revisions', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_revisions', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.model_provider_reference_reservations', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.model_provider_reference_reservations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_reference_reservations', 'UPDATE')
               AND NOT has_any_column_privilege(current_user, 'projects_config.model_provider_reference_reservations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_reference_reservations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_reference_reservations', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.model_provider_credential_operations', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.model_provider_credential_operations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_credential_operations', 'UPDATE')
               AND NOT has_any_column_privilege(current_user, 'projects_config.model_provider_credential_operations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_credential_operations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_credential_operations', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'consumed_at', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'consumed_by_kind', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'preflight_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'operation_id', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_credential_preflights', 'TRUNCATE')
               AND has_table_privilege(current_user, 'projects_config.model_provider_host_requests', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.model_provider_host_requests', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_host_requests', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'delivery_state', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'attempt_count', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'next_attempt_at', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'lease_owner', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'lease_token', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'lease_expires_at', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'environment_destination_id', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'delivered_at', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'failure_code', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'last_attempt_at', 'UPDATE')
               AND has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'updated_at', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'request_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'tenant_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'scope', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'project_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'provider_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'provider_revision', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'configuration_hash', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'operation_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'actor_id', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'host', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'port', 'UPDATE')
               AND NOT has_column_privilege(current_user, 'projects_config.model_provider_host_requests', 'created_at', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_host_requests', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.model_provider_host_requests', 'TRUNCATE')
               AND NOT has_table_privilege(current_user, 'projects_config.installation_identity', 'INSERT')
               AND NOT has_any_column_privilege(current_user, 'projects_config.installation_identity', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.installation_identity', 'UPDATE')
               AND NOT has_any_column_privilege(current_user, 'projects_config.installation_identity', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.installation_identity', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.installation_identity', 'TRUNCATE')
               AND NOT has_table_privilege(current_user, 'projects_config.installation_identity', 'REFERENCES')
               AND NOT has_any_column_privilege(current_user, 'projects_config.installation_identity', 'REFERENCES')
               AND NOT has_table_privilege(current_user, 'projects_config.installation_identity', 'TRIGGER')
               AND has_table_privilege(current_user, 'projects_config.native_acs_policy_preparations', 'SELECT')
               AND has_table_privilege(current_user, 'projects_config.native_acs_policy_preparations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.native_acs_policy_preparations', 'UPDATE')
               AND NOT has_any_column_privilege(
                   current_user, 'projects_config.native_acs_policy_preparations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.native_acs_policy_preparations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.native_acs_policy_preparations', 'TRUNCATE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'SELECT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'projects_config.project_setup_operations', 'TRUNCATE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.lock_casting_authority(uuid, text, text, text, bigint, text, boolean)',
                   'EXECUTE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.lock_native_acs_policy_authority(uuid, text, text, text, bigint, text, text, uuid[], text[], text[], text[], bigint[])',
                   'EXECUTE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.lock_credential_publication_authority(uuid, text, text, text, bigint, uuid, bigint, text, text)',
                   'EXECUTE')
               AND NOT has_function_privilege(
                   current_user,
                   'projects_config.guard_model_provider_reference_cross_table()',
                   'EXECUTE')
               AND NOT has_function_privilege(
                   current_user,
                   'projects_config.guard_provider_reference_installation()',
                   'EXECUTE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
                   'EXECUTE')
               AND has_function_privilege(
                   current_user,
                   'projects_config.read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)',
                   'EXECUTE')
               AND NOT EXISTS (
                   SELECT 1
                   FROM pg_catalog.pg_proc AS routine
                   CROSS JOIN LATERAL pg_catalog.aclexplode(
                       COALESCE(routine.proacl, pg_catalog.acldefault('f', routine.proowner))) AS acl
                   WHERE routine.oid IN (
                       pg_catalog.to_regprocedure(
                           'projects_config.issue_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)'),
                       pg_catalog.to_regprocedure(
                           'projects_config.read_p1_project(uuid, text, text, text, uuid, bigint, uuid, bigint)'),
                       pg_catalog.to_regprocedure(
                           'projects_config.lock_native_acs_policy_authority(uuid, text, text, text, bigint, text, text, uuid[], text[], text[], text[], bigint[])'),
                       pg_catalog.to_regprocedure(
                           'projects_config.guard_model_provider_reference_cross_table()'),
                       pg_catalog.to_regprocedure(
                           'projects_config.guard_provider_reference_installation()'))
                     AND acl.grantee = 0
                     AND acl.privilege_type = 'EXECUTE')
            """,
            connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Projects & Config runtime database principal must have SELECT on authority tables, no membership writes, no DELETE or TRUNCATE on authority tables, SELECT/INSERT/UPDATE but no DELETE/TRUNCATE on hosted_model_reasoning_catalog and model_providers, SELECT/INSERT but no UPDATE/DELETE/TRUNCATE on model_provider_revisions, model_provider_reference_reservations, and native_acs_policy_preparations, no access to project_setup_operations, EXECUTE on projects_config.lock_casting_authority, lock_native_acs_policy_authority, issue_p1_project, and read_p1_project, and no schema CREATE privilege. Role-assignment writes are optional, but when present they must be exactly INSERT on project_role_assignments and authority_audit plus UPDATE on project_role_assignments (state, revision, revoked_by, revoked_at).");
        await ProjectsConfigRuntimeGrantPlan.VerifyProviderGuardsAsync(connection, null, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Applies migrations and runtime grants in one transaction under a PostgreSQL advisory lock.
    /// </summary>
    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        DbContextOptions<ProjectsConfigDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await MigrateAsync(connection, transaction, options, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task MigrateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DbContextOptions<ProjectsConfigDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(options);

        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_catalog.pg_advisory_xact_lock(pg_catalog.hashtext(@lock_name))",
            connection,
            transaction))
        {
            advisoryLock.Parameters.AddWithValue("lock_name", LockName);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ProjectsConfigRuntimeGrantPlan.VerifyMigrationPrincipalAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        var builder = new DbContextOptionsBuilder<ProjectsConfigDbContext>(options);
        builder.UseNpgsql(connection, npgsql =>
            npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema));
        await using var context = new ProjectsConfigDbContext(builder.Options);
        await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await context.GetService<IHistoryRepository>().CreateIfNotExistsAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        await ProjectsConfigRuntimeGrantPlan.ApplyAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Drains already-dispatched credential operations as the Projects migration table owner.
    /// </summary>
    public static async Task SettleCredentialOperationsAsync(
        NpgsqlDataSource dataSource,
        TimeSpan? lockTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        var timeoutSeconds = Math.Max(1, (int)Math.Ceiling((lockTimeout ?? TimeSpan.FromSeconds(30)).TotalSeconds));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ProjectsConfigRuntimeGrantPlan.VerifyMigrationPrincipalAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SET LOCAL lock_timeout = '{timeoutSeconds}s';
            LOCK TABLE projects_config.model_provider_credential_operations IN SHARE MODE;
            """,
            connection,
            transaction);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
