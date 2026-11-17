using System.Collections.Immutable;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task GovernanceDecisionAppendIsCanonicalFencedAndIdempotent()
    {
        var request = GovernanceAppendRequest("chain-" + Guid.NewGuid().ToString("N"));
        var first = await CreateGovernanceDecisionStore().AppendAsync(request, CancellationToken.None);
        var replay = await CreateGovernanceDecisionStore().AppendAsync(request, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first with { Replayed = true }, replay);
        Assert.Equal(1, first.Sequence);
        Assert.Equal(1, first.FenceGeneration);
        Assert.Equal(new string('0', 64), first.PreviousHash);

        await using var db = CreateDbContext();
        var record = await db.GovernanceDecisions.AsNoTracking().SingleAsync(item => item.DecisionId == first.DecisionId);
        var outbox = await db.GovernanceDecisionOutbox.AsNoTracking().SingleAsync(item => item.OutboxId == first.OutboxId);
        var head = await db.GovernanceDecisionStreamHeads.AsNoTracking().SingleAsync(item =>
            item.ScopeId == request.ScopeId && item.ResourceId == request.ResourceId);
        Assert.Equal(first.RecordHash,
            ProjectGovernanceDecisionCanonicalJson.ChainHash(first.PreviousHash, record.CanonicalRecord));
        Assert.Equal(first.RecordHash, head.HeadHash);
        Assert.Equal(first.Sequence, outbox.Sequence);
        Assert.Equal(first.RecordHash, outbox.RecordHash);
        Assert.Equal(record.WriteTransactionId, outbox.WriteTransactionId);

        var changed = request with
        {
            Decision = request.Decision with { SelectedRule = "changed-rule" },
        };
        await Assert.ThrowsAsync<ProjectConfigException>(() =>
            CreateGovernanceDecisionStore().AppendAsync(changed, CancellationToken.None));

        await Assert.ThrowsAsync<ProjectConfigException>(() =>
            CreateGovernanceDecisionStore().AppendAsync(
                request with { IdempotencyKey = "stale-" + Guid.NewGuid().ToString("N") },
                CancellationToken.None));

        Assert.Equal(1, await db.GovernanceDecisions.CountAsync(item => item.ScopeId == request.ScopeId));
        Assert.Equal(1, await db.GovernanceDecisionOutbox.CountAsync(item => item.DecisionId == first.DecisionId));
    }

    [Fact]
    public async Task GovernanceDecisionStreamsAreIndependentAndConcurrentWritersAreFenced()
    {
        var scopeId = "chain-" + Guid.NewGuid().ToString("N");
        var left = GovernanceAppendRequest(scopeId, resourceId: "policy");
        var right = GovernanceAppendRequest(scopeId, resourceId: "admission");
        var leftReceipt = await CreateGovernanceDecisionStore().AppendAsync(left, CancellationToken.None);
        var rightReceipt = await CreateGovernanceDecisionStore().AppendAsync(right, CancellationToken.None);
        Assert.Equal(1, leftReceipt.Sequence);
        Assert.Equal(1, rightReceipt.Sequence);

        var concurrentScope = "chain-" + Guid.NewGuid().ToString("N");
        var first = GovernanceAppendRequest(concurrentScope, key: "writer-a");
        var second = GovernanceAppendRequest(concurrentScope, key: "writer-b");
        var writes = await Task.WhenAll(
            AppendOutcomeAsync(first),
            AppendOutcomeAsync(second));
        Assert.Single(writes, result => result.Receipt is not null);
        Assert.Single(writes, result => result.Conflict);
        await using var db = CreateDbContext();
        Assert.Equal(1, await db.GovernanceDecisions.CountAsync(item => item.ScopeId == concurrentScope));
        Assert.Equal(1, await db.GovernanceDecisionOutbox
            .Join(db.GovernanceDecisions, item => item.DecisionId, item => item.DecisionId, (_, decision) => decision)
            .CountAsync(item => item.ScopeId == concurrentScope));
    }

    [Fact]
    public async Task GovernanceDecisionAppendRollsBackHeadRecordAndOutboxTogether()
    {
        var request = GovernanceAppendRequest("rollback-" + Guid.NewGuid().ToString("N"));
        var suffix = Guid.NewGuid().ToString("N");
        var function = $"projects_config.fail_governance_outbox_{suffix}";
        var trigger = $"fail_governance_outbox_{suffix}";
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var command = new NpgsqlCommand($"""
            CREATE FUNCTION {function}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                RAISE EXCEPTION 'forced outbox failure' USING ERRCODE = 'P0001';
            END;
            $body$;
            CREATE TRIGGER {trigger}
            BEFORE INSERT ON projects_config.project_governance_decision_outbox
            FOR EACH ROW EXECUTE FUNCTION {function}();
            """, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await Assert.ThrowsAsync<PostgresException>(() =>
                CreateGovernanceDecisionStore().AppendAsync(request, CancellationToken.None));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"""
                DROP TRIGGER IF EXISTS {trigger} ON projects_config.project_governance_decision_outbox;
                DROP FUNCTION IF EXISTS {function}();
                """, connection);
            await cleanup.ExecuteNonQueryAsync();
        }

        await using var db = CreateDbContext();
        Assert.Empty(await db.GovernanceDecisionStreamHeads.AsNoTracking()
            .Where(item => item.ScopeId == request.ScopeId).ToArrayAsync());
        Assert.Empty(await db.GovernanceDecisions.AsNoTracking()
            .Where(item => item.ScopeId == request.ScopeId).ToArrayAsync());
        await using var verifyOutbox = new NpgsqlCommand("""
            SELECT count(*) FROM projects_config.project_governance_decision_outbox
            WHERE payload ->> 'scopeId' = @scope_id
            """, connection);
        verifyOutbox.Parameters.AddWithValue("scope_id", request.ScopeId);
        Assert.Equal(0L, (long)(await verifyOutbox.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task GovernanceDecisionTransactionIdsCannotBeSpoofedAcrossTransactions()
    {
        var scopeId = "transaction-id-" + Guid.NewGuid().ToString("N");
        var decisionId = Guid.NewGuid();
        var hash = new string('a', 64);
        var zeroHash = new string('0', 64);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using (var transaction = (Npgsql.NpgsqlTransaction)await connection.BeginTransactionAsync())
        {
            await using (var createHead = new NpgsqlCommand("""
                INSERT INTO projects_config.project_governance_decision_stream_heads
                    (scope_type, scope_id, resource_type, resource_id, sequence, fence_generation, head_hash, updated_at)
                VALUES ('Project', @scope_id, 'governance', 'policy', 0, 0, @zero_hash, @now)
                """, connection, transaction))
            {
                createHead.Parameters.AddWithValue("scope_id", scopeId);
                createHead.Parameters.AddWithValue("zero_hash", zeroHash);
                createHead.Parameters.AddWithValue("now", now);
                await createHead.ExecuteNonQueryAsync();
            }

            await using (var insertDecision = new NpgsqlCommand("""
                INSERT INTO projects_config.project_governance_decisions
                    (scope_type, scope_id, resource_type, resource_id, sequence, decision_id, idempotency_key,
                     request_digest, previous_hash, record_hash, canonical_record, write_transaction_id, recorded_at)
                VALUES ('Project', @scope_id, 'governance', 'policy', 1, @decision_id, 'forged-xid',
                        @request_digest, @previous_hash, @record_hash, @canonical_record, 'forged-xid', @now)
                """, connection, transaction))
            {
                insertDecision.Parameters.AddWithValue("scope_id", scopeId);
                insertDecision.Parameters.AddWithValue("decision_id", decisionId);
                insertDecision.Parameters.AddWithValue("request_digest", hash);
                insertDecision.Parameters.AddWithValue("previous_hash", zeroHash);
                insertDecision.Parameters.AddWithValue("record_hash", hash);
                insertDecision.Parameters.AddWithValue("canonical_record", NpgsqlTypes.NpgsqlDbType.Bytea, new byte[] { 1 });
                insertDecision.Parameters.AddWithValue("now", now);
                await insertDecision.ExecuteNonQueryAsync();
            }

            await using (var insertOutbox = new NpgsqlCommand("""
                INSERT INTO projects_config.project_governance_decision_outbox
                    (outbox_id, decision_id, event_type, sequence, record_hash, payload,
                     write_transaction_id, created_at)
                VALUES (@decision_id, @decision_id, 'project.governance_decision_appended.v1', 1,
                        @record_hash, '{}'::jsonb, 'forged-xid', @now)
                """, connection, transaction))
            {
                insertOutbox.Parameters.AddWithValue("decision_id", decisionId);
                insertOutbox.Parameters.AddWithValue("record_hash", hash);
                insertOutbox.Parameters.AddWithValue("now", now);
                await insertOutbox.ExecuteNonQueryAsync();
            }

            await using (var verifyStamp = new NpgsqlCommand("""
                SELECT decision.write_transaction_id, outbox.write_transaction_id,
                       pg_catalog.pg_current_xact_id()::text
                FROM projects_config.project_governance_decisions AS decision
                JOIN projects_config.project_governance_decision_outbox AS outbox
                  ON outbox.decision_id = decision.decision_id
                WHERE decision.decision_id = @decision_id
                """, connection, transaction))
            {
                verifyStamp.Parameters.AddWithValue("decision_id", decisionId);
                await using var reader = await verifyStamp.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                var currentTransactionId = reader.GetString(2);
                Assert.Equal(currentTransactionId, reader.GetString(0));
                Assert.Equal(currentTransactionId, reader.GetString(1));
            }
            await transaction.CommitAsync();
        }

        await using var secondTransaction =
            (Npgsql.NpgsqlTransaction)await connection.BeginTransactionAsync();
        await using var advanceHead = new NpgsqlCommand("""
            UPDATE projects_config.project_governance_decision_stream_heads
            SET sequence = 1, fence_generation = 1, head_hash = @record_hash, updated_at = @now
            WHERE scope_type = 'Project' AND scope_id = @scope_id
              AND resource_type = 'governance' AND resource_id = 'policy'
            """, connection, secondTransaction);
        advanceHead.Parameters.AddWithValue("scope_id", scopeId);
        advanceHead.Parameters.AddWithValue("record_hash", hash);
        advanceHead.Parameters.AddWithValue("now", now);
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => advanceHead.ExecuteNonQueryAsync());
        Assert.Equal("55000", rejected.SqlState);
    }

    [Fact]
    public async Task GovernanceDecisionAppendRejectsAnOversizedCanonicalRecordBeforeCommit()
    {
        var request = GovernanceAppendRequest("oversize-" + Guid.NewGuid().ToString("N")) with
        {
            Decision = SampleDecision() with { SelectedRule = new string('x', 64_500) },
        };
        await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateGovernanceDecisionStore().AppendAsync(request, CancellationToken.None));

        await using var db = CreateDbContext();
        Assert.Empty(await db.GovernanceDecisionStreamHeads.AsNoTracking()
            .Where(item => item.ScopeId == request.ScopeId).ToArrayAsync());
        Assert.Empty(await db.GovernanceDecisions.AsNoTracking()
            .Where(item => item.ScopeId == request.ScopeId).ToArrayAsync());
    }

    [Fact]
    public async Task GovernanceDecisionRuntimeCannotMutateRecordsOrAdvanceAnUnmatchedHead()
    {
        var request = GovernanceAppendRequest("grants-" + Guid.NewGuid().ToString("N"));
        var receipt = await CreateGovernanceDecisionStore().AppendAsync(request, CancellationToken.None);
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var setRole = new NpgsqlCommand(
            """SET ROLE "agentweaver-id-projects-config" """,
            connection))
            await setRole.ExecuteNonQueryAsync();
        try
        {
            await using var privileges = new NpgsqlCommand("""
                SELECT has_table_privilege(current_user, 'projects_config.project_governance_decisions', 'DELETE')
                   OR has_table_privilege(current_user, 'projects_config.project_governance_decisions', 'UPDATE')
                   OR has_table_privilege(current_user, 'projects_config.project_governance_decision_outbox', 'TRUNCATE')
                   OR has_table_privilege(current_user, 'projects_config.project_governance_decision_stream_heads', 'DELETE')
                   OR has_table_privilege(current_user, 'projects_config.project_governance_decision_stream_heads', 'TRUNCATE'),
                       has_column_privilege(current_user, 'projects_config.project_governance_decision_stream_heads', 'sequence', 'UPDATE')
                """, connection);
            await using var reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            await reader.DisposeAsync();

            await using var updateDecision = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decisions
                SET record_hash = @hash WHERE decision_id = @decision_id
                """, connection);
            updateDecision.Parameters.AddWithValue("hash", new string('b', 64));
            updateDecision.Parameters.AddWithValue("decision_id", receipt.DecisionId);
            var denied = await Assert.ThrowsAsync<PostgresException>(() => updateDecision.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);

            await using var advanceHead = new NpgsqlCommand("""
                UPDATE projects_config.project_governance_decision_stream_heads
                SET sequence = sequence + 1, fence_generation = fence_generation + 1
                WHERE scope_id = @scope_id AND resource_id = @resource_id
                """, connection);
            advanceHead.Parameters.AddWithValue("scope_id", request.ScopeId);
            advanceHead.Parameters.AddWithValue("resource_id", request.ResourceId);
            var rejected = await Assert.ThrowsAsync<PostgresException>(() => advanceHead.ExecuteNonQueryAsync());
            Assert.Equal("55000", rejected.SqlState);
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("RESET ROLE", connection);
            await resetRole.ExecuteNonQueryAsync();
        }

        await using var migrationConnection = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using var deleteHead = new NpgsqlCommand("""
            DELETE FROM projects_config.project_governance_decision_stream_heads
            WHERE scope_id = @scope_id AND resource_id = @resource_id
            """, migrationConnection);
        deleteHead.Parameters.AddWithValue("scope_id", request.ScopeId);
        deleteHead.Parameters.AddWithValue("resource_id", request.ResourceId);
        var deleteRejected = await Assert.ThrowsAsync<PostgresException>(() => deleteHead.ExecuteNonQueryAsync());
        Assert.Equal("55000", deleteRejected.SqlState);

        await using var truncateHeads = new NpgsqlCommand(
            "TRUNCATE TABLE projects_config.project_governance_decision_stream_heads CASCADE",
            migrationConnection);
        var truncateRejected = await Assert.ThrowsAsync<PostgresException>(() => truncateHeads.ExecuteNonQueryAsync());
        Assert.Equal("55000", truncateRejected.SqlState);
    }

    [Fact]
    public void GovernanceDecisionCanonicalizesRoleRevisionsByGuidText()
    {
        var decision = SampleDecision() with
        {
            EffectiveRoleRevisions =
            [
                new ProjectGovernanceRoleRevision(Guid.Parse("80000000-0000-0000-0000-000000000000"), 8),
                new ProjectGovernanceRoleRevision(Guid.Parse("70000000-0000-0000-0000-000000000000"), 7),
            ],
        };
        var canonical = ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(decision);
        Assert.Equal(
            [
                Guid.Parse("70000000-0000-0000-0000-000000000000"),
                Guid.Parse("80000000-0000-0000-0000-000000000000"),
            ],
            canonical.EffectiveRoleRevisions.Select(item => item.AssignmentId));
    }

    [Fact]
    public void GovernanceDecisionCanonicalBytesAndChainHashAreStableAndCoverEveryField()
    {
        var decision = SampleDecision();
        var baseline = ProjectGovernanceDecisionCanonicalJson.Serialize(
            ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(decision));
        var expected = """
            {"decisionType":"policy","outcome":"allow","approvalState":"notRequired","policyRevision":3,"policyArtifactSha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","selectedRule":"allow-read","interventionPoint":"beforeTool","requestId":"request-1","actionId":"action-1","runId":"run-1","agentId":"agent-1","roleId":"owner","humanEntraOid":"11111111-1111-1111-1111-111111111111","tenantId":"22222222-2222-2222-2222-222222222222","brokerIssuer":"issuer-1","brokerSubject":"subject-1","effectiveRoleRevisions":[{"assignmentId":"33333333-3333-3333-3333-333333333333","revision":4}],"ownerReceiptId":"receipt-1","ownerReceiptPosition":5,"compilerGeneration":"compiler-1","applicationGeneration":"application-1","correlationId":"correlation-1","decidedAt":"2026-01-02T03:04:05+00:00"}
            """;
        Assert.Equal(expected, Encoding.UTF8.GetString(baseline));
        Assert.DoesNotContain("budget", Encoding.UTF8.GetString(baseline), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", Encoding.UTF8.GetString(baseline), StringComparison.OrdinalIgnoreCase);

        var completeRecord = new ProjectGovernanceDecisionV1(
            1,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "Project",
            "scope-1",
            "governance",
            "policy",
            1,
            new string('0', 64),
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            decision);
        var completeBytes = ProjectGovernanceDecisionCanonicalJson.Serialize(completeRecord);
        var expectedComplete =
            "{\"schemaVersion\":1,\"decisionId\":\"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa\",\"scopeType\":\"Project\",\"scopeId\":\"scope-1\",\"resourceType\":\"governance\",\"resourceId\":\"policy\",\"sequence\":1,\"previousHash\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"recordedAt\":\"2026-01-02T03:04:05+00:00\",\"decision\":" +
            expected.Trim() + "}";
        Assert.Equal(expectedComplete, Encoding.UTF8.GetString(completeBytes));
        var chainHash = ProjectGovernanceDecisionCanonicalJson.ChainHash(
            completeRecord.PreviousHash,
            completeBytes);
        Assert.Equal("a0e0bf05fb6707408aab89e2471f2bedfde7bac857aff647e9fdd42754b32ca1", chainHash);
        Assert.NotEqual(
            chainHash,
            ProjectGovernanceDecisionCanonicalJson.ChainHash(new string('1', 64), completeBytes));

        var variants = new[]
        {
            decision with { DecisionType = "admission" },
            decision with { Outcome = ProjectGovernanceOutcome.Deny },
            decision with { ApprovalState = ProjectGovernanceApprovalState.Approved },
            decision with { PolicyRevision = 4 },
            decision with { PolicyArtifactSha256 = new string('b', 64) },
            decision with { SelectedRule = "changed" },
            decision with { InterventionPoint = "afterTool" },
            decision with { RequestId = "request-2" },
            decision with { ActionId = "action-2" },
            decision with { RunId = "run-2" },
            decision with { AgentId = "agent-2" },
            decision with { RoleId = "viewer" },
            decision with { HumanEntraOid = Guid.Parse("44444444-4444-4444-4444-444444444444") },
            decision with { TenantId = Guid.Parse("55555555-5555-5555-5555-555555555555") },
            decision with { BrokerIssuer = "issuer-2" },
            decision with { BrokerSubject = "subject-2" },
            decision with { EffectiveRoleRevisions = [new(Guid.Parse("66666666-6666-6666-6666-666666666666"), 7)] },
            decision with { OwnerReceiptId = "receipt-2" },
            decision with { OwnerReceiptPosition = 6 },
            decision with { CompilerGeneration = "compiler-2" },
            decision with { ApplicationGeneration = "application-2" },
            decision with { CorrelationId = "correlation-2" },
            decision with { DecidedAt = decision.DecidedAt.AddSeconds(1) },
        };

        var baselineDigest = ProjectGovernanceDecisionCanonicalJson.Digest(baseline);
        Assert.All(variants, variant =>
        {
            var bytes = ProjectGovernanceDecisionCanonicalJson.Serialize(
                ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(variant));
            Assert.NotEqual(baselineDigest, ProjectGovernanceDecisionCanonicalJson.Digest(bytes));
        });
        var completeDigest = ProjectGovernanceDecisionCanonicalJson.Digest(completeBytes);
        Assert.All(new[]
        {
            completeRecord with { SchemaVersion = 2 },
            completeRecord with { DecisionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb") },
            completeRecord with { ScopeType = "Platform" },
            completeRecord with { ScopeId = "scope-2" },
            completeRecord with { ResourceType = "other" },
            completeRecord with { ResourceId = "other" },
            completeRecord with { Sequence = 2 },
            completeRecord with { PreviousHash = new string('1', 64) },
            completeRecord with { RecordedAt = completeRecord.RecordedAt.AddSeconds(1) },
            completeRecord with { Decision = decision with { SelectedRule = "other" } },
        }, variant =>
        {
            var bytes = ProjectGovernanceDecisionCanonicalJson.Serialize(variant);
            Assert.NotEqual(completeDigest, ProjectGovernanceDecisionCanonicalJson.Digest(bytes));
        });
        Assert.Throws<ArgumentException>(() =>
            ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(decision with { TenantId = null }));
        Assert.Throws<ArgumentException>(() =>
            ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(decision with { BrokerSubject = null }));
        Assert.Throws<ArgumentException>(() =>
            ProjectGovernanceDecisionCanonicalJson.NormalizeAndValidate(decision with { DecisionType = "invalid type" }));
    }

    private ProjectGovernanceDecisionStore CreateGovernanceDecisionStore() =>
        new(new PooledDbContextFactory<ProjectsConfigDbContext>(CreateDbContextOptions()), TimeProvider.System);

    private static ProjectGovernanceDecisionAppendRequest GovernanceAppendRequest(
        string scopeId,
        string resourceId = "policy",
        string? key = null) =>
        new(
            NativeAcsPolicyScopeType.Project,
            scopeId,
            "governance",
            resourceId,
            ExpectedSequence: 0,
            ExpectedFenceGeneration: 0,
            ExpectedHeadHash: new string('0', 64),
            IdempotencyKey: key ?? "decision-" + Guid.NewGuid().ToString("N"),
            Decision: SampleDecision());

    private static ProjectGovernanceDecisionInput SampleDecision() =>
        new(
            "policy",
            ProjectGovernanceOutcome.Allow,
            ProjectGovernanceApprovalState.NotRequired,
            PolicyRevision: 3,
            PolicyArtifactSha256: new string('a', 64),
            SelectedRule: "allow-read",
            InterventionPoint: "beforeTool",
            RequestId: "request-1",
            ActionId: "action-1",
            RunId: "run-1",
            AgentId: "agent-1",
            RoleId: "owner",
            HumanEntraOid: Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TenantId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            BrokerIssuer: "issuer-1",
            BrokerSubject: "subject-1",
            EffectiveRoleRevisions:
            [
                new ProjectGovernanceRoleRevision(Guid.Parse("33333333-3333-3333-3333-333333333333"), 4),
            ],
            OwnerReceiptId: "receipt-1",
            OwnerReceiptPosition: 5,
            CompilerGeneration: "compiler-1",
            ApplicationGeneration: "application-1",
            CorrelationId: "correlation-1",
            DecidedAt: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

    private async Task<(ProjectGovernanceDecisionReceipt? Receipt, bool Conflict)> AppendOutcomeAsync(
        ProjectGovernanceDecisionAppendRequest request)
    {
        try
        {
            return (await CreateGovernanceDecisionStore().AppendAsync(request, CancellationToken.None), false);
        }
        catch (ProjectConfigException)
        {
            return (null, true);
        }
    }
}
