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
    /// Every version <see cref="MigrateAsync"/> can apply, in order; 017 is intentionally absent.
    /// </summary>
    /// <remarks>Operator tooling compares this catalog with its reviewed plan before applying anything.</remarks>
    public static IReadOnlyList<int> SupportedVersions { get; } =
        // Version 035 adds queue ordering; 036 and 037 use the row-lock trigger introduced in 021.
        // Version 038 adds the Source Control pull-request operation ledger.
        [.. Enumerable.Range(1, 16).Concat([18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 35, 36, 37, 38])];

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

        // Fail on any version this build doesn't know, or on a version whose prerequisite is missing.
        // Optional owner features remain compatible with their complete pre-migration shapes
        // until the reviewed maintenance plan applies them.
        if (applied.Any(version => version is not (>= 1 and <= 16) and not (18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 35 or 36 or 37 or 38)) ||
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
            (applied.Contains(22) && !applied.Contains(21)) ||
            (applied.Contains(23) && !applied.Contains(22)) ||
            (applied.Contains(24) && !applied.Contains(23)) ||
            (applied.Contains(25) && !applied.Contains(2)) ||
            (applied.Contains(26) && !applied.Contains(21)) ||
            (applied.Contains(27) && !applied.Contains(26)) ||
            // The Guardrails evidence table builds on the latest schema that ships before it.
            (applied.Contains(28) && !applied.Contains(27)) ||
            // Spend budget exposure follows the same rule and is ordered directly after 028.
            (applied.Contains(29) && !applied.Contains(28)) ||
            // Promotion and origin tables reference backlog tasks with the content columns from 026.
            // The migrator still applies 030 after 029 because it follows catalog order.
            (applied.Contains(30) && !applied.Contains(26)) ||
            // Promotion dismissals reference both caller dismissals from 025 and promotions from 030.
            (applied.Contains(31) && (!applied.Contains(30) || !applied.Contains(25))) ||
            // 032 records reference repository pins, which migration 012 adds.
            (applied.Contains(32) && !applied.Contains(12)) ||
            // Repository identity extends the private links from 024, not optional PR recording.
            (applied.Contains(33) && !applied.Contains(24)) ||
            // Queue positions extend project backlog tasks from 016.
            (applied.Contains(35) && !applied.Contains(16)) ||
            // Root model-turn admission uses the immutable row-lock guard from 021.
            (applied.Contains(36) && !applied.Contains(21)) ||
            (applied.Contains(37) && !applied.Contains(21)) ||
            (applied.Contains(38) && !applied.Contains(12)))
            throw new InvalidOperationException("Unsupported Orchestrator coordination schema version.");
        // Apply every catalog version that is not yet recorded, in catalog order. Operator tooling relies on
        // this: it can only cap what runs by checking that the catalog itself ends at the approved version.
        foreach (var version in SupportedVersions.Where(version => !applied.Contains(version)))
        {
            // Map each version to its migration name. The embedded file name is built from it below;
            // version 10 is the only file without a numeric prefix.
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
                22 => "run_list_completion",
                23 => "project_run_requests",
                24 => "chief_of_staff",
                25 => "user_notification_dismissals",
                26 => "backlog_task_content",
                27 => "periodic_issue_sync",
                28 => "guardrails_evaluations",
                29 => "spend_budget_exposure",
                30 => "backlog_task_origin",
                31 => "user_notification_promotion_dismissals",
                // 032 adds stored pull-request records before the independent Priority-link update in 033.
                32 => "source_control_pull_request_records",
                33 => "priority_link_repository_identity",
                35 => "backlog_task_queue_position",
                36 => "maf_root_model_turn_admissions",
                37 => "maf_child_spawn_authorization",
                38 => "source_control_pull_request_operations",
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
        // Read every check as one row of columns. Migration counts come first; then the existence of
        // tables, columns, constraints, and triggers. Three columns cover version 029: its migration
        // record, the spend_budget_reservations table, and the spend_budget_accounting_cursors table.
        // Six columns cover version 030: its migration record, its two tables, and its three unique indexes.
        // Two columns cover version 031: its migration record and the promotion dismissal table.
        // Additive migrations 032, 036, 037, and 038 remain optional to readiness so the owner can start before
        // maintenance applies the reviewed plan; accept only their complete table-and-ledger shapes.
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
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version NOT IN (1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 35, 36, 37, 38)),
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
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 22) = 1,
                EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass(@runs)
                    AND attname = 'completed_at' AND attnum > 0 AND NOT attisdropped),
                to_regclass(@runListIndex) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 23) = 1,
                to_regclass(@projectRunRequests) IS NOT NULL,
                to_regclass(@projectRunCancellations) IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = to_regclass(@runs)
                    AND attname = 'cancelled_at' AND attnum > 0 AND NOT attisdropped),
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 24) = 1,
                to_regclass(@chiefOfStaffConversations) IS NOT NULL,
                to_regclass(@chiefOfStaffPriorities) IS NOT NULL,
                to_regclass(@chiefOfStaffPriorityLinks) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 25) = 1,
                to_regclass(@userDismissals) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 26) = 1,
                (SELECT count(*) FROM pg_attribute
                    WHERE attrelid = to_regclass(@backlogTasks) AND NOT attisdropped
                      AND attname IN ('title', 'description', 'lifecycle', 'issue_provider_id',
                          'issue_repository_owner', 'issue_repository_name', 'issue_number')) = 7,
                to_regclass(@backlogIssueUnique) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 27) = 1,
                to_regclass(@issueSyncProjects) IS NOT NULL,
                to_regclass(@issueSyncReceipts) IS NOT NULL,
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 28) = 1,
                to_regclass(@guardrailsEvaluations) IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_trigger
                    WHERE tgrelid = to_regclass(@guardrailsEvaluations)
                        AND tgname = 'guardrails_evaluations_immutable'
                        AND tgfoid = to_regprocedure(@guardrailsMutationFunction)
                        AND tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal),
                -- Version 029 checks: the migration is recorded once and both spend budget tables exist.
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 29) = 1,
                to_regclass(@spendBudgetReservations) IS NOT NULL,
                to_regclass(@spendBudgetCursors) IS NOT NULL,
                -- Version 030 checks: the migration is recorded once, both tables exist, and so do the
                -- unique indexes that enforce one promotion per plan, one key per project, and one task per item.
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 30) = 1,
                to_regclass(@backlogPlanPromotions) IS NOT NULL,
                to_regclass(@backlogTaskOrigins) IS NOT NULL,
                to_regclass(@backlogPromotionPlanUnique) IS NOT NULL,
                to_regclass(@backlogPromotionIdempotencyUnique) IS NOT NULL,
                to_regclass(@backlogOriginItemUnique) IS NOT NULL,
                -- Version 031 checks: the migration is recorded once and the promotion dismissal table exists.
                (SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 31) = 1,
                to_regclass(@userPromotionDismissals) IS NOT NULL,
                -- Both schema shapes are supported during rollout. A partially applied 033 is not.
                (((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 33) = 0
                    AND (SELECT count(*) FROM pg_attribute
                        WHERE attrelid = to_regclass(@chiefOfStaffPriorityLinks) AND NOT attisdropped
                          AND attname IN ('provider_id', 'repository_owner', 'repository_name')) = 0
                    AND to_regclass(@priorityOldUnique) IS NOT NULL
                    AND to_regclass(@priorityRepositoryUnique) IS NULL AND to_regclass(@priorityLegacyUnique) IS NULL)
                 OR ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 33) = 1
                    AND (SELECT count(*) FROM pg_attribute
                        WHERE attrelid = to_regclass(@chiefOfStaffPriorityLinks) AND NOT attisdropped
                          AND attname IN ('provider_id', 'repository_owner', 'repository_name')) = 3
                    AND to_regclass(@priorityRepositoryUnique) IS NOT NULL
                    AND to_regclass(@priorityLegacyUnique) IS NOT NULL
                    AND to_regclass(@priorityOldUnique) IS NULL
                    AND EXISTS (SELECT 1 FROM pg_constraint WHERE conrelid = to_regclass(@chiefOfStaffPriorityLinks)
                        AND conname = 'ck_chief_of_staff_priority_links_repository_identity' AND convalidated))),
                -- 032 is optional at startup: the PR list reports it as not available until it is applied.
                -- Accept "not applied" (no ledger row, no table) or "fully applied" (row, table, and
                -- index). Anything in between is a partial apply and fails verification.
                ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 32) = 0
                    AND to_regclass(@sourceControlPullRequests) IS NULL) OR
                ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 32) = 1
                    AND to_regclass(@sourceControlPullRequests) IS NOT NULL
                    AND to_regclass(@sourceControlPullRequestsIndex) IS NOT NULL),
                -- 036 is additive; a ready applied table has every admission column and its immutability trigger.
                ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 36) = 0
                    AND to_regclass(@rootModelTurnAdmissions) IS NULL) OR
                ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 36) = 1
                    AND to_regclass(@rootModelTurnAdmissions) IS NOT NULL
                    AND (SELECT count(*) FROM pg_attribute
                        WHERE attrelid = to_regclass(@rootModelTurnAdmissions) AND NOT attisdropped
                          AND attname IN ('project_id', 'run_id', 'root_session_id', 'request_id',
                              'actor_issuer', 'actor_id', 'tenant_id', 'accepted_selection_hash',
                              'prompt_hash', 'execution_fence', 'decision_id', 'decision_state_version',
                              'created_at')) = 13
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@rootModelTurnAdmissions) AND contype = 'p'
                          AND pg_get_constraintdef(oid) = 'PRIMARY KEY (project_id, run_id, request_id)')
                    AND EXISTS (SELECT 1 FROM pg_trigger
                        WHERE tgrelid = to_regclass(@rootModelTurnAdmissions)
                          AND tgname = 'maf_root_model_turn_admissions_no_update'
                          AND tgfoid = to_regprocedure(@updateGuard) AND tgtype = 27
                          AND tgenabled = 'O' AND NOT tgisinternal
                          AND tgattr = ''::int2vector AND tgqual IS NULL AND tgnargs = 0)),
                -- Queue positions are optional during rollout. Reject a ledger/column mismatch.
                (((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 35) = 0
                    AND NOT EXISTS (SELECT 1 FROM pg_attribute
                        WHERE attrelid = to_regclass(@backlogTasks) AND attname = 'queue_position'
                          AND attnum > 0 AND NOT attisdropped)
                    AND to_regclass(@backlogPositionIndex) IS NULL)
                 OR ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 35) = 1
                    AND EXISTS (SELECT 1 FROM pg_attribute
                        WHERE attrelid = to_regclass(@backlogTasks) AND attname = 'queue_position'
                          AND atttypid = 'bigint'::regtype AND attnotnull
                          AND attnum > 0 AND NOT attisdropped)
                    AND to_regclass(@backlogPositionIndex) IS NOT NULL)),
                -- 037 is additive; accept an absent ledger/table pair or the complete pair of immutable evidence tables.
                (((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 37) = 0
                    AND to_regclass(@childSpawnAuthorizations) IS NULL
                    AND to_regclass(@childSpawnConsumptions) IS NULL)
                 OR ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 37) = 1
                    AND to_regclass(@childSpawnAuthorizations) IS NOT NULL
                    AND to_regclass(@childSpawnConsumptions) IS NOT NULL
                    AND (SELECT count(*) FROM pg_attribute
                        WHERE attrelid = to_regclass(@childSpawnAuthorizations) AND NOT attisdropped
                          AND attname IN ('project_id', 'run_id', 'spawn_intent_id', 'intent_hash',
                              'parent_session_id', 'root_session_id', 'actor_issuer', 'actor_id', 'tenant_id',
                              'accepted_selection_hash', 'context_revision', 'project_revision',
                              'project_configuration_revision', 'platform_runtime_revision', 'membership_revision',
                              'role_revision', 'execution_fence', 'plan_id', 'plan_revision', 'workflow_step_id',
                              'work_item_id', 'grant_id', 'grant_revision', 'policy_receipt_id',
                              'policy_provider_id', 'policy_adapter_version', 'policy_options_schema_version',
                              'policy_options_revision', 'policy_outcome', 'policy_reason_code', 'created_at')) = 31
                    AND (SELECT count(*) FROM pg_attribute
                        WHERE attrelid = to_regclass(@childSpawnConsumptions) AND NOT attisdropped
                          AND attname IN ('project_id', 'run_id', 'spawn_intent_id', 'intent_hash', 'consumed_at')) = 5
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@childSpawnAuthorizations) AND contype = 'p'
                          AND pg_get_constraintdef(oid) = 'PRIMARY KEY (project_id, run_id, spawn_intent_id)')
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@childSpawnConsumptions) AND contype = 'p'
                          AND pg_get_constraintdef(oid) = 'PRIMARY KEY (project_id, run_id, spawn_intent_id)')
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@childSpawnAuthorizations)
                          AND conname = 'uq_maf_child_spawn_authorization_receipt_hash'
                          AND contype = 'u' AND convalidated)
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@childSpawnAuthorizations)
                          AND conname = 'fk_maf_child_spawn_authorization_policy_receipt'
                          AND contype = 'f' AND convalidated)
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@childSpawnConsumptions)
                          AND conname = 'fk_maf_child_spawn_consumption_receipt'
                          AND contype = 'f' AND convalidated)
                    AND (SELECT count(*) FROM pg_trigger
                        WHERE tgrelid IN (to_regclass(@childSpawnAuthorizations),
                            to_regclass(@childSpawnConsumptions))
                          AND tgname IN ('maf_child_spawn_authorizations_no_update',
                              'maf_child_spawn_consumptions_no_update')
                          AND tgfoid = to_regprocedure(@updateGuard) AND tgtype = 27
                          AND tgenabled = 'O' AND NOT tgisinternal
                          AND tgattr = ''::int2vector AND tgqual IS NULL AND tgnargs = 0) = 2))
                ,
                -- 038 is additive; a persisted PR-operation ledger must be either fully absent or complete.
                (((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 38) = 0
                    AND to_regclass(@sourceControlPullRequestOperations) IS NULL)
                 OR ((SELECT count(*) FROM {quotedSchema}.coordination_schema_migrations WHERE version = 38) = 1
                    AND to_regclass(@sourceControlPullRequestOperations) IS NOT NULL
                    AND (SELECT count(*) FROM pg_attribute
                        WHERE attrelid = to_regclass(@sourceControlPullRequestOperations) AND NOT attisdropped
                          AND attname IN ('project_id', 'run_id', 'session_id', 'operation_id', 'pin_id',
                              'request_fingerprint', 'pull_request_number', 'created_at', 'completed_at')) = 9
                    AND EXISTS (SELECT 1 FROM pg_constraint
                        WHERE conrelid = to_regclass(@sourceControlPullRequestOperations) AND contype = 'p'
                          AND pg_get_constraintdef(oid) =
                              'PRIMARY KEY (project_id, run_id, session_id, operation_id)')))
            """, connection);
        command.Parameters.AddWithValue(
            "runListIndex", NpgsqlDbType.Text, $"{schema}.ix_accepted_runs_project_created");
        command.Parameters.AddWithValue(
            "chiefOfStaffConversations", NpgsqlDbType.Text, $"{schema}.chief_of_staff_conversations");
        command.Parameters.AddWithValue(
            "chiefOfStaffPriorities", NpgsqlDbType.Text, $"{schema}.chief_of_staff_priorities");
        command.Parameters.AddWithValue(
            "chiefOfStaffPriorityLinks", NpgsqlDbType.Text, $"{schema}.chief_of_staff_priority_links");
        command.Parameters.AddWithValue("priorityRepositoryUnique", NpgsqlDbType.Text,
            $"{schema}.uq_chief_of_staff_priority_links_repository_target");
        command.Parameters.AddWithValue("priorityLegacyUnique", NpgsqlDbType.Text,
            $"{schema}.uq_chief_of_staff_priority_links_legacy_target");
        command.Parameters.AddWithValue("priorityOldUnique", NpgsqlDbType.Text,
            $"{schema}.uq_chief_of_staff_priority_links_target");
        command.Parameters.AddWithValue("projectRunRequests", NpgsqlDbType.Text, $"{schema}.project_run_requests");
        command.Parameters.AddWithValue(
            "projectRunCancellations", NpgsqlDbType.Text, $"{schema}.project_run_cancellations");
        command.Parameters.AddWithValue("runs", NpgsqlDbType.Text, $"{schema}.accepted_runs");
        command.Parameters.AddWithValue("userDismissals", NpgsqlDbType.Text, $"{schema}.user_notification_dismissals");
        // Bind the relation name as data; schema validation alone does not prove the table exists.
        command.Parameters.AddWithValue("userPromotionDismissals", NpgsqlDbType.Text, $"{schema}.user_notification_promotion_dismissals");
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
        command.Parameters.AddWithValue(
            "backlogPositionIndex", NpgsqlDbType.Text, $"{schema}.ix_backlog_tasks_project_state_position");
        command.Parameters.AddWithValue("issueSyncProjects", NpgsqlDbType.Text, $"{schema}.issue_sync_projects");
        command.Parameters.AddWithValue("issueSyncReceipts", NpgsqlDbType.Text, $"{schema}.issue_sync_receipts");
        command.Parameters.AddWithValue("sourceControlPullRequests", NpgsqlDbType.Text,
            $"{schema}.source_control_pull_requests");
        command.Parameters.AddWithValue("sourceControlPullRequestsIndex", NpgsqlDbType.Text,
            $"{schema}.ix_source_control_pull_requests_project_recorded");
        command.Parameters.AddWithValue("rootModelTurnAdmissions", NpgsqlDbType.Text,
            $"{schema}.maf_root_model_turn_admissions");
        command.Parameters.AddWithValue("childSpawnAuthorizations", NpgsqlDbType.Text,
            $"{schema}.maf_child_spawn_authorization_receipts");
        command.Parameters.AddWithValue("childSpawnConsumptions", NpgsqlDbType.Text,
            $"{schema}.maf_child_spawn_authorization_consumptions");
        command.Parameters.AddWithValue("sourceControlPullRequestOperations", NpgsqlDbType.Text,
            $"{schema}.source_control_pull_request_operations");
        command.Parameters.AddWithValue("backlogIssueUnique", NpgsqlDbType.Text,
            $"{schema}.backlog_tasks_repository_issue_unique");
        command.Parameters.AddWithValue("backlogDependencies", NpgsqlDbType.Text, $"{schema}.backlog_dependencies");
        command.Parameters.AddWithValue(
            "backlogPlanPromotions", NpgsqlDbType.Text, $"{schema}.backlog_plan_promotions");
        command.Parameters.AddWithValue("backlogTaskOrigins", NpgsqlDbType.Text, $"{schema}.backlog_task_origins");
        command.Parameters.AddWithValue(
            "backlogPromotionPlanUnique", NpgsqlDbType.Text, $"{schema}.uq_backlog_plan_promotions_plan");
        command.Parameters.AddWithValue(
            "backlogPromotionIdempotencyUnique", NpgsqlDbType.Text, $"{schema}.uq_backlog_plan_promotions_idempotency");
        command.Parameters.AddWithValue(
            "backlogOriginItemUnique", NpgsqlDbType.Text, $"{schema}.uq_backlog_task_origins_item");
        command.Parameters.AddWithValue(
            "reviewedRemoteToolSnapshots", NpgsqlDbType.Text, $"{schema}.reviewed_remote_tool_snapshots");
        command.Parameters.AddWithValue(
            "snapshotMutationFunction", NpgsqlDbType.Text, $"{schema}.reject_reviewed_remote_tool_snapshot_mutation()");
        // Table names for the version 029 existence checks.
        command.Parameters.AddWithValue(
            "spendBudgetReservations", NpgsqlDbType.Text, $"{schema}.spend_budget_reservations");
        command.Parameters.AddWithValue(
            "spendBudgetCursors", NpgsqlDbType.Text, $"{schema}.spend_budget_accounting_cursors");
        // Version 028 checks: the migration is recorded once, the evidence table exists, and its immutability
        // trigger is installed, enabled, and calls the reject function. Trigger type 27 means
        // BEFORE UPDATE OR DELETE, FOR EACH ROW, so no evidence row can be changed or removed.
        command.Parameters.AddWithValue(
            "guardrailsEvaluations", NpgsqlDbType.Text, $"{schema}.guardrails_evaluations");
        command.Parameters.AddWithValue(
            "guardrailsMutationFunction", NpgsqlDbType.Text, $"{schema}.reject_guardrails_evaluation_mutation()");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        // Each required version must be recorded exactly once, no unknown versions may exist, and every
        // existence check must be true. Adding a check to the query also requires extending the final range.
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
            Enumerable.Range(50, 4).Any(column => !reader.GetBoolean(column)) ||
            !reader.GetBoolean(53) || !reader.GetBoolean(54) ||
            !reader.GetBoolean(55) || !reader.GetBoolean(56) ||
            // Columns 57 onward are boolean checks. The three version 028 checks, the three version 029
            // checks, the six version 030 checks, and the two version 031 checks precede the
            // optional-033, optional-032, optional-036, optional-035, optional-037, and optional-038 shape checks.
            // A partial apply never counts as ready.
            Enumerable.Range(57, 49).Any(column => !reader.GetBoolean(column)))
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
