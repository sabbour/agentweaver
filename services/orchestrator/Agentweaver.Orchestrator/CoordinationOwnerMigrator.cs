using System.Text.RegularExpressions;
using Agentweaver.Persistence.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Applies and verifies the Orchestrator coordination schema migrations in an explicit operation.
/// </summary>
public static class CoordinationOwnerMigrator
{
    private static readonly Regex SchemaPattern = new(
        "^[a-z][a-z0-9_]{0,62}\\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// Applies the supported embedded migrations in order under a schema-scoped PostgreSQL lock.
    /// </summary>
    /// <remarks>The caller explicitly invokes this method; ordinary service startup uses <see cref="VerifyAsync"/> instead.</remarks>
    /// <exception cref="InvalidOperationException">The migration history is unsupported or a required embedded migration is missing.</exception>
    public static async Task MigrateAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        await new PostgresOutbox(dataSource, schema).InitializeAsync(cancellationToken);

        var quotedSchema = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var advisoryLock = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('agentweaver.coordination.migrate'), hashtext(@schema))",
            connection, transaction))
        {
            advisoryLock.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
            await advisoryLock.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var createSchema = new NpgsqlCommand(
            $"CREATE SCHEMA IF NOT EXISTS {quotedSchema}", connection, transaction))
            await createSchema.ExecuteNonQueryAsync(cancellationToken);
        await using (var createVersions = new NpgsqlCommand($"""
            CREATE TABLE IF NOT EXISTS {quotedSchema}.coordination_schema_migrations (
                version integer PRIMARY KEY,
                applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
            )
            """, connection, transaction))
            await createVersions.ExecuteNonQueryAsync(cancellationToken);

        var applied = new List<int>();
        await using (var query = new NpgsqlCommand(
            $"SELECT version FROM {quotedSchema}.coordination_schema_migrations", connection, transaction))
        await using (var reader = await query.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                applied.Add(reader.GetInt32(0));

        if (applied.Any(version => version is not (>= 1 and <= 16) and not (18 or 19 or 20 or 21 or 25 or 26)) ||
            (applied.Contains(2) && !applied.Contains(1)) ||
            (applied.Contains(3) && !applied.Contains(2)) ||
            (applied.Contains(4) && !applied.Contains(3)) ||
            (applied.Contains(5) && !applied.Contains(4)) ||
            (applied.Contains(6) && !applied.Contains(5)) ||
            (applied.Contains(7) && !applied.Contains(6)) ||
            (applied.Contains(8) && !applied.Contains(7)) ||
            (applied.Contains(9) && !applied.Contains(8)) ||
            (applied.Contains(10) && !applied.Contains(9)) ||
            (applied.Contains(11) && !applied.Contains(10)) ||
            (applied.Contains(12) && !applied.Contains(11)) ||
            (applied.Contains(13) && !applied.Contains(12)) ||
            (applied.Contains(14) && !applied.Contains(13)) ||
            (applied.Contains(15) && !applied.Contains(14)) ||
            (applied.Contains(16) && !applied.Contains(15)) ||
            (applied.Contains(18) && !applied.Contains(15)) ||
            (applied.Contains(19) && !applied.Contains(15)) ||
            (applied.Contains(20) && !applied.Contains(19)) ||
            (applied.Contains(21) && !applied.Contains(20)) ||
            (applied.Contains(25) && !applied.Contains(2)) ||
            (applied.Contains(26) && !applied.Contains(21)))
            throw new InvalidOperationException("Unsupported Orchestrator coordination schema version.");
        foreach (var version in Enumerable.Range(1, 16).Concat([18, 19, 20, 21, 25, 26])
            .Where(version => !applied.Contains(version)))
        {
            var migrationName = version switch
            {
                1 => "coordination_owner",
                2 => "typed_decisions_and_checkpoints",
                3 => "accepted_run_selection_context",
                4 => "session_tree",
                5 => "explicit_session_forks",
                6 => "session_fork_admission",
                7 => "execution_outcomes_and_recovery",
                8 => "run_selection_context_versions",
                9 => "runtime_owner_context",
                10 => "runtime_registration",
                11 => "runtime_usage_source",
                12 => "source_control_owner",
                13 => "source_control_github_app_pins",
                14 => "source_control_output_captures",
                15 => "maf_execution_evidence",
                16 => "project_backlog",
                18 => "reviewed_remote_tool_snapshots",
                19 => "session_suspend_resume",
                20 => "maf_buildtest_workspace_seed",
                21 => "row_lock_record_updates",
                25 => "user_notification_dismissals",
                26 => "backlog_task_content",
                _ => throw new InvalidOperationException("Unsupported Orchestrator coordination schema version.")
            };
            var filename = version == 10 ? $"{migrationName}.sql" : $"{version:000}_{migrationName}.sql";
            await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(
                $"Agentweaver.Orchestrator.Migrations.{filename}")
                ?? throw new InvalidOperationException("The coordination owner migration resource is missing.");
            using var text = new StreamReader(resource);
            var sql = (await text.ReadToEndAsync(cancellationToken))
                .Replace("{schema}", quotedSchema, StringComparison.Ordinal);
            await using (var migration = new NpgsqlCommand(sql, connection, transaction))
                await migration.ExecuteNonQueryAsync(cancellationToken);
            await using (var record = new NpgsqlCommand(
                $"INSERT INTO {quotedSchema}.coordination_schema_migrations (version) VALUES ({version})",
                connection, transaction))
                await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Checks that the recorded migration versions, tables, columns, and immutability guards are current.
    /// </summary>
    /// <exception cref="InvalidOperationException">The schema is missing an expected migration or database object.</exception>
    public static async Task VerifyAsync(
        NpgsqlDataSource dataSource,
        string schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ValidateSchema(schema);
        var quotedSchema = Quote(schema);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 1),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 2),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 3),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 4),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 5),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 6),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 7),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 8),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 9),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 10),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 11),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 12),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 13),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 14),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 15),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 19),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version NOT IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 25, 26)),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version = 1),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version = 2),
                (SELECT count(*) FROM {quotedSchema}.outbox_schema_migrations WHERE version NOT IN (1, 2)),
                to_regclass(@runs) IS NOT NULL,
                to_regclass(@sessions) IS NOT NULL,
                to_regclass(@messages) IS NOT NULL,
                to_regclass(@requests) IS NOT NULL,
                to_regclass(@notifications) IS NOT NULL,
                to_regclass(@outbox) IS NOT NULL,
                to_regclass(@inbox) IS NOT NULL,
                to_regclass(@decisions) IS NOT NULL,
                to_regclass(@decisionOutbox) IS NOT NULL,
                to_regclass(@gates) IS NOT NULL,
                to_regclass(@grants) IS NOT NULL,
                to_regclass(@receipts) IS NOT NULL,
                to_regclass(@checkpoints) IS NOT NULL,
                to_regclass(@selectionContexts) IS NOT NULL,
                to_regclass(@treeCommands) IS NOT NULL,
                to_regclass(@idleSubscriptions) IS NOT NULL,
                to_regclass(@idleNotifications) IS NOT NULL,
                to_regclass(@forkTargetReservation) IS NOT NULL,
                to_regclass(@executionOperations) IS NOT NULL,
                to_regclass(@selectionContextVersions) IS NOT NULL,
                to_regclass(@sourceControlPins) IS NOT NULL,
                to_regclass(@sourceControlMergeIntents) IS NOT NULL,
                to_regclass(@sourceControlWebhookDeliveries) IS NOT NULL,
                to_regclass(@sourceControlOutputCaptures) IS NOT NULL,
                EXISTS (
                    SELECT 1 FROM pg_attribute
                    WHERE attrelid = to_regclass(@grants)
                      AND attname = 'source_control_intent_id'
                      AND attnum > 0 AND NOT attisdropped
                ),
                EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass(@sessions)
                    AND attname = 'work_plan_item_id' AND attnum > 0 AND NOT attisdropped),
                to_regclass(@runtimeHeads) IS NOT NULL,
                to_regclass(@runtimeRevisions) IS NOT NULL,
                to_regclass(@sdkSources) IS NOT NULL,
                to_regclass(@usageObservations) IS NOT NULL,
                to_regclass(@mafExecutionRunGuards) IS NOT NULL,
                to_regclass(@mafExecutionDispatches) IS NOT NULL,
                to_regclass(@mafExecutionOutputWitnesses) IS NOT NULL,
                to_regclass(@sessionConsistencyManifests) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 20) = 1,
                EXISTS (SELECT 1 FROM pg_attribute
                    WHERE attrelid = to_regclass(@checkpoints)
                        AND attname = 'workspace_seed' AND atttypid = 'bytea'::regtype
                        AND attnum > 0 AND NOT attisdropped),
                EXISTS (SELECT 1 FROM pg_constraint
                    WHERE conrelid = to_regclass(@checkpoints)
                        AND conname = 'fk_maf_checkpoint_parent' AND contype = 'f'),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 16) = 1,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 18) = 1,
                to_regclass(@backlogProjects) IS NOT NULL,
                to_regclass(@backlogTasks) IS NOT NULL,
                to_regclass(@backlogDependencies) IS NOT NULL,
                to_regclass(@reviewedRemoteToolSnapshots) IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid = to_regclass(@reviewedRemoteToolSnapshots)
                        AND tgname = 'reviewed_remote_tool_snapshots_immutable'
                        AND tgfoid = to_regprocedure(@snapshotMutationFunction)
                        AND tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 21) = 1,
                EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgrelid = to_regclass(@decisions) AND tgname = 'coordinator_decisions_no_update'
                      AND tgfoid = to_regprocedure(@updateGuard) AND tgtype = 19
                      AND tgenabled = 'O' AND NOT tgisinternal
                      AND tgattr = ''::int2vector AND tgqual IS NULL AND tgnargs = 0),
                EXISTS (SELECT 1 FROM pg_catalog.pg_trigger
                    WHERE tgrelid = to_regclass(@checkpoints) AND tgname = 'maf_workflow_checkpoints_no_update'
                      AND tgfoid = to_regprocedure(@updateGuard) AND tgtype = 19
                      AND tgenabled = 'O' AND NOT tgisinternal
                      AND tgattr = ''::int2vector AND tgqual IS NULL AND tgnargs = 0),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 25) = 1,
                to_regclass(@userDismissals) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 26) = 1,
                (SELECT count(*) FROM pg_attribute
                    WHERE attrelid = to_regclass(@backlogTasks) AND NOT attisdropped
                      AND attname IN ('title', 'description', 'lifecycle', 'issue_provider_id',
                          'issue_repository_owner', 'issue_repository_name', 'issue_number')) = 7,
                to_regclass(@backlogIssueUnique) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("runs", NpgsqlDbType.Text, $"{schema}.accepted_runs");
        command.Parameters.AddWithValue("userDismissals", NpgsqlDbType.Text, $"{schema}.user_notification_dismissals");
        command.Parameters.AddWithValue("sessions", NpgsqlDbType.Text, $"{schema}.coordination_sessions");
        command.Parameters.AddWithValue("messages", NpgsqlDbType.Text, $"{schema}.coordination_messages");
        command.Parameters.AddWithValue("requests", NpgsqlDbType.Text, $"{schema}.coordination_requests");
        command.Parameters.AddWithValue("notifications", NpgsqlDbType.Text, $"{schema}.parent_notifications");
        command.Parameters.AddWithValue("outbox", NpgsqlDbType.Text, $"{schema}.outbox_events");
        command.Parameters.AddWithValue("inbox", NpgsqlDbType.Text, $"{schema}.consumer_inbox_receipts");
        command.Parameters.AddWithValue("decisions", NpgsqlDbType.Text, $"{schema}.coordinator_decisions");
        command.Parameters.AddWithValue(
            "decisionOutbox", NpgsqlDbType.Text, $"{schema}.coordinator_decision_outbox");
        command.Parameters.AddWithValue("gates", NpgsqlDbType.Text, $"{schema}.coordinator_gates");
        command.Parameters.AddWithValue("grants", NpgsqlDbType.Text, $"{schema}.executable_action_grants");
        command.Parameters.AddWithValue("receipts", NpgsqlDbType.Text, $"{schema}.policy_evaluation_receipts");
        command.Parameters.AddWithValue("checkpoints", NpgsqlDbType.Text, $"{schema}.maf_workflow_checkpoints");
        command.Parameters.AddWithValue(
            "selectionContexts", NpgsqlDbType.Text, $"{schema}.coordinator_run_selection_contexts");
        command.Parameters.AddWithValue("runtimeHeads", NpgsqlDbType.Text, $"{schema}.runtime_registration_heads");
        command.Parameters.AddWithValue("runtimeRevisions", NpgsqlDbType.Text, $"{schema}.runtime_registration_revisions");
        command.Parameters.AddWithValue("sdkSources", NpgsqlDbType.Text, $"{schema}.runtime_sdk_sources");
        command.Parameters.AddWithValue("usageObservations", NpgsqlDbType.Text, $"{schema}.runtime_usage_observations");
        command.Parameters.AddWithValue(
            "mafExecutionRunGuards", NpgsqlDbType.Text, $"{schema}.maf_execution_run_guards");
        command.Parameters.AddWithValue(
            "mafExecutionDispatches", NpgsqlDbType.Text, $"{schema}.maf_execution_dispatches");
        command.Parameters.AddWithValue(
            "mafExecutionOutputWitnesses", NpgsqlDbType.Text, $"{schema}.maf_execution_output_witnesses");
        command.Parameters.AddWithValue(
            "sessionConsistencyManifests", NpgsqlDbType.Text, $"{schema}.session_consistency_manifests");
        command.Parameters.AddWithValue("updateGuard", NpgsqlDbType.Text, $"{schema}.reject_row_lock_record_update()");
        command.Parameters.AddWithValue(
            "treeCommands", NpgsqlDbType.Text, $"{schema}.coordination_tree_commands");
        command.Parameters.AddWithValue(
            "idleSubscriptions", NpgsqlDbType.Text, $"{schema}.coordination_idle_subscriptions");
        command.Parameters.AddWithValue(
            "idleNotifications", NpgsqlDbType.Text, $"{schema}.coordination_idle_notifications");
        command.Parameters.AddWithValue(
            "forkTargetReservation", NpgsqlDbType.Text, $"{schema}.uq_coordination_tree_commands_fork_target_reservation");
        command.Parameters.AddWithValue(
            "executionOperations", NpgsqlDbType.Text, $"{schema}.coordination_execution_operations");
        command.Parameters.AddWithValue(
            "selectionContextVersions", NpgsqlDbType.Text, $"{schema}.coordinator_run_selection_context_versions");
        command.Parameters.AddWithValue("sourceControlPins", NpgsqlDbType.Text, $"{schema}.source_control_repository_pins");
        command.Parameters.AddWithValue(
            "sourceControlMergeIntents", NpgsqlDbType.Text, $"{schema}.source_control_merge_intents");
        command.Parameters.AddWithValue(
            "sourceControlWebhookDeliveries", NpgsqlDbType.Text, $"{schema}.source_control_webhook_deliveries");
        command.Parameters.AddWithValue(
            "sourceControlOutputCaptures", NpgsqlDbType.Text, $"{schema}.source_control_output_captures");
        command.Parameters.AddWithValue("backlogProjects", NpgsqlDbType.Text, $"{schema}.backlog_projects");
        command.Parameters.AddWithValue("backlogTasks", NpgsqlDbType.Text, $"{schema}.backlog_tasks");
        command.Parameters.AddWithValue("backlogIssueUnique", NpgsqlDbType.Text,
            $"{schema}.backlog_tasks_repository_issue_unique");
        command.Parameters.AddWithValue("backlogDependencies", NpgsqlDbType.Text, $"{schema}.backlog_dependencies");
        command.Parameters.AddWithValue(
            "reviewedRemoteToolSnapshots", NpgsqlDbType.Text, $"{schema}.reviewed_remote_tool_snapshots");
        command.Parameters.AddWithValue(
            "snapshotMutationFunction", NpgsqlDbType.Text, $"{schema}.reject_reviewed_remote_tool_snapshot_mutation()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) ||
            reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1 || reader.GetInt64(2) != 1 ||
            reader.GetInt64(3) != 1 || reader.GetInt64(4) != 1 || reader.GetInt64(5) != 1 ||
            reader.GetInt64(6) != 1 || reader.GetInt64(7) != 1 ||
            reader.GetInt64(8) != 1 || reader.GetInt64(9) != 1 ||
            reader.GetInt64(10) != 1 || reader.GetInt64(11) != 1 ||
            reader.GetInt64(12) != 1 || reader.GetInt64(13) != 1 ||
            reader.GetInt64(14) != 1 || reader.GetInt64(15) != 1 ||
            reader.GetInt64(16) != 0 ||
            reader.GetInt64(17) != 1 || reader.GetInt64(18) != 1 ||
            reader.GetInt64(19) != 0 ||
            Enumerable.Range(20, 25).Any(column => !reader.GetBoolean(column)) ||
            Enumerable.Range(45, 5).Any(column => !reader.GetBoolean(column)) ||
            Enumerable.Range(50, 3).Any(column => !reader.GetBoolean(column)) ||
            !reader.GetBoolean(53) || !reader.GetBoolean(54) ||
            !reader.GetBoolean(55) || !reader.GetBoolean(56) ||
            Enumerable.Range(57, 15).Any(column => !reader.GetBoolean(column)))
            throw new InvalidOperationException(
                "Orchestrator coordination schema is not current; run the explicit --migrate command.");
    }

    /// <summary>
    /// Rejects unsafe or reserved PostgreSQL schema identifiers before they are interpolated into SQL.
    /// </summary>
    private static void ValidateSchema(string schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        if (!SchemaPattern.IsMatch(schema) || schema is "public" or "pg_catalog" or "information_schema" ||
            schema.StartsWith("pg_", StringComparison.Ordinal))
            throw new ArgumentException("A non-reserved lowercase schema identifier is required.", nameof(schema));
    }

    /// <summary>
    /// Quotes a schema name after it has passed the strict identifier validation.
    /// </summary>
    private static string Quote(string schema) => $"\"{schema}\"";
}
