using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Agentweaver.Api.Diagnostics;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Metrics;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;

namespace Agentweaver.Tests.Metrics;

/// <summary>
/// Tests for <see cref="MetricsService"/> aggregation logic against a REAL temp-file SQLite store
/// (Principle VII: no mocks of the store or its query logic). Seeds runs/projects/backlog tasks via
/// the real <see cref="SqliteRunStore"/>, <see cref="SqliteProjectStore"/>, and
/// <see cref="SqliteBacklogTaskStore"/>, then asserts the computed dashboard/overview numbers.
/// </summary>
public sealed class MetricsServiceTests
{
    private static HeartbeatStatusStore MakeHeartbeat(bool enabled = true)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Coordinator:HeartbeatEnabled"] = enabled ? "true" : "false",
            })
            .Build();
        return new HeartbeatStatusStore(config);
    }

    private static Project MakeProject(ProjectId id, string owner = "alice", string name = "Proj") => new()
    {
        Id               = id,
        Name             = name,
        Origin           = ProjectOrigin.Blank(),
        WorkingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
        DefaultBranch    = "main",
        Owner            = owner,
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State            = ProjectState.Active,
        CreatedAt        = DateTimeOffset.UtcNow,
        UpdatedAt        = DateTimeOffset.UtcNow,
    };

    private static Run MakeRun(
        ProjectId projectId,
        RunStatus status,
        DateTimeOffset startedAt,
        DateTimeOffset? endedAt = null,
        string? agent = null,
        RunOrigin origin = RunOrigin.Interactive) => new()
    {
        Id                = RunId.New(),
        RepositoryPath    = Path.Combine(Path.GetTempPath(), "repo"),
        OriginatingBranch = "main",
        ModelSource       = ModelSource.GitHubCopilot,
        Task              = "Do the thing",
        SubmittingUser    = "alice",
        Status            = status,
        StartedAt         = startedAt,
        EndedAt           = endedAt,
        ProjectId         = projectId,
        AgentName         = agent,
        Origin            = origin,
    };

    private static void WriteTeam(Project project, params (string Name, string Role)[] members)
    {
        var squadDir = Path.Combine(project.WorkingDirectory, ".squad");
        Directory.CreateDirectory(squadDir);

        var lines = new List<string>
        {
            "# Squad Team",
            "",
            $"> {project.Name}",
            "",
            "## Coordinator",
            "",
            "| Name | Role | Notes |",
            "|------|------|-------|",
            "| Squad | Coordinator | Routes work, enforces handoffs and reviewer gates. |",
            "",
            "## Members",
            "",
            "| Name | Role | Charter | Status |",
            "|------|------|---------|--------|",
        };

        lines.AddRange(members.Select(m =>
            $"| {m.Name} | {m.Role} | .squad/agents/{m.Name}/charter.md | active |"));
        lines.Add("");
        lines.Add("## Project Context");
        lines.Add("");
        lines.Add($"- **Project:** {project.Name}");
        lines.Add("- **Universe:** test");

        File.WriteAllLines(Path.Combine(squadDir, "team.md"), lines);
    }

    private static void WriteRegistry(Project project, params (string RegistryName, string PersistentName)[] members)
    {
        var castingDir = Path.Combine(project.WorkingDirectory, ".squad", "casting");
        Directory.CreateDirectory(castingDir);

        var entries = members.Select(m =>
            $$"""
                "{{m.RegistryName}}": {
                  "name": "{{m.RegistryName}}",
                  "persistentName": "{{m.PersistentName}}",
                  "universe": "test",
                  "defaultModel": "",
                  "status": "Active",
                  "createdAt": "2026-01-01T00:00:00Z",
                  "previousName": null,
                  "succeededBy": null,
                  "retiredAt": null,
                  "charterPath": ".squad/agents/{{m.PersistentName.ToLowerInvariant()}}/charter.md"
                }
            """);

        File.WriteAllText(Path.Combine(castingDir, "registry.json"),
            $$"""
            {
              "agents": {
            {{string.Join(",\n", entries)}}
              }
            }
            """);
    }

    [Fact]
    public async Task Dashboard_ComputesSummaryThroughputAndLeaderboard_FromRealRuns()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var now = DateTimeOffset.UtcNow;
        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);
        WriteTeam(project,
            ("morpheus", "Core Implementer"),
            ("tank", "Quality Reviewer"));

        // A: morpheus, merged, this week, finished (10 min duration)
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Merged,
            now.AddDays(-1), now.AddDays(-1).AddMinutes(10), agent: "morpheus"));
        // B: morpheus, in_progress, today (active)
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.InProgress,
            now.AddMinutes(-5), agent: "morpheus"));
        // C: tank, failed, 2 days ago, finished
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Failed,
            now.AddDays(-2), now.AddDays(-2).AddMinutes(4), agent: "tank"));
        // D: morpheus, merged, 40 days ago (outside week + 30d window)
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Merged,
            now.AddDays(-40), now.AddDays(-40).AddMinutes(20), agent: "morpheus"));

        var dto = await service.GetProjectDashboardAsync(project);

        // summary
        dto.Summary.RunsTotal.Should().Be(4);
        dto.Summary.RunsThisWeek.Should().Be(3);      // A, B, C
        dto.Summary.ActiveRuns.Should().Be(1);        // B
        dto.Summary.ActiveAgents.Should().Be(1);      // morpheus on B
        dto.Summary.TasksDoneThisWeek.Should().Be(1); // A (D is 40d ago)

        // throughput: 30 days zero-filled
        dto.Throughput.Should().HaveCount(30);
        dto.Throughput.Sum(p => p.Created).Should().Be(3); // A, B, C (D outside window)
        dto.Throughput.Sum(p => p.Done).Should().Be(2);    // A (merged), C (failed=finished)
        dto.Throughput.Last().Date.Should().Be(now.UtcDateTime.ToString("yyyy-MM-dd"));

        // leaderboard
        dto.AgentLeaderboard.Should().HaveCount(2);
        var morpheus = dto.AgentLeaderboard[0];
        morpheus.Agent.Should().Be("morpheus"); // sorted by runs_total desc
        morpheus.RoleTitle.Should().Be("Core Implementer");
        morpheus.RunsTotal.Should().Be(3);       // A, B, D
        morpheus.RunsThisWeek.Should().Be(2);    // A, B
        morpheus.SuccessfulRuns.Should().Be(2);  // A, D merged
        morpheus.TerminalRuns.Should().Be(2);    // B is in_progress and excluded
        morpheus.SuccessRate.Should().Be(1.0);   // successful terminal runs / terminal runs
        morpheus.AvgDurationMs.Should().NotBeNull();
        morpheus.AvgDurationMs!.Value.Should().BeApproximately((10 + 20) / 2.0 * 60_000, 1.0);

        var tank = dto.AgentLeaderboard[1];
        tank.Agent.Should().Be("tank");
        tank.RoleTitle.Should().Be("Quality Reviewer");
        tank.RunsTotal.Should().Be(1);
        tank.SuccessfulRuns.Should().Be(0);
        tank.TerminalRuns.Should().Be(1);
        tank.SuccessRate.Should().Be(0d);
        tank.AvgDurationMs!.Value.Should().BeApproximately(4 * 60_000, 1.0);
    }

    [Fact]
    public async Task Dashboard_MapsRoles_FromNormalizedTeamRosterRegistryAndCoordinator()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var now = DateTimeOffset.UtcNow;
        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);
        WriteTeam(project,
            ("Harry Potter", "Full-stack Engineer"),
            ("Dumbledore", "Systems Architect"));
        WriteRegistry(project, ("dumbledore-architect", "Dumbledore"));

        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Completed,
            now.AddMinutes(-30), now.AddMinutes(-20), agent: "harry-potter"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Completed,
            now.AddMinutes(-20), now.AddMinutes(-10), agent: "dumbledore-architect"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Merged,
            now.AddMinutes(-10), now.AddMinutes(-1), agent: "Coordinator"));

        var dto = await service.GetProjectDashboardAsync(project);

        dto.AgentLeaderboard.Single(e => e.Agent == "harry-potter").RoleTitle.Should().Be("Full-stack Engineer");
        dto.AgentLeaderboard.Single(e => e.Agent == "dumbledore-architect").RoleTitle.Should().Be("Systems Architect");
        dto.AgentLeaderboard.Single(e => e.Agent == "Coordinator").RoleTitle.Should().Be("Coordinator");
    }

    [Fact]
    public async Task Dashboard_SuccessRate_UsesTerminalRunsOnly_AndHandlesZeroTerminalRuns()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var now = DateTimeOffset.UtcNow;
        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);

        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Merged,        now.AddMinutes(-70), now.AddMinutes(-69), agent: "neo"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Completed,     now.AddMinutes(-60), now.AddMinutes(-59), agent: "neo"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.AssembleReady, now.AddMinutes(-50), now.AddMinutes(-49), agent: "neo"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Failed,        now.AddMinutes(-40), now.AddMinutes(-39), agent: "neo"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.AwaitingReview, now.AddMinutes(-30), agent: "neo"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.InProgress,    now.AddMinutes(-20), agent: "trinity"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Pending,       now.AddMinutes(-10), agent: "trinity"));

        var dto = await service.GetProjectDashboardAsync(project);

        var neo = dto.AgentLeaderboard.Single(e => e.Agent == "neo");
        neo.SuccessfulRuns.Should().Be(3);
        neo.TerminalRuns.Should().Be(4);
        neo.SuccessRate.Should().BeApproximately(0.75, 1e-9);

        var trinity = dto.AgentLeaderboard.Single(e => e.Agent == "trinity");
        trinity.SuccessfulRuns.Should().Be(0);
        trinity.TerminalRuns.Should().Be(0);
        trinity.SuccessRate.Should().Be(0d);
    }

    [Fact]
    public async Task Dashboard_NoRuns_ReturnsZeroedSummaryAndEmptyLeaderboard()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var project = MakeProject(ProjectId.New());
        await projectStore.InsertAsync(project);

        var dto = await service.GetProjectDashboardAsync(project);

        dto.Summary.RunsTotal.Should().Be(0);
        dto.Summary.ActiveAgents.Should().Be(0);
        dto.Throughput.Should().HaveCount(30);
        dto.Throughput.Sum(p => p.Created).Should().Be(0);
        dto.AgentLeaderboard.Should().BeEmpty();
    }

    [Fact]
    public async Task Overview_AggregatesAcrossProjects_FromRealStores()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var runStore = new SqliteRunStore(test.Db);
        var projectStore = new SqliteProjectStore(test.Db);
        var backlogStore = new SqliteBacklogTaskStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat());

        var now = DateTimeOffset.UtcNow;
        var p1 = MakeProject(ProjectId.New(), owner: "alice", name: "Alpha");
        var p2 = MakeProject(ProjectId.New(), owner: "bob", name: "Beta");
        await projectStore.InsertAsync(p1);
        await projectStore.InsertAsync(p2);

        // P1: in_progress (live session + active), merged-today (done_today)
        await runStore.InsertAsync(MakeRun(p1.Id, RunStatus.InProgress, now.AddMinutes(-5), agent: "morpheus"));
        await runStore.InsertAsync(MakeRun(p1.Id, RunStatus.Merged, now.AddHours(-1), now.AddMinutes(-30), agent: "tank"));
        // P2: pending (queued), merge_failed (health -> degraded)
        await runStore.InsertAsync(MakeRun(p2.Id, RunStatus.Pending, now.AddMinutes(-10),
            origin: RunOrigin.BacklogPickup));
        await runStore.InsertAsync(MakeRun(p2.Id, RunStatus.MergeFailed, now.AddHours(-2), now.AddHours(-1)));

        // Backlog: P1 ready (queued + active), P2 backlog (ignored)
        await backlogStore.InsertAsync(BacklogTask_Ready(p1.Id, "a0"));
        await backlogStore.InsertAsync(BacklogTask_Backlog(p2.Id, "b0"));

        var dto = await service.GetOverviewAsync();

        // at_a_glance
        dto.AtAGlance.InFlight.Should().Be(1);       // P1 in_progress
        dto.AtAGlance.QueuedWork.Should().Be(2);     // P2 pending run + P1 ready task
        dto.AtAGlance.DoneToday.Should().Be(1);      // P1 merged today
        dto.AtAGlance.ActiveProjects.Should().Be(1); // only P1 has in_progress-or-ready
        dto.AtAGlance.Health.Should().Be("degraded"); // P2 merge_failed

        // live_sessions
        dto.LiveSessions.Should().ContainSingle();
        dto.LiveSessions[0].ProjectName.Should().Be("Alpha");
        dto.LiveSessions[0].Agent.Should().Be("morpheus");
        dto.LiveSessions[0].Status.Should().Be("in_progress");

        // active_workflow_runs: in_progress (P1) + pending (P2)
        dto.ActiveWorkflowRuns.Should().HaveCount(2);
        dto.ActiveWorkflowRuns.Select(w => w.Trigger).Should().Contain(new[] { "interactive", "backlog_pickup" });

        // active_projects list: both P1 (active) and P2 (queued pending)
        dto.ActiveProjects.Should().HaveCount(2);
        var alpha = dto.ActiveProjects.Single(p => p.ProjectName == "Alpha");
        alpha.ActiveCount.Should().Be(1);
        alpha.QueuedCount.Should().Be(1); // ready task
        alpha.LastActivityUtc.Should().NotBeNull();
        var beta = dto.ActiveProjects.Single(p => p.ProjectName == "Beta");
        beta.ActiveCount.Should().Be(0);
        beta.QueuedCount.Should().Be(1); // pending run

        // recent_activity: all 4 runs
        dto.RecentActivity.Should().HaveCount(4);
        dto.RecentActivity.Select(a => a.Kind).Should().Contain("in_progress");
    }

    [Fact]
    public async Task Overview_HealthDegraded_WhenHeartbeatDisabled()
    {
        await using var test = await TestSqliteDb.CreateAsync();
        var projectStore = new SqliteProjectStore(test.Db);
        var service = new MetricsService(test.Db, projectStore, MakeHeartbeat(enabled: false));

        var dto = await service.GetOverviewAsync();

        dto.AtAGlance.Health.Should().Be("degraded");
        dto.AtAGlance.InFlight.Should().Be(0);
        dto.LiveSessions.Should().BeEmpty();
    }

    private static BacklogTask BacklogTask_Ready(ProjectId projectId, string orderKey) => new()
    {
        Id          = BacklogTaskId.New(),
        ProjectId   = projectId,
        Title       = "Ready task",
        Description = null,
        State       = BacklogTaskState.Ready,
        OrderKey    = orderKey,
        CapturedBy  = "alice",
        CreatedAt   = DateTimeOffset.UtcNow,
        CommittedAt = DateTimeOffset.UtcNow,
        ClaimedAt   = null,
        RunId       = null,
    };

    private static BacklogTask BacklogTask_Backlog(ProjectId projectId, string orderKey) => new()
    {
        Id          = BacklogTaskId.New(),
        ProjectId   = projectId,
        Title       = "Backlog task",
        Description = null,
        State       = BacklogTaskState.Backlog,
        OrderKey    = orderKey,
        CapturedBy  = "alice",
        CreatedAt   = DateTimeOffset.UtcNow,
        CommittedAt = null,
        ClaimedAt   = null,
        RunId       = null,
    };

    private const char Ellipsis = '\u2026';

    [Fact]
    public void Truncate_LongMultiWordTask_EndsWithEllipsis_AndNotMidWord()
    {
        var value = "Refactor the authentication middleware and update the dependent integration tests across services";

        var result = MetricsService.Truncate(value, 80);

        result.Length.Should().BeLessThanOrEqualTo(80);
        result.Should().EndWith(Ellipsis.ToString());

        // The text before the ellipsis must be whole words only (a prefix of the original split on
        // spaces), i.e. no partial trailing token.
        var body = result[..^1];
        var originalWords = value.Split(' ');
        var bodyWords = body.Split(' ');
        originalWords.Take(bodyWords.Length).Should().Equal(bodyWords);
        body.Should().NotEndWith(" ");
    }

    [Fact]
    public void Truncate_ShortTask_ReturnedUnchanged_NoEllipsis()
    {
        var value = "Do the thing";

        var result = MetricsService.Truncate(value, 80);

        result.Should().Be(value);
        result.Should().NotContain(Ellipsis.ToString());
    }

    [Fact]
    public void Truncate_UnbrokenTokenLongerThanMax_StillTruncatesWithEllipsis()
    {
        var value = new string('x', 200);

        var result = MetricsService.Truncate(value, 80);

        result.Length.Should().Be(80);
        result.Should().EndWith(Ellipsis.ToString());
        result[..^1].Should().Be(new string('x', 79));
    }
}
