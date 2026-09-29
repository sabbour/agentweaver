using System.Data.Common;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class PreviewPublicationPostgresTests(PostgresFixture pg)
{
    [PostgresRequiredFact]
    public async Task RecoveryGeneration_RefusesOldRenewalAndPublishesOnlyReplacementReadyPair()
    {
        var run = await CreateRunAsync();
        var original = new EfRunStore(pg.Factory);
        var replacement = new PreviewPublicationLeaseRunStore(new EfRunStore(pg.Factory));
        var until = DateTimeOffset.UtcNow.AddMinutes(3);
        (await original.TryAcquirePreviewPublicationAsync(run.Id, "old", until, run.LifecycleGeneration))
            .Should().BeTrue();
        await using (var db = await pg.Factory.CreateDbContextAsync())
            (await db.Runs.Where(r => r.RunId == run.Id.ToString())
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LifecycleGeneration, r => r.LifecycleGeneration + 1)))
                .Should().Be(1);
        var nextGeneration = run.LifecycleGeneration + 1;
        (await original.TryAcquirePreviewPublicationAsync(run.Id, "stale-new-owner", until, run.LifecycleGeneration))
            .Should().BeFalse();
        (await original.TryRenewPreviewPublicationAsync(run.Id, "old", until, run.LifecycleGeneration))
            .Should().BeFalse();
        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            original, run.Id, "old", run.LifecycleGeneration, CancellationToken.None)).Should().BeFalse();
        await original.EndPreviewPublicationAsync(run.Id, "old");
        (await replacement.TryAcquirePreviewPublicationAsync(run.Id, "new", until, nextGeneration))
            .Should().BeTrue();
        await original.EndPreviewPublicationAsync(run.Id, "old");
        (await replacement.IsPreviewPublicationOwnerAsync(run.Id, "new")).Should().BeTrue();

        var stream = new EfRunEventStream(pg.Factory);
        var streams = new RunStreamStore(stream);
        var preview = new RecordingPreviewService();
        var session = preview.Session(run.Id.ToString(), 5173);
        (await SandboxEndpoints.PublishPreviewReadyAsync(
            session, new { preview_runner_session_id = "old-process" }, preview, streams, replacement,
            CancellationToken.None, "old", run.LifecycleGeneration)).Should().BeFalse();
        var result = await SandboxEndpoints.StartPreviewForRunAsync(
            run.Id.ToString(), 5173, (await replacement.GetAsync(run.Id))!, preview, null!,
            streams, NullLogger.Instance, CancellationToken.None, "new-process", replacement,
            publicationLeaseOwner: "new", publicationGeneration: nextGeneration);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Select(e => e.Type).Should().Equal(
            EventTypes.SandboxPreviewReady, EventTypes.CoordinatorPreviewReady);
    }

    [PostgresRequiredFact]
    public async Task CommittedReadyAfterOwnerLoss_ReusesPublishedRouteWithoutSecondReadyPair()
    {
        var run = await CreateRunAsync();
        var runId = run.Id.ToString();
        var store = new PreviewPublicationLeaseRunStore(new EfRunStore(pg.Factory));
        var stream = new EfRunEventStream(pg.Factory);
        var streams = new RunStreamStore(stream);
        var preview = new RecordingPreviewService();
        var route = preview.Session(runId, 5173);
        preview.ExistingSession = route;
        const string oldOwner = "old-api";
        const string retryOwner = "new-api";
        (await store.TryAcquirePreviewPublicationAsync(
            run.Id, oldOwner, DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeTrue();
        (await SandboxEndpoints.PublishPreviewReadyAsync(
            route, new
            {
                source = "preview-api", lifecycle_generation = run.LifecycleGeneration,
                preview_runner_session_id = "shared-session", target_port = 5173,
                session_id = route.Token, preview_url = route.PreviewUrl,
            },
            preview, streams, store, CancellationToken.None, oldOwner, run.LifecycleGeneration))
            .Should().BeTrue();
        (await store.TryRenewPreviewPublicationAsync(
            run.Id, oldOwner, DateTimeOffset.UtcNow.AddMinutes(-1), run.LifecycleGeneration)).Should().BeTrue();
        (await store.TryAcquirePreviewPublicationAsync(
            run.Id, retryOwner, DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeTrue();

        var result = await SandboxEndpoints.StartPreviewForRunAsync(
            runId, 5173, run, preview, null!, streams, NullLogger.Instance,
            CancellationToken.None, "shared-session", store, publicationLeaseOwner: retryOwner,
            publicationGeneration: run.LifecycleGeneration);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        preview.StartCalls.Should().Be(0);
        (await stream.GetPersistedEventsAsync(runId)).Select(e => e.Type).Should().Equal(
            EventTypes.SandboxPreviewReady, EventTypes.CoordinatorPreviewReady);
        await store.EndPreviewPublicationAsync(run.Id, retryOwner);
    }

    [PostgresRequiredFact]
    public async Task RestartedStore_ReclaimsExpiredOwnerAndFencesReadyEvents()
    {
        var run = await CreateRunAsync();
        var original = new EfRunStore(pg.Factory);
        const string originalOwner = "lost-api-request";
        const string retryOwner = "new-api-request";
        const string session = "same-healthy-preview-session";
        (await original.TryAcquirePreviewPublicationAsync(
            run.Id, originalOwner, DateTimeOffset.UtcNow.AddMilliseconds(300), run.LifecycleGeneration)).Should().BeTrue();

        var store = new PreviewPublicationLeaseRunStore(new EfRunStore(pg.Factory));
        var stream = new EfRunEventStream(pg.Factory);
        var preview = new RecordingPreviewService();
        var sessionResult = preview.Session(run.Id.ToString(), 5173);
        var paused = new PausingPreviewEventStream(stream);
        var staleAppend = SandboxEndpoints.PublishPreviewReadyAsync(
            sessionResult, new { preview_runner_session_id = session },
            preview, new RunStreamStore(paused), store,
            CancellationToken.None, originalOwner, run.LifecycleGeneration);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(350);
        (await store.TryAcquirePreviewPublicationAsync(
            run.Id, retryOwner, DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeTrue();
        paused.Resume.SetResult();
        (await staleAppend.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        (await original.TryRenewPreviewPublicationAsync(
            run.Id, originalOwner, DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeFalse();
        await original.EndPreviewPublicationAsync(run.Id, originalOwner);
        (await store.IsPreviewPublicationOwnerAsync(run.Id, retryOwner)).Should().BeTrue();

        var streams = new RunStreamStore(stream);
        (await SandboxEndpoints.PublishPreviewReadyAsync(
            sessionResult, new { preview_runner_session_id = session }, preview, streams, store,
            CancellationToken.None, retryOwner, run.LifecycleGeneration + 1)).Should().BeFalse();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();

        var result = await SandboxEndpoints.StartPreviewForRunAsync(
            run.Id.ToString(), 5173, run, preview, null!, streams, NullLogger.Instance,
            CancellationToken.None, session, store, publicationLeaseOwner: retryOwner,
            publicationGeneration: run.LifecycleGeneration);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Select(e => e.Type).Should().Equal(
            EventTypes.SandboxPreviewReady, EventTypes.CoordinatorPreviewReady);
        await store.EndPreviewPublicationAsync(run.Id, retryOwner);
    }

    [PostgresRequiredFact]
    public async Task RestartedStore_RefusesLiveCompetingOwner()
    {
        var run = await CreateRunAsync();
        var original = new EfRunStore(pg.Factory);
        (await original.TryAcquirePreviewPublicationAsync(
            run.Id, "live-attempt", DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeTrue();
        var retry = new EfRunStore(pg.Factory);
        (await retry.TryAcquirePreviewPublicationAsync(
            run.Id, "different-attempt-same-session", DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration))
            .Should().BeFalse();
        (await original.TryRenewPreviewPublicationAsync(
            run.Id, "live-attempt", DateTimeOffset.UtcNow.AddMinutes(4), run.LifecycleGeneration)).Should().BeTrue();
    }

    [PostgresRequiredFact]
    public async Task ExpiredOwnerCannotReserveCleanup_ReplacementHoldsLeaseThroughStop()
    {
        var run = await CreateRunAsync();
        var stale = new EfRunStore(pg.Factory);
        var replacement = new EfRunStore(pg.Factory);
        (await stale.TryAcquirePreviewPublicationAsync(
            run.Id, "stale", DateTimeOffset.UtcNow.AddMinutes(-1), run.LifecycleGeneration)).Should().BeTrue();
        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            stale, run.Id, "stale", run.LifecycleGeneration, CancellationToken.None)).Should().BeFalse();
        (await replacement.TryAcquirePreviewPublicationAsync(
            run.Id, "replacement", DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeTrue();
        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            replacement, run.Id, "replacement", run.LifecycleGeneration, CancellationToken.None)).Should().BeTrue();
        (await stale.TryAcquirePreviewPublicationAsync(
            run.Id, "next", DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration)).Should().BeFalse();
        await stale.EndPreviewPublicationAsync(run.Id, "stale");
        (await replacement.IsPreviewPublicationOwnerAsync(run.Id, "replacement")).Should().BeTrue();
    }

    [PostgresRequiredFact]
    public async Task TerminalCleanupReservation_BlocksReopenUntilReleased()
    {
        var run = await CreateRunAsync();
        var store = new EfRunStore(pg.Factory);
        (await store.TerminalizeForTestAsync(run.Id, RunStatus.Failed)).Should().BeTrue();
        (await SandboxEndpoints.CanCleanUpPreviewProcessAsync(
            store, run.Id, "cleanup-owner", run.LifecycleGeneration, CancellationToken.None)).Should().BeTrue();
        (await store.TryReopenTerminalToInProgressAsync(run.Id)).Should().BeFalse();
        await store.EndPreviewPublicationAsync(run.Id, "cleanup-owner");
        (await store.TryReopenTerminalToInProgressAsync(run.Id)).Should().BeTrue();
    }

    [PostgresRequiredFact]
    public Task TerminalizationWinsDuringConditionalUpdate_NoReadyEvents() =>
        AssertTerminalizationWinsAsync(RunStatus.Failed, hasLocalEntry: true);

    [PostgresRequiredFact]
    public Task AssembleReadyWinsDuringConditionalUpdate_NoReadyEvents() =>
        AssertTerminalizationWinsAsync(RunStatus.AssembleReady, hasLocalEntry: true);

    [PostgresRequiredFact]
    public async Task TerminalizationWithoutLocalEntry_NoReadyEventsAndCleansPublication()
    {
        await AssertTerminalizationWinsAsync(RunStatus.Failed, hasLocalEntry: false);
        await AssertTerminalizationWinsAsync(RunStatus.AssembleReady, hasLocalEntry: false);
    }

    private async Task AssertTerminalizationWinsAsync(RunStatus status, bool hasLocalEntry)
    {
        var run = await CreateRunAsync();
        var terminalOutcome = TerminalRunOutcome.Create(
            status,
            status switch
            {
                RunStatus.Failed => EventTypes.RunFailed,
                RunStatus.AssembleReady => EventTypes.RunAssembleReady,
                _ => throw new ArgumentOutOfRangeException(nameof(status)),
            },
            new { reason = "conditional update winner" },
            DateTimeOffset.UtcNow,
            1);
        await using var terminalDb = await pg.CreateDbContextAsync();
        await using var terminalTx = await terminalDb.Database.BeginTransactionAsync();
        await terminalDb.Runs.Where(r => r.RunId == run.Id.ToString())
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status.ToApiString())
                .SetProperty(r => r.EndedAt, terminalOutcome.OccurredAt));
        terminalDb.TerminalRunOutcomes.Add(new TerminalRunOutcomeRecord
        {
            RunId = run.Id.ToString(),
            LifecycleGeneration = 1,
            Status = status.ToApiString(),
            EventType = terminalOutcome.EventType,
            PayloadJson = terminalOutcome.Payload.GetRawText(),
            OccurredAt = terminalOutcome.OccurredAt,
        });
        await terminalDb.SaveChangesAsync();
        var observer = new UpdateObserver();
        var stream = new EfRunEventStream(Factory(observer));
        var streams = new RunStreamStore(stream);
        var entry = hasLocalEntry ? streams.Create(run.Id.ToString(), run.SubmittingUser) : null;
        var preview = new RecordingPreviewService();

        var append = SandboxEndpoints.PublishPreviewReadyAsync(
            preview.Session(run.Id.ToString(), 5173), new { }, preview, streams,
            new EfRunStore(pg.Factory), CancellationToken.None);
        await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            append.IsCompleted.Should().BeFalse("the conditional append must wait for the run row");
            (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
        }
        finally
        {
            await terminalTx.CommitAsync();
        }

        (await append.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        entry?.GetSnapshotSince(0).Events.Should().BeEmpty();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
        preview.StopCalls.Should().Be(1);
        if (!hasLocalEntry)
            streams.Get(run.Id.ToString()).Should().BeNull();
    }

    [PostgresRequiredFact]
    public async Task ActiveRunWithoutLocalEntry_PersistsOneReadyPairAndPreservesHistory()
    {
        var run = await CreateRunAsync();
        var stream = new EfRunEventStream(pg.Factory);
        await stream.AppendAsync(run.Id.ToString(),
            new RunEvent(0, EventTypes.RunStarted, new { run_id = run.Id.ToString() }, DateTimeOffset.UtcNow));
        var streams = new RunStreamStore(stream);
        var preview = new RecordingPreviewService();
        var store = new EfRunStore(pg.Factory);
        (await store.TryAcquirePreviewPublicationAsync(
            run.Id, "publication-owner", DateTimeOffset.UtcNow.AddMinutes(3), run.LifecycleGeneration))
            .Should().BeTrue();

        var result = await SandboxEndpoints.StartPreviewForRunAsync(
            run.Id.ToString(), 5173, run, preview, null!, streams, NullLogger.Instance,
            CancellationToken.None, runStore: store, publicationLeaseOwner: "publication-owner",
            publicationGeneration: run.LifecycleGeneration);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        var events = await stream.GetPersistedEventsAsync(run.Id.ToString());
        events.Select(e => e.Type).Should().Equal(
            EventTypes.RunStarted, EventTypes.SandboxPreviewReady, EventTypes.CoordinatorPreviewReady);
        events.Select(e => e.Sequence).Should().Equal(1, 2, 3);
        streams.Get(run.Id.ToString()).Should().BeNull("publication must not create an incomplete local history");
        preview.StopCalls.Should().Be(0);
    }

    [PostgresRequiredFact]
    public async Task PublicationWins_TerminalUpdateWaitsUntilBothEventsCommit()
    {
        var run = await CreateRunAsync();
        var commit = new CommitPause();
        var stream = new EfRunEventStream(Factory(commit));
        var entry = new RunStreamStore(stream).Create(run.Id.ToString(), run.SubmittingUser);
        var append = entry.TryRecordPreviewReadyAsync(new { }, new EfRunStore(pg.Factory), CancellationToken.None);
        await commit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var observer = new UpdateObserver();
        var terminal = new EfRunStore(Factory(observer)).TrySetTerminalOutcomeAsync(
            run.Id,
            TerminalRunOutcome.Create(
                RunStatus.Failed, EventTypes.RunFailed, new { reason = "terminated" },
                DateTimeOffset.UtcNow, 1),
            "terminated");
        try
        {
            await observer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            terminal.IsCompleted.Should().BeFalse("publication owns the run row until the batch commits");
            entry.GetSnapshotSince(0).Events.Should().BeEmpty();
            (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty("neither ready event is committed yet");
        }
        finally
        {
            commit.Resume.TrySetResult();
        }
        (await append.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await terminal.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeTrue();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Select(e => e.Type)
            .Should().Equal(EventTypes.SandboxPreviewReady, EventTypes.CoordinatorPreviewReady);
    }

    [PostgresRequiredFact]
    public async Task CommitFailure_RollsBackBothReadyEvents()
    {
        var run = await CreateRunAsync();
        var commit = new CommitPause { Fail = true };
        var stream = new EfRunEventStream(Factory(commit));
        var entry = new RunStreamStore(stream).Create(run.Id.ToString(), run.SubmittingUser);
        var append = entry.TryRecordPreviewReadyAsync(new { }, new EfRunStore(pg.Factory), CancellationToken.None);
        await commit.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        commit.Resume.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => append);
        entry.GetSnapshotSince(0).Events.Should().BeEmpty();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
    }

    private async Task<Run> CreateRunAsync()
    {
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = ".",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "preview publication transaction regression",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await new EfRunStore(pg.Factory).InsertAsync(run);
        return run;
    }

    private IDbContextFactory<MemoryDbContext> Factory(IInterceptor interceptor) =>
        new ContextFactory(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql(pg.ConnectionString).AddInterceptors(interceptor).Options);

    private sealed class ContextFactory(DbContextOptions<MemoryDbContext> options) : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }

    private sealed class RecordingPreviewService : ISandboxPreviewService
    {
        public Task<PreviewSession> StartRunBoundPreviewAsync(
            string runId, int targetPort, string ownerUserId, int expectedLifecycleGeneration,
            CancellationToken ct = default, string? previewRunnerSessionId = null,
            string? publicationLeaseOwner = null) =>
            StartPreviewAsync(runId, targetPort, ownerUserId, ct, previewRunnerSessionId);

        public int StopCalls;
        public int StartCalls;
        public PreviewSession? ExistingSession;
        public bool Enabled => true;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;
        public PreviewSession Session(string runId, int port) =>
            new("preview-token", runId, "preview-pod", port, "https://preview.example.test", DateTimeOffset.UtcNow);
        public Task<PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null)
        {
            StartCalls++;
            return Task.FromResult(Session(runId, targetPort));
        }
        public Task StopPreviewAsync(string token, CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreviewSession>>(
                ExistingSession is { } session && session.RunId == runId ? [session] : []);
        public Task KeepAliveAsync(string token, CancellationToken ct = default) => Task.CompletedTask;
        public Task<PreviewLifecycleState> ReconcilePreviewLifecycleAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult(PreviewLifecycleState.Previewable);
        public Task<bool> VerifyTokenForRunAsync(string token, string runId, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<int> ReapAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class UpdateObserver : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.OrdinalIgnoreCase)
                || (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                    && command.CommandText.Contains("runs", StringComparison.OrdinalIgnoreCase)))
                Entered.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CommitPause : DbTransactionInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail { get; init; }

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (Fail)
                throw new InvalidOperationException("conditional publication commit rejected");
            return result;
        }
    }
}
