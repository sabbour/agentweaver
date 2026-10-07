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
    public async Task FreshSchemaAppliesAddressedMessagesProjectFactsExplicitForksAndUsage()
    {
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);

        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".sessions_schema_migrations
            """, connection);
        Assert.Equal(7, Convert.ToInt32(await command.ExecuteScalarAsync()));
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
        Assert.Equal(7L, reader.GetInt64(1));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task AdmittedSchemaUpgradesWithoutChangingProjectFactsMessagesOrMigrationHistory(int version)
    {
        await ApplyMigrationAsync("Agentweaver.EventsAndSessions.Migrations.001_sessions_journal.sql");
        await ApplyMigrationAsync("Agentweaver.EventsAndSessions.Migrations.002_addressed_messages.sql");
        await ApplyMigrationAsync("Agentweaver.EventsAndSessions.Migrations.003_project_facts.sql");
        if (version == 4)
            await ApplyMigrationAsync("Agentweaver.EventsAndSessions.Migrations.004_explicit_session_forks.sql");
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".sessions_schema_migrations (version) VALUES (1), (2), (3);
            INSERT INTO "{_schema}".project_fact_streams (project_id, last_position)
            VALUES ('current-project', 9)
            """, connection))
            await seed.ExecuteNonQueryAsync();
        if (version == 4)
        {
            await using var connection = await _fixture.DataSource.OpenConnectionAsync();
            await using var seed = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".sessions_schema_migrations (version) VALUES (4)
                """, connection);
            await seed.ExecuteNonQueryAsync();
        }
        var historyBefore = await ReadMigrationHistoryAsync(version);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema));
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.MigrateAsync(_fixture.DataSource, _schema);
        await EventsAndSessionsMigrator.VerifyAsync(_fixture.DataSource, _schema);
        Assert.Equal(historyBefore, await ReadMigrationHistoryAsync(version));

        await using var verifyConnection = await _fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT
                last_position,
                (SELECT count(*) FROM "{_schema}".sessions_schema_migrations),
                to_regclass(@messages) IS NOT NULL,
                to_regclass(@usage) IS NOT NULL,
                to_regclass(@rates) IS NOT NULL
            FROM "{_schema}".project_fact_streams
            WHERE project_id = 'current-project'
            """, verifyConnection);
        verify.Parameters.AddWithValue("messages", $"{_schema}.addressed_messages");
        verify.Parameters.AddWithValue("usage", $"{_schema}.usage_ledger");
        verify.Parameters.AddWithValue("rates", $"{_schema}.usage_rate_cards");
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(9L, reader.GetInt64(0));
        Assert.Equal(7L, reader.GetInt64(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task MigrationRejectsVersionGaps(int lastVersion)
    {
        await using (var connection = await _fixture.DataSource.OpenConnectionAsync())
        await using (var setup = new NpgsqlCommand($"""
            CREATE TABLE "{_schema}".sessions_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            INSERT INTO "{_schema}".sessions_schema_migrations (version) VALUES (1), ({lastVersion})
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

    private async Task<string> ReadMigrationHistoryAsync(int version)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text
            FROM "{_schema}".sessions_schema_migrations m WHERE version <= @version
            """, connection);
        command.Parameters.AddWithValue("version", version);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
