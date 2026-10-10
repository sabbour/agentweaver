using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Environment;

public static class EnvironmentMigrator
{
    private const string LockName = "agentweaver.environment.migrate";

    public static async Task VerifyMigrationsAppliedAsync(
        NpgsqlDataSource dataSource,
        DbContextOptions<EnvironmentDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var schemaCheck = new NpgsqlCommand(
            """
            SELECT pg_catalog.to_regnamespace('environment') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."__ef_migrations_history"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."owners"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."lifecycle_operations"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."owner_effects"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."sandbox_leases"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."remote_mcp_connections"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."remote_mcp_connection_configurations"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."remote_mcp_catalog_snapshots"') IS NOT NULL
               AND pg_catalog.to_regclass('"environment"."remote_mcp_connection_idempotency"') IS NOT NULL
            """,
            connection);
        if (await schemaCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Environment schema or migration history is missing. Run the explicit Environment migration command before starting the service.");

        var builder = new DbContextOptionsBuilder<EnvironmentDbContext>(options);
        builder.UseNpgsql(
            connection,
            npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history",
                EnvironmentDbContext.Schema));
        await using var context = new EnvironmentDbContext(builder.Options);
        var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false))
            .ToArray();
        if (pending.Length > 0)
            throw new InvalidOperationException(
                $"The Environment database has pending migrations ({string.Join(", ", pending)}). Run the explicit Environment migration command before starting the service.");
    }

    public static async Task VerifyRuntimeAuthorityAsync(
        NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            """
            SELECT has_schema_privilege(current_user, 'environment', 'USAGE')
               AND NOT has_schema_privilege(current_user, 'environment', 'CREATE')
               AND has_table_privilege(current_user, 'environment.owners', 'SELECT')
               AND has_table_privilege(current_user, 'environment.owners', 'INSERT')
               AND has_table_privilege(current_user, 'environment.owners', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.owners', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.owners', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.lifecycle_operations', 'SELECT')
               AND has_table_privilege(current_user, 'environment.lifecycle_operations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'environment.lifecycle_operations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.lifecycle_operations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.lifecycle_operations', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.owner_effects', 'SELECT')
               AND has_table_privilege(current_user, 'environment.owner_effects', 'INSERT')
               AND has_table_privilege(current_user, 'environment.owner_effects', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.owner_effects', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.owner_effects', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.sandbox_leases', 'SELECT')
               AND has_table_privilege(current_user, 'environment.sandbox_leases', 'INSERT')
               AND has_table_privilege(current_user, 'environment.sandbox_leases', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.sandbox_leases', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.sandbox_leases', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connections', 'SELECT')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connections', 'INSERT')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connections', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connections', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connections', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connection_configurations', 'SELECT')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connection_configurations', 'INSERT')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connection_configurations', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connection_configurations', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connection_configurations', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.remote_mcp_catalog_snapshots', 'SELECT')
               AND has_table_privilege(current_user, 'environment.remote_mcp_catalog_snapshots', 'INSERT')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_catalog_snapshots', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_catalog_snapshots', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_catalog_snapshots', 'TRUNCATE')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connection_idempotency', 'SELECT')
               AND has_table_privilege(current_user, 'environment.remote_mcp_connection_idempotency', 'INSERT')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connection_idempotency', 'UPDATE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connection_idempotency', 'DELETE')
               AND NOT has_table_privilege(current_user, 'environment.remote_mcp_connection_idempotency', 'TRUNCATE')
            """,
            connection);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
            throw new InvalidOperationException(
                "The Environment runtime principal must have required owner-scoped lifecycle and connection DML, append-only history/catalog/idempotency access, no schema CREATE, and no physical DELETE/TRUNCATE authority.");
    }

    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        DbContextOptions<EnvironmentDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext(@lock_name))",
            connection))
        {
            advisoryLock.Parameters.AddWithValue("lock_name", LockName);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var builder = new DbContextOptionsBuilder<EnvironmentDbContext>(options);
            builder.UseNpgsql(
                connection,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history",
                    EnvironmentDbContext.Schema));
            await using var context = new EnvironmentDbContext(builder.Options);
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext(@lock_name))",
                connection);
            release.Parameters.AddWithValue("lock_name", LockName);
            await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
