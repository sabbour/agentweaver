using System.Data.Common;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Infrastructure.Ef;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class PreviewPublicationPostgresTests(PostgresFixture pg)
{
    [PostgresRequiredFact]
    public async Task TerminalizationWinsDuringConditionalUpdate_NoReadyEvents()
    {
        var run = await CreateRunAsync();
        await using var terminalDb = await pg.CreateDbContextAsync();
        await using var terminalTx = await terminalDb.Database.BeginTransactionAsync();
        await terminalDb.Runs.Where(r => r.RunId == run.Id.ToString())
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "failed"));
        var observer = new UpdateObserver();
        var stream = new EfRunEventStream(Factory(observer));
        var entry = new RunStreamStore(stream).Create(run.Id.ToString(), run.SubmittingUser);

        var append = entry.TryRecordPreviewReadyAsync(new { }, new EfRunStore(pg.Factory), CancellationToken.None);
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
        entry.GetSnapshotSince(0).Events.Should().BeEmpty();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
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
