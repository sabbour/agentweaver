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
