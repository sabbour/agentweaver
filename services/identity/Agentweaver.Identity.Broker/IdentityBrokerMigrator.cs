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
