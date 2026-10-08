using Agentweaver.Orchestrator;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class CoordinationOwnerMigrationTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "coordination_upgrade_" + Guid.NewGuid().ToString("N");

    public async Task InitializeAsync() =>
        await new PostgresOutbox(fixture.DataSource, _schema).InitializeAsync();

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task AdmittedSchemaAndNativeIntermediateVersionsUpgradeWithoutRewritingHistory(int version)
    {
        string[] migrations =
        [
            "001_coordination_owner.sql",
            "002_typed_decisions_and_checkpoints.sql",
            "003_accepted_run_selection_context.sql",
            "004_session_tree.sql",
            "005_explicit_session_forks.sql",
            "006_session_fork_admission.sql",
            "007_execution_outcomes_and_recovery.sql",
            "008_run_selection_context_versions.sql",
            "009_runtime_owner_context.sql",
            "runtime_registration.sql",
            "011_runtime_usage_source.sql",
            "012_source_control_owner.sql",
            "013_source_control_github_app_pins.sql"
        ];
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using (var setup = new NpgsqlCommand($"""
                CREATE TABLE "{_schema}".coordination_schema_migrations (
                    version integer PRIMARY KEY,
                    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
                )
                """, connection))
                await setup.ExecuteNonQueryAsync();
            for (var index = 0; index < version; index++)
            {
                var resourceName = $"Agentweaver.Orchestrator.Migrations.{migrations[index]}";
                await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
                using var reader = new StreamReader(resource);
                var sql = (await reader.ReadToEndAsync()).Replace("{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
                await using var migration = new NpgsqlCommand(sql, connection);
                await migration.ExecuteNonQueryAsync();
                await using var record = new NpgsqlCommand($"""
                    INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (@version)
                    """, connection);
                record.Parameters.AddWithValue("version", index + 1);
                await record.ExecuteNonQueryAsync();
            }
        }
        var historyBefore = await ReadMigrationHistoryAsync(version);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        Assert.Equal(historyBefore, await ReadMigrationHistoryAsync(version));
        await using var verifyConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var verify = new NpgsqlCommand($"""
            SELECT count(*) FROM "{_schema}".coordination_schema_migrations
            """, verifyConnection);
        Assert.Equal(13L, await verify.ExecuteScalarAsync());
    }

    private async Task<string> ReadMigrationHistoryAsync(int version)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text
            FROM "{_schema}".coordination_schema_migrations m WHERE version <= @version
            """, connection);
        command.Parameters.AddWithValue("version", version);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
