using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Identity.Broker;

/// <summary>
/// Applies the broker's EF Core migrations under a Postgres advisory lock held on the
/// SAME connection that runs the migration, so two concurrent instances cannot race each
/// other's DDL. The lock is scoped to this schema's name, not a global migration lock.
/// </summary>
public static class IdentityBrokerMigrator
{
    public static async Task VerifyMigrationsAppliedAsync(
        NpgsqlDataSource dataSource,
        DbContextOptions<IdentityBrokerDbContext> options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(options);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var schemaCheck = new NpgsqlCommand(
            """
            SELECT pg_catalog.to_regnamespace('identity_broker') IS NOT NULL
               AND pg_catalog.to_regclass('"identity_broker"."__ef_migrations_history"') IS NOT NULL
            """,
            connection);
        if (await schemaCheck.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException(
                "The Identity broker schema or migration history is missing. Run the approved Identity broker migration Job before starting the runtime.");
        }

        var builder = new DbContextOptionsBuilder<IdentityBrokerDbContext>(options);
        builder.UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(
            "__ef_migrations_history", IdentityBrokerDbContext.Schema));
        await using var context = new IdentityBrokerDbContext(builder.Options);

        IEnumerable<string> pendingMigrations;
        try
        {
            pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.InsufficientPrivilege)
        {
            throw new InvalidOperationException(
                "The Identity broker runtime principal cannot inspect its migration history. Verify its approved schema and table grants.",
                exception);
        }

        var pending = pendingMigrations.ToArray();
        if (pending.Length > 0)
        {
            throw new InvalidOperationException(
                $"The Identity broker database has pending migrations ({string.Join(", ", pending)}). Run the approved Identity broker migration Job before starting the runtime.");
        }
    }

    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource, DbContextOptions<IdentityBrokerDbContext> options,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_lock(hashtext('agentweaver.identity_broker.migrate'))", connection))
        {
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }

        try
        {
            var builder = new DbContextOptionsBuilder<IdentityBrokerDbContext>(options);
            builder.UseNpgsql(connection, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", IdentityBrokerDbContext.Schema));
            await using var migrationContext = new IdentityBrokerDbContext(builder.Options);
            await migrationContext.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            await using var release = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtext('agentweaver.identity_broker.migrate'))", connection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
