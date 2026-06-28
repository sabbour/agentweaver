using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Runs;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Agentweaver.Tests.PostgresIntegration;

/// <summary>
/// spec-018 regression proof: the LIVE Postgres cutover crashed at startup with
/// <c>SqliteException SQLite Error 1: 'no such table: runs'</c> from
/// <c>SqliteRunStore.GetByStatusAsync</c> called by <c>WorkflowRestartService.RecoverAsync</c>,
/// because ~20 services injected the CONCRETE SqliteRunStore type which always resolved the
/// raw SQLite registration regardless of <c>Database:Provider</c>.
///
/// <para>This test boots the REAL application (<see cref="Program"/>) with
/// <c>Database:Provider=Postgres</c> against a real <c>postgres:16</c> Testcontainer, applies
/// migrations, and exercises the EXACT crash path: <c>Program.cs</c> calls
/// <c>WorkflowRestartService.RecoverAsync</c> (→ <c>IRunStore.GetByStatusAsync</c>) at startup,
/// so a successful boot alone proves the regression is fixed. It additionally asserts that in
/// Postgres mode <see cref="IRunStore"/> resolves to <see cref="EfRunStore"/> and the concrete
/// <see cref="SqliteRunStore"/> is NOT registered, then runs a full run lifecycle through the
/// interface.</para>
///
/// <para>Skipped automatically when Docker is unavailable (Testcontainers throws on startup).</para>
/// </summary>
[Trait("Category", "PostgresIntegration")]
public sealed class PostgresAppBootTests : IClassFixture<PostgresAppBootTests.AppFixture>
{
    private readonly AppFixture _fixture;
    public PostgresAppBootTests(AppFixture fixture) => _fixture = fixture;

    [Fact]
    public void AppBoot_InPostgresMode_ResolvesEfRunStore_AndDoesNotRegisterSqliteRunStore()
    {
        using var scope = _fixture.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The interface must resolve to the EF/Postgres implementation, never the SQLite one.
        sp.GetRequiredService<IRunStore>().Should().BeOfType<EfRunStore>(
            "Postgres mode must bind IRunStore to EfRunStore");

        // Nothing may resolve a concrete SqliteRunStore in Postgres mode — the raw SQLite
        // registration is gone, so a stray concrete injection would fail fast at boot instead
        // of silently opening an empty ephemeral SQLite DB and crashing on first query.
        _fixture.Services.GetService<SqliteRunStore>().Should().BeNull(
            "SqliteRunStore must NOT be registered in Postgres mode");
        _fixture.Services.GetService<SqliteWorkflowRunStore>().Should().BeNull(
            "SqliteWorkflowRunStore must NOT be registered in Postgres mode");
        _fixture.Services.GetService<SqliteRunRevisionStore>().Should().BeNull(
            "SqliteRunRevisionStore must NOT be registered in Postgres mode");

        // The store consumers that crashed in prod must now hold the interface, not the concrete.
        sp.GetRequiredService<IWorkflowRunStore>().Should().BeOfType<EfWorkflowRunStore>();
        sp.GetRequiredService<IRunRevisionStore>().Should().BeOfType<EfRunRevisionStore>();
    }

    [Fact]
    public async Task WorkflowRestartService_RecoverAsync_RunsAgainstPostgres_AndFailsStrandedRun()
    {
        var runStore = _fixture.Services.GetRequiredService<IRunStore>();
        runStore.Should().BeOfType<EfRunStore>();

        // Seed a stranded InProgress run (the state the recovery sweep must act on).
        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = "/repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "spec-018 recovery proof",
            SubmittingUser = "tank",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        // This is the EXACT call that threw 'no such table: runs' in production.
        var restart = _fixture.Services.GetRequiredService<WorkflowRestartService>();
        var recover = async () => await restart.RecoverAsync(CancellationToken.None);
        await recover.Should().NotThrowAsync(
            "RecoverAsync → IRunStore.GetByStatusAsync must run against Postgres, never SQLite");

        // The stranded run must have been transitioned to a terminal Failed status.
        var recovered = await runStore.GetAsync(runId);
        recovered.Should().NotBeNull();
        recovered!.Status.Should().Be(RunStatus.Failed,
            "a stranded non-coordinator InProgress run is failed by the recovery sweep");
    }

