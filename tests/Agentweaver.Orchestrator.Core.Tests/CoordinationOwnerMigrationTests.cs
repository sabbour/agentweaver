using Agentweaver.Orchestrator;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed partial class CoordinationOwnerMigrationTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
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
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(29)]
    public async Task AdmittedSchemaAndNativeIntermediateVersionsUpgradeWithoutRewritingHistory(int version)
    {
        await ApplyMigrationsThroughAsync(version);
        var historyBefore = await ReadMigrationHistoryAsync(version);
        if (version < 29)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        Assert.Equal(historyBefore, await ReadMigrationHistoryAsync(version));
    }

    [Fact]
    public async Task FreshInitializationAndExactReplayInstallAllRegisteredResources()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        var history = await ReadMigrationHistoryAsync(int.MaxValue);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        Assert.Equal(history, await ReadMigrationHistoryAsync(int.MaxValue));
    }

    [Theory]
    [InlineData(19)]
    [InlineData(20)]
    public async Task ExistingSuspensionAndSeedSchemasInstallPreviouslyUnregisteredResources(int version)
    {
        await ApplyMigrationsThroughAsync(version, includePreviouslyUnregistered: false);
        var history = await ReadMigrationHistoryAsync(version, includePreviouslyUnregistered: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        Assert.Equal(history, await ReadMigrationHistoryAsync(version, includePreviouslyUnregistered: false));
        var completeHistory = await ReadMigrationHistoryAsync(int.MaxValue);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        Assert.Equal(completeHistory, await ReadMigrationHistoryAsync(int.MaxValue));
    }

    [Theory]
    [InlineData(17)]
    [InlineData(999)]
    public async Task RejectsUnregisteredSchemaVersion(int version)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (@version)
            """, connection))
        {
            command.Parameters.AddWithValue("version", version);
            await command.ExecuteNonQueryAsync();
        }

        var history = await ReadMigrationHistoryAsync(int.MaxValue);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        Assert.Equal(history, await ReadMigrationHistoryAsync(int.MaxValue));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public async Task RejectsMissingPrerequisitesWithoutRewritingHistory(int missingVersion)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            DELETE FROM "{_schema}".coordination_schema_migrations WHERE version = @version
            """, connection))
        {
            command.Parameters.AddWithValue("version", missingVersion);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var history = await ReadMigrationHistoryAsync(int.MaxValue);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        Assert.Equal(history, await ReadMigrationHistoryAsync(int.MaxValue));
    }

    [Theory]
    [InlineData("backlog_projects")]
    [InlineData("backlog_tasks")]
    [InlineData("backlog_dependencies")]
    [InlineData("reviewed_remote_tool_snapshots")]
    public async Task VerificationRequiresRegisteredTables(string table)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"DROP TABLE \"{_schema}\".{table} CASCADE", connection))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Theory]
    [InlineData("executable_action_grants", "source_control_intent_id")]
    [InlineData("maf_workflow_checkpoints", "workspace_seed")]
    public async Task VerificationRequiresColumnsWithoutDependingOnRuntimeTablePermissions(
        string table, string column)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE \"{_schema}\".{table} DROP COLUMN {column} CASCADE", connection))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationRequiresTheEnabledSnapshotImmutabilityTrigger(bool drop)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var sql = drop
            ? $"DROP TRIGGER reviewed_remote_tool_snapshots_immutable ON \"{_schema}\".reviewed_remote_tool_snapshots"
            : $"ALTER TABLE \"{_schema}\".reviewed_remote_tool_snapshots DISABLE TRIGGER reviewed_remote_tool_snapshots_immutable";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(sql, connection))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    private async Task ApplyMigrationsThroughAsync(int version, bool includePreviouslyUnregistered = true)
    {
        (int Version, string Filename)[] migrations =
        [
            (1, "001_coordination_owner.sql"),
            (2, "002_typed_decisions_and_checkpoints.sql"),
            (3, "003_accepted_run_selection_context.sql"),
            (4, "004_session_tree.sql"),
            (5, "005_explicit_session_forks.sql"),
            (6, "006_session_fork_admission.sql"),
            (7, "007_execution_outcomes_and_recovery.sql"),
            (8, "008_run_selection_context_versions.sql"),
            (9, "009_runtime_owner_context.sql"),
            (10, "runtime_registration.sql"),
            (11, "011_runtime_usage_source.sql"),
            (12, "012_source_control_owner.sql"),
            (13, "013_source_control_github_app_pins.sql"),
            (14, "014_source_control_output_captures.sql"),
            (15, "015_maf_execution_evidence.sql"),
            (16, "016_project_backlog.sql"),
            (18, "018_reviewed_remote_tool_snapshots.sql"),
            (19, "019_session_suspend_resume.sql"),
            (20, "020_maf_buildtest_workspace_seed.sql"),
            (21, "021_row_lock_record_updates.sql"),
            (22, "022_run_list_completion.sql"),
            (29, "029_spend_budget_exposure.sql")
        ];
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($"""
            CREATE TABLE "{_schema}".coordination_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """, connection))
            await setup.ExecuteNonQueryAsync();
        foreach (var migration in migrations.Where(item => item.Version <= version &&
            (includePreviouslyUnregistered || item.Version is not (16 or 18))))
        {
            var resourceName = $"Agentweaver.Orchestrator.Migrations.{migration.Filename}";
            await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
            using var reader = new StreamReader(resource);
            var sql = (await reader.ReadToEndAsync()).Replace("{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
            await using var apply = new NpgsqlCommand(sql, connection);
            await apply.ExecuteNonQueryAsync();
            await using var record = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (@version)
                """, connection);
            record.Parameters.AddWithValue("version", migration.Version);
            await record.ExecuteNonQueryAsync();
        }
    }

    private async Task AssertCurrentSchemaAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 19),
                to_regclass(@manifests) IS NOT NULL,
                (SELECT count(*) FROM information_schema.columns
                 WHERE table_schema = @schema AND table_name = 'coordination_execution_operations'
                  AND column_name IN (
                      'owner_execution_fence', 'reserved_manifest_id', 'operation_phase',
                      'phase_version', 'progress')),
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid = to_regclass(@operations) AND tgname = 'coordination_execution_operations_suspend_resume_phase'
                   AND NOT tgisinternal),
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid = to_regclass(@manifests) AND tgname IN (
                    'session_consistency_manifests_immutable', 'session_consistency_manifests_no_truncate')
                   AND NOT tgisinternal),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 20),
                (SELECT count(*) FROM information_schema.columns
                 WHERE table_schema = @schema AND table_name = 'maf_workflow_checkpoints'
                   AND column_name = 'workspace_seed' AND data_type = 'bytea' AND is_nullable = 'YES'),
                (SELECT pg_get_constraintdef(oid) FROM pg_constraint
                 WHERE conrelid = to_regclass(@checkpoints) AND conname = 'ck_maf_workspace_seed_bytes'
                   AND contype = 'c' AND convalidated),
                (SELECT pg_get_constraintdef(oid) FROM pg_constraint
                 WHERE conrelid = to_regclass(@checkpoints) AND conname = 'fk_maf_checkpoint_parent'
                   AND contype = 'f' AND convalidated AND confrelid = to_regclass(@checkpoints)
                   AND confdeltype = 'r'),
                (SELECT array_agg(version ORDER BY version)
                 FROM "{_schema}".coordination_schema_migrations),
                to_regclass(@backlogProjects) IS NOT NULL,
                to_regclass(@backlogTasks) IS NOT NULL,
                to_regclass(@backlogDependencies) IS NOT NULL,
                to_regclass(@snapshots) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid = to_regclass(@snapshots)
                   AND tgname = 'reviewed_remote_tool_snapshots_immutable'
                   AND tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal),
                (SELECT count(*) FROM information_schema.columns
                 WHERE table_schema = @schema AND table_name = 'accepted_runs'
                   AND column_name = 'completed_at' AND is_nullable = 'YES'),
                to_regclass(@runListIndex) IS NOT NULL,
                to_regclass(@runCompletedIndex) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid IN (to_regclass(@reservations), to_regclass(@cursors))
                   AND tgname IN ('spend_budget_reservations_guard', 'spend_budget_reservations_no_truncate',
                       'spend_budget_accounting_cursors_guard', 'spend_budget_accounting_cursors_no_truncate')
                   AND tgenabled = 'O' AND NOT tgisinternal)
            """, connection);
        command.Parameters.AddWithValue("runListIndex", $"{_schema}.ix_accepted_runs_project_created");
        command.Parameters.AddWithValue("runCompletedIndex", $"{_schema}.ix_accepted_runs_project_completed");
        command.Parameters.AddWithValue("schema", _schema);
        command.Parameters.AddWithValue("manifests", $"{_schema}.session_consistency_manifests");
        command.Parameters.AddWithValue("operations", $"{_schema}.coordination_execution_operations");
        command.Parameters.AddWithValue("checkpoints", $"{_schema}.maf_workflow_checkpoints");
        command.Parameters.AddWithValue("backlogProjects", $"{_schema}.backlog_projects");
        command.Parameters.AddWithValue("backlogTasks", $"{_schema}.backlog_tasks");
        command.Parameters.AddWithValue("backlogDependencies", $"{_schema}.backlog_dependencies");
        command.Parameters.AddWithValue("snapshots", $"{_schema}.reviewed_remote_tool_snapshots");
        command.Parameters.AddWithValue("reservations", $"{_schema}.spend_budget_reservations");
        command.Parameters.AddWithValue("cursors", $"{_schema}.spend_budget_accounting_cursors");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(5L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(2L, reader.GetInt64(4));
        Assert.Equal(1L, reader.GetInt64(5));
        Assert.Equal(1L, reader.GetInt64(6));
        var seedConstraint = reader.GetString(7);
        Assert.Contains("workspace_seed IS NULL", seedConstraint);
        Assert.Contains("octet_length(workspace_seed) >= 1", seedConstraint);
        Assert.Contains("octet_length(workspace_seed) <= 67188876", seedConstraint);
        var parentConstraint = reader.GetString(8);
        Assert.StartsWith(
            "FOREIGN KEY (project_id, run_id, session_id, store_name, parent_checkpoint_id)",
            parentConstraint);
        Assert.Contains(
            "maf_workflow_checkpoints(project_id, run_id, session_id, store_name, checkpoint_id)",
            parentConstraint);
        Assert.EndsWith("ON DELETE RESTRICT", parentConstraint);
        Assert.Equal(Enumerable.Range(1, 16).Concat([18, 19, 20, 21, 22, 29]), reader.GetFieldValue<int[]>(9));
        Assert.True(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
        Assert.True(reader.GetBoolean(12));
        Assert.True(reader.GetBoolean(13));
        Assert.Equal(1L, reader.GetInt64(14));
        Assert.Equal(1L, reader.GetInt64(15));
        Assert.True(reader.GetBoolean(16));
        Assert.True(reader.GetBoolean(17));
        Assert.Equal(4L, reader.GetInt64(18));
    }

    private async Task<string> ReadMigrationHistoryAsync(int version, bool includePreviouslyUnregistered = true)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT jsonb_agg(to_jsonb(m) ORDER BY version)::text
            FROM "{_schema}".coordination_schema_migrations m WHERE version <= @version
                AND (@includePreviouslyUnregistered OR version NOT IN (16, 18))
            """, connection);
        command.Parameters.AddWithValue("version", version);
        command.Parameters.AddWithValue("includePreviouslyUnregistered", includePreviouslyUnregistered);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
