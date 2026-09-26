using Agentweaver.Api.Memory;
using Agentweaver.Api.Tools;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Memory;

public sealed class KnowledgeRevisionTests
{
    [Fact]
    public async Task MemoryUpdate_RequiresCurrentRevision_AndKeepsImmutableHistory()
    {
        await using var fixture = await RevisionFixture.CreateAsync();
        var memory = await fixture.CreateMemoryAsync("original");

        var updated = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            fixture.Db,
            memory.Id,
            memory.Revision,
            "learning",
            "high",
            "updated",
            ",searchable,",
            KnowledgeLifecycleStates.Active,
            null,
            "tester",
            "corrected guidance",
            CancellationToken.None);

        updated.Status.Should().Be(KnowledgeWriteStatus.Updated);
        updated.Record!.Revision.Should().Be(2);

        var stale = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            fixture.Db,
            memory.Id,
            memory.Revision,
            "learning",
            "low",
            "stale overwrite",
            null,
            KnowledgeLifecycleStates.Active,
            null,
            "tester",
            "stale edit",
            CancellationToken.None);

        stale.Status.Should().Be(KnowledgeWriteStatus.Stale);
        stale.CurrentRevision.Should().Be(2);

        var revisions = await fixture.Db.AgentMemoryRevisions
            .AsNoTracking()
            .OrderBy(r => r.Revision)
            .ToListAsync();
        revisions.Select(r => r.Content).Should().Equal("original", "updated");
        revisions[1].PreviousRevisionId.Should().Be(revisions[0].RevisionId);

        revisions[0].Content = "tampered";
        fixture.Db.AgentMemoryRevisions.Update(revisions[0]);
        var save = () => fixture.Db.SaveChangesAsync();
        await save.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Knowledge revisions are immutable.");
    }

    [Fact]
    public async Task Restore_CreatesNewPendingRevision_WithoutChangingHistoricalRevision()
    {
        await using var fixture = await RevisionFixture.CreateAsync();
        var memory = await fixture.CreateMemoryAsync("original", approved: true);
        var update = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            fixture.Db,
            memory.Id,
            1,
            memory.Type,
            memory.Importance,
            "newer",
            memory.Tags,
            KnowledgeLifecycleStates.Active,
            null,
            "tester",
            "new information",
            CancellationToken.None);

        var restored = await KnowledgeRevisionWriter.RestoreMemoryAsync(
            fixture.Db,
            memory.Id,
            update.Record!.Revision,
            revisionToRestore: 1,
            "tester",
            "restore proven guidance",
            CancellationToken.None);

        restored.Status.Should().Be(KnowledgeWriteStatus.Updated);
        restored.Record!.Revision.Should().Be(3);
        restored.Record.Content.Should().Be("original");
        restored.Record.TrustState.Should().Be(MemoryTrustStates.Pending);
        restored.Record.ApprovedBy.Should().BeNull();

        var revisions = await fixture.Db.AgentMemoryRevisions
            .AsNoTracking()
            .OrderBy(r => r.Revision)
            .ToListAsync();
        revisions.Select(r => r.Content).Should().Equal("original", "newer", "original");
        revisions[0].TrustState.Should().Be(MemoryTrustStates.Approved);
    }

    [Fact]
    public async Task Replacement_MustStayInProject_AndCannotCreateCycle()
    {
        await using var fixture = await RevisionFixture.CreateAsync();
        var first = await fixture.CreateMemoryAsync("first");
        var second = await fixture.CreateMemoryAsync("second");
        var foreign = await fixture.CreateMemoryAsync("foreign", projectId: "other-project");

        var crossProject = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            fixture.Db, first.Id, first.Revision, first.Type, first.Importance, first.Content,
            first.Tags, KnowledgeLifecycleStates.Superseded, foreign.Id, "tester", "replace",
            CancellationToken.None);
        crossProject.Status.Should().Be(KnowledgeWriteStatus.InvalidReplacement);

        var replaced = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            fixture.Db, first.Id, first.Revision, first.Type, first.Importance, first.Content,
            first.Tags, KnowledgeLifecycleStates.Superseded, second.Id, "tester", "replace",
            CancellationToken.None);
        replaced.Status.Should().Be(KnowledgeWriteStatus.Updated);

        var cycle = await KnowledgeRevisionWriter.UpdateMemoryAsync(
            fixture.Db, second.Id, second.Revision, second.Type, second.Importance, second.Content,
            second.Tags, KnowledgeLifecycleStates.Superseded, first.Id, "tester", "cycle",
            CancellationToken.None);
        cycle.Status.Should().Be(KnowledgeWriteStatus.ReplacementCycle);
    }

    [Fact]
    public async Task ConcurrentReciprocalReplacements_CannotCreateCycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentweaver-revisions-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            int firstId;
            int secondId;
            await using (var seed = new MemoryDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var first = RevisionFixture.NewMemory("cycle-project", "first");
                var second = RevisionFixture.NewMemory("cycle-project", "second");
                seed.AgentMemory.AddRange(first, second);
                await seed.SaveChangesAsync();
                firstId = first.Id;
                secondId = second.Id;
            }

            await using (var firstDb = new MemoryDbContext(options))
            await using (var secondDb = new MemoryDbContext(options))
            {
                var results = await Task.WhenAll(
                    KnowledgeRevisionWriter.UpdateMemoryAsync(
                        firstDb, firstId, 1, "learning", "high", "first", null,
                        KnowledgeLifecycleStates.Superseded, secondId, "tester", "replace", CancellationToken.None),
                    KnowledgeRevisionWriter.UpdateMemoryAsync(
                        secondDb, secondId, 1, "learning", "high", "second", null,
                        KnowledgeLifecycleStates.Superseded, firstId, "tester", "replace", CancellationToken.None));

                results.Count(result => result.Status == KnowledgeWriteStatus.Updated).Should().Be(1);
                results.Count(result => result.Status == KnowledgeWriteStatus.ReplacementCycle).Should().Be(1);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConcurrentReciprocalDecisionSupersessions_CannotCreateCycle()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentweaver-decision-revisions-{Guid.NewGuid():N}.db");
        try
        {
            var options = new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            int firstId;
            int secondId;
            await using (var seed = new MemoryDbContext(options))
            {
                await seed.Database.EnsureCreatedAsync();
                var first = RevisionFixture.NewDecision("cycle-project", "first");
                var second = RevisionFixture.NewDecision("cycle-project", "second");
                seed.Decisions.AddRange(first, second);
                await seed.SaveChangesAsync();
                firstId = first.Id;
                secondId = second.Id;
            }

            await using (var firstDb = new MemoryDbContext(options))
            await using (var secondDb = new MemoryDbContext(options))
            {
                var results = await Task.WhenAll(
                    KnowledgeRevisionWriter.UpdateDecisionAsync(
                        firstDb, firstId, 1, "first", null, KnowledgeLifecycleStates.Superseded,
                        secondId, "tester", "replace", null, CancellationToken.None),
                    KnowledgeRevisionWriter.UpdateDecisionAsync(
                        secondDb, secondId, 1, "second", null, KnowledgeLifecycleStates.Superseded,
                        firstId, "tester", "replace", null, CancellationToken.None));

                results.Count(result => result.Status == KnowledgeWriteStatus.Updated).Should().Be(1);
                results.Count(result => result.Status == KnowledgeWriteStatus.ReplacementCycle).Should().Be(1);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SqliteMigration_SeedsRevisionOneForLegacyRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Agentweaver.Api"))
            .Options;
        await using var db = new MemoryDbContext(options);
        await db.Database.MigrateAsync("20260926000737_PersistWorkflowFanContext");
        var now = DateTimeOffset.UtcNow.ToString("O");
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AgentMemory"
                ("ProjectId","AgentName","Type","Importance","Content","SourceKind","SourceIdentity","TrustState","ApprovedBy","ApprovedAt","CreatedAt","UpdatedAt")
            VALUES ('legacy-project','smith','learning','high','legacy memory','legacy','legacy-user','approved','legacy-owner',{now},{now},{now});
            INSERT INTO "Decisions"
                ("ProjectId","AgentName","Type","Status","Title","Content","SourceKind","SourceIdentity","TrustState","ApprovedBy","ApprovedAt","CreatedAt","UpdatedAt")
            VALUES ('legacy-project','coordinator','architectural','active','Legacy','legacy decision','legacy','legacy-user','approved','legacy-owner',{now},{now},{now});
            """);

        await db.Database.MigrateAsync();
        await KnowledgeRevisionBackfill.EnsureLegacyFingerprintsAsync(db);

        var memory = await db.AgentMemory.AsNoTracking().SingleAsync();
        memory.Revision.Should().Be(1);
        memory.CurrentRevisionId.Should().NotBeNullOrWhiteSpace();
        var memoryRevision = await db.AgentMemoryRevisions.AsNoTracking().SingleAsync();
        memoryRevision.Reason.Should().Be("legacy import");
        memoryRevision.SourceIdentityFingerprint.Should().NotBeNullOrWhiteSpace();
        memoryRevision.SourceIdentityFingerprint.Should().NotContain("test-user");
        var decision = await db.Decisions.AsNoTracking().SingleAsync();
        decision.Revision.Should().Be(1);
        decision.CurrentRevisionId.Should().NotBeNullOrWhiteSpace();
        var decisionRevision = await db.DecisionRevisions.AsNoTracking().SingleAsync();
        decisionRevision.Reason.Should().Be("legacy import");
        decisionRevision.SourceIdentityFingerprint.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task LegacySourcePreparation_RepairsMissingRevisionSchemaDespiteMigrationHistory()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("Agentweaver.Api"))
            .Options;
        await using var db = new MemoryDbContext(options);
        await db.Database.MigrateAsync("20260926000737_PersistWorkflowFanContext");

        var state = await SqliteToPostgresMigrator.PrepareKnowledgeRevisionSourceSchemaAsync(
            db, CancellationToken.None);

        state.MemoryRevisionsPresent.Should().BeFalse();
        state.DecisionRevisionsPresent.Should().BeFalse();
        (await db.AgentMemoryRevisions.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.DecisionRevisions.AsNoTracking().CountAsync()).Should().Be(0);
    }

    private sealed class RevisionFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        public MemoryDbContext Db { get; }

        private RevisionFixture(SqliteConnection connection, MemoryDbContext db)
        {
            _connection = connection;
            Db = db;
        }

        public static async Task<RevisionFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
                .UseSqlite(connection)
                .Options);
            await db.Database.EnsureCreatedAsync();
            return new RevisionFixture(connection, db);
        }

        public async Task<AgentMemory> CreateMemoryAsync(
            string content,
            bool approved = false,
            string projectId = "project-1")
        {
            var now = DateTimeOffset.UtcNow;
            var memory = new AgentMemory
            {
                ProjectId = projectId,
                AgentName = "smith",
                Type = "learning",
                Importance = "medium",
                Content = content,
                TrustState = approved ? MemoryTrustStates.Approved : MemoryTrustStates.Pending,
                ApprovedBy = approved ? "owner@example.test" : null,
                ApprovedAt = approved ? now : null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            Db.AgentMemory.Add(memory);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
            return await Db.AgentMemory.AsNoTracking().SingleAsync(m => m.Id == memory.Id);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _connection.DisposeAsync();
        }

        public static AgentMemory NewMemory(string projectId, string content) =>
            new()
            {
                ProjectId = projectId,
                AgentName = "smith",
                Type = "learning",
                Importance = "high",
                Content = content,
                SourceKind = MemorySourceKinds.Human,
                SourceIdentity = "test-user",
                TrustState = MemoryTrustStates.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

        public static Decision NewDecision(string projectId, string content) =>
            new()
            {
                ProjectId = projectId,
                AgentName = "coordinator",
                Type = "architectural",
                Status = KnowledgeLifecycleStates.Active,
                Title = content,
                Content = content,
                SourceKind = MemorySourceKinds.Human,
                SourceIdentity = "test-user",
                TrustState = MemoryTrustStates.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
    }
}
