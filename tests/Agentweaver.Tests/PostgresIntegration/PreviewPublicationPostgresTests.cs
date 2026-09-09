using System.Data.Common;
using Agentweaver.Api.Contracts;
using Agentweaver.Api.Endpoints;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Sandbox.Preview;
using Agentweaver.Domain;
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
        await using var terminalDb = await pg.CreateDbContextAsync();
        await using var terminalTx = await terminalDb.Database.BeginTransactionAsync();
        await terminalDb.Runs.Where(r => r.RunId == run.Id.ToString())
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status.ToApiString()));
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

        var result = await SandboxEndpoints.StartPreviewForRunAsync(
            run.Id.ToString(), 5173, run, preview, null!, streams, NullLogger.Instance,
            CancellationToken.None, runStore: new EfRunStore(pg.Factory));

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
        var terminal = new EfRunStore(Factory(observer)).TrySetTerminalStatusAsync(
            run.Id, RunStatus.Failed, DateTimeOffset.UtcNow, "terminated");
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
        public int StopCalls;
        public bool Enabled => true;
        public int AllowedPortMin => 3000;
        public int AllowedPortMax => 9000;
        public PreviewSession Session(string runId, int port) =>
            new("preview-token", runId, "preview-pod", port, "https://preview.example.test", DateTimeOffset.UtcNow);
        public Task<PreviewSession> StartPreviewAsync(
            string runId, int targetPort, string ownerUserId, CancellationToken ct = default,
            string? previewRunnerSessionId = null) => Task.FromResult(Session(runId, targetPort));
        public Task StopPreviewAsync(string token, CancellationToken ct = default)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<PreviewSession>> ListForRunAsync(string runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<PreviewSession>>([]);
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
            if (command.CommandText.Contains("UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("runs", StringComparison.OrdinalIgnoreCase))
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
