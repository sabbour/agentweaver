using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>
/// Integration tests for the EF/Postgres data layer (spec-018 P2).
/// Each test in this class requires a running postgres:16 container via Testcontainers.
/// Run with: dotnet test --filter "Category=PostgresIntegration"
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class MigrationValidityTests(PostgresFixture pg)
{
    // ─────────────────────────────────────────────────────────────────────────
    // 1. MIGRATION: schema applied cleanly, all tables / indexes / triggers exist
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Migration_MigrateAsync_Discovers_InitialPostgres()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddDbContextFactory<MemoryDbContext>(opts =>
            opts.UseNpgsql(pg.ConnectionString,
                n => n.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var migrations = db.Database.GetMigrations().ToList();
        migrations.Should().Contain("20260627000000_InitialPostgres",
            "migration must be discoverable via [DbContext] attribute + MigrationsAssembly config");
    }

    [Fact]
    public async Task Migration_AllExpectedTables_Exist()
    {
        var expectedTables = new[]
        {
            "runs", "run_revisions", "projects", "backlog_tasks",
            "workflow_runs", "cast_proposals",
            "\"RunEvents\"", "\"AgentMemory\"", "\"Decisions\"",
        };

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();

        foreach (var table in expectedTables)
        {
            var tableName = table.Trim('"');
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public' AND table_name=$1;";
            cmd.Parameters.AddWithValue(tableName);
            var count = (long)(await cmd.ExecuteScalarAsync())!;
            count.Should().Be(1, $"table '{tableName}' must exist after migration");
        }
    }

    [Fact]
    public async Task Migration_AppendOnlyTriggers_ExistOnRunRevisions()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT trigger_name FROM information_schema.triggers WHERE event_object_table = 'run_revisions' ORDER BY trigger_name;";
        await using var reader = await cmd.ExecuteReaderAsync();

        var triggers = new List<string>();
        while (await reader.ReadAsync())
            triggers.Add(reader.GetString(0));

        triggers.Should().Contain("trg_run_revisions_no_delete",
            "DELETE trigger must exist on run_revisions");
        triggers.Should().Contain("trg_run_revisions_no_update",
            "UPDATE trigger must exist on run_revisions");
    }

    [Fact]
    public async Task Migration_PartialUniqueIndex_ExistsOnBacklogTasks()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT indexname FROM pg_indexes
            WHERE tablename = 'backlog_tasks' AND indexname = 'IX_backlog_tasks_orderkey_unique';
            """;
        var result = await cmd.ExecuteScalarAsync();
        result.Should().NotBeNull("partial unique index IX_backlog_tasks_orderkey_unique must exist");
    }

    /// <summary>
    /// Two parallel workers race to claim the same run — exactly one must win.
    /// </summary>
    [Fact]
    public async Task Lease_ConcurrentClaim_ExactlyOneWins()
    {
        var runId = "run-lease-test-" + Guid.NewGuid().ToString("N")[..8];
        await using var db = await pg.CreateDbContextAsync();
        db.Runs.Add(new Agentweaver.Api.Memory.RunRecord
        {
            RunId = runId,
            RepositoryPath = "/r",
            OriginatingBranch = "main",
            ModelSource = "github_copilot",
            Task = "t",
            SubmittingUser = "u",
            Status = "in_progress",
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var store = new PostgresRunLeaseStore(pg.Factory);
        var ttl = TimeSpan.FromSeconds(30);

        var task1 = store.TryClaimAsync(runId, "worker-A", ttl);
        var task2 = store.TryClaimAsync(runId, "worker-B", ttl);
        var results = await Task.WhenAll(task1, task2);

        var winners = results.Count(r => r.Claimed);
        winners.Should().Be(1, "exactly one worker must win the CAS claim");
    }

    /// <summary>
    /// An expired lease is reclaimable by a second worker.
    /// </summary>
    [Fact]
    public async Task Lease_ExpiredLease_IsReclaimable()
    {
        var runId = "run-reclaim-" + Guid.NewGuid().ToString("N")[..8];
        await using var db = await pg.CreateDbContextAsync();
        db.Runs.Add(new Agentweaver.Api.Memory.RunRecord
        {
            RunId = runId,
            RepositoryPath = "/r",
            OriginatingBranch = "main",
            ModelSource = "github_copilot",
            Task = "t",
            SubmittingUser = "u",
            Status = "in_progress",
            StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var store = new PostgresRunLeaseStore(pg.Factory);

        var (claimed1, token1) = await store.TryClaimAsync(runId, "worker-A", TimeSpan.FromSeconds(1));
        claimed1.Should().BeTrue("first claim should succeed");

        await Task.Delay(TimeSpan.FromSeconds(2));

        var (claimed2, token2) = await store.TryClaimAsync(runId, "worker-B", TimeSpan.FromSeconds(30));
        claimed2.Should().BeTrue("worker-B should reclaim the expired lease");
        token2.Should().BeGreaterThan(token1, "fencing token must be strictly increasing");
    }
}

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RunRevisionAppendOnlyTests(PostgresFixture pg)
{
    // ─────────────────────────────────────────────────────────────────────────
    // 2. run_revisions APPEND-ONLY: UPDATE and DELETE must throw (trigger fires)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunRevisions_Insert_Succeeds()
    {
        await using var db = await pg.CreateDbContextAsync();
        var runId = RunId.New().ToString();
        db.RunRevisions.Add(new RunRevisionRecord
        {
            RunId = runId,
            RevisionNumber = 1,
            ReviewerUser = "alice",
            CreatedAt = DateTimeOffset.UtcNow,
            RawComment = "raw comment",
            SanitizedComment = "sanitized comment",
            PreviousTreeHash = "abc123",
        });
        var act = async () => await db.SaveChangesAsync();
        await act.Should().NotThrowAsync("INSERT into run_revisions must succeed");
    }

    [Fact]
    public async Task RunRevisions_Update_ThrowsViaDirectSql()
    {
        // First insert a revision
        var runId = RunId.New().ToString();
        await using var setupDb = await pg.CreateDbContextAsync();
        setupDb.RunRevisions.Add(new RunRevisionRecord
        {
            RunId = runId,
            RevisionNumber = 1,
            ReviewerUser = "alice",
            CreatedAt = DateTimeOffset.UtcNow,
            RawComment = "original",
            SanitizedComment = "original",
            PreviousTreeHash = "hash0",
        });
        await setupDb.SaveChangesAsync();

        // Now try an UPDATE via raw SQL — trigger must fire
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE run_revisions SET reviewer_user = 'bob' WHERE run_id = '{runId}' AND revision_number = 1;";

        var act = async () => await cmd.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only",
                "the trigger must raise EXCEPTION with 'append-only' in the message");
    }

    [Fact]
    public async Task RunRevisions_Delete_ThrowsViaDirectSql()
    {
        var runId = RunId.New().ToString();
        await using var setupDb = await pg.CreateDbContextAsync();
        setupDb.RunRevisions.Add(new RunRevisionRecord
        {
            RunId = runId,
            RevisionNumber = 1,
            ReviewerUser = "alice",
            CreatedAt = DateTimeOffset.UtcNow,
            RawComment = "to delete",
            SanitizedComment = "to delete",
            PreviousTreeHash = "hash0",
        });
        await setupDb.SaveChangesAsync();

        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"DELETE FROM run_revisions WHERE run_id = '{runId}' AND revision_number = 1;";

        var act = async () => await cmd.ExecuteNonQueryAsync();
        (await act.Should().ThrowAsync<PostgresException>())
            .Which.MessageText.Should().Contain("append-only",
                "the trigger must raise EXCEPTION with 'append-only' in the message");
    }

    [Fact]
    public async Task EfRunRevisionStore_GetMaxRevisionNumber_ReturnsZeroWhenEmpty()
    {
        var store = new EfRunRevisionStore(pg.Factory);
        var runId = RunId.New();
        var max = await store.GetMaxRevisionNumberAsync(runId);
        max.Should().Be(0, "max revision number should be 0 when no revisions exist");
    }

    [Fact]
    public async Task EfRunRevisionStore_InsertAndGetMax_Works()
    {
        var store = new EfRunRevisionStore(pg.Factory);
        var runId = RunId.New();
        await store.InsertRevisionAsync(runId, 1, "alice", "raw1", "sanitized1", "hash0");
        await store.InsertRevisionAsync(runId, 2, "alice", "raw2", "sanitized2", "hash1");
        var max = await store.GetMaxRevisionNumberAsync(runId);
        max.Should().Be(2);
    }
}

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EfRunStoreCasTests(PostgresFixture pg)
{
    // ─────────────────────────────────────────────────────────────────────────
    // 3. CAS STATUS TRANSITIONS: stale-status update affects 0 rows
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<RunId> InsertAwaitingReviewRunAsync(EfRunStore store)
    {
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = "/repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);
        await store.UpdateReviewReadyAsync(runId, "tree-hash-1", "diff", 0);
        return runId;
    }

    [Fact]
    public async Task TryStartMerging_ReturnsTrue_OnFirstCall_False_OnSecond()
    {
        var store = new EfRunStore(pg.Factory);
        var runId = await InsertAwaitingReviewRunAsync(store);

        var first = await store.TryStartMergingAsync(runId);
        first.Should().BeTrue("first CAS must win on awaiting_review run");

        var afterFirst = await store.GetAsync(runId);
        afterFirst!.Status.Should().Be(RunStatus.Merging);

        var second = await store.TryStartMergingAsync(runId);
        second.Should().BeFalse("second CAS must lose because run is already merging");

        var afterSecond = await store.GetAsync(runId);
        afterSecond!.Status.Should().Be(RunStatus.Merging,
            "status must remain merging after failed second CAS");
    }

    [Fact]
    public async Task RevertMerging_TransitionsMergingBackToAwaitingReview()
    {
        var store = new EfRunStore(pg.Factory);
        var runId = await InsertAwaitingReviewRunAsync(store);
        await store.TryStartMergingAsync(runId);

        var reverted = await store.RevertMergingAsync(runId);
        reverted.Should().BeTrue();

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.AwaitingReview);
    }

    [Fact]
    public async Task CompleteMerging_TransitionsMergingToMerged_PersistsResult()
    {
        var store = new EfRunStore(pg.Factory);
        var runId = await InsertAwaitingReviewRunAsync(store);
        await store.TryStartMergingAsync(runId);

        var endedAt = DateTimeOffset.UtcNow;
        var result = "merged:abc1234";
        var completed = await store.CompleteMergingAsync(runId, RunStatus.Merged, endedAt, result);
        completed.Should().BeTrue();

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.Merged);
        run.Result.Should().Be(result);
        run.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task TrySetTerminalStatus_OnMergingRun_Succeeds()
    {
        var store = new EfRunStore(pg.Factory);
        var runId = await InsertAwaitingReviewRunAsync(store);
        await store.TryStartMergingAsync(runId);

        var recovered = await store.TrySetTerminalStatusAsync(
            runId, RunStatus.Failed, DateTimeOffset.UtcNow, "send_response_failed");
        recovered.Should().BeTrue("TrySetTerminalStatus must succeed on a non-terminal (Merging) run");

        var run = await store.GetAsync(runId);
        run!.Status.Should().Be(RunStatus.Failed);
        run.Result.Should().Be("send_response_failed");
    }

    [Fact]
    public async Task TrySetTerminalStatus_OnAlreadyTerminalRun_ReturnsFalse()
    {
        var store = new EfRunStore(pg.Factory);
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = "/repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "test",
            SubmittingUser = "alice",
            Status = RunStatus.Merged,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);

        // Attempt to transition an already-terminal run — should affect 0 rows (CAS guard)
        var result = await store.TrySetTerminalStatusAsync(
            runId, RunStatus.Failed, DateTimeOffset.UtcNow, "should-not-overwrite");
        result.Should().BeFalse("CAS must reject a transition on an already-terminal run");

        var after = await store.GetAsync(runId);
        after!.Status.Should().Be(RunStatus.Merged, "terminal status must not be overwritten");
    }

    [Fact]
    public async Task DwellComputation_ReviewWaitMs_IsNonNegative()
    {
        var store = new EfRunStore(pg.Factory);
        var runId = RunId.New();
        var run = new Run
        {
            Id = runId,
            RepositoryPath = "/repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "dwell test",
            SubmittingUser = "alice",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);

        // Set review-ready (records ReviewReadyAt)
        var reviewReadyAt = DateTimeOffset.UtcNow;
        await store.UpdateReviewReadyAsync(runId, "hash", "diff", 0, now: reviewReadyAt);

        // Small delay to ensure measurable dwell
        await Task.Delay(50);

        var approvedAt = DateTimeOffset.UtcNow;
        var transitioned = await store.TryTransitionReviewAsync(
            runId, RunStatus.Merged, approvedAt, "merged:ok");
        transitioned.Should().BeTrue();

        // Re-read — the C# dwell computation (not julianday) must produce a non-negative value
        await using var db = await pg.CreateDbContextAsync();
        var id = runId.ToString();
        var rec = await db.Runs.AsNoTracking().FirstAsync(r => r.RunId == id);
        rec.ReviewWaitMs.Should().BeGreaterThanOrEqualTo(0,
            "dwell computation must produce a non-negative value");
    }
}

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EfBacklogTaskStoreTests(PostgresFixture pg)
{
    // ─────────────────────────────────────────────────────────────────────────
    // 4. PARTIAL UNIQUE INDEX + CONCURRENT CLAIM
    // ─────────────────────────────────────────────────────────────────────────

    private async Task<Project> InsertProjectAsync()
    {
        var proj = MakeProject();
        var store = new EfProjectStore(pg.Factory);
        await store.InsertAsync(proj);
        return proj;
    }

    [Fact]
    public async Task PartialUniqueIndex_DuplicateOrderKey_InSameBucket_IsRejected()
    {
        var project = await InsertProjectAsync();
        var store = new EfBacklogTaskStore(pg.Factory);
        await store.InsertAsync(MakeReadyTask(project.Id, "order-dup-1"));

        var dup = MakeReadyTask(project.Id, "order-dup-1"); // same project + state + key
        var act = async () => await store.InsertAsync(dup);
        await act.Should().ThrowAsync<Exception>(
            "inserting a duplicate order_key in the same (project,state) bucket must violate the partial unique index");
    }

    [Fact]
    public async Task PartialUniqueIndex_SameOrderKey_InDifferentBuckets_IsAllowed()
    {
        var project = await InsertProjectAsync();
        var store = new EfBacklogTaskStore(pg.Factory);

        // One task in 'ready', one in 'claimed' — the partial index only covers backlog/ready
        var readyTask = MakeReadyTask(project.Id, "key-x");
        await store.InsertAsync(readyTask);

        // Claim the ready task to move it to 'claimed'
        var runId = RunId.New();
        var coordinatorRun = MakeCoordinatorRun(project.Id, runId);
        var claimResult = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, readyTask.Id, coordinatorRun, DateTimeOffset.UtcNow);
        claimResult.Should().Be(ClaimReserveResult.Won);

        // Insert a second task with the same key in 'ready' — allowed because the first is now 'claimed'
        var newReadyTask = MakeReadyTask(project.Id, "key-x");
        var act = async () => await store.InsertAsync(newReadyTask);
        await act.Should().NotThrowAsync(
            "same order_key in 'claimed' state is excluded from the partial unique index, so new 'ready' entry is fine");
    }

    [Fact]
    public async Task ConcurrentClaim_ExactlyOneWins()
    {
        var project = await InsertProjectAsync();
        var store = new EfBacklogTaskStore(pg.Factory);
        var task = MakeReadyTask(project.Id, "concurrent-key");
        await store.InsertAsync(task);

        // Launch two concurrent claims for the same task
        var run1Id = RunId.New();
        var run2Id = RunId.New();
        var run1 = MakeCoordinatorRun(project.Id, run1Id);
        var run2 = MakeCoordinatorRun(project.Id, run2Id);

        var t1 = store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id, run1, DateTimeOffset.UtcNow);
        var t2 = store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id, run2, DateTimeOffset.UtcNow);

        var results = await Task.WhenAll(t1, t2);

        results.Should().ContainSingle(r => r == ClaimReserveResult.Won,
            "exactly one concurrent claim must win");
        results.Should().ContainSingle(r => r == ClaimReserveResult.Lost,
            "the other concurrent claim must lose");
    }

    [Fact]
    public async Task ListReadyForClaim_ReturnsByOrderKey_TopN()
    {
        var project = await InsertProjectAsync();
        var store = new EfBacklogTaskStore(pg.Factory);

        var keys = new[] { "g", "b", "t", "n", "c" };
        foreach (var k in keys)
            await store.InsertAsync(MakeReadyTask(project.Id, k));

        var top3 = await store.ListReadyForClaimAsync(project.Id, 3);
        top3.Select(t => t.OrderKey).Should().Equal(new[] { "b", "c", "g" },
            "list must return top-N by ascending order_key");
    }

    [Fact]
    public async Task TryMoveToReady_CasTransition_BacklogToReady()
    {
        var project = await InsertProjectAsync();
        var store = new EfBacklogTaskStore(pg.Factory);
        var task = MakeBacklogTask(project.Id, "backlog-key");
        await store.InsertAsync(task);

        var moved = await store.TryMoveToReadyAsync(
            project.Id, task.Id, "ready-key", DateTimeOffset.UtcNow);
        moved.Should().BeTrue();

        var result = await store.GetAsync(project.Id, task.Id);
        result!.State.Should().Be(BacklogTaskState.Ready);
        result.OrderKey.Should().Be("ready-key");
    }
}

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class EfProjectStoreUpsertTests(PostgresFixture pg)
{
    // ─────────────────────────────────────────────────────────────────────────
    // 5. UPSERT IDEMPOTENCY — project, workflow run, cast proposal stores
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EfProjectStore_Insert_And_Update_RoundTrip()
    {
        var store = new EfProjectStore(pg.Factory);
        var project = MakeProject();
        await store.InsertAsync(project);

        var retrieved = await store.GetAsync(project.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be(project.Name);

        var now = DateTimeOffset.UtcNow;
        await store.UpdateNameAsync(project.Id, "Updated Name", now);

        var after = await store.GetAsync(project.Id);
        after!.Name.Should().Be("Updated Name");
    }

    [Fact]
    public async Task EfProjectStore_TryBeginDelete_TransitionsActiveToDeleting()
    {
        var store = new EfProjectStore(pg.Factory);
        var project = MakeProject();
        await store.InsertAsync(project);

        var result = await store.TryBeginDeleteAsync(project.Id);
        result.Should().BeTrue();

        var after = await store.GetAsync(project.Id);
        after!.State.Should().Be(ProjectState.Deleting);

        // Second call on deleting state must return false (CAS guard)
        var second = await store.TryBeginDeleteAsync(project.Id);
        second.Should().BeFalse("project is already in deleting state");
    }

    [Fact]
    public async Task EfWorkflowRunStore_Insert_And_SetPath_Works()
    {
        var store = new EfWorkflowRunStore(pg.Factory);
        var project = MakeProject();
        await new EfProjectStore(pg.Factory).InsertAsync(project);

        var wfRun = new WorkflowRun
        {
            Id = Guid.NewGuid().ToString("N"),
            ProjectId = project.Id,
            Task = "wf task",
            SubmittingUser = "alice",
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(wfRun);

        await store.SetOrchestrationWorktreePathAsync(wfRun.Id, "/worktrees/wf1");
        var path = await store.GetOrchestrationWorktreePathAsync(wfRun.Id);
        path.Should().Be("/worktrees/wf1");
    }

    [Fact]
    public async Task EfCastProposalStore_StoreAndGet_Roundtrip()
    {
        var store = new EfCastProposalStore(pg.Factory);
        var projectId = ProjectId.New().ToString();
        var proposal = new Agentweaver.Squad.Model.CastProposal(
            ProposalId: Guid.NewGuid().ToString("N"),
            Mode: Agentweaver.Squad.Model.CastMode.Scenario,
            Universe: "test-universe",
            Members: [],
            ExistingTeamPresent: false,
            RunId: null,
            Warnings: [],
            Rationale: null);

        store.Store(projectId, proposal, "alice");

        // Immediate get from in-memory cache
        var (retrieved, owner) = store.Get(projectId, proposal.ProposalId);
        retrieved.Should().NotBeNull();
        owner.Should().Be("alice");
    }

    [Fact]
    public async Task EfCastProposalStore_Store_IsIdempotent_SecondStoreOverwrites()
    {
        var store = new EfCastProposalStore(pg.Factory);
        var projectId = ProjectId.New().ToString();
        var proposal = new Agentweaver.Squad.Model.CastProposal(
            ProposalId: Guid.NewGuid().ToString("N"),
            Mode: Agentweaver.Squad.Model.CastMode.Scenario,
            Universe: "test-universe",
            Members: [],
            ExistingTeamPresent: false,
            RunId: null,
            Warnings: [],
            Rationale: null);

        // Store twice — second call should be a no-exception upsert
        store.Store(projectId, proposal, "alice");
        var act = () => store.Store(projectId, proposal, "bob");
        act.Should().NotThrow("storing the same proposal twice must be idempotent");

        // Give DB writes time to flush (best-effort in store)
        await Task.Delay(100);

        await using var db = await pg.CreateDbContextAsync();
        var count = await db.CastProposals
            .CountAsync(c => c.Id == proposal.ProposalId);
        count.Should().Be(1, "there should be exactly one row after idempotent upsert");
    }
}
