using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Auth.OAuth;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Coordinator;
using Agentweaver.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;

namespace Agentweaver.Tests;

public sealed class StartupRecoveryServiceTests
{
    [Fact]
    public async Task TestHostInitializationOptOut_PreservesHealthWhileOAuthReconciliationIsPending()
    {
        await using var factory = new RecoveryFactory(
            _ => Task.CompletedTask,
            ct => Task.Delay(Timeout.InfiniteTimeSpan, ct),
            bypassInitializationGate: true);
        using var client = factory.CreateClient();

        factory.Services.GetRequiredService<OAuthStaticClientReconciler>().IsInitialized.Should().BeFalse();
        (await client.GetAsync("/api/ping")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OAuthReconcileTransientFailure_RetriesWhilePingRemainsLive()
    {
        var attempts = 0;
        await using var factory = new RecoveryFactory(
            _ => Task.CompletedTask,
            _ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("transient database outage"))
                : Task.CompletedTask);
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/ping")).StatusCode.Should().Be(HttpStatusCode.OK);
        await WaitUntilAsync(async () => (await client.GetAsync("/api/health")).StatusCode == HttpStatusCode.OK);
        Volatile.Read(ref attempts).Should().Be(2);
    }

    [Fact]
    public async Task RegisteredProductionHost_PingResponsiveWhileRecoverySlow_AndOAuthReadinessFailsClosed()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oauthRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stages = new List<string>();
        var seededRun = RunId.New();
        await using var factory = new RecoveryFactory(
            async (services, ct) =>
            {
                var store = services.GetRequiredService<IRunStore>();
                await store.InsertAsync(new Run
                {
                    Id = seededRun,
                    RepositoryPath = "missing-repo",
                    OriginatingBranch = "main",
                    ModelSource = ModelSource.GitHubCopilot,
                    Task = "slow startup recovery",
                    SubmittingUser = "test-user",
                    Status = RunStatus.InProgress,
                    StartedAt = DateTimeOffset.UtcNow,
                }, ct);
                lock (stages) stages.Add("workflow");
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                await services.GetRequiredService<WorkflowRestartService>().RecoverAsync(ct);
                lock (stages) stages.Add("coordinator");
                await services.GetRequiredService<CoordinatorRunService>().RecoverInterruptedRunsAsync(ct);
                lock (stages) stages.Add("watchdog");
                await services.GetRequiredService<CoordinatorReconciler>().SweepAsync(ct);
                lock (stages) stages.Add("done");
            },
            ct => oauthRelease.Task.WaitAsync(ct));
        using var client = factory.CreateClient();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        (await client.GetAsync("/api/ping")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/health")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/oauth/authorize")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        oauthRelease.SetResult();
        await WaitUntilAsync(async () => (await client.GetAsync("/api/health")).StatusCode == HttpStatusCode.OK);
        release.SetResult();
        await WaitUntilAsync(() => Task.FromResult(stages.Count == 4));
        stages.Should().Equal("workflow", "coordinator", "watchdog", "done");
        (await factory.Services.GetRequiredService<IRunStore>().GetAsync(seededRun))!.Status
            .Should().Be(RunStatus.Failed);
        (await client.GetAsync("/api/ping")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task LeaderDeath_TransfersSweep_AndCompletedLeaderReleasesLockForNextPass()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        var sweeps = 0;
        Task<(bool, IAsyncDisposable)> Acquire(CancellationToken _)
        {
            var leader = Interlocked.CompareExchange(ref held, 1, 0) == 0;
            return Task.FromResult<(bool, IAsyncDisposable)>((leader,
                new Lease(() => { if (leader) Interlocked.Exchange(ref held, 0); })));
        }
        async Task Sweep(CancellationToken ct)
        {
            Interlocked.Increment(ref sweeps);
            var active = Interlocked.Increment(ref concurrent);
            Interlocked.Exchange(ref maxConcurrent, Math.Max(active, Volatile.Read(ref maxConcurrent)));
            try
            {
                if (Volatile.Read(ref sweeps) == 1)
                {
                    firstEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                else
                {
                    secondEntered.SetResult();
                    await secondRelease.Task.WaitAsync(ct);
                    secondCompleted.SetResult();
                }
            }
            finally { Interlocked.Decrement(ref concurrent); }
        }
        StartupRecoveryService Create() => new(Acquire, Sweep, NullLogger<StartupRecoveryService>.Instance,
            retryInterval: TimeSpan.FromMilliseconds(10));
        await using var first = CreateApp(Create());
        await first.StartAsync();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second = CreateApp(Create());
        await second.StartAsync();
        await first.StopAsync();
        await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        secondRelease.SetResult();
        await secondCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var third = CreateApp(Create());
        await third.StartAsync();
        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref sweeps) >= 3));
        Volatile.Read(ref sweeps).Should().BeGreaterThanOrEqualTo(3);
        Volatile.Read(ref maxConcurrent).Should().Be(1);
        await third.StopAsync();
        await second.StopAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!await predicate())
            await Task.Delay(20, deadline.Token);
    }

    private sealed class RecoveryFactory : WebApplicationFactory<Program>
    {
        private readonly Func<IServiceProvider, CancellationToken, Task> _sweep;
        private readonly Func<CancellationToken, Task> _oauth;
        private readonly bool _bypassInitializationGate;
        private readonly string _directory = Path.Combine(Environment.CurrentDirectory,
            ".startup-recovery-tests", Guid.NewGuid().ToString("N"));

        public RecoveryFactory(
            Func<CancellationToken, Task> sweep,
            Func<CancellationToken, Task> oauth,
            bool bypassInitializationGate = false)
            : this((_, ct) => sweep(ct), oauth, bypassInitializationGate)
        {
        }

        public RecoveryFactory(
            Func<IServiceProvider, CancellationToken, Task> sweep,
            Func<CancellationToken, Task> oauth,
            bool bypassInitializationGate = false)
        {
            _sweep = sweep;
            _oauth = oauth;
            _bypassInitializationGate = bypassInitializationGate;
            Directory.CreateDirectory(_directory);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Database:Path"] = Path.Combine(_directory, "runs.db"),
                    ["Worktrees:BasePath"] = Path.Combine(_directory, "worktrees"),
                    ["Checkpoints:Path"] = Path.Combine(_directory, "checkpoints"),
                    ["Coordinator:Checkpoints:Path"] = Path.Combine(_directory, "coordinator"),
                    ["Testing:BypassGitHubOrgAuthorization"] = "true",
                    ["Testing:BypassGitHubTokenAuth"] = "true",
                    ["Testing:BypassOAuthInitializationGate"] = _bypassInitializationGate.ToString(),
                    ["Auth:Mode"] = "GitHubLegacy",
                    ["Git:Author:Name"] = "Test",
                    ["Git:Author:Email"] = "test@localhost",
                    ["Providers:GitHubCopilot:ApiKey"] = "test-copilot-key",
                    ["Providers:GitHubCopilot:Endpoint"] = "https://api.githubcopilot.com",
                    ["Providers:GitHubCopilot:Model"] = "gpt-4o",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<StartupRecoveryStages>();
                services.AddSingleton(sp => new StartupRecoveryStages(sp)
                { Override = ct => _sweep(sp, ct) });
                services.RemoveAll<OAuthStaticClientReconciler>();
                services.AddSingleton(sp => new OAuthStaticClientReconciler(
                    sp.GetRequiredService<IServiceScopeFactory>(),
                    sp.GetRequiredService<OAuthServerConfiguration>(),
                    sp.GetRequiredService<IHostApplicationLifetime>(),
                    NullLogger<OAuthStaticClientReconciler>.Instance)
                { ReconcileOverride = _oauth, RetryInterval = TimeSpan.FromMilliseconds(50) });
            });
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            for (var attempt = 0; attempt < 5 && Directory.Exists(_directory); attempt++)
            {
                try { Directory.Delete(_directory, recursive: true); }
                catch (IOException) { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public async Task SlowRecovery_DoesNotBlockHostStartupOrPing_AndStopsWithHost()
    {
        using var started = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateService(
            _ => Task.FromResult<(bool, IAsyncDisposable)>((true, new Lease())),
            async ct =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled.SetResult();
                    throw;
                }
            },
            applicationStarted: started.Token);
        await using var app = CreateApp(service);

        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        entered.Task.IsCompleted.Should().BeFalse("recovery must wait for the listener to start");
        (await app.GetTestClient().GetAsync("/api/ping")).EnsureSuccessStatusCode();
        started.Cancel();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await app.GetTestClient().GetAsync("/api/ping")).EnsureSuccessStatusCode();

        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TwoReplicas_OnlyLeaderRunsRecovery_WhileBothServePing()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var attempts = 0;
        var sweeps = 0;

        Task<(bool, IAsyncDisposable)> Acquire(CancellationToken _)
        {
            var leader = Interlocked.CompareExchange(ref held, 1, 0) == 0;
            if (Interlocked.Increment(ref attempts) == 2)
                attempted.TrySetResult();
            return Task.FromResult<(bool, IAsyncDisposable)>(
                (leader, new Lease(() => { if (leader) Interlocked.Exchange(ref held, 0); })));
        }

        Task Sweep(CancellationToken ct)
        {
            Interlocked.Increment(ref sweeps);
            entered.TrySetResult();
            return release.Task.WaitAsync(ct);
        }

        await using var first = CreateApp(CreateService(Acquire, Sweep));
        await first.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var second = CreateApp(CreateService(Acquire, Sweep));
        await second.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        (await first.GetTestClient().GetAsync("/api/ping")).EnsureSuccessStatusCode();
        (await second.GetTestClient().GetAsync("/api/ping")).EnsureSuccessStatusCode();
        Volatile.Read(ref sweeps).Should().Be(1);
        Volatile.Read(ref held).Should().Be(1);

        release.SetResult();
        await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await second.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Read(ref held).Should().Be(0);
    }

    [Fact]
    public async Task TimedOutSweep_ReleasesLeaderLockAndRetries()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquired = 0;
        var released = 0;
        var sweeps = 0;
        var service = new StartupRecoveryService(
            _ =>
            {
                Interlocked.Increment(ref acquired);
                return Task.FromResult<(bool, IAsyncDisposable)>(
                    (true, new Lease(() => Interlocked.Increment(ref released))));
            },
            async ct =>
            {
                if (Interlocked.Increment(ref sweeps) == 1)
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                else
                    completed.SetResult();
            },
            NullLogger<StartupRecoveryService>.Instance,
            sweepTimeout: TimeSpan.FromMilliseconds(50),
            retryInterval: TimeSpan.FromMilliseconds(10));
        await using var app = CreateApp(service);

        await app.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        (await app.GetTestClient().GetAsync("/api/ping")).EnsureSuccessStatusCode();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Volatile.Read(ref acquired).Should().Be(2);
        Volatile.Read(ref released).Should().Be(2);
        Volatile.Read(ref sweeps).Should().Be(2);
    }

    [Fact]
    public async Task CompletedSweep_ReleasesLockAndRunsAgainOnNextInterval()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = 0;
        var maxConcurrent = 0;
        var sweeps = 0;
        var service = new StartupRecoveryService(
            _ =>
            {
                Interlocked.CompareExchange(ref held, 1, 0).Should().Be(0);
                return Task.FromResult<(bool, IAsyncDisposable)>((true,
                    new Lease(() => Interlocked.Exchange(ref held, 0))));
            },
            async ct =>
            {
                var count = Interlocked.Increment(ref sweeps);
                Interlocked.Exchange(ref maxConcurrent, Math.Max(Volatile.Read(ref maxConcurrent), Volatile.Read(ref held)));
                if (count == 2) completed.TrySetResult();
                await Task.CompletedTask;
            },
            NullLogger<StartupRecoveryService>.Instance,
            retryInterval: TimeSpan.FromMilliseconds(10));
        await using var app = CreateApp(service);
        await app.StartAsync();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await app.StopAsync();
        Volatile.Read(ref held).Should().Be(0);
        Volatile.Read(ref maxConcurrent).Should().Be(1);
    }

    private static StartupRecoveryService CreateService(
        Func<CancellationToken, Task<(bool IsLeader, IAsyncDisposable Lease)>> acquire,
        Func<CancellationToken, Task> recover,
        CancellationToken? applicationStarted = null) =>
        new(acquire, recover, NullLogger<StartupRecoveryService>.Instance,
            applicationStarted: applicationStarted);

    private static WebApplication CreateApp(StartupRecoveryService recovery)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHostedService(_ => recovery);
        var app = builder.Build();
        app.MapGet("/api/ping", () => Results.Ok(new { status = "ok" }));
        return app;
    }

    private sealed class Lease(Action? onDispose = null) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            onDispose?.Invoke();
            return ValueTask.CompletedTask;
        }
    }
}
