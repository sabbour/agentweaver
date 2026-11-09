using Agentweaver.Identity;
using Agentweaver.Orchestrator;
using Agentweaver.Persistence.Postgres;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    // Version 29 adds the spend budget reservation and accounting cursor tables.
    [InlineData(29)]
    // Version 30 adds the plan promotion and task origin tables.
    [InlineData(30)]
    // Version 31 adds the caller dismissal table for promotion notices.
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(34)]
    [InlineData(35)]
    [InlineData(36)]
    [InlineData(37)]
    [InlineData(38)]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    [InlineData(45)]
    public async Task AdmittedSchemaAndNativeIntermediateVersionsUpgradeWithoutRewritingHistory(int version)
    {
        // A partial history must fail verification until every registered resource is installed.
        await ApplyMigrationsThroughAsync(version);
        var historyBefore = await ReadMigrationHistoryAsync(version);
        // Both complete baseline schemas and the forward repository-identity extension are readable.
        if (version < 31)
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        // The reviewed catalog applies PR records before the independent Priority-link update.
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
        // Replay must leave the original migration receipts untouched.
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

    [Fact]
    public async Task RootAdmissionMigrationIsOptionalBeforeItIsApplied()
    {
        await ApplyMigrationsThroughAsync(33);

        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 36),
                to_regclass(@table) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("table", $"{_schema}.maf_root_model_turn_admissions");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task ChildSpawnAuthorizationMigrationIsOptionalBeforeItIsApplied()
    {
        await ApplyMigrationsThroughAsync(36);

        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 37),
                to_regclass(@receipts) IS NOT NULL,
                to_regclass(@consumptions) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("receipts", $"{_schema}.maf_child_spawn_authorization_receipts");
        command.Parameters.AddWithValue("consumptions", $"{_schema}.maf_child_spawn_authorization_consumptions");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
    }

    [Fact]
    public async Task PullRequestOperationMigrationIsOptionalBeforeItIsApplied()
    {
        await ApplyMigrationsThroughAsync(37);

        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 38),
                to_regclass(@table) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("table", $"{_schema}.source_control_pull_request_operations");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task RuntimeExecutionEvidenceMigrationIsOptionalBeforeItIsApplied()
    {
        await ApplyMigrationsThroughAsync(38);

        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 39),
                to_regclass(@table) IS NOT NULL
            """, connection);
        command.Parameters.AddWithValue("table", $"{_schema}.maf_runtime_evidence_receipts");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.False(reader.GetBoolean(1));
    }

    [Fact]
    public async Task RuntimeNumericNoSendMigrationPreservesLegacyReceiptsAndRequiresExactAllowlist()
    {
        await ApplyMigrationsThroughAsync(40);
        const string projectId = "numeric-no-send-project";
        const string runId = "numeric-no-send-run";
        var originals = new[]
        {
            ("numeric-limit-denial", Guid.NewGuid(), new string('a', 64)),
            ("replacement-comparison", Guid.NewGuid(), new string('b', 64))
        };
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using (var acceptedRun = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".accepted_runs
                    (project_id, run_id, accepted_selection, accepted_selection_hash,
                     accepted_by_issuer, accepted_by_subject)
                VALUES (@project, @run, jsonb_build_object(), repeat('a', 64), 'issuer', 'actor')
                """, connection))
            {
                acceptedRun.Parameters.AddWithValue("project", projectId);
                acceptedRun.Parameters.AddWithValue("run", runId);
                await acceptedRun.ExecuteNonQueryAsync();
            }

            foreach (var (kind, attemptId, payloadHash) in originals)
                await InsertEvidenceAsync(connection, kind, attemptId, payloadHash);
        }

        var legacyRows = await ReadEvidenceRowsAsync();
        var attemptIdForPending = Guid.NewGuid();
        var denialReceiptId = Guid.NewGuid();
        var cursorHash = new string('c', 64);
        var pendingEvidence = RuntimeNumericLimitNativeNoSendReceipt.Seal(new(
            1,
            RuntimeNumericLimitNativeNoSendIdentity.ReceiptId(
                projectId, runId, attemptIdForPending, denialReceiptId),
            projectId, runId, "root", "root", Guid.NewGuid(), 1, 1,
            attemptIdForPending, attemptIdForPending, new string('d', 64),
            denialReceiptId, new string('e', 64), new string('f', 64),
            new string('1', 64), Guid.NewGuid(), 1, new string('2', 64),
            cursorHash, cursorHash, 0, true, DateTimeOffset.UtcNow, string.Empty));
        var ledger = new RuntimeExecutionEvidenceLedgerStore(_schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            var pending = await Assert.ThrowsAsync<CoordinationException>(() =>
                ledger.AppendNumericLimitNativeNoSendAsync(
                    connection, transaction, pendingEvidence, CancellationToken.None));
            Assert.Equal("database_update_pending", pending.Code);
            Assert.Equal(StatusCodes.Status503ServiceUnavailable, pending.StatusCode);
        }

        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        Assert.Equal(legacyRows, await ReadEvidenceRowsAsync());
        var migratedHistory = await ReadMigrationHistoryAsync(int.MaxValue);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        Assert.Equal(migratedHistory, await ReadMigrationHistoryAsync(int.MaxValue));
        Assert.Equal(legacyRows, await ReadEvidenceRowsAsync());

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var weaken = new NpgsqlCommand($"""
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                DROP CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check;
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                ADD CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check CHECK (
                    receipt_kind IN ('numeric-limit-denial', 'replacement-comparison'));
            """, connection))
            await weaken.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var restore = new NpgsqlCommand($"""
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                DROP CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check;
            ALTER TABLE "{_schema}".maf_runtime_evidence_receipts
                ADD CONSTRAINT maf_runtime_evidence_receipts_receipt_kind_check CHECK (
                    receipt_kind IN (
                        'numeric-limit-denial',
                        'replacement-comparison',
                        'numeric-limit-native-no-send',
                        'native-root-turn-completion',
                        'native-root-turn-replay'));
            """, connection))
            await restore.ExecuteNonQueryAsync();
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);

        var companionAttempt = Guid.NewGuid();
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await InsertEvidenceAsync(connection, "numeric-limit-native-no-send",
                companionAttempt, new string('c', 64));
            await InsertEvidenceAsync(connection, "native-root-turn-completion",
                Guid.NewGuid(), new string('d', 64));
            var unknownKind = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertEvidenceAsync(connection, "unexpected-evidence-kind",
                    Guid.NewGuid(), new string('d', 64)));
            Assert.Equal(PostgresErrorCodes.CheckViolation, unknownKind.SqlState);
        }
        Assert.Equal(4, (await ReadEvidenceRowsAsync()).Length);
        await AssertAppendOnlyAsync($"""
            UPDATE "{_schema}".maf_runtime_evidence_receipts
            SET payload_hash = repeat('e', 64)
            WHERE receipt_kind = 'numeric-limit-native-no-send'
            """);
        await AssertAppendOnlyAsync($"""
            DELETE FROM "{_schema}".maf_runtime_evidence_receipts
            WHERE receipt_kind = 'numeric-limit-native-no-send'
            """);
        await AssertAppendOnlyAsync($"TRUNCATE \"{_schema}\".maf_runtime_evidence_receipts");

        async Task InsertEvidenceAsync(
            NpgsqlConnection connection, string kind, Guid attempt, string payloadHash)
        {
            await using var insert = new NpgsqlCommand($"""
                INSERT INTO "{_schema}".maf_runtime_evidence_receipts
                    (project_id, run_id, receipt_id, receipt_kind, attempt_id, payload_hash, receipt_json)
                VALUES (@project, @run, @receipt, @kind, @attempt, @hash,
                    jsonb_build_object('projectId', @project, 'runId', @run,
                        'receiptId', @receipt::text, 'canonicalPayloadHash', @hash))
                """, connection);
            insert.Parameters.AddWithValue("project", projectId);
            insert.Parameters.AddWithValue("run", runId);
            insert.Parameters.AddWithValue("receipt", Guid.NewGuid());
            insert.Parameters.AddWithValue("kind", kind);
            insert.Parameters.AddWithValue("attempt", attempt);
            insert.Parameters.AddWithValue("hash", payloadHash);
            await insert.ExecuteNonQueryAsync();
        }

        async Task<string[]> ReadEvidenceRowsAsync()
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand($"""
                SELECT receipt_kind || '|' || attempt_id::text || '|' ||
                    payload_hash::text || '|' || receipt_json::text
                FROM "{_schema}".maf_runtime_evidence_receipts
                WHERE project_id = @project AND run_id = @run
                ORDER BY receipt_kind, attempt_id
                """, connection);
            read.Parameters.AddWithValue("project", projectId);
            read.Parameters.AddWithValue("run", runId);
            var rows = new List<string>();
            await using var reader = await read.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(reader.GetString(0));
            return rows.ToArray();
        }

        async Task AssertAppendOnlyAsync(string sql)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Contains("append-only", error.MessageText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task P1InvocationProvenanceMigrationIsOptionalBeforeItIsApplied()
    {
        await ApplyMigrationsThroughAsync(39);

        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 40),
                to_regclass(@provenance) IS NOT NULL,
                to_regclass(@bindings) IS NOT NULL,
                EXISTS (SELECT 1 FROM pg_attribute
                    WHERE attrelid = to_regclass(@requests) AND attname = 'p1_setup_invocation_id'
                      AND attnum > 0 AND NOT attisdropped)
            """, connection);
        command.Parameters.AddWithValue("provenance", $"{_schema}.p1_run_request_provenance");
        command.Parameters.AddWithValue("bindings", $"{_schema}.p1_invocation_bindings");
        command.Parameters.AddWithValue("requests", $"{_schema}.project_run_requests");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
    }

    [Fact]
    public async Task NonP1InteractiveRequestsRemainUsableBeforeP1ProvenanceMigration()
    {
        await ApplyMigrationsThroughAsync(39);
        var store = new CoordinationOwnerStore(fixture.DataSource, _schema, TimeProvider.System);
        var actor = new CoordinationActor("https://identity.example/", "legacy-actor");
        var request = new CreateProjectRunRequest("Continue with the existing flow", 1);
        var created = await store.CreateProjectRunAsync(
            actor, "tenant-1", "project-1", "legacy-request", request, CancellationToken.None);
        var replay = await store.CreateProjectRunAsync(
            actor, "tenant-1", "project-1", "legacy-request", request, CancellationToken.None);

        Assert.Equal(created, replay);
        Assert.Null(created.P1SetupInvocationId);
        var started = await store.StartProjectRunAsync(
            actor, "tenant-1", "project-1", created.RunId,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            CancellationToken.None);
        Assert.Equal("started", started.State);
    }

    [Fact]
    public async Task RootAdmissionMigrationUsesItsActualPrerequisiteWithoutReservedSiblingVersions()
    {
        await ApplyMigrationsThroughAsync(21);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var resourceName = "Agentweaver.Orchestrator.Migrations.036_maf_root_model_turn_admissions.sql";
        await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
        using var reader = new StreamReader(resource);
        var sql = (await reader.ReadToEndAsync()).Replace("{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using (var apply = new NpgsqlCommand(sql, connection))
            await apply.ExecuteNonQueryAsync();
        await using (var record = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (36)
            """, connection))
            await record.ExecuteNonQueryAsync();

        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
    }

    [Fact]
    public async Task ChildSpawnAuthorizationMigrationUsesItsActualPrerequisiteWithoutReservedSiblingVersions()
    {
        await ApplyMigrationsThroughAsync(21);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var resourceName = "Agentweaver.Orchestrator.Migrations.037_maf_child_spawn_authorization.sql";
        await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
        using var reader = new StreamReader(resource);
        var sql = (await reader.ReadToEndAsync()).Replace("{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using (var apply = new NpgsqlCommand(sql, connection))
            await apply.ExecuteNonQueryAsync();
        await using (var record = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (37)
            """, connection))
            await record.ExecuteNonQueryAsync();

        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
    }

    [Fact]
    public async Task PullRequestOperationMigrationUsesItsActualPrerequisiteWithoutReservedSiblingVersions()
    {
        await ApplyMigrationsThroughAsync(12);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var resourceName = "Agentweaver.Orchestrator.Migrations.038_source_control_pull_request_operations.sql";
        await using var resource = typeof(CoordinationOwnerMigrator).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource '{resourceName}'.");
        using var reader = new StreamReader(resource);
        var sql = (await reader.ReadToEndAsync()).Replace("{schema}", $"\"{_schema}\"", StringComparison.Ordinal);
        await using (var apply = new NpgsqlCommand(sql, connection))
            await apply.ExecuteNonQueryAsync();
        await using (var record = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (38)
            """, connection))
            await record.ExecuteNonQueryAsync();

        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await AssertCurrentSchemaAsync();
    }

    [Fact]
    public async Task UnknownSchemaVersionLogsTheReadinessFailureReasonAtWarningLevel()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var insert = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".coordination_schema_migrations (version) VALUES (34)
            """, connection))
            await insert.ExecuteNonQueryAsync();

        // Version 34 is reserved and outside the set accepted by the readiness verifier.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
        var sink = new ReadinessCapturingLogger();

        Assert.True(new ReadinessFailureLogger(sink).TryLog(failure));

        var entry = Assert.Single(sink.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(nameof(InvalidOperationException), entry.Message);
        Assert.Contains(failure.Message, entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task RepositoryIdentityUpgradeAppliesReviewed032AndPreservesLegacyRows()
    {
        await ApplyMigrationsThroughAsync(31);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand($"""
            INSERT INTO "{_schema}".chief_of_staff_priority_lists (tenant_id, owner_issuer, owner_subject)
                VALUES ('tenant', 'issuer', 'subject');
            INSERT INTO "{_schema}".chief_of_staff_priorities
                (priority_id, tenant_id, owner_issuer, owner_subject, outcome, position, create_key_hash, create_fingerprint)
                VALUES ('priority', 'tenant', 'issuer', 'subject', 'Ship', 0, repeat('a', 64), repeat('b', 64));
            INSERT INTO "{_schema}".chief_of_staff_priority_links
                (priority_id, link_id, kind, project_id, item_number, created_at)
                VALUES ('priority', 'legacy', 'issue', 'project', 42, '2026-10-20T00:00:00Z');
            """, connection))
            await seed.ExecuteNonQueryAsync();

        // The new app remains ready before the schema update, but must not discard new link identity.
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        var store = new ChiefOfStaffStore(fixture.DataSource, _schema);
        var actor = new CoordinationActor("issuer", "subject");
        var read = await store.GetPriorityAsync(actor, "tenant", "priority", default);
        Assert.Single(read!.Links);
        var blocked = await Assert.ThrowsAsync<CoordinationException>(() => store.AddLinkAsync(actor, "tenant", "priority",
            new(read.Revision, "issue", "project", null, null, 42, null, "github", new("octo", "api")), default));
        Assert.Equal("database_update_pending", blocked.Code);
        Assert.Equal(503, blocked.StatusCode);
        // Existing editing and non-repository links still work on 031.
        var changed = await store.UpdatePriorityAsync(actor, "tenant", "priority", new(read.Revision, "Ship safely", null, null, null), default);
        var withRun = await store.AddLinkAsync(actor, "tenant", "priority",
            new(changed.Revision, "run", "project", "missing-run", null, null, null), default);
        await store.RemoveLinkAsync(actor, "tenant", "priority",
            Assert.Single(withRun.Links, link => link.Kind == "run").LinkId, withRun.Revision, default);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var check = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 33),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 32),
                (SELECT count(*) FROM "{_schema}".chief_of_staff_priority_links
                    WHERE link_id = 'legacy' AND item_number = 42 AND provider_id IS NULL
                      AND repository_owner IS NULL AND repository_name IS NULL
                      AND created_at = '2026-10-20T00:00:00Z'),
                -- A pre-033 owner rejects repository identity and later Core migrations.
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations
                    WHERE version NOT IN (1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,43,44,45)),
                to_regclass(@obsolete) IS NULL
            """, connection);
        check.Parameters.AddWithValue("obsolete", $"{_schema}.uq_chief_of_staff_priority_links_target");
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        // Old owner images reject the later versions; deploy them before applying these migrations.
        Assert.Equal(9L, reader.GetInt64(3));
        Assert.True(reader.GetBoolean(4));
    }

    [Fact]
    public async Task RepositoryIdentityPreflightPreservesConflictingHistoryAndRollsBack()
    {
        await ApplyMigrationsThroughAsync(31);
        var store = new ChiefOfStaffStore(fixture.DataSource, _schema);
        var (priority, _) = await store.CreatePriorityAsync(new("issuer", "subject"), "tenant", "preflight",
            new("Ship", null, null), default);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        // Only this disposable fixture removes the old guard to simulate corrupted pre-upgrade data.
        await using (var seed = new NpgsqlCommand($"""
            DROP INDEX "{_schema}".uq_chief_of_staff_priority_links_target;
            INSERT INTO "{_schema}".chief_of_staff_priority_links
                (priority_id, link_id, kind, project_id, item_number)
            VALUES ('{priority.PriorityId}', 'legacy-a', 'issue', 'project', 42),
                   ('{priority.PriorityId}', 'legacy-b', 'issue', 'project', 42);
            """, connection))
            await seed.ExecuteNonQueryAsync();
        var failure = await Assert.ThrowsAsync<PostgresException>(() =>
            CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema));
        Assert.Contains("Priority links violate repository identity uniqueness", failure.MessageText);
        // The migration transaction leaves both original rows intact and records no successful update.
        await using var check = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{_schema}".chief_of_staff_priority_links),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 33),
                (SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = @schema AND table_name = 'chief_of_staff_priority_links'
                      AND column_name IN ('provider_id', 'repository_owner', 'repository_name'))
            """, connection);
        check.Parameters.AddWithValue("schema", _schema);
        await using var reader = await check.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(0L, reader.GetInt64(1));
        Assert.Equal(0L, reader.GetInt64(2));
    }

    [Theory]
    [InlineData("column")]
    [InlineData("constraint")]
    [InlineData("receipt")]
    [InlineData("table")]
    public async Task RuntimeRunStartReadinessRejectsPartialInstallation(string missing)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var damage = new NpgsqlCommand(missing switch
        {
            "column" => $"ALTER TABLE \"{_schema}\".runtime_run_start_intents DROP COLUMN failure_code",
            "constraint" => $"ALTER TABLE \"{_schema}\".runtime_run_start_intents DROP CONSTRAINT runtime_run_start_intents_pkey",
            "receipt" => $"DELETE FROM \"{_schema}\".coordination_schema_migrations WHERE version = 42",
            _ => $"DROP TABLE \"{_schema}\".runtime_run_start_intents"
        }, connection);
        await damage.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Fact]
    public async Task RuntimeRunStartReadinessAcceptsPre042AndInstalled042WithoutTablePrivileges()
    {
        await ApplyMigrationsThroughAsync(41);
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        var role = "readiness_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var grants = new NpgsqlCommand($"""
                CREATE ROLE "{role}" NOLOGIN;
                GRANT USAGE ON SCHEMA "{_schema}" TO "{role}";
                GRANT SELECT ON ALL TABLES IN SCHEMA "{_schema}" TO "{role}";
                REVOKE ALL ON "{_schema}".runtime_run_start_intents FROM "{role}";
                """, connection))
                await grants.ExecuteNonQueryAsync();
            var restrictedConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Options = $"-c role={role}"
            };
            await using var restricted = NpgsqlDataSource.Create(restrictedConnection.ConnectionString);
            await CoordinationOwnerMigrator.VerifyAsync(restricted, _schema);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand(
                $"DROP OWNED BY \"{role}\"; DROP ROLE \"{role}\"", connection);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepositoryIdentityReadinessRejectsPartialSchema(bool missingIndex)
    {
        await ApplyMigrationsThroughAsync(31);
        if (missingIndex)
            await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        // A receipt without its index, or a column without its receipt, is never a supported shape.
        await using var damage = new NpgsqlCommand(missingIndex
            ? $"DROP INDEX \"{_schema}\".uq_chief_of_staff_priority_links_repository_target"
            : $"ALTER TABLE \"{_schema}\".chief_of_staff_priority_links ADD COLUMN provider_id varchar(256)", connection);
        await damage.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
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
    [InlineData(2)]
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
    [InlineData("user_notification_dismissals")]
    [InlineData("guardrails_evaluations")]
    [InlineData("backlog_plan_promotions")]
    [InlineData("backlog_task_origins")]
    [InlineData("user_notification_promotion_dismissals")]
    [InlineData("source_control_pull_requests")]
    [InlineData("maf_root_model_turn_admissions")]
    [InlineData("maf_child_spawn_authorization_receipts")]
    [InlineData("maf_child_spawn_authorization_consumptions")]
    [InlineData("source_control_pull_request_operations")]
    public async Task VerificationRequiresRegisteredTables(string table)
    {
        // A version receipt alone is insufficient when its required table is missing.
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerificationRequiresTheEnabledRootAdmissionImmutabilityTrigger(bool drop)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var sql = drop
            ? $"DROP TRIGGER maf_root_model_turn_admissions_no_update ON \"{_schema}\".maf_root_model_turn_admissions"
            : $"ALTER TABLE \"{_schema}\".maf_root_model_turn_admissions DISABLE TRIGGER maf_root_model_turn_admissions_no_update";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(sql, connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Theory]
    [InlineData("maf_child_spawn_authorization_receipts", "maf_child_spawn_authorizations_no_update", false)]
    [InlineData("maf_child_spawn_authorization_receipts", "maf_child_spawn_authorizations_no_update", true)]
    [InlineData("maf_child_spawn_authorization_consumptions", "maf_child_spawn_consumptions_no_update", false)]
    [InlineData("maf_child_spawn_authorization_consumptions", "maf_child_spawn_consumptions_no_update", true)]
    public async Task VerificationRequiresEnabledChildSpawnEvidenceImmutabilityTriggers(
        string table,
        string trigger,
        bool drop)
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        var operation = drop
            ? $"DROP TRIGGER {trigger} ON \"{_schema}\".{table}"
            : $"ALTER TABLE \"{_schema}\".{table} DISABLE TRIGGER {trigger}";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(operation, connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Fact]
    public async Task VerificationRequiresTheRootAdmissionActorColumns()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE \"{_schema}\".maf_root_model_turn_admissions DROP COLUMN actor_issuer", connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Fact]
    public async Task VerificationRequiresTheRootAdmissionRequestPrimaryKey()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            $"ALTER TABLE \"{_schema}\".maf_root_model_turn_admissions DROP CONSTRAINT maf_root_model_turn_admissions_pkey",
            connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    [Fact]
    public async Task VerificationRejectsAConditionalRootAdmissionImmutabilityTrigger()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            DROP TRIGGER maf_root_model_turn_admissions_no_update
                ON "{_schema}".maf_root_model_turn_admissions;
            CREATE TRIGGER maf_root_model_turn_admissions_no_update
                BEFORE UPDATE OR DELETE ON "{_schema}".maf_root_model_turn_admissions
                FOR EACH ROW WHEN (OLD.request_id <> '00000000-0000-0000-0000-000000000001'::uuid)
                EXECUTE FUNCTION "{_schema}".reject_row_lock_record_update()
            """, connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema));
    }

    private async Task ApplyMigrationsThroughAsync(int version, bool includePreviouslyUnregistered = true)
    {
        // Reconstruct real intermediate schemas from embedded SQL, including the intentional version gaps.
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
            (23, "023_project_run_requests.sql"),
            (24, "024_chief_of_staff.sql"),
            (25, "025_user_notification_dismissals.sql"),
            (26, "026_backlog_task_content.sql"),
            (27, "027_periodic_issue_sync.sql"),
            (28, "028_guardrails_evaluations.sql"),
            // The spend budget migration follows 028.
            (29, "029_spend_budget_exposure.sql"),
            (30, "030_backlog_task_origin.sql"),
            (31, "031_user_notification_promotion_dismissals.sql"),
            (32, "032_source_control_pull_request_records.sql"),
            (33, "033_priority_link_repository_identity.sql"),
            (35, "035_backlog_task_queue_position.sql"),
            (36, "036_maf_root_model_turn_admissions.sql"),
            (37, "037_maf_child_spawn_authorization.sql"),
            (38, "038_source_control_pull_request_operations.sql"),
            (39, "039_runtime_execution_evidence.sql"),
            (40, "040_p1_run_request_provenance.sql"),
            (41, "041_runtime_numeric_native_no_send.sql"),
            (42, "042_runtime_run_start.sql"),
            (43, "043_runtime_root_turn_completion.sql"),
            (44, "044_runtime_replacement_operations.sql"),
            (45, "045_runtime_root_turn_replay.sql")
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
                (SELECT count(*) FROM information_schema.columns
                 WHERE table_schema = @schema AND table_name = 'accepted_runs'
                   AND column_name = 'cancelled_at' AND is_nullable = 'YES'),
                to_regclass(@projectRunRequests) IS NOT NULL,
                to_regclass(@projectRunCancellations) IS NOT NULL,
                (SELECT pg_get_constraintdef(oid) FROM pg_constraint
                 WHERE conrelid = to_regclass(@runs) AND conname = 'accepted_runs_execution_state_check'),
                to_regclass(@cosConversations) IS NOT NULL,
                to_regclass(@cosPriorities) IS NOT NULL,
                to_regclass(@cosLinks) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid IN (to_regclass(@reservations), to_regclass(@cursors))
                   AND tgname IN ('spend_budget_reservations_guard', 'spend_budget_reservations_no_truncate',
                       'spend_budget_accounting_cursors_guard', 'spend_budget_accounting_cursors_no_truncate')
                   AND tgenabled = 'O' AND NOT tgisinternal),
                to_regclass(@rootModelTurnAdmissions) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid = to_regclass(@rootModelTurnAdmissions)
                   AND tgname = 'maf_root_model_turn_admissions_no_update'
                   AND tgenabled = 'O' AND NOT tgisinternal),
                to_regclass(@childSpawnAuthorizations) IS NOT NULL,
                to_regclass(@childSpawnConsumptions) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid IN (to_regclass(@childSpawnAuthorizations), to_regclass(@childSpawnConsumptions))
                   AND tgname IN ('maf_child_spawn_authorizations_no_update', 'maf_child_spawn_consumptions_no_update')
                   AND tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal),
                to_regclass(@runtimeEvidenceReceipts) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid = to_regclass(@runtimeEvidenceReceipts)
                   AND tgname = 'maf_runtime_evidence_receipts_immutable'
                   AND tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal),
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid = to_regclass(@runtimeEvidenceReceipts)
                   AND tgname = 'maf_runtime_evidence_receipts_no_truncate'
                   AND tgtype = 34
                   AND tgenabled = 'O' AND NOT tgisinternal),
                to_regclass(@p1RunRequestProvenance) IS NOT NULL,
                to_regclass(@p1InvocationBindings) IS NOT NULL,
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid IN (to_regclass(@p1RunRequestProvenance), to_regclass(@p1InvocationBindings))
                   AND tgname IN ('p1_run_request_provenance_immutable', 'p1_invocation_bindings_immutable')
                   AND tgtype = 27 AND tgenabled = 'O' AND NOT tgisinternal),
                (SELECT count(*) FROM pg_trigger
                 WHERE tgrelid IN (to_regclass(@p1RunRequestProvenance), to_regclass(@p1InvocationBindings))
                   AND tgname IN ('p1_run_request_provenance_no_truncate', 'p1_invocation_bindings_no_truncate')
                   AND tgtype = 34 AND tgenabled = 'O' AND NOT tgisinternal),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 40),
                EXISTS (SELECT 1 FROM pg_attribute
                    WHERE attrelid = to_regclass(@projectRunRequests) AND attname = 'p1_setup_invocation_id'
                      AND atttypid = 'uuid'::regtype AND attnum > 0 AND NOT attisdropped),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 41),
                (SELECT pg_get_constraintdef(oid) FROM pg_constraint
                 WHERE conrelid = to_regclass(@runtimeEvidenceReceipts)
                   AND conname = 'maf_runtime_evidence_receipts_receipt_kind_check'),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 42),
                to_regclass(@runtimeRunStartIntents) IS NOT NULL,
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 43),
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 44),
                to_regclass(@runtimeReplacementOperations) IS NOT NULL,
                to_regclass(@runtimeReplacementActiveIndex) IS NOT NULL,
                (SELECT count(*) FROM "{_schema}".coordination_schema_migrations WHERE version = 45)
            """, connection);
        command.Parameters.AddWithValue("projectRunRequests", $"{_schema}.project_run_requests");
        command.Parameters.AddWithValue("projectRunCancellations", $"{_schema}.project_run_cancellations");
        command.Parameters.AddWithValue("runs", $"{_schema}.accepted_runs");
        command.Parameters.AddWithValue("cosConversations", $"{_schema}.chief_of_staff_conversations");
        command.Parameters.AddWithValue("cosPriorities", $"{_schema}.chief_of_staff_priorities");
        command.Parameters.AddWithValue("cosLinks", $"{_schema}.chief_of_staff_priority_links");
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
        // The two spend budget tables whose guard triggers the last column counts.
        command.Parameters.AddWithValue("reservations", $"{_schema}.spend_budget_reservations");
        command.Parameters.AddWithValue("cursors", $"{_schema}.spend_budget_accounting_cursors");
        command.Parameters.AddWithValue("rootModelTurnAdmissions", $"{_schema}.maf_root_model_turn_admissions");
        command.Parameters.AddWithValue("childSpawnAuthorizations",
            $"{_schema}.maf_child_spawn_authorization_receipts");
        command.Parameters.AddWithValue("childSpawnConsumptions",
            $"{_schema}.maf_child_spawn_authorization_consumptions");
        command.Parameters.AddWithValue("runtimeEvidenceReceipts",
            $"{_schema}.maf_runtime_evidence_receipts");
        command.Parameters.AddWithValue("p1RunRequestProvenance",
            $"{_schema}.p1_run_request_provenance");
        command.Parameters.AddWithValue("p1InvocationBindings",
            $"{_schema}.p1_invocation_bindings");
        command.Parameters.AddWithValue("runtimeRunStartIntents",
            $"{_schema}.runtime_run_start_intents");
        command.Parameters.AddWithValue("runtimeReplacementOperations",
            $"{_schema}.runtime_replacement_operations");
        command.Parameters.AddWithValue("runtimeReplacementActiveIndex",
            $"{_schema}.uq_runtime_replacement_one_active");
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
        // The default history skips reserved versions 017 and 034.
        Assert.Equal(
            Enumerable.Range(1, 16).Concat([18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45]),
            reader.GetFieldValue<int[]>(9));
        Assert.True(reader.GetBoolean(10));
        Assert.True(reader.GetBoolean(11));
        Assert.True(reader.GetBoolean(12));
        Assert.True(reader.GetBoolean(13));
        Assert.Equal(1L, reader.GetInt64(14));
        Assert.Equal(1L, reader.GetInt64(15));
        Assert.True(reader.GetBoolean(16));
        Assert.True(reader.GetBoolean(17));
        Assert.Equal(1L, reader.GetInt64(18));
        Assert.True(reader.GetBoolean(19));
        Assert.True(reader.GetBoolean(20));
        Assert.Contains("'cancelled'", reader.GetString(21));
        Assert.True(reader.GetBoolean(22));
        Assert.True(reader.GetBoolean(23));
        Assert.True(reader.GetBoolean(24));
        // Both spend budget tables have an enabled guard trigger and no-truncate trigger: 2 tables x 2 = 4.
        Assert.Equal(4L, reader.GetInt64(25));
        Assert.True(reader.GetBoolean(26));
        Assert.Equal(1L, reader.GetInt64(27));
        Assert.True(reader.GetBoolean(28));
        Assert.True(reader.GetBoolean(29));
        Assert.Equal(2L, reader.GetInt64(30));
        Assert.True(reader.GetBoolean(31));
        Assert.Equal(1L, reader.GetInt64(32));
        Assert.Equal(1L, reader.GetInt64(33));
        Assert.True(reader.GetBoolean(34));
        Assert.True(reader.GetBoolean(35));
        Assert.Equal(2L, reader.GetInt64(36));
        Assert.Equal(2L, reader.GetInt64(37));
        Assert.Equal(1L, reader.GetInt64(38));
        Assert.True(reader.GetBoolean(39));
        Assert.Equal(1L, reader.GetInt64(40));
        Assert.Contains("numeric-limit-native-no-send", reader.GetString(41));
        Assert.Contains("native-root-turn-completion", reader.GetString(41));
        Assert.Equal(1L, reader.GetInt64(42));
        Assert.True(reader.GetBoolean(43));
        Assert.Equal(1L, reader.GetInt64(44));
        Assert.Equal(1L, reader.GetInt64(45));
        Assert.True(reader.GetBoolean(46));
        Assert.True(reader.GetBoolean(47));
        Assert.Contains("native-root-turn-replay", reader.GetString(41));
        Assert.Equal(1L, reader.GetInt64(48));
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
