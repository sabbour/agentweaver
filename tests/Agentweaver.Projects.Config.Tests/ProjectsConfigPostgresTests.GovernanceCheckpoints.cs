using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public void GovernanceCheckpointCanonicalBytesAndHashAreStable()
    {
        var checkpoint = ProjectGovernanceCheckpointCanonicalJson.Create(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Project",
            "scope-1",
            "governance",
            "policy",
            1,
            new string('0', 64),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var bytes = ProjectGovernanceCheckpointCanonicalJson.Serialize(checkpoint);

        Assert.Equal(
            "{\"schemaVersion\":1,\"checkpointId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"scopeType\":\"Project\",\"scopeId\":\"scope-1\",\"resourceType\":\"governance\",\"resourceId\":\"policy\",\"sequence\":1,\"headHash\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"createdAt\":\"2026-01-02T03:04:05+00:00\",\"checkpointHash\":\"ec23b8a02ee70a3086c37f52422f7d653ef1489907cf5b0c18392740e4374223\"}",
            Encoding.UTF8.GetString(bytes));
        Assert.True(ProjectGovernanceCheckpointCanonicalJson.TryDeserialize(bytes, out var parsed));
        Assert.Equal(checkpoint, parsed);
        Assert.False(ProjectGovernanceCheckpointCanonicalJson.TryDeserialize(
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
                "\"sequence\":1",
                "\"sequence\":2",
                StringComparison.Ordinal)),
            out _));
    }

    [Fact]
    public async Task GovernanceCheckpointExportVerifiesTheChainPrefixAndDetectsRecordTampering()
    {
        var scopeId = "checkpoint-" + Guid.NewGuid().ToString("N");
        var append = GovernanceAppendRequest(scopeId, key: "append-first");
        var receipt = await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var objectStore = new MemoryObjectStore();
        var checkpointStore = CreateGovernanceCheckpointStore(objectStore);
        var createRequest = GovernanceCheckpointRequest(append, "checkpoint-first");
        var checkpoint = await checkpointStore.CreateAsync(createRequest, CancellationToken.None);
        var exported = await checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None);

        Assert.Equal(receipt.Sequence, checkpoint.Sequence);
        Assert.Equal(receipt.RecordHash, checkpoint.HeadHash);
        Assert.True(exported.Retained);
        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Matches,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));

        var tampered = append with
        {
            ExpectedSequence = receipt.Sequence,
            ExpectedFenceGeneration = receipt.FenceGeneration,
            ExpectedHeadHash = receipt.RecordHash,
            IdempotencyKey = "append-second",
        };
        await CreateGovernanceDecisionStore().AppendAsync(tampered, CancellationToken.None);
        var replay = await checkpointStore.CreateAsync(createRequest, CancellationToken.None);
        Assert.Equal(checkpoint, replay);
        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Matches,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));

        await TamperGovernanceDecisionAsync(scopeId);
        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Mismatch,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));
    }

    [Fact]
    public async Task GovernanceCheckpointCreateReplaysConcurrentInsertRace()
    {
        var append = GovernanceAppendRequest(
            "checkpoint-concurrent-" + Guid.NewGuid().ToString("N"),
            key: "append-before-checkpoint");
        await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var request = GovernanceCheckpointRequest(append, "concurrent-create");
        var store = CreateGovernanceCheckpointStore();

        Task<ProjectGovernanceCheckpointV1> first;
        Task<ProjectGovernanceCheckpointV1> second;
        Exception? observationFailure = null;
        await using (var blocker = await fixture.DataSource.OpenConnectionAsync())
        await using (var transaction = await blocker.BeginTransactionAsync())
        {
            await using (var lockTable = new NpgsqlCommand(
                "LOCK TABLE projects_config.project_governance_decision_checkpoints IN SHARE MODE",
                blocker,
                transaction))
                await lockTable.ExecuteNonQueryAsync();

            first = store.CreateAsync(request, CancellationToken.None);
            second = store.CreateAsync(request, CancellationToken.None);
            try
            {
                await WaitForBlockedCheckpointInsertsAsync(blocker.ProcessID, minimum: 2);
                Assert.False(first.IsCompleted);
                Assert.False(second.IsCompleted);
            }
            catch (Exception exception)
            {
                observationFailure = exception;
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        ProjectGovernanceCheckpointV1[] checkpoints;
        try
        {
            checkpoints = await Task.WhenAll(first, second);
        }
        catch (Exception operationFailure)
        {
            if (observationFailure is not null)
                throw new AggregateException(observationFailure, operationFailure);
            throw;
        }
        if (observationFailure is not null)
            ExceptionDispatchInfo.Capture(observationFailure).Throw();

        await using var observer = await fixture.DataSource.OpenConnectionAsync();
        await using var stored = new NpgsqlCommand("""
            SELECT checkpoint_id, checkpoint_hash
            FROM projects_config.project_governance_decision_checkpoints
            WHERE scope_type = @scope_type AND scope_id = @scope_id
              AND resource_type = @resource_type AND resource_id = @resource_id
              AND idempotency_key = @idempotency_key
            """, observer);
        AddCheckpointRequestParameters(stored, request);
        await using var reader = await stored.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var storedId = reader.GetGuid(0);
        var storedHash = reader.GetString(1);
        Assert.False(await reader.ReadAsync());
        Assert.All(checkpoints, checkpoint => Assert.Equal(storedId, checkpoint.CheckpointId));
        Assert.All(checkpoints, checkpoint => Assert.Equal(storedHash, checkpoint.CheckpointHash));
    }

    [Fact]
    public async Task GovernanceCheckpointCreateRetriesMatchingUniqueConstraintExhaustion()
    {
        var append = GovernanceAppendRequest(
            "checkpoint-unique-" + Guid.NewGuid().ToString("N"),
            key: "append-before-unique-checkpoint");
        await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var suffix = Guid.NewGuid().ToString("N");
        var sequence = $"projects_config.test_checkpoint_attempts_{suffix}";
        var function = $"projects_config.test_checkpoint_unique_{suffix}";
        var trigger = $"test_checkpoint_unique_{suffix}";
        var request = GovernanceCheckpointRequest(append, "unique-exhaustion");

        await using var owner = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($$"""
            CREATE SEQUENCE {{sequence}};
            CREATE FUNCTION {{function}}() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                IF pg_trigger_depth() = 1 THEN
                    PERFORM nextval('{{sequence}}');
                    INSERT INTO projects_config.project_governance_decision_checkpoints
                        (checkpoint_id, scope_type, scope_id, resource_type, resource_id, sequence,
                         idempotency_key, head_hash, checkpoint_hash, canonical_checkpoint)
                    VALUES
                        (gen_random_uuid(), NEW.scope_type, NEW.scope_id, NEW.resource_type, NEW.resource_id,
                         NEW.sequence, NEW.idempotency_key, NEW.head_hash, NEW.checkpoint_hash,
                         NEW.canonical_checkpoint);
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {{trigger}}
            BEFORE INSERT ON projects_config.project_governance_decision_checkpoints
            FOR EACH ROW EXECUTE FUNCTION {{function}}();
            """, owner))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                CreateGovernanceCheckpointStore().CreateAsync(request, CancellationToken.None));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
            Assert.Equal(
                "IX_project_governance_decision_checkpoints_stream_idempotency",
                exception.ConstraintName);
            await using var readAttempts = new NpgsqlCommand($"SELECT last_value FROM {sequence}", owner);
            Assert.Equal(3, (long)(await readAttempts.ExecuteScalarAsync())!);
            await AssertCheckpointRowCountAsync(owner, request, expected: 0);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($$"""
                DROP TRIGGER {{trigger}} ON projects_config.project_governance_decision_checkpoints;
                DROP FUNCTION {{function}}();
                DROP SEQUENCE {{sequence}};
                """, owner);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GovernanceCheckpointCreateRetriesSerializationFailureAtCommit()
    {
        var append = GovernanceAppendRequest(
            "checkpoint-commit-" + Guid.NewGuid().ToString("N"),
            key: "append-before-commit-checkpoint");
        await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var suffix = Guid.NewGuid().ToString("N");
        var sequence = $"projects_config.test_checkpoint_attempts_{suffix}";
        var function = $"projects_config.test_checkpoint_commit_{suffix}";
        var trigger = $"test_checkpoint_commit_{suffix}";
        var request = GovernanceCheckpointRequest(append, "commit-exhaustion");

        await using var owner = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($$"""
            CREATE SEQUENCE {{sequence}};
            CREATE FUNCTION {{function}}() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                PERFORM nextval('{{sequence}}');
                RAISE EXCEPTION USING ERRCODE = '40001', MESSAGE = 'checkpoint commit error';
            END;
            $body$;
            CREATE CONSTRAINT TRIGGER {{trigger}}
            AFTER INSERT ON projects_config.project_governance_decision_checkpoints
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION {{function}}();
            """, owner))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                CreateGovernanceCheckpointStore().CreateAsync(request, CancellationToken.None));

            Assert.Equal(PostgresErrorCodes.SerializationFailure, exception.SqlState);
            await using var readAttempts = new NpgsqlCommand($"SELECT last_value FROM {sequence}", owner);
            Assert.Equal(3, (long)(await readAttempts.ExecuteScalarAsync())!);
            await AssertCheckpointRowCountAsync(owner, request, expected: 0);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($$"""
                DROP TRIGGER {{trigger}} ON projects_config.project_governance_decision_checkpoints;
                DROP FUNCTION {{function}}();
                DROP SEQUENCE {{sequence}};
                """, owner);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData("P0001", null)]
    [InlineData("23505", "other_checkpoint_index")]
    public async Task GovernanceCheckpointCreateDoesNotRetryUnrelatedDatabaseErrors(
        string sqlState,
        string? constraint)
    {
        var append = GovernanceAppendRequest(
            "checkpoint-error-" + Guid.NewGuid().ToString("N"),
            key: "append-before-error-checkpoint");
        await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var suffix = Guid.NewGuid().ToString("N");
        var sequence = $"projects_config.test_checkpoint_attempts_{suffix}";
        var function = $"projects_config.test_checkpoint_error_{suffix}";
        var trigger = $"test_checkpoint_error_{suffix}";
        var constraintOption = constraint is null
            ? string.Empty
            : $", CONSTRAINT = '{constraint}'";
        var request = GovernanceCheckpointRequest(append, "database-error");

        await using var owner = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($$"""
            CREATE SEQUENCE {{sequence}};
            CREATE FUNCTION {{function}}() RETURNS trigger
            LANGUAGE plpgsql AS $body$
            BEGIN
                PERFORM nextval('{{sequence}}');
                RAISE EXCEPTION USING
                    ERRCODE = '{{sqlState}}',
                    MESSAGE = 'checkpoint test error'{{constraintOption}};
            END;
            $body$;
            CREATE TRIGGER {{trigger}}
            BEFORE INSERT ON projects_config.project_governance_decision_checkpoints
            FOR EACH ROW EXECUTE FUNCTION {{function}}();
            """, owner))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                CreateGovernanceCheckpointStore().CreateAsync(request, CancellationToken.None));

            Assert.Equal(sqlState, exception.SqlState);
            await using var readAttempts = new NpgsqlCommand($"SELECT last_value FROM {sequence}", owner);
            Assert.Equal(1, (long)(await readAttempts.ExecuteScalarAsync())!);
            await AssertCheckpointRowCountAsync(owner, request, expected: 0);
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($$"""
                DROP TRIGGER {{trigger}} ON projects_config.project_governance_decision_checkpoints;
                DROP FUNCTION {{function}}();
                DROP SEQUENCE {{sequence}};
                """, owner);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task GovernanceCheckpointCreateKeepsIdempotencyKeyScopedToTheStream()
    {
        var firstRequest = GovernanceCheckpointRequest(
            GovernanceAppendRequest("checkpoint-stream-first-" + Guid.NewGuid().ToString("N")),
            "shared-key");
        var secondRequest = GovernanceCheckpointRequest(
            GovernanceAppendRequest("checkpoint-stream-second-" + Guid.NewGuid().ToString("N")),
            "shared-key");
        var store = CreateGovernanceCheckpointStore();

        var first = await store.CreateAsync(firstRequest, CancellationToken.None);
        var second = await store.CreateAsync(secondRequest, CancellationToken.None);

        Assert.NotEqual(first.CheckpointId, second.CheckpointId);
        Assert.NotEqual(first.CheckpointHash, second.CheckpointHash);
    }

    [Fact]
    public async Task GovernanceCheckpointCreateRejectsCorruptStoredReplay()
    {
        var request = GovernanceCheckpointRequest(
            GovernanceAppendRequest("checkpoint-corrupt-" + Guid.NewGuid().ToString("N")),
            "corrupt-replay");
        var store = CreateGovernanceCheckpointStore();
        var checkpoint = await store.CreateAsync(request, CancellationToken.None);

        await using var owner = await fixture.DataSource.OpenConnectionAsync();
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE projects_config.project_governance_decision_checkpoints " +
            "DISABLE TRIGGER project_governance_decision_checkpoints_immutable",
            owner))
            await disable.ExecuteNonQueryAsync();
        try
        {
            await using var corrupt = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decision_checkpoints
                SET canonical_checkpoint = @content
                WHERE checkpoint_id = @checkpoint_id
                """, owner);
            corrupt.Parameters.AddWithValue("content", NpgsqlDbType.Bytea, new byte[] { 0 });
            corrupt.Parameters.AddWithValue("checkpoint_id", checkpoint.CheckpointId);
            Assert.Equal(1, await corrupt.ExecuteNonQueryAsync());
        }
        finally
        {
            await using var enable = new NpgsqlCommand(
                "ALTER TABLE projects_config.project_governance_decision_checkpoints " +
                "ENABLE TRIGGER project_governance_decision_checkpoints_immutable",
                owner);
            await enable.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.CreateAsync(request, CancellationToken.None));
        Assert.Equal("The stored governance checkpoint is invalid.", exception.Message);
    }

    [Fact]
    public async Task GovernanceCheckpointVerificationRejectsStoredDecisionIdMismatch()
    {
        var scopeId = "checkpoint-decision-id-" + Guid.NewGuid().ToString("N");
        var append = GovernanceAppendRequest(scopeId);
        await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var checkpointStore = CreateGovernanceCheckpointStore(new MemoryObjectStore());
        var checkpoint = await checkpointStore.CreateAsync(
            GovernanceCheckpointRequest(append, "checkpoint-decision-id"),
            CancellationToken.None);
        await checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None);

        await TamperGovernanceDecisionIdAsync(scopeId);

        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Mismatch,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));
    }

    [Fact]
    public async Task GovernanceCheckpointVerificationReturnsMismatchForMalformedDecisionRecord()
    {
        var scopeId = "checkpoint-malformed-" + Guid.NewGuid().ToString("N");
        var append = GovernanceAppendRequest(scopeId);
        await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var checkpointStore = CreateGovernanceCheckpointStore(new MemoryObjectStore());
        var checkpoint = await checkpointStore.CreateAsync(
            GovernanceCheckpointRequest(append, "checkpoint-malformed"),
            CancellationToken.None);
        await checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None);

        await TamperGovernanceDecisionWithMalformedRecordAsync(scopeId);

        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Mismatch,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));
    }

    [Theory]
    [InlineData("delete-first")]
    [InlineData("reorder")]
    [InlineData("truncate-tail")]
    public async Task GovernanceCheckpointVerificationRejectsDeletedReorderedAndTruncatedChains(string tamper)
    {
        var scopeId = "checkpoint-chain-" + Guid.NewGuid().ToString("N");
        var firstAppend = GovernanceAppendRequest(scopeId, key: "append-first");
        var firstReceipt = await CreateGovernanceDecisionStore().AppendAsync(firstAppend, CancellationToken.None);
        await CreateGovernanceDecisionStore().AppendAsync(firstAppend with
        {
            ExpectedSequence = firstReceipt.Sequence,
            ExpectedFenceGeneration = firstReceipt.FenceGeneration,
            ExpectedHeadHash = firstReceipt.RecordHash,
            IdempotencyKey = "append-second",
        }, CancellationToken.None);
        var checkpointStore = CreateGovernanceCheckpointStore(new MemoryObjectStore());
        var checkpoint = await checkpointStore.CreateAsync(
            GovernanceCheckpointRequest(firstAppend, "checkpoint-chain"),
            CancellationToken.None);
        await checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None);

        await TamperGovernanceDecisionChainAsync(scopeId, tamper);

        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Mismatch,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));
    }

    [Fact]
    public async Task GovernanceCheckpointExportFailureLeavesAnIdempotentCheckpointForRetry()
    {
        var scopeId = "checkpoint-retry-" + Guid.NewGuid().ToString("N");
        var append = GovernanceAppendRequest(scopeId, key: "append-first");
        var receipt = await CreateGovernanceDecisionStore().AppendAsync(append, CancellationToken.None);
        var objectStore = new MemoryObjectStore { FailNextWrite = true };
        var checkpointStore = CreateGovernanceCheckpointStore(objectStore);
        var checkpoint = await checkpointStore.CreateAsync(
            GovernanceCheckpointRequest(append, "checkpoint-retry"),
            CancellationToken.None);

        await Assert.ThrowsAsync<IOException>(() =>
            checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None));

        await CreateGovernanceDecisionStore().AppendAsync(append with
        {
            ExpectedSequence = receipt.Sequence,
            ExpectedFenceGeneration = receipt.FenceGeneration,
            ExpectedHeadHash = receipt.RecordHash,
            IdempotencyKey = "append-second",
        }, CancellationToken.None);
        var retry = await checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None);

        Assert.True(retry.Retained);
        Assert.Equal(checkpoint.CheckpointHash, retry.CheckpointHash);
        Assert.Equal(checkpoint.Sequence, AssertCheckpoint(retry.Content).Sequence);
        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Matches,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));
    }

    [Fact]
    public async Task GovernanceCheckpointWithoutStorageIsNotReportedAsRetained()
    {
        var append = GovernanceAppendRequest("checkpoint-local-" + Guid.NewGuid().ToString("N"));
        var checkpointStore = CreateGovernanceCheckpointStore();
        var checkpoint = await checkpointStore.CreateAsync(
            GovernanceCheckpointRequest(append, "checkpoint-local"),
            CancellationToken.None);

        Assert.Equal(0, checkpoint.Sequence);
        Assert.Equal(new string('0', 64), checkpoint.HeadHash);
        var exported = await checkpointStore.ExportAsync(checkpoint.CheckpointId, CancellationToken.None);

        Assert.False(exported.Retained);
        Assert.Equal(
            ProjectGovernanceCheckpointVerification.Unknown,
            await checkpointStore.VerifyRetainedAsync(checkpoint.CheckpointHash, CancellationToken.None));
    }

    private ProjectGovernanceCheckpointStore CreateGovernanceCheckpointStore(
        IObjectStore? retainedStore = null) =>
        new(
            new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()),
            retainedStore,
            TimeProvider.System);

    private static ProjectGovernanceCheckpointCreateRequest GovernanceCheckpointRequest(
        ProjectGovernanceDecisionAppendRequest append,
        string idempotencyKey) =>
        new(
            append.ScopeType,
            append.ScopeId,
            append.ResourceType,
            append.ResourceId,
            idempotencyKey);

    private static ProjectGovernanceCheckpointV1 AssertCheckpoint(byte[] bytes)
    {
        Assert.True(ProjectGovernanceCheckpointCanonicalJson.TryDeserialize(bytes, out var checkpoint));
        return Assert.IsType<ProjectGovernanceCheckpointV1>(checkpoint);
    }

    private async Task WaitForBlockedCheckpointInsertsAsync(int blockerProcessId, int minimum)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var observer = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
        while (true)
        {
            await using var blocked = new NpgsqlCommand("""
                SELECT count(*)
                FROM pg_stat_activity
                WHERE @blocker = ANY(pg_blocking_pids(pid))
                  AND query LIKE '%project_governance_decision_checkpoints%'
                  AND query LIKE '%INSERT%'
                """, observer);
            blocked.Parameters.AddWithValue("blocker", blockerProcessId);
            if ((long)(await blocked.ExecuteScalarAsync(timeout.Token))! >= minimum)
                return;
            await Task.Delay(20, timeout.Token);
        }
    }

    private static void AddCheckpointRequestParameters(
        NpgsqlCommand command,
        ProjectGovernanceCheckpointCreateRequest request)
    {
        command.Parameters.AddWithValue("scope_type", request.ScopeType.ToString());
        command.Parameters.AddWithValue("scope_id", request.ScopeId);
        command.Parameters.AddWithValue("resource_type", request.ResourceType);
        command.Parameters.AddWithValue("resource_id", request.ResourceId);
        command.Parameters.AddWithValue("idempotency_key", request.IdempotencyKey);
    }

    private static async Task AssertCheckpointRowCountAsync(
        NpgsqlConnection connection,
        ProjectGovernanceCheckpointCreateRequest request,
        long expected)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*)
            FROM projects_config.project_governance_decision_checkpoints
            WHERE scope_type = @scope_type AND scope_id = @scope_id
              AND resource_type = @resource_type AND resource_id = @resource_id
              AND idempotency_key = @idempotency_key
            """, connection);
        AddCheckpointRequestParameters(command, request);
        Assert.Equal(expected, (long)(await command.ExecuteScalarAsync())!);
    }

    private async Task TamperGovernanceDecisionAsync(string scopeId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE projects_config.project_governance_decisions DISABLE TRIGGER project_governance_decisions_immutable",
            connection))
            await disable.ExecuteNonQueryAsync();
        try
        {
            var canonicalRecord = await ReadGovernanceDecisionBytesAsync(connection, scopeId, 1);
            var tampered = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(canonicalRecord).Replace(
                    "\"selectedRule\":\"allow-read\"",
                    "\"selectedRule\":\"tampered-rule\"",
                    StringComparison.Ordinal));
            await using var update = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decisions
                SET canonical_record = @canonical_record
                WHERE scope_id = @scope_id AND sequence = 1
                """, connection);
            update.Parameters.AddWithValue("canonical_record", NpgsqlDbType.Bytea, tampered);
            update.Parameters.AddWithValue("scope_id", scopeId);
            await update.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var enable = new NpgsqlCommand(
                "ALTER TABLE projects_config.project_governance_decisions ENABLE TRIGGER project_governance_decisions_immutable",
                connection);
            await enable.ExecuteNonQueryAsync();
        }
    }

    private async Task TamperGovernanceDecisionIdAsync(string scopeId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        Guid decisionId;
        await using (var read = new NpgsqlCommand("""
            SELECT decision_id
            FROM projects_config.project_governance_decisions
            WHERE scope_id = @scope_id AND sequence = 1
            """, connection))
        {
            read.Parameters.AddWithValue("scope_id", scopeId);
            decisionId = (Guid)(await read.ExecuteScalarAsync())!;
        }

        var changedDecisionId = Guid.NewGuid();
        await using (var disable = new NpgsqlCommand("""
            ALTER TABLE projects_config.project_governance_decisions
                DISABLE TRIGGER project_governance_decisions_immutable;
            ALTER TABLE projects_config.project_governance_decision_outbox
                DISABLE TRIGGER project_governance_decision_outbox_immutable
            """, connection))
            await disable.ExecuteNonQueryAsync();
        try
        {
            await using (var deleteOutbox = new NpgsqlCommand("""
                DELETE FROM projects_config.project_governance_decision_outbox
                WHERE decision_id = @decision_id
                """, connection))
            {
                deleteOutbox.Parameters.AddWithValue("decision_id", decisionId);
                Assert.Equal(1, await deleteOutbox.ExecuteNonQueryAsync());
            }

            await using var updateDecision = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decisions
                SET decision_id = @changed_decision_id
                WHERE scope_id = @scope_id AND sequence = 1
                """, connection);
            updateDecision.Parameters.AddWithValue("changed_decision_id", changedDecisionId);
            updateDecision.Parameters.AddWithValue("scope_id", scopeId);
            Assert.Equal(1, await updateDecision.ExecuteNonQueryAsync());
        }
        finally
        {
            await using var enable = new NpgsqlCommand("""
                ALTER TABLE projects_config.project_governance_decision_outbox
                    ENABLE TRIGGER project_governance_decision_outbox_immutable;
                ALTER TABLE projects_config.project_governance_decisions
                    ENABLE TRIGGER project_governance_decisions_immutable
                """, connection);
            await enable.ExecuteNonQueryAsync();
        }
    }

    private async Task TamperGovernanceDecisionWithMalformedRecordAsync(string scopeId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE projects_config.project_governance_decisions DISABLE TRIGGER project_governance_decisions_immutable",
            connection))
            await disable.ExecuteNonQueryAsync();
        try
        {
            var record = JsonNode.Parse(Encoding.UTF8.GetString(
                await ReadGovernanceDecisionBytesAsync(connection, scopeId, 1)))!.AsObject();
            record["decision"]!.AsObject().Remove("effectiveRoleRevisions");
            var malformed = Encoding.UTF8.GetBytes(record.ToJsonString());
            await using var update = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decisions
                SET canonical_record = @canonical_record
                WHERE scope_id = @scope_id AND sequence = 1
                """, connection);
            update.Parameters.AddWithValue("canonical_record", NpgsqlDbType.Bytea, malformed);
            update.Parameters.AddWithValue("scope_id", scopeId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }
        finally
        {
            await using var enable = new NpgsqlCommand(
                "ALTER TABLE projects_config.project_governance_decisions ENABLE TRIGGER project_governance_decisions_immutable",
                connection);
            await enable.ExecuteNonQueryAsync();
        }
    }

    private async Task TamperGovernanceDecisionChainAsync(string scopeId, string tamper)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var disable = new NpgsqlCommand("""
            ALTER TABLE projects_config.project_governance_decisions
                DISABLE TRIGGER project_governance_decisions_immutable;
            ALTER TABLE projects_config.project_governance_decision_outbox
                DISABLE TRIGGER project_governance_decision_outbox_immutable
            """, connection))
            await disable.ExecuteNonQueryAsync();
        try
        {
            if (tamper == "reorder")
            {
                var first = await ReadGovernanceDecisionBytesAsync(connection, scopeId, 1);
                var second = await ReadGovernanceDecisionBytesAsync(connection, scopeId, 2);
                await using var reorder = new NpgsqlCommand("""
                    UPDATE projects_config.project_governance_decisions
                    SET canonical_record = CASE sequence
                        WHEN 1 THEN @second
                        WHEN 2 THEN @first
                    END
                    WHERE scope_id = @scope_id AND sequence IN (1, 2)
                    """, connection);
                reorder.Parameters.AddWithValue("first", NpgsqlDbType.Bytea, first);
                reorder.Parameters.AddWithValue("second", NpgsqlDbType.Bytea, second);
                reorder.Parameters.AddWithValue("scope_id", scopeId);
                await reorder.ExecuteNonQueryAsync();
            }
            else
            {
                var sequence = tamper == "delete-first" ? 1 : 2;
                await using (var deleteOutbox = new NpgsqlCommand("""
                    DELETE FROM projects_config.project_governance_decision_outbox
                    WHERE decision_id = (
                        SELECT decision_id
                        FROM projects_config.project_governance_decisions
                        WHERE scope_id = @scope_id AND sequence = @sequence
                    )
                    """, connection))
                {
                    deleteOutbox.Parameters.AddWithValue("scope_id", scopeId);
                    deleteOutbox.Parameters.AddWithValue("sequence", sequence);
                    await deleteOutbox.ExecuteNonQueryAsync();
                }
                await using var deleteDecision = new NpgsqlCommand("""
                    DELETE FROM projects_config.project_governance_decisions
                    WHERE scope_id = @scope_id AND sequence = @sequence
                    """, connection);
                deleteDecision.Parameters.AddWithValue("scope_id", scopeId);
                deleteDecision.Parameters.AddWithValue("sequence", sequence);
                await deleteDecision.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await using var enable = new NpgsqlCommand("""
                ALTER TABLE projects_config.project_governance_decision_outbox
                    ENABLE TRIGGER project_governance_decision_outbox_immutable;
                ALTER TABLE projects_config.project_governance_decisions
                    ENABLE TRIGGER project_governance_decisions_immutable
                """, connection);
            await enable.ExecuteNonQueryAsync();
        }
    }

    private static async Task<byte[]> ReadGovernanceDecisionBytesAsync(
        NpgsqlConnection connection,
        string scopeId,
        long sequence)
    {
        await using var command = new NpgsqlCommand("""
            SELECT canonical_record
            FROM projects_config.project_governance_decisions
            WHERE scope_id = @scope_id AND sequence = @sequence
            """, connection);
        command.Parameters.AddWithValue("scope_id", scopeId);
        command.Parameters.AddWithValue("sequence", sequence);
        return (byte[])(await command.ExecuteScalarAsync())!;
    }

    private sealed class MemoryObjectStore : IObjectStore
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
        private int _failNextWrite;

        public bool FailNextWrite
        {
            set => Interlocked.Exchange(ref _failNextWrite, value ? 1 : 0);
        }

        public async Task WriteAsync(
            ObjectKey key,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _failNextWrite, 0) == 1)
                throw new IOException("The test storage writer failed.");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (!_objects.TryAdd(key.Value, buffer.ToArray()))
                throw new IOException("The test object already exists.");
        }

        public Task<ObjectRead?> ReadAsync(
            ObjectKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_objects.TryGetValue(key.Value, out var content))
                return Task.FromResult<ObjectRead?>(null);
            var copy = content.ToArray();
            return Task.FromResult<ObjectRead?>(
                new ObjectRead(new MemoryStream(copy, writable: false), copy.Length, () => { }));
        }

        public Task<bool> DeleteAsync(
            ObjectKey key,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_objects.TryRemove(key.Value, out _));
        }
    }
}
