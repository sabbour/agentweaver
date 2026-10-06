using Agentweaver.EventsAndSessions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.EventsAndSessions.Tests;

[Collection("Sessions PostgreSQL")]
public sealed class EventsAndSessionsMigrationTests : IAsyncLifetime
{
    private readonly SessionsPostgresFixture _fixture;
    private readonly string _schema = "migration_" + Guid.NewGuid().ToString("N");

    public EventsAndSessionsMigrationTests(SessionsPostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync() =>
        await new PostgresOutbox(_fixture.DataSource, _schema).InitializeAsync();

    public async Task DisposeAsync()
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task FreshSchemaAppliesAddressedMessagesThenProjectFacts()
    {
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".sessions_schema_migrations
            """, connection);
        Assert.Equal(3, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task LegacyProjectFactsAtVersionTwoUpgradesWithoutReapplyingFactsMigration()
    {
        // Before integration with addressed messages, the same project-facts DDL was recorded as version 2.
        await ApplyMigrationAsync("Agentweaver.EventsAndSessions.Migrations.001_sessions_journal.sql");
        await ApplyMigrationAsync("Agentweaver.EventsAndSessions.Migrations.003_project_facts.sql");

        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".sessions_schema_migrations (version) VALUES (1), (2);
            INSERT INTO "{_schema}".project_fact_streams (project_id, last_position)
            VALUES ('legacy-project', 7)
            """, connection))
            await seed.ExecuteNonQueryAsync();

        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);

        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT
                last_position,
                (SELECT count(*) FROM "{_schema}".sessions_schema_migrations)
            FROM "{_schema}".project_fact_streams
            WHERE project_id = 'legacy-project'
            """, verifyConnection);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(7L, reader.GetInt64(0));
        Assert.Equal(3L, reader.GetInt64(1));
    }

    [Fact]
    public async Task MigrationRejectsVersionGaps()
    {
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var setup = new NpgsqlCommand($"""
            CREATE TABLE "{_schema}".sessions_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            INSERT INTO "{_schema}".sessions_schema_migrations (version) VALUES (1), (3)
            """, connection))
            await setup.ExecuteNonQueryAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema));
        Assert.Equal("Unsupported Events & Sessions schema version.", error.Message);
    }

    private async Task ApplyMigrationAsync(string resourceName)
    {
        await using var resource = typeof(EventsAndSessionsMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
        using var reader = new StreamReader(resource);
        var sql = (await reader.ReadToEndAsync()).Replace("{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
