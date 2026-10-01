using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Runs;
using Agentweaver.Api;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
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
/// <c>StartupRecoveryService</c> calls <c>WorkflowRestartService.RecoverAsync</c>
/// (→ <c>IRunStore.GetByStatusAsync</c>) after the host starts. The explicit recovery
/// assertion below proves that path works. It additionally asserts that in
/// Postgres mode the <see cref="IRunStore"/> chain contains <see cref="EfRunStore"/> and the
/// concrete <see cref="SqliteRunStore"/> is NOT registered, then runs a full run lifecycle
/// through the interface.</para>
///
/// <para><see cref="IRunStore"/> is a decorator chain, not a single object: since #1315
/// <see cref="PreviewPublicationLeaseRunStore"/> wraps the provider store. These assertions
/// therefore use <see cref="RunStoreChain.Find{T}"/> rather than an exact type check. Do not
/// change them back to <c>BeOfType</c> — the backing store is what this test protects, not the
/// identity of the outermost wrapper.</para>
///
/// <para>Requires a running Docker daemon for the Postgres Testcontainer.</para>
/// </summary>
[Trait("Category", "PostgresIntegration")]
public sealed class PostgresAppBootTests : IClassFixture<PostgresAppBootTests.AppFixture>
{
    private readonly AppFixture _fixture;
    public PostgresAppBootTests(AppFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task WorkerHost_ResolvesChildWorkProjectionClaimFromSharedSingleton()
    {
        using var worker = new PostgresWebApplicationFactory(_fixture.ConnectionString, AppRole.Worker);
        using var client = worker.CreateClient();
        (await client.GetAsync("/readyz")).EnsureSuccessStatusCode();

        var services = worker.Services;
        var childWork = services.GetRequiredService<Agentweaver.Api.Workflows.WorkflowChildWorkService>();
        childWork.Should().NotBeNull();
        var guard = services.GetRequiredService<RunActiveClaimGuard>();
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<RunActiveClaimGuard>().Should().BeSameAs(guard);

        var runId = RunId.New();
        await using var claim = await guard.AcquireAsync(runId, CancellationToken.None);
        using var waiting = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var acquireAgain = async () =>
        {
            await using var second = await scope.ServiceProvider
                .GetRequiredService<RunActiveClaimGuard>()
                .AcquireAsync(runId, waiting.Token);
        };
        await acquireAgain.Should().ThrowAsync<OperationCanceledException>();
    }

    [PostgresFact]
    public async Task PostgresLeader_ExcludesOtherRole_UntilLeaderExits()
    {
        const long isolatedTestLockKey = 0x4157_5243_5652_5903L;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["ConnectionStrings:Postgres"] = _fixture.ConnectionString,
                ["App:Role"] = AppRole.Web,
            }).Build();
        var worker = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Database:Provider"] = "postgres",
                ["ConnectionStrings:Postgres"] = _fixture.ConnectionString,
                ["App:Role"] = AppRole.Worker,
            }).Build();
        StartupRecoveryLeader.LockKeyForRole(configuration)
            .Should().Be(StartupRecoveryLeader.LockKeyForRole(worker));
        await using (var leader = await StartupRecoveryLeader.AcquireAsync(
            configuration, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, isolatedTestLockKey))
        {
            leader.IsLeader.Should().BeTrue();
            await using var waiter = await StartupRecoveryLeader.AcquireAsync(
                worker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, isolatedTestLockKey);
            waiter.IsLeader.Should().BeFalse("other-role sweeps cannot follow a successful leader");
        }

        await using (var waiter = await StartupRecoveryLeader.AcquireAsync(
            worker, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, isolatedTestLockKey))
        {
            waiter.IsLeader.Should().BeTrue("a different role can take over after the leader exits");
        }
    }

    [PostgresFact]
    public void AppBoot_InPostgresMode_ResolvesEfRunStore_AndDoesNotRegisterSqliteRunStore()
    {
        using var scope = _fixture.Services.CreateScope();
        var sp = scope.ServiceProvider;

        // The interface must resolve to the EF/Postgres implementation, never the SQLite one.
        // IRunStore is wrapped by PreviewPublicationLeaseRunStore, so assert on the chain: the
        // decorator's identity is not the point, the backing store is.
        var runStore = sp.GetRequiredService<IRunStore>();
        RunStoreChain.Find<EfRunStore>(runStore).Should().NotBeNull(
            "Postgres mode must bind IRunStore to EfRunStore");
        RunStoreChain.Find<SqliteRunStore>(runStore).Should().BeNull(
            "no SQLite store may appear anywhere in the Postgres run-store chain");
        sp.GetRequiredService<RunActiveClaimGuard>().Should().BeSameAs(
            _fixture.Services.GetRequiredService<RunActiveClaimGuard>());

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

    [PostgresFact]
    public async Task WorkflowRestartService_RecoverAsync_RunsAgainstPostgres_AndFailsStrandedRun()
    {
        var runStore = _fixture.Services.GetRequiredService<IRunStore>();
        RunStoreChain.Find<EfRunStore>(runStore).Should().NotBeNull();

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

    [PostgresFact]
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

        (await runStore.TerminalizeForTestAsync(
            runId, RunStatus.Failed, ct: CancellationToken.None)).Should().BeTrue();

        var afterUpdate = await runStore.GetByStatusAsync(RunStatus.InProgress);
        afterUpdate.Select(r => r.Id).Should().NotContain(runId,
            "after the status update the run must no longer appear under InProgress");

        var fetched = await runStore.GetAsync(runId);
        fetched!.Status.Should().Be(RunStatus.Failed);
    }

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
        public string ConnectionString => _container.GetConnectionString();

        public async Task InitializeAsync()
        {
            await _container.StartAsync();
            _factory = new PostgresWebApplicationFactory(_container.GetConnectionString());
            // Force the host to build and start; recovery now runs independently of boot.
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

        private readonly string _role;

        public PostgresWebApplicationFactory(string connectionString, string role = AppRole.Web)
        {
            _connectionString = connectionString;
            _role = role;
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
            builder.UseSetting("App:Role", _role);

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
                    ["App:Role"] = _role,

                    ["Worktrees:BasePath"] = _worktreesPath,
                    ["Checkpoints:Path"] = _checkpointsPath,
                    ["Coordinator:Checkpoints:Path"] = _coordinatorCheckpointsPath,
                    ["Testing:BypassGitHubOrgAuthorization"] = "true",
                    ["Testing:BypassGitHubTokenAuth"] = "true",
                    ["Auth:Mode"] = "GitHubLegacy",
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
