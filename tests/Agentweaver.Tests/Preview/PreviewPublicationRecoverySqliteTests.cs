using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.Preview;

public sealed class PreviewPublicationRecoverySqliteTests
{
    [Fact]
    public async Task RestartedStore_ReclaimsExpiredOwnerAndFencesReadyEvents()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = db.FilePath,
        }).Build();
        using (var memory = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={SqliteMemoryDbPathResolver.Resolve(config)}").Options))
            await memory.Database.EnsureCreatedAsync();
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = ".", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "recover preview",
            SubmittingUser = "owner", Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var first = new SqliteRunStore(db.Db);
        await first.InsertAsync(run);
        var runId = run.Id.ToString();
        const string originalOwner = "lost-api-request";
        const string retryOwner = "new-api-request";
        const string session = "same-healthy-preview-session";
        (await first.TryAcquirePreviewPublicationAsync(
            run.Id, originalOwner, DateTimeOffset.UtcNow.AddMilliseconds(300))).Should().BeTrue();

        var restarted = new SqliteRunStore(db.Db);
        var store = new PreviewPublicationLeaseRunStore(
            new RunActiveClaimGuardedRunStore(restarted, new RunActiveClaimGuard()));
        var events = new SqliteRunEventStream(config);
        var preview = new RecoveryPreviewService();
        var sessionResult = preview.Session(runId);
        var paused = new PausingPreviewEventStream(events);
        var staleStreams = new RunStreamStore(paused);
        var staleAppend = SandboxEndpoints.PublishPreviewReadyAsync(
            sessionResult, new { preview_runner_session_id = session }, preview, staleStreams, store,
            CancellationToken.None, originalOwner, run.LifecycleGeneration);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(350);
        (await store.TryAcquirePreviewPublicationAsync(
            run.Id, retryOwner, DateTimeOffset.UtcNow.AddMinutes(3))).Should().BeTrue();
        paused.Resume.SetResult();
        (await staleAppend.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse(
            "the stale append started before takeover but must check ownership at the commit boundary");
        (await first.TryRenewPreviewPublicationAsync(
            run.Id, originalOwner, DateTimeOffset.UtcNow.AddMinutes(3))).Should().BeFalse();
        await first.EndPreviewPublicationAsync(run.Id, originalOwner);
        (await store.IsPreviewPublicationOwnerAsync(run.Id, retryOwner)).Should().BeTrue();

        var streams = new RunStreamStore(events);
        (await SandboxEndpoints.PublishPreviewReadyAsync(
            sessionResult, new { preview_runner_session_id = session }, preview, streams, store,
            CancellationToken.None, retryOwner, run.LifecycleGeneration + 1)).Should().BeFalse();
        (await events.GetPersistedEventsAsync(runId)).Should().BeEmpty();

        var result = await SandboxEndpoints.StartPreviewForRunAsync(
            runId, 5173, run, preview, null!, streams, NullLogger.Instance,
            CancellationToken.None, session, store, publicationLeaseOwner: retryOwner,
            publicationGeneration: run.LifecycleGeneration);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        (await events.GetPersistedEventsAsync(runId)).Select(e => e.Type).Should().Equal(
            EventTypes.SandboxPreviewReady, EventTypes.CoordinatorPreviewReady);
        await store.EndPreviewPublicationAsync(run.Id, retryOwner);
    }

    [Fact]
    public async Task RestartedStore_RefusesLiveCompetingOwner()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = ".", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "competing preview",
            SubmittingUser = "owner", Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var original = new SqliteRunStore(db.Db);
        await original.InsertAsync(run);
        (await original.TryAcquirePreviewPublicationAsync(
            run.Id, "live-attempt", DateTimeOffset.UtcNow.AddMinutes(3))).Should().BeTrue();
        var retry = new SqliteRunStore(db.Db);
        (await retry.TryAcquirePreviewPublicationAsync(
            run.Id, "different-attempt-same-session", DateTimeOffset.UtcNow.AddMinutes(3)))
            .Should().BeFalse();
        (await original.TryRenewPreviewPublicationAsync(
            run.Id, "live-attempt", DateTimeOffset.UtcNow.AddMinutes(4))).Should().BeTrue();
    }

    [Fact]
    public async Task CleanupReservation_ExpiredOwnerLosesAndLiveOwnerBlocksTakeoverDuringStop()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = ".", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "preview cleanup race",
            SubmittingUser = "owner", Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var staleStore = new SqliteRunStore(db.Db);
        var replacement = new SqliteRunStore(db.Db);
        await staleStore.InsertAsync(run);
        (await staleStore.TryAcquirePreviewPublicationAsync(
            run.Id, "stale", DateTimeOffset.UtcNow.AddMinutes(-1))).Should().BeTrue();

        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            staleStore, run.Id, "stale", CancellationToken.None)).Should().BeFalse();
        (await replacement.TryAcquirePreviewPublicationAsync(
            run.Id, "replacement", DateTimeOffset.UtcNow.AddMinutes(3))).Should().BeTrue();
        await staleStore.EndPreviewPublicationAsync(run.Id, "stale");
        (await replacement.IsPreviewPublicationOwnerAsync(run.Id, "replacement")).Should().BeTrue();

        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = Task.Run(async () =>
        {
            if (!await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
                replacement, run.Id, "replacement", CancellationToken.None))
                return false;
            stopEntered.SetResult();
            await finishStop.Task;
            return true;
        });
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            (await staleStore.TryAcquirePreviewPublicationAsync(
                run.Id, "next", DateTimeOffset.UtcNow.AddMinutes(3)))
                .Should().BeFalse("the cleanup reservation covers the stop even when takeover races it");
        }
        finally
        {
            finishStop.SetResult();
        }
        (await cleanup.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        await replacement.EndPreviewPublicationAsync(run.Id, "replacement");
        (await staleStore.TryAcquirePreviewPublicationAsync(
            run.Id, "next", DateTimeOffset.UtcNow.AddMinutes(3))).Should().BeTrue(
            "takeover is allowed after the bounded process stop releases its reservation");
        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            staleStore, run.Id, "stale", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task TerminalCleanupReservation_BlocksReopenUntilProcessStopReleasesIt()
    {
        await using var db = await TestSqliteDb.CreateAsync();
        var run = new Run
        {
            Id = RunId.New(), RepositoryPath = ".", OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot, Task = "terminal preview cleanup",
            SubmittingUser = "owner", Status = RunStatus.Failed,
            StartedAt = DateTimeOffset.UtcNow,
        };
        var store = new SqliteRunStore(db.Db);
        await store.InsertAsync(run);
        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            store, run.Id, "cleanup-owner", CancellationToken.None)).Should().BeTrue();
        (await store.TryReopenTerminalToInProgressAsync(run.Id)).Should().BeFalse();
        await store.EndPreviewPublicationAsync(run.Id, "cleanup-owner");
        (await store.TryReopenTerminalToInProgressAsync(run.Id)).Should().BeTrue();
    }

    private sealed class RecoveryPreviewService : ISandboxPreviewService
    {
        public bool Enabled => true;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;
        public PreviewSession Session(string runId) =>
            new("preview-token", runId, "preview-pod", 5173, "https://preview.example.test", DateTimeOffset.UtcNow);
        public Task<PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null) => Task.FromResult(Session(runId));
        public Task StopPreviewAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreviewSession>>([]);
        public Task KeepAliveAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PreviewLifecycleState> ReconcilePreviewLifecycleAsync(
            string runId, CancellationToken ct = default) => Task.FromResult(PreviewLifecycleState.Previewable);
        public Task<bool> VerifyTokenForRunAsync(
            string token, string runId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<int> ReapAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
