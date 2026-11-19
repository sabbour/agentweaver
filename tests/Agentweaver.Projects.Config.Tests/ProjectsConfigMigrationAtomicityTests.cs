using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigMigrationAtomicityTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    [Fact]
    public async Task SchemaMustExistForBothBaselineAndAtomicMigration()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var admin = NpgsqlDataSource.Create(container.GetConnectionString());
        await using (var connection = await admin.OpenConnectionAsync())
        await using (var setup = new NpgsqlCommand("""
            CREATE ROLE "agentweaver-id-projects-config-migration"
              LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
            CREATE ROLE "agentweaver-id-projects-config" NOLOGIN;
            """, connection))
            await setup.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Options = "-c role=agentweaver-id-projects-config-migration",
        }.ConnectionString;
        await using var migration = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(migration, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        await using (var baseline = new ProjectsConfigDbContext(options))
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => baseline.Database.MigrateAsync());
            Assert.Equal("42501", failure.SqlState);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(migration, options));
        await using var verify = await admin.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("SELECT to_regnamespace('projects_config') IS NULL", verify);
        Assert.True((bool)(await query.ExecuteScalarAsync())!);
    }

    private sealed class ExtraPropertyCustomizer(ModelCustomizerDependencies dependencies)
        : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ProjectRecord>().Property<string>("unmigrated_probe");
        }
    }

    private DbContextOptions<ProjectsConfigDbContext> Options(bool drift = false)
    {
        var builder = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema));
        if (drift)
            builder.ReplaceService<IModelCustomizer, ExtraPropertyCustomizer>();
        return builder.Options;
    }

    private async Task<string[]> HistoryAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT array_agg("MigrationId" ORDER BY "MigrationId") FROM projects_config.__ef_migrations_history
            """, connection);
        return (string[])(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task PendingModelChangesFailInBothMigrationEntries()
    {
        var before = await HistoryAsync();
        var transactional = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var connection = await fixture.MigrationDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ProjectsConfigMigrator.MigrateAsync(connection, transaction, Options(drift: true));
        });
        Assert.Contains("PendingModelChangesWarning", transactional.Message, StringComparison.Ordinal);
        var production = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, Options(drift: true)));
        Assert.Contains("PendingModelChangesWarning", production.Message, StringComparison.Ordinal);
        Assert.Equal(before, await HistoryAsync());
    }

    [Fact]
    public async Task RepeatProductionMigrationPreservesHistory()
    {
        var before = await HistoryAsync();
        await ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, Options());
        Assert.Equal(before, await HistoryAsync());
    }
}
