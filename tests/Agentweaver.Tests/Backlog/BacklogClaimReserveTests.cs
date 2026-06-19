using FluentAssertions;
using Microsoft.Data.Sqlite;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using static Agentweaver.Tests.Backlog.BacklogTestData;

namespace Agentweaver.Tests.Backlog;

/// <summary>
/// The correctness-critical claim+reserve tests (FR-008, FR-009, FR-011 / SC-002). Every test runs
/// against a REAL <see cref="SqliteBacklogTaskStore"/> whose
/// <see cref="SqliteBacklogTaskStore.TryClaimAndReserveCoordinatorRunAsync"/> executes a single SQLite
/// transaction spanning backlog_tasks + runs (the reserved coordinator run is identity-shaped like an
/// interactive coordinator run: workflow_run_id IS NULL, no workflow_runs envelope). No store logic is mocked.
/// </summary>
public sealed class BacklogClaimReserveTests
{
    private static async Task<long> ScalarAsync(SqliteDb db, string sql, params (string, object)[] args)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // =========================================================================
    // 1. ATOMIC EXACTLY-ONCE CLAIM under concurrency (FR-009 / SC-002).
    // =========================================================================
    [Fact]
    public async Task ConcurrentClaims_OfSameTask_ExactlyOneWins_NoOrphanNoDuplicate()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);

        var project = MakeProject();
        await projects.InsertAsync(project);
        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        const int contenders = 8;
        var runIds = Enumerable.Range(0, contenders).Select(_ => RunId.New()).ToArray();
        using var barrier = new Barrier(contenders);

        var claimTasks = Enumerable.Range(0, contenders).Select(i => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await store.TryClaimAndReserveCoordinatorRunAsync(
                project.Id, task.Id,
                MakeCoordinatorRun(project.Id, runIds[i]),
                DateTimeOffset.UtcNow);
        })).ToArray();

        var results = await Task.WhenAll(claimTasks);

        // Exactly one Won; every other is Lost (never ProjectUnavailable — the project is active).
        results.Count(r => r == ClaimReserveResult.Won).Should().Be(1);
        results.Count(r => r == ClaimReserveResult.Lost).Should().Be(contenders - 1);
        results.Should().NotContain(ClaimReserveResult.ProjectUnavailable);

        var winnerIdx = Array.FindIndex(results, r => r == ClaimReserveResult.Won);
        var winnerRunId = runIds[winnerIdx];

        // Task ends Claimed with exactly the winner's run_id.
        var claimed = await store.GetAsync(project.Id, task.Id);
        claimed!.State.Should().Be(BacklogTaskState.Claimed);
        claimed.RunId.Should().Be(winnerRunId);
        claimed.ClaimedAt.Should().NotBeNull();

        // Exactly one coordinator run exists for the project, and it belongs to the winner. No
        // workflow_runs envelope is written (parity with interactive coordinator runs).
        var runs = await runStore.GetRunsByProjectAsync(project.Id, includeChildren: true);
        runs.Should().ContainSingle().Which.Id.Should().Be(winnerRunId);
        (await ScalarAsync(testDb.Db, "SELECT COUNT(*) FROM workflow_runs WHERE project_id = $p;",
            ("$p", project.Id.ToString()))).Should().Be(0);

        // Every loser persisted NOTHING (no orphan run).
        for (var i = 0; i < contenders; i++)
        {
            if (i == winnerIdx) continue;
            (await runStore.GetAsync(runIds[i])).Should().BeNull("loser run must not be persisted");
        }
    }

    // =========================================================================
    // 1b. Sequential overlapping claim: a second claim after the first Won returns Lost.
    // =========================================================================
    [Fact]
    public async Task SecondClaim_AfterFirstWon_ReturnsLost_AndPersistsNothing()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);

        var project = MakeProject();
        await projects.InsertAsync(project);
        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        var firstRun = RunId.New();
        var first = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id,
            MakeCoordinatorRun(project.Id, firstRun), DateTimeOffset.UtcNow);
        first.Should().Be(ClaimReserveResult.Won);

        var secondRun = RunId.New();
        var second = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id,
            MakeCoordinatorRun(project.Id, secondRun), DateTimeOffset.UtcNow);
        second.Should().Be(ClaimReserveResult.Lost);

        (await store.GetAsync(project.Id, task.Id))!.RunId.Should().Be(firstRun);
        (await runStore.GetAsync(secondRun)).Should().BeNull();
    }

    // =========================================================================
    // 2. PROJECT-UNAVAILABLE leaves the task Ready (FR-008 / FR-011).
    // =========================================================================
    [Fact]
    public async Task Claim_AgainstDeletingProject_ReturnsProjectUnavailable_AndLeavesTaskReady()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);

        var project = MakeProject();
        await projects.InsertAsync(project);
        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        // Move the project out of Active (Deleting) so the run-insert gate fails.
        (await projects.TryBeginDeleteAsync(project.Id)).Should().BeTrue();

        var runId = RunId.New();
        var result = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id,
            MakeCoordinatorRun(project.Id, runId), DateTimeOffset.UtcNow);

        result.Should().Be(ClaimReserveResult.ProjectUnavailable);

        // The whole transaction rolled back: task is still Ready, unclaimed, order preserved.
        var after = await store.GetAsync(project.Id, task.Id);
        after!.State.Should().Be(BacklogTaskState.Ready);
        after.RunId.Should().BeNull();
        after.ClaimedAt.Should().BeNull();
        after.OrderKey.Should().Be("n");

        // The task is still a claim candidate (not consumed).
        (await store.ListReadyForClaimAsync(project.Id, 10)).Should().ContainSingle()
            .Which.Id.Should().Be(task.Id);

        // Nothing persisted.
        (await runStore.GetAsync(runId)).Should().BeNull();
    }

    // =========================================================================
    // 6. RUN ORIGIN marker + GetByRunId recovery linkage (governance fix).
    // =========================================================================
    [Fact]
    public async Task ReservedRun_RoundTrips_WithBacklogPickupOrigin_AndIsLinkedToTask()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var store = new SqliteBacklogTaskStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);

        var project = MakeProject();
        await projects.InsertAsync(project);
        var task = MakeReadyTask(project.Id, "n");
        await store.InsertAsync(task);

        var runId = RunId.New();
        var won = await store.TryClaimAndReserveCoordinatorRunAsync(
            project.Id, task.Id,
            MakeCoordinatorRun(project.Id, runId), DateTimeOffset.UtcNow);
        won.Should().Be(ClaimReserveResult.Won);

        // Durable origin marker + shape the recovery path relies on.
        var run = await runStore.GetAsync(runId);
        run.Should().NotBeNull();
        run!.Origin.Should().Be(RunOrigin.BacklogPickup);
        run.AgentName.Should().Be("Coordinator");
        run.ParentRunId.Should().BeNull();
        run.Status.Should().Be(RunStatus.InProgress);
        // Identity parity with interactive coordinator runs: no distinct workflow_run_id, so the board
        // navigates by run_id and every coordinator detail endpoint resolves the run.
        run.WorkflowRunId.Should().BeNull();

        // GetByRunId resolves the 1:1 claimed task (the durable marker recovery uses).
        var linked = await store.GetByRunIdAsync(runId);
        linked!.Id.Should().Be(task.Id);
        linked.CapturedBy.Should().Be(task.CapturedBy);
    }

    // =========================================================================
    // 6b. Ordinary project runs default to Interactive origin (discrimination basis).
    // =========================================================================
    [Fact]
    public async Task OrdinaryProjectRun_PersistsInteractiveOrigin()
    {
        await using var testDb = await TestSqliteDb.CreateAsync();
        var projects = new SqliteProjectStore(testDb.Db);
        var runStore = new SqliteRunStore(testDb.Db);

        var project = MakeProject();
        await projects.InsertAsync(project);

        var runId = RunId.New();
        var interactive = MakeCoordinatorRun(project.Id, runId);
        (await runStore.TryCreateProjectRunAsync(interactive)).Should().BeTrue();

        var run = await runStore.GetAsync(runId);
        run!.Origin.Should().Be(RunOrigin.Interactive);
    }
}