    [Fact]
    public async Task RunLifecycle_ThroughIRunStore_WorksAgainstPostgres()
    {
        var runStore = _fixture.Services.GetRequiredService<IRunStore>();

        var runId = RunId.New();
        await runStore.InsertAsync(new Run
        {
            Id = runId,
            RepositoryPath = "/repo",
            OriginatingBranch = "main",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "lifecycle",
            SubmittingUser = "tank",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        });

        var inProgress = await runStore.GetByStatusAsync(RunStatus.InProgress);
        inProgress.Select(r => r.Id).Should().Contain(runId,
            "GetByStatusAsync must return the freshly inserted run from Postgres");

        await runStore.UpdateStatusAsync(runId, RunStatus.Failed, DateTimeOffset.UtcNow, CancellationToken.None);

        var afterUpdate = await runStore.GetByStatusAsync(RunStatus.InProgress);
        afterUpdate.Select(r => r.Id).Should().NotContain(runId,
            "after the status update the run must no longer appear under InProgress");

        var fetched = await runStore.GetAsync(runId);
        fetched!.Status.Should().Be(RunStatus.Failed);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BUG B regression: MetricsService used SQLite-only SQL (julianday) against the
    // concrete SqliteDb, which in Postgres mode has no `runs` table → the dashboard /
    // overview endpoints threw 'SQLite Error 1: no such table: runs' (HTTP 500).
    // These tests exercise the provider-agnostic MetricsService against real Postgres.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MetricsService_ProjectDashboard_AggregatesAgainstPostgres_DoesNotThrow()
    {
        var projectStore = _fixture.Services.GetRequiredService<IProjectStore>();
        var runStore = _fixture.Services.GetRequiredService<IRunStore>();
        var metrics = _fixture.Services.GetRequiredService<Agentweaver.Api.Metrics.MetricsService>();

        var project = MakeProject("Dashboard-PG");
        await projectStore.InsertAsync(project);

        var now = DateTimeOffset.UtcNow;
        // A: merged this week, finished (10 min).
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Merged, now.AddDays(-1), now.AddDays(-1).AddMinutes(10), "morpheus"));
        // B: in_progress (active).
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.InProgress, now.AddMinutes(-5), null, "morpheus"));
        // C: failed 2 days ago, finished.
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Failed, now.AddDays(-2), now.AddDays(-2).AddMinutes(4), "tank"));

        // The EXACT call that threw 'no such table: runs' (HTTP 500) in Postgres mode.
        var dto = await metrics.GetProjectDashboardAsync(project, CancellationToken.None);

        dto.Summary.RunsTotal.Should().Be(3, "exactly the three seeded runs belong to this project");
        dto.Summary.RunsThisWeek.Should().Be(3);
        dto.Summary.ActiveRuns.Should().Be(1, "only run B is in_progress");
        dto.Summary.ActiveAgents.Should().Be(1, "morpheus is the only agent on an in_progress run");
        dto.Summary.TasksDoneThisWeek.Should().Be(1, "only the merged run A is a success terminal this week");

        dto.Throughput.Should().HaveCount(30);
        dto.Throughput.Sum(p => p.Created).Should().Be(3);
        dto.Throughput.Sum(p => p.Done).Should().Be(2, "A (merged) and C (failed) are finished");

        dto.AgentLeaderboard.Should().HaveCount(2);
        var morpheus = dto.AgentLeaderboard.Single(e => e.Agent == "morpheus");
        morpheus.RunsTotal.Should().Be(2);
        morpheus.SuccessfulRuns.Should().Be(1);
        morpheus.TerminalRuns.Should().Be(1, "the in_progress run is not terminal");
        morpheus.AvgDurationMs.Should().NotBeNull();
        morpheus.AvgDurationMs!.Value.Should().BeApproximately(10 * 60_000, 1.0,
            "the merged run ran 10 minutes with no review dwell");
        var tank = dto.AgentLeaderboard.Single(e => e.Agent == "tank");
        tank.TerminalRuns.Should().Be(1);
        tank.SuccessRate.Should().Be(0d);
    }

    [Fact]
    public async Task MetricsService_Overview_AggregatesAgainstPostgres_DoesNotThrow()
    {
        var projectStore = _fixture.Services.GetRequiredService<IProjectStore>();
        var runStore = _fixture.Services.GetRequiredService<IRunStore>();
        var metrics = _fixture.Services.GetRequiredService<Agentweaver.Api.Metrics.MetricsService>();

        var project = MakeProject("Overview-PG");
        await projectStore.InsertAsync(project);

        var now = DateTimeOffset.UtcNow;
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.InProgress, now.AddMinutes(-3), null, "neo"));
        await runStore.InsertAsync(MakeRun(project.Id, RunStatus.Merged, now.AddHours(-1), now.AddMinutes(-30), "trinity"));

        // The global overview previously 500'd on the same SQLite-only-SQL defect.
        var dto = await metrics.GetOverviewAsync(CancellationToken.None);

        dto.Should().NotBeNull();
        dto.AtAGlance.InFlight.Should().BeGreaterThanOrEqualTo(1);
        dto.LiveSessions.Should().Contain(s => s.ProjectName == "Overview-PG" && s.Agent == "neo");
        dto.ActiveProjects.Should().Contain(p => p.ProjectName == "Overview-PG" && p.ActiveCount >= 1);
        dto.RecentActivity.Should().Contain(a => a.ProjectName == "Overview-PG");
    }

    private static Project MakeProject(string name) => new()
    {
        Id               = ProjectId.New(),
        Name             = name,
        Origin           = ProjectOrigin.Blank(),
        WorkingDirectory = Path.Combine(Path.GetTempPath(), $"aw-pg-metrics-{Guid.NewGuid():N}"),
        DefaultBranch    = "main",
        Owner            = "tank",
        ProviderSettings = new ProjectProviderSettings { DefaultProvider = ModelSource.GitHubCopilot },
        State            = ProjectState.Active,
        CreatedAt        = DateTimeOffset.UtcNow,
        UpdatedAt        = DateTimeOffset.UtcNow,
    };

    private static Run MakeRun(
        ProjectId projectId, RunStatus status, DateTimeOffset startedAt, DateTimeOffset? endedAt, string agent) => new()
    {
        Id                = RunId.New(),
        RepositoryPath    = "/repo",
        OriginatingBranch = "main",
        ModelSource       = ModelSource.GitHubCopilot,
        Task              = "metrics regression seed",
        SubmittingUser    = "tank",
        Status            = status,
        StartedAt         = startedAt,
        EndedAt           = endedAt,
        ProjectId         = projectId,
        AgentName         = agent,
    };

    /// <summary>
    /// Boots the real API once for the whole test class with Database:Provider=Postgres backed by
    /// a postgres:16 Testcontainer. The container + app boot are shared across the class's tests.
    /// </summary>
    public sealed class AppFixture : IAsyncLifetime
    {
        private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("awboot").WithUsername("awboot").WithPassword("awboot")
            .WithCleanUp(true).Build();

        private PostgresWebApplicationFactory _factory = null!;

        public IServiceProvider Services => _factory.Services;

        public async Task InitializeAsync()
        {
            await _container.StartAsync();
            _factory = new PostgresWebApplicationFactory(_container.GetConnectionString());
            // Force the host to build and run startup (which calls WorkflowRestartService.RecoverAsync,
            // CoordinatorRunService.RecoverInterruptedRunsAsync and CoordinatorReconciler.SweepAsync —
            // all against Postgres). A throw here is the regression reproducing.
            using var client = _factory.CreateClient();
        }

        public async Task DisposeAsync()
        {
            await _factory.DisposeAsync();
            await _container.DisposeAsync();
        }
    }

    private sealed class PostgresWebApplicationFactory : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly string _worktreesPath;
        private readonly string _checkpointsPath;
        private readonly string _coordinatorCheckpointsPath;

        public PostgresWebApplicationFactory(string connectionString)
        {
            _connectionString = connectionString;
            _worktreesPath = Path.Combine(Path.GetTempPath(), $"aw-pg-wt-{Guid.NewGuid():N}");
            _checkpointsPath = Path.Combine(Path.GetTempPath(), $"aw-pg-cp-{Guid.NewGuid():N}");
            _coordinatorCheckpointsPath = Path.Combine(Path.GetTempPath(), $"aw-pg-ccp-{Guid.NewGuid():N}");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Database:Provider is read SYNCHRONOUSLY during service registration in Program.cs,
            // before ConfigureAppConfiguration sources are layered in. UseSetting writes to host
            // configuration, which IS visible to builder.Configuration at registration time — this
            // is what actually flips the app into Postgres mode. The connection string is read
            // lazily (inside the DbContext options delegate), so InMemoryCollection is sufficient
            // for it.
            builder.UseSetting("Database:Provider", "postgres");
            builder.UseSetting("ConnectionStrings:Postgres", _connectionString);

            // Program.cs registers BOTH AddDbContextFactory<MemoryDbContext> (singleton) and
            // AddDbContext<MemoryDbContext> (scoped) in Postgres mode. That is a valid production
            // pattern, but the test host defaults to the Development environment which turns on
            // scope validation, causing the singleton factory to fail resolving the scoped
            // DbContext options from the root provider. Production does not validate scopes, so we
            // mirror that here to exercise the real boot path.
            builder.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = false;
                options.ValidateOnBuild = false;
            });

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Belt-and-suspenders: also present as app configuration for lazy reads.
                    ["Database:Provider"] = "postgres",
                    ["ConnectionStrings:Postgres"] = _connectionString,

                    ["Worktrees:BasePath"] = _worktreesPath,
                    ["Checkpoints:Path"] = _checkpointsPath,
                    ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                    ["Testing:BypassGitHubOrgAuthorization"] = "true",
                    ["Testing:BypassGitHubTokenAuth"] = "true",
                    ["Auth:ApiKey"] = "test-api-key-12345",
                    ["Auth:User"] = "test-user",
                    ["Git:Author:Name"] = "Test",
                    ["Git:Author:Email"] = "test@localhost",
                    ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                    ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                    ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                    ["Providers:MicrosoftFoundry:ApiKey"] = "test-foundry-key",
                    ["Providers:MicrosoftFoundry:Endpoint"] = "https://test.openai.azure.com",
                    ["Providers:MicrosoftFoundry:Deployment"] = "gpt-4o",
                    ["RunBounds:MaxSteps"] = "50",
                    ["RunBounds:MaxMinutes"] = "10",
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            foreach (var dir in new[] { _worktreesPath, _checkpointsPath, _coordinatorCheckpointsPath })
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
