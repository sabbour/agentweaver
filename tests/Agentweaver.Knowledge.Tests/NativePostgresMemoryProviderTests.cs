using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Knowledge;
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
