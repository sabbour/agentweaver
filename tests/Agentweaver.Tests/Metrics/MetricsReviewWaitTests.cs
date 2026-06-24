using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Metrics;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Metrics;

/// <summary>
/// Verifies that time a run spends parked in the awaiting_review human-review gate is accrued into
/// runs.review_wait_ms on every exit, and that the dashboard leaderboard AvgDurationMs excludes that
/// dwell. Driven through the actual <see cref="SqliteRunStore"/> transition methods against a
/// temp-file SQLite database (Constitution VII: no mocks). Controlled timestamps are passed via the
/// methods' <c>now</c> parameter so assertions are deterministic (no sleeps).
/// </summary>
public sealed class MetricsReviewWaitTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static HeartbeatStatusStore MakeHeartbeat()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Coordinator:HeartbeatEnabled"] = "true" })
            .Build();
        return new HeartbeatStatusStore(config);
    }

    private static Project MakeProject(ProjectId id) => new()
    {
        Id               = id,
        Name             = "Proj",
        Origin           = ProjectOrigin.Blank(),
        WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        DefaultBranch    = "main",
        Owner            = "alice",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State            = ProjectState.Active,
        CreatedAt        = DateTimeOffset.UtcNow,
        UpdatedAt        = DateTimeOffset.UtcNow,
    };

    private static Run MakeInProgressRun(ProjectId projectId, RunId id, DateTimeOffset startedAt, string agent) => new()
    {
        Id                = id,
        RepositoryPath    = Path.Combine(Path.GetTempPath(), "repo"),
        OriginatingBranch = "main",
        ModelSource       = ModelSource.GitHubCopilot,
        Task              = "Do the thing",
        SubmittingUser    = "alice",
        Status            = RunStatus.InProgress,
        StartedAt         = startedAt,
        ProjectId         = projectId,
        AgentName         = agent,
    };

    private static async Task<long> ReadReviewWaitMsAsync(SqliteDb db, RunId runId)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT review_wait_ms FROM runs WHERE run_id = $id;";
        cmd.Parameters.AddWithValue("$id", runId.ToString());
        var value = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(value);
    }

    [Fact]
    public async Task ReviewDwell_IsAccrued_AndExcludedFromLeaderboardDuration()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);

        var t0 = Base;                       // started
        var t1 = Base.AddMinutes(5);         // entered awaiting_review (5 min working so far)
        var t2 = Base.AddMinutes(35);        // merged (30 min human-review dwell)

        var runId = RunId.New();
        await runStore.InsertAsync(MakeInProgressRun(project.Id, runId, t0, "morpheus"));
        await runStore.UpdateReviewReadyAsync(runId, "tree", "diff", 1, now: t1);
        (await runStore.TryStartMergingAsync(runId, "alice", now: t2)).Should().BeTrue();
        (await runStore.CompleteMergingAsync(runId, RunStatus.Merged, t2, result: "ok")).Should().BeTrue();

        // review_wait_ms == t2 - t1 == 30 min
        var waitMs = await ReadReviewWaitMsAsync(test.Db, runId);
        waitMs.Should().BeCloseTo(30 * 60_000, 50);

        // leaderboard duration == working time only == t1 - t0 == 5 min (dwell excluded)
        var dto = await service.GetProjectDashboardAsync(project);
        var entry = dto.AgentLeaderboard.Single(e => e.Agent == "morpheus");
        entry.AvgDurationMs.Should().NotBeNull();
        entry.AvgDurationMs!.Value.Should().BeApproximately(5 * 60_000, 50);
    }

    [Fact]
    public async Task ReviseLoop_SumsAllReviewDwellIntervals()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);

        var t0 = Base;                          // started
        var t1 = Base.AddMinutes(5);            // review-ready #1
        var t2 = Base.AddMinutes(15);           // request-changes -> in_progress (dwell #1 = 10 min)
        var t3 = Base.AddMinutes(18);           // review-ready #2
        var t4 = Base.AddMinutes(38);           // merged (dwell #2 = 20 min)

        var runId = RunId.New();
        await runStore.InsertAsync(MakeInProgressRun(project.Id, runId, t0, "tank"));
        await runStore.UpdateReviewReadyAsync(runId, "tree", "diff", 1, now: t1);
        (await runStore.TryTransitionReviewToInProgressAsync(runId, now: t2)).Should().BeTrue();
        await runStore.UpdateReviewReadyAsync(runId, "tree2", "diff2", 2, now: t3);
        (await runStore.TryStartMergingAsync(runId, "alice", now: t4)).Should().BeTrue();
        (await runStore.CompleteMergingAsync(runId, RunStatus.Merged, t4, result: "ok")).Should().BeTrue();

        // review_wait_ms == (t2 - t1) + (t4 - t3) == 10 + 20 == 30 min
        var waitMs = await ReadReviewWaitMsAsync(test.Db, runId);
        waitMs.Should().BeCloseTo(30 * 60_000, 50);

        // duration excludes both dwells: (t4 - t0) - 30 min == 38 - 30 == 8 min working
        var dto = await service.GetProjectDashboardAsync(project);
        var entry = dto.AgentLeaderboard.Single(e => e.Agent == "tank");
        entry.AvgDurationMs!.Value.Should().BeApproximately(8 * 60_000, 50);
    }

    [Fact]
    public async Task NoReviewPath_ReviewWaitStaysZero_AndDurationUnchanged()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);

        var t0 = Base;
        var tEnd = Base.AddMinutes(7);

        // A coordinator child run that ends at assemble_ready never enters awaiting_review.
        var runId = RunId.New();
        await runStore.InsertAsync(MakeInProgressRun(project.Id, runId, t0, "neo"));
        (await runStore.SetAssembleReadyAsync(runId, "tree", "branch", "diff", 1, tEnd)).Should().BeTrue();

        var waitMs = await ReadReviewWaitMsAsync(test.Db, runId);
        waitMs.Should().Be(0);

        var dto = await service.GetProjectDashboardAsync(project);
        var entry = dto.AgentLeaderboard.Single(e => e.Agent == "neo");
        entry.AvgDurationMs!.Value.Should().BeApproximately(7 * 60_000, 50);
    }

    [Fact]
    public void ActiveDurationHelper_ExcludesReviewDwell_AndClampsAtZero()
    {
        var started = Base;
        var ended = Base.AddMinutes(35);

        // 35 min elapsed minus 30 min review dwell == 5 min working time.
        MetricsService.ActiveDurationMsExcludingReview(started, ended, 30 * 60_000)
            .Should().BeApproximately(5 * 60_000, 0.001);

        // No review dwell leaves the full elapsed time.
        MetricsService.ActiveDurationMsExcludingReview(started, ended, 0)
            .Should().BeApproximately(35 * 60_000, 0.001);

        // Review dwell exceeding elapsed (clock skew / rounding) clamps at 0, never negative.
        MetricsService.ActiveDurationMsExcludingReview(started, ended, 40 * 60_000)
            .Should().Be(0d);
    }
}
