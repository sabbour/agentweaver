using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Xunit;

namespace Agentweaver.Knowledge.Tests;

[Collection("Knowledge PostgreSQL")]
public sealed class NativePostgresMemoryProviderTests(KnowledgePostgresFixture postgres)
{
    [Fact]
    public async Task MigrationVerifiesAndRecordsPersistAcrossProviderRestart()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        await KnowledgeMigrator.VerifyAsync(database.DataSource, database.Options.Schema);

        var created = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "persisted"),
            "create-1");
        var restarted = new NativePostgresMemoryProvider(database.DataSource, database.Options);

        var records = await restarted.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a"));

        Assert.Equal(KnowledgeWriteStatus.Created, created.Status);
        var record = Assert.Single(records.Items);
        Assert.Equal(created.Record!.RecordId, record.RecordId);
        Assert.Equal("persisted", record.Content);
    }

    [Fact]
    public async Task SearchesAndReadsRemainIsolatedByProjectAndAgent()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var first = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "a"),
            "isolation-1");
        await database.Provider.CreateAsync(
            Record("project-a", "agent-b", KnowledgeRecordKind.Memory, "note", "b"),
            "isolation-2");
        var otherProject = await database.Provider.CreateAsync(
            Record("project-b", "agent-a", KnowledgeRecordKind.Memory, "note", "other"),
            "isolation-3");

        var own = await database.Provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-a"));
        var otherAgent = await database.Provider.SearchAsync(new KnowledgeRecordQuery("project-a", "agent-b"));
        var otherProjectRead = await database.Provider.ReadAsync("project-a", otherProject.Record!.RecordId);

        Assert.Equal(first.Record!.RecordId, Assert.Single(own.Items).RecordId);
        Assert.Equal("b", Assert.Single(otherAgent.Items).Content);
        Assert.Null(otherProjectRead);
    }

    [Fact]
    public async Task RevisionsEnforceCompareAndSwapAndIdempotencyIsStable()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var original = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "original"),
            "write-1");
        var input = Update(original.Record!, expectedRevision: 1, content: "updated");

        var updated = await database.Provider.UpdateAsync(input, "write-2");
        var stale = await database.Provider.UpdateAsync(input, "write-3");
        var revisions = await database.Provider.ReadRevisionsAsync(
            "project-a", original.Record!.RecordId, 1, 10);
        var duplicate = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "idempotent"),
            "retry-key");
        var retry = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "idempotent"),
            "retry-key");
        var conflict = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            database.Provider.CreateAsync(
                Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "different"),
                "retry-key"));

        Assert.Equal(KnowledgeWriteStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Record!.Revision);
        Assert.Equal(KnowledgeWriteStatus.Stale, stale.Status);
        Assert.Equal(2, stale.CurrentRevision);
        Assert.Equal([2, 1], revisions.Items.Select(item => item.Revision));
        Assert.True(retry.IsDuplicate);
        Assert.Equal(duplicate.Record!.RecordId, retry.Record!.RecordId);
        Assert.Equal("idempotency_conflict", conflict.Code);
    }

    [Fact]
    public async Task ConcurrentRetriesDeduplicateAndCompareAndSwapHasOneWinner()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var create = Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "same request");

        var retries = await Task.WhenAll(
            database.Provider.CreateAsync(create, "concurrent-create"),
            database.Provider.CreateAsync(create, "concurrent-create"));

        Assert.Single(retries, result => !result.IsDuplicate);
        var duplicate = Assert.Single(retries, result => result.IsDuplicate);
        Assert.Equal(retries[0].Record!.RecordId, retries[1].Record!.RecordId);
        Assert.True(duplicate.IsDuplicate);
        Assert.Single((await database.Provider.SearchAsync(
            new KnowledgeRecordQuery("project-a", "agent-a"))).Items);

        var original = retries[0].Record!;
        var updates = await Task.WhenAll(
            database.Provider.UpdateAsync(Update(original, 1, "winner A"), "concurrent-update-a"),
            database.Provider.UpdateAsync(Update(original, 1, "winner B"), "concurrent-update-b"));

        Assert.Single(updates, result => result.Status == KnowledgeWriteStatus.Updated);
        Assert.Single(updates, result => result.Status == KnowledgeWriteStatus.Stale);
        var revisions = await database.Provider.ReadRevisionsAsync(
            "project-a", original.RecordId, 1, 10);
        Assert.Equal([2, 1], revisions.Items.Select(item => item.Revision));
    }

    [Fact]
    public async Task DecisionSupersessionRestoreAndApprovalPreserveImmutableHistory()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var firstProposal = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architecture", "first",
                sourceRunId: "run-a"),
            "decision-first-proposal");
        var firstPromotion = await database.Provider.PromoteProposalAsync(
            "project-a", "run-a", firstProposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "decision-first-promotion");
        var secondProposal = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architecture", "second",
                sourceRunId: "run-a"),
            "decision-second-proposal");
        var secondPromotion = await database.Provider.PromoteProposalAsync(
            "project-a", "run-a", secondProposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "decision-second-promotion");
        var first = firstPromotion.Decision!;
        var second = secondPromotion.Decision!;

        var superseded = await database.Provider.UpdateAsync(
            DecisionUpdate(first, KnowledgeRecordState.Superseded, second.RecordId),
            "decision-supersede");
        var cycle = await database.Provider.UpdateAsync(
            DecisionUpdate(second, KnowledgeRecordState.Superseded, first.RecordId),
            "decision-cycle");
        Assert.Equal(KnowledgeWriteStatus.Updated, superseded.Status);
        Assert.Equal(KnowledgeWriteStatus.ReplacementCycle, cycle.Status);
        Assert.Equal(second.RecordId, superseded.Record!.SupersededByRecordId);

        var archived = await database.Provider.UpdateAsync(
            DecisionUpdate(second, KnowledgeRecordState.Archived, null),
            "decision-archive");
        var approvalWhileArchived = await database.Provider.ApproveDecisionAsync(
            new KnowledgeDecisionApproval("project-a", second.RecordId, 2, Actor, "must not unarchive"),
            "decision-approval-archived");
        Assert.Equal(KnowledgeRecordState.Archived, archived.Record!.State);
        Assert.Equal(KnowledgeWriteStatus.InvalidState, approvalWhileArchived.Status);

        var restoreInput = new KnowledgeRecordRestore("project-a", second.RecordId, 2, 1, Actor, "restore");
        var restored = await database.Provider.RestoreAsync(restoreInput, "decision-restore");
        var restarted = new NativePostgresMemoryProvider(database.DataSource, database.Options);
        var duplicateRestore = await restarted.RestoreAsync(restoreInput, "decision-restore");
        Assert.Equal(KnowledgeRecordState.Active, restored.Record!.State);
        Assert.Equal(KnowledgeTrustState.Pending, restored.Record.TrustState);
        Assert.True(duplicateRestore.IsDuplicate);
        Assert.Equal(restored.Record.RevisionId, duplicateRestore.Record!.RevisionId);

        var approved = await restarted.ApproveDecisionAsync(
            new KnowledgeDecisionApproval("project-a", second.RecordId, 3, Actor, "approve restored"),
            "decision-approve-restored");
        Assert.Equal(KnowledgeTrustState.Approved, approved.Record!.TrustState);
        var staleRestore = await restarted.RestoreAsync(
            restoreInput with { ExpectedRevision = 2 }, "decision-restore-stale");
        Assert.Equal(KnowledgeWriteStatus.Stale, staleRestore.Status);
        Assert.Equal(4, staleRestore.CurrentRevision);

        var history = await restarted.ReadRevisionsAsync("project-a", second.RecordId, 1, 10);
        Assert.Equal([4, 3, 2, 1], history.Items.Select(item => item.Revision));
        Assert.Equal("decision_approved", history.Items[0].ChangeKind);
        Assert.Equal("decision_restored", history.Items[1].ChangeKind);
        Assert.Equal("decision_archived", history.Items[2].ChangeKind);
        Assert.All(history.Items, revision => Assert.Equal(Actor, revision.ActorFingerprint));
    }

    [Fact]
    public async Task VersionedTransferPreservesExactAgentHistoryAndImportReplay()
    {
        await using var source = await KnowledgeDatabase.CreateAsync(postgres);
        var memory = await source.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "before"),
            "transfer-memory");
        var updatedMemory = await source.Provider.UpdateAsync(
            Update(memory.Record!, 1, "after"), "transfer-memory-update");
        var proposal = await source.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architecture", "decision",
                sourceRunId: "run-a"),
            "transfer-decision-proposal");
        var firstPromotion = await source.Provider.PromoteProposalAsync(
            "project-a", "run-a", proposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "transfer-decision-promotion");
        var replacementProposal = await source.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architecture", "replacement",
                sourceRunId: "run-a"),
            "transfer-replacement-proposal");
        var replacementPromotion = await source.Provider.PromoteProposalAsync(
            "project-a", "run-a", replacementProposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "transfer-replacement-promotion");
        var supersededDecision = await source.Provider.UpdateAsync(
            DecisionUpdate(
                firstPromotion.Decision!,
                KnowledgeRecordState.Superseded,
                replacementPromotion.Decision!.RecordId),
            "transfer-decision-supersede");
        var foreignProposal = await source.Provider.CreateAsync(
            Record("project-a", "agent-b", KnowledgeRecordKind.Proposal, "architecture", "foreign",
                sourceRunId: "run-a"),
            "transfer-foreign-proposal");
        await source.Provider.PromoteProposalAsync(
            "project-a", "run-a", foreignProposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "transfer-foreign-promotion");

        var bundle = await source.Provider.ExportAsync("project-a", "agent-a");
        Assert.Equal(KnowledgeRecordTransferContract.Format, bundle.Format);
        Assert.Equal(3, bundle.Records.Length);
        Assert.All(bundle.Records, entry => Assert.Equal("agent-a", entry.Record.AgentId));
        Assert.Equal(KnowledgeRecordState.Superseded, supersededDecision.Record!.State);

        await using var destination = await KnowledgeDatabase.CreateAsync(postgres);
        var imported = await destination.Provider.ImportAsync(
            bundle, "run-import", Actor, "transfer-import");
        var restarted = new NativePostgresMemoryProvider(destination.DataSource, destination.Options);
        var duplicate = await restarted.ImportAsync(
            bundle, "run-import", Actor, "transfer-import");
        Assert.True(duplicate.IsDuplicate);
        Assert.Equal(imported.Records.Select(record => record.RecordId), duplicate.Records.Select(record => record.RecordId));
        Assert.All(imported.Records, record =>
        {
            Assert.Equal(KnowledgeRecordState.Active, record.State);
            Assert.Equal(KnowledgeTrustState.Pending, record.TrustState);
            Assert.Null(record.SupersededByRecordId);
        });

        var importedMemory = imported.Records.Single(record => record.Kind == KnowledgeRecordKind.Memory);
        Assert.Equal(updatedMemory.Record!.Content, importedMemory.Content);
        Assert.Equal(KnowledgeTrustState.Pending, importedMemory.TrustState);
        Assert.Equal("run-import", importedMemory.SourceRunId);
        var importedHistory = await restarted.ReadRevisionsAsync(
            "project-a", importedMemory.RecordId, 1, 10);
        Assert.Equal(3, importedHistory.TotalCount);
        Assert.Equal("imported", importedHistory.Items[0].ChangeKind);
        Assert.Equal(Actor, importedHistory.Items[0].ActorFingerprint);
        Assert.Equal(
            bundle.Records.Single(entry => entry.Record.Kind == KnowledgeRecordKind.Memory)
                .Revisions.Select(revision => revision.RevisionId),
            importedHistory.Items
                .Where(revision => revision.ChangeKind != "imported")
                .OrderBy(revision => revision.Revision)
                .Select(revision => revision.RevisionId));

        var importedDecision = imported.Records.Single(record =>
            record.RecordId == firstPromotion.Decision!.RecordId);
        var importedDecisionHistory = await restarted.ReadRevisionsAsync(
            "project-a", importedDecision.RecordId, 1, 10);
        Assert.Contains(importedDecisionHistory.Items, revision =>
            revision.ChangeKind == "decision_superseded" &&
            revision.SupersededByRecordId == replacementPromotion.Decision!.RecordId);
        var reexport = await restarted.ExportAsync("project-a", "agent-a");
        Assert.Equal(3, reexport.Records.Length);
        var collision = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            restarted.ImportAsync(bundle, "run-import", Actor, "transfer-different-key"));
        Assert.Equal("knowledge_transfer_conflict", collision.Code);
    }

    [Fact]
    public async Task TransferRejectsSupersessionTargetsFromAnotherAgent()
    {
        await using var source = await KnowledgeDatabase.CreateAsync(postgres);
        var proposal = await source.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architecture", "source",
                sourceRunId: "run-a"),
            "foreign-reference-source");
        await source.Provider.PromoteProposalAsync(
            "project-a", "run-a", proposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "foreign-reference-source-promotion");
        var bundle = await source.Provider.ExportAsync("project-a", "agent-a");
        var original = Assert.Single(bundle.Records);

        await using var destination = await KnowledgeDatabase.CreateAsync(postgres);
        var foreignProposal = await destination.Provider.CreateAsync(
            Record("project-a", "agent-b", KnowledgeRecordKind.Proposal, "architecture", "foreign target",
                sourceRunId: "run-a"),
            "foreign-reference-target");
        var foreignDecision = await destination.Provider.PromoteProposalAsync(
            "project-a", "run-a", foreignProposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "foreign-reference-target-promotion");
        var foreignEntry = SupersedeTransferEntry(original, foreignDecision.Decision!.RecordId);
        var foreignBundle = bundle with { Records = bundle.Records.Replace(original, foreignEntry) };

        var exception = await Assert.ThrowsAsync<KnowledgeApiException>(() =>
            destination.Provider.ImportAsync(foreignBundle, "run-import", Actor, "foreign-reference-import"));

        Assert.Equal("invalid_replacement", exception.Code);
        Assert.Equal(StatusCodes.Status409Conflict, exception.StatusCode);
    }

    [Fact]
    public async Task DecisionSupersessionRejectsForeignAgentTargetsThroughoutTheChain()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);

        async Task<KnowledgeRecord> CreateDecisionInAgentAsync(string agentId, string key)
        {
            var proposal = await database.Provider.CreateAsync(
                Record("project-a", agentId, KnowledgeRecordKind.Proposal, "architecture", key,
                    sourceRunId: "run-a"),
                $"create-{key}");
            var promoted = await database.Provider.PromoteProposalAsync(
                "project-a", "run-a", proposal.Record!.RecordId, 1, Actor,
                Authorization("project-a", "run-a"), $"promote-{key}");
            return promoted.Decision!;
        }

        var source = await CreateDecisionInAgentAsync("agent-a", "foreign-direct-source");
        var foreignTarget = await CreateDecisionInAgentAsync("agent-b", "foreign-direct-target");
        var direct = await database.Provider.UpdateAsync(
            DecisionUpdate(source, KnowledgeRecordState.Superseded, foreignTarget.RecordId),
            "foreign-direct-replacement");
        Assert.Equal(KnowledgeWriteStatus.InvalidReplacement, direct.Status);

        var transitiveSource = await CreateDecisionInAgentAsync("agent-a", "foreign-chain-source");
        var sameAgentTarget = await CreateDecisionInAgentAsync("agent-a", "foreign-chain-target");
        var foreignDownstream = await CreateDecisionInAgentAsync("agent-b", "foreign-chain-downstream");
        await using (var connection = await postgres.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            UPDATE "{database.Options.Schema}".knowledge_records
            SET state = 'Superseded', superseded_by_record_id = @replacement
            WHERE project_id = @project AND record_id = @record
            """, connection))
        {
            command.Parameters.AddWithValue("replacement", foreignDownstream.RecordId);
            command.Parameters.AddWithValue("project", "project-a");
            command.Parameters.AddWithValue("record", sameAgentTarget.RecordId);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var transitive = await database.Provider.UpdateAsync(
            DecisionUpdate(transitiveSource, KnowledgeRecordState.Superseded, sameAgentTarget.RecordId),
            "foreign-transitive-replacement");

        Assert.Equal(KnowledgeWriteStatus.InvalidReplacement, transitive.Status);
        Assert.Equal(KnowledgeRecordState.Active,
            (await database.Provider.ReadAsync("project-a", transitiveSource.RecordId))!.State);
    }

    [Fact]
    public async Task ConcurrentDecisionUpdateAndImportAcquireGraphBeforeIdempotencyReceipt()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var proposal = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architecture", "lock order",
                sourceRunId: "run-a"),
            "lock-order-proposal");
        var promotion = await database.Provider.PromoteProposalAsync(
            "project-a", "run-a", proposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "lock-order-promotion");
        var decision = promotion.Decision!;
        var bundle = await database.Provider.ExportAsync("project-a", "agent-a");
        const long pauseLockId = 1981040611;
        const string sharedKey = "decision-graph-ordering";

        await using (var setup = await postgres.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            CREATE FUNCTION "{database.Options.Schema}".pause_knowledge_write_receipt()
            RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.idempotency_key = '{sharedKey}' THEN
                    PERFORM pg_advisory_xact_lock({pauseLockId}::bigint);
                END IF;
                RETURN NEW;
            END
            $body$;
            CREATE TRIGGER pause_knowledge_write_receipt
            AFTER INSERT ON "{database.Options.Schema}".knowledge_write_idempotency
            FOR EACH ROW EXECUTE FUNCTION "{database.Options.Schema}".pause_knowledge_write_receipt();
            """, setup))
            await command.ExecuteNonQueryAsync();

        await using var blocker = await postgres.DataSource.OpenConnectionAsync();
        await using (var command = new NpgsqlCommand(
                         "SELECT pg_advisory_lock(@lock_id)", blocker))
        {
            command.Parameters.AddWithValue("lock_id", NpgsqlTypes.NpgsqlDbType.Bigint, pauseLockId);
            await command.ExecuteNonQueryAsync();
        }

        Task<KnowledgeRecordWriteResult>? updateTask = null;
        Task<KnowledgeRecordImportResult>? importTask = null;
        var blockerReleased = false;
        try
        {
            updateTask = database.Provider.UpdateAsync(
                DecisionUpdate(decision, KnowledgeRecordState.Archived, null),
                sharedKey);
            await WaitForDecisionWriteLockWaitersAsync(postgres.DataSource, minimumWaiters: 1);
            importTask = database.Provider.ImportAsync(
                bundle, "run-import", Actor, sharedKey);
            await WaitForDecisionWriteLockWaitersAsync(postgres.DataSource, minimumWaiters: 2);

            await using (var command = new NpgsqlCommand(
                             "SELECT pg_advisory_unlock(@lock_id)", blocker))
            {
                command.Parameters.AddWithValue("lock_id", NpgsqlTypes.NpgsqlDbType.Bigint, pauseLockId);
                Assert.True((bool)(await command.ExecuteScalarAsync())!);
            }
            blockerReleased = true;

            var updated = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(KnowledgeWriteStatus.Updated, updated.Status);
            var conflict = await Assert.ThrowsAsync<KnowledgeApiException>(async () =>
                await importTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("idempotency_conflict", conflict.Code);
            Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
        }
        finally
        {
            if (!blockerReleased)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(@lock_id)", blocker);
                command.Parameters.AddWithValue("lock_id", NpgsqlTypes.NpgsqlDbType.Bigint, pauseLockId);
                await command.ExecuteScalarAsync();
            }

            try
            {
                if (updateTask is { IsCompleted: false })
                    await updateTask.WaitAsync(TimeSpan.FromSeconds(10));
                if (importTask is { IsCompleted: false })
                    await importTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                await using var cleanup = await postgres.DataSource.OpenConnectionAsync();
                await using var command = new NpgsqlCommand($"""
                    DROP TRIGGER IF EXISTS pause_knowledge_write_receipt
                        ON "{database.Options.Schema}".knowledge_write_idempotency;
                    DROP FUNCTION IF EXISTS "{database.Options.Schema}".pause_knowledge_write_receipt();
                    """, cleanup);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    [Fact]
    public async Task ProposalPromotionAndRejectionPersistExpectedRecordsAndAcceptedOutboxEvent()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var accepted = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architectural", "accept",
                sourceRunId: "run-a"),
            "proposal-1");
        var rejected = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "scope", "reject",
                sourceRunId: "run-a"),
            "proposal-2");

        var rejection = await database.Provider.RejectProposalAsync(
            "project-a", "run-a", rejected.Record!.RecordId, 1, Actor, "reject-1");
        var promotion = await database.Provider.PromoteProposalAsync(
            "project-a", "run-a", accepted.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "promote-1");

        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT event_type, payload->>'effectId', payload->>'recordVersion', payload::text FROM \"{database.Options.Schema}\".outbox_events",
            connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(KnowledgeRecordState.Rejected, rejection.Record!.State);
        Assert.Equal(KnowledgeWriteStatus.Updated, promotion.Status);
        Assert.NotNull(promotion.OutboxEventId);
        Assert.Equal(KnowledgeRecordState.Promoted, promotion.Proposal!.State);
        Assert.Equal(KnowledgeRecordKind.Decision, promotion.Decision!.Kind);
        Assert.Equal("knowledge.accepted-effect", reader.GetString(0));
        Assert.Equal(promotion.Decision.RecordId.ToString(), reader.GetString(1));
        Assert.Equal("1", reader.GetString(2));
        Assert.DoesNotContain("decision_type", reader.GetString(3), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("content", reader.GetString(3), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PromotionRollsBackRecordsRevisionAndOutboxWhenReceiptInsertFails()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var proposal = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architectural", "accept",
                sourceRunId: "run-a"),
            "boundary-proposal");
        await using (var connection = await postgres.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            CREATE OR REPLACE FUNCTION "{database.Options.Schema}".reject_accepted_effect_insert()
            RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                RAISE EXCEPTION 'blocked accepted-effect insert for transaction test';
                RETURN NEW;
            END
            $body$;
            CREATE TRIGGER reject_accepted_effect_insert
            BEFORE INSERT ON "{database.Options.Schema}".outbox_events
            FOR EACH ROW WHEN (NEW.event_type = 'knowledge.accepted-effect')
            EXECUTE FUNCTION "{database.Options.Schema}".reject_accepted_effect_insert();
            """, connection))
            await command.ExecuteNonQueryAsync();

        await Assert.ThrowsAsync<KnowledgeStorageUnavailableException>(() =>
            database.Provider.PromoteProposalAsync(
                "project-a",
                "run-a",
                proposal.Record!.RecordId,
                1,
                Actor,
                Authorization("project-a", "run-a"),
                "boundary-promotion"));

        var unchanged = await database.Provider.ReadAsync("project-a", proposal.Record!.RecordId);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged.Revision);
        Assert.Equal(KnowledgeRecordState.Pending, unchanged.State);
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            SELECT
                (SELECT count(*) FROM "{database.Options.Schema}".knowledge_records
                    WHERE project_id = 'project-a' AND kind = 'Decision'),
                (SELECT count(*) FROM "{database.Options.Schema}".knowledge_revisions
                    WHERE record_id = @proposal AND change_kind = 'proposal_promoted'),
                (SELECT count(*) FROM "{database.Options.Schema}".outbox_events
                    WHERE event_type = 'knowledge.accepted-effect'),
                (SELECT count(*) FROM "{database.Options.Schema}".knowledge_write_idempotency
                    WHERE idempotency_key = 'boundary-promotion'),
                (SELECT count(*) FROM "{database.Options.Schema}".outbox_streams
                    WHERE stream_id = 'knowledge/project-a/run-a')
            """, connection))
        {
            command.Parameters.AddWithValue("proposal", proposal.Record.RecordId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.All(Enumerable.Range(0, 5), index => Assert.Equal(0L, reader.GetInt64(index)));
        }

        await using (var connection = await postgres.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            DROP TRIGGER reject_accepted_effect_insert ON "{database.Options.Schema}".outbox_events;
            DROP FUNCTION "{database.Options.Schema}".reject_accepted_effect_insert();
            """, connection))
            await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ContextCandidatesAreOrderedAndFilteredByTrustAgentAndRun()
    {
        await using var database = await KnowledgeDatabase.CreateAsync(postgres);
        var proposal = await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Proposal, "architectural", "decision",
                sourceRunId: "run-a"),
            "context-1");
        await database.Provider.PromoteProposalAsync(
            "project-a", "run-a", proposal.Record!.RecordId, 1, Actor,
            Authorization("project-a", "run-a"), "context-promote");
        await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.Memory, "note", "own-memory"),
            "context-2");
        var privateMemory = await database.Provider.CreateAsync(
            Record("project-a", "agent-b", KnowledgeRecordKind.Memory, "note", "private-memory"),
            "context-3");
        var crossTeamMemory = await database.Provider.CreateAsync(
            Record("project-a", "agent-b", KnowledgeRecordKind.Memory, "note", "cross-team-memory"),
            "context-3-shared");
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var approve = new NpgsqlCommand($"""
            UPDATE "{database.Options.Schema}".knowledge_records
            SET trust_state = 'Approved'
            WHERE project_id = @project AND record_id = @record
            """, connection))
        {
            approve.Parameters.AddWithValue("project", "project-a");
            approve.Parameters.AddWithValue("record", crossTeamMemory.Record!.RecordId);
            Assert.Equal(1, await approve.ExecuteNonQueryAsync());
        }
        await using (var connection = await database.DataSource.OpenConnectionAsync())
        await using (var tag = new NpgsqlCommand($"""
            UPDATE "{database.Options.Schema}".knowledge_records
            SET tags = @tags
            WHERE project_id = @project AND record_id = @record
            """, connection))
        {
            tag.Parameters.AddWithValue("tags", new[] { "Cross-Team" });
            tag.Parameters.AddWithValue("project", "project-a");
            tag.Parameters.AddWithValue("record", crossTeamMemory.Record.RecordId);
            Assert.Equal(1, await tag.ExecuteNonQueryAsync());
        }
        await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.SessionContext, "session", "current-run",
                sourceRunId: "run-a"),
            "context-4");
        await database.Provider.CreateAsync(
            Record("project-a", "agent-a", KnowledgeRecordKind.SessionContext, "session", "other-run",
                sourceRunId: "run-b"),
            "context-5");

        var candidates = await database.Provider.ReadContextCandidatesAsync(
            "project-a", "agent-a", "run-a", 10);

        Assert.Equal(
            [
                KnowledgeRecordKind.Decision,
                KnowledgeRecordKind.Memory,
                KnowledgeRecordKind.Memory,
                KnowledgeRecordKind.SessionContext
            ],
            candidates.Records.Select(record => record.Kind));
        Assert.Contains(candidates.Records, record => record.Content == "own-memory");
        Assert.Contains(candidates.Records, record => record.Content == "cross-team-memory");
        Assert.DoesNotContain(candidates.Records, record =>
            record.RecordId == privateMemory.Record!.RecordId || record.Content == "other-run");
        Assert.All(candidates.Records, record => Assert.NotEqual(KnowledgeRecordKind.Proposal, record.Kind));
    }

    private static async Task WaitForDecisionWriteLockWaitersAsync(
        NpgsqlDataSource dataSource,
        int minimumWaiters)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            await using var connection = await dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("""
                SELECT count(*)
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND (query LIKE '%knowledge_write_idempotency%'
                       OR query LIKE '%agentweaver.knowledge.decision-graph%')
                """, connection);
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (count >= minimumWaiters)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }
        throw new TimeoutException(
            $"PostgreSQL did not expose {minimumWaiters} blocked decision-write operations in time.");
    }

    private const string Actor = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static KnowledgeRecordCreate Record(
        string projectId,
        string agentId,
        KnowledgeRecordKind kind,
        string type,
        string content,
        string? sourceRunId = null) =>
        new(
            projectId, agentId, kind, type, kind == KnowledgeRecordKind.Proposal ? "proposal" : null,
            content, null, "medium", ["knowledge"], sourceRunId, null, Actor, "created");

    private static KnowledgeRecordUpdate Update(
        KnowledgeRecord record,
        int expectedRevision,
        string content) =>
        new(
            record.ProjectId, record.RecordId, expectedRevision, record.Type, record.Title, content,
            record.Rationale, record.Importance, record.Tags, KnowledgeRecordState.Active, Actor, "updated");

    private static KnowledgeRecordUpdate DecisionUpdate(
        KnowledgeRecord record,
        KnowledgeRecordState state,
        Guid? supersededByRecordId) =>
        new(
            record.ProjectId,
            record.RecordId,
            record.Revision,
            record.Type,
            record.Title,
            record.Content,
            record.Rationale,
            record.Importance,
            record.Tags,
            state,
            Actor,
            state == KnowledgeRecordState.Superseded ? "superseded" : state.ToString(),
            supersededByRecordId);

    private static KnowledgeRecordTransferEntry SupersedeTransferEntry(
        KnowledgeRecordTransferEntry entry,
        Guid replacementId)
    {
        var previous = entry.Revisions[^1];
        var revision = previous with
        {
            Revision = previous.Revision + 1,
            RevisionId = Guid.NewGuid(),
            PreviousRevisionId = previous.RevisionId,
            State = KnowledgeRecordState.Superseded,
            SupersededByRecordId = replacementId,
            ChangeKind = "decision_superseded",
            Reason = "superseded"
        };
        return entry with
        {
            Record = entry.Record with
            {
                Revision = revision.Revision,
                PreviousRevisionId = previous.RevisionId,
                RevisionId = revision.RevisionId,
                State = KnowledgeRecordState.Superseded,
                SupersededByRecordId = replacementId
            },
            Revisions = entry.Revisions.Add(revision)
        };
    }

    private static AcceptedEffectAuthorizationBounds Authorization(string projectId, string runId) =>
        new(
            "https://identity.test/",
            "actor-a",
            "tenant-a",
            projectId,
            runId,
            ProjectAuthorityResourceType.Project,
            projectId,
            1,
            1,
            1,
            1,
            "context-v1");

    internal sealed class KnowledgeDatabase(
        NativePostgresMemoryOptions options,
        NpgsqlDataSource dataSource,
        NativePostgresMemoryProvider provider) : IAsyncDisposable
    {
        public NativePostgresMemoryOptions Options { get; } = options;
        public NpgsqlDataSource DataSource { get; } = dataSource;
        public NativePostgresMemoryProvider Provider { get; } = provider;

        public static async Task<KnowledgeDatabase> CreateAsync(KnowledgePostgresFixture fixture)
        {
            var schema = $"knowledge_{Guid.NewGuid():N}";
            var role = $"knowledge_runtime_{Guid.NewGuid():N}";
            const string password = "knowledge-test-password";
            var options = new NativePostgresMemoryOptions(
                "postgres-resource", fixture.DatabaseName, 1, schema, "options-v1", 1);
            await KnowledgeMigrator.MigrateAsync(fixture.DataSource, schema);
            await using var adminConnection = await fixture.DataSource.OpenConnectionAsync();
            await using (var grants = new NpgsqlCommand($"""
                CREATE ROLE "{role}" LOGIN PASSWORD '{password}';
                GRANT USAGE ON SCHEMA "{schema}" TO "{role}";
                GRANT SELECT, INSERT, UPDATE ON "{schema}".knowledge_records TO "{role}";
                GRANT SELECT, INSERT ON "{schema}".knowledge_revisions TO "{role}";
                GRANT SELECT, INSERT ON "{schema}".memory_provider_bindings TO "{role}";
                GRANT SELECT, INSERT, UPDATE ON "{schema}".knowledge_write_idempotency TO "{role}";
                GRANT SELECT, INSERT, UPDATE ON "{schema}".outbox_streams TO "{role}";
                GRANT SELECT, INSERT, UPDATE ON "{schema}".outbox_events TO "{role}";
                GRANT SELECT ON "{schema}".outbox_schema_migrations TO "{role}";
                GRANT SELECT ON "{schema}".knowledge_schema_migrations TO "{role}";
                """, adminConnection))
                await grants.ExecuteNonQueryAsync();
            var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Username = role,
                Password = password
            };
            var dataSource = NpgsqlDataSource.Create(connection.ConnectionString);
            return new KnowledgeDatabase(
                options,
                dataSource,
                new NativePostgresMemoryProvider(dataSource, options));
        }

        public async ValueTask DisposeAsync() => await DataSource.DisposeAsync();
    }
}
