using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using Agentweaver.Tests.Helpers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Runtime;

public sealed class ConditionalRunEventBatchTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Environment.CurrentDirectory, ".test-artifacts", "conditional-events-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(RunStatus.Completed, true)]
    [InlineData(RunStatus.Failed, true)]
    [InlineData(RunStatus.Declined, true)]
    [InlineData(RunStatus.Merged, true)]
    [InlineData(RunStatus.MergeFailed, true)]
    [InlineData(RunStatus.AssembleReady, true)]
    [InlineData(RunStatus.Completed, false)]
    [InlineData(RunStatus.Failed, false)]
    [InlineData(RunStatus.Declined, false)]
    [InlineData(RunStatus.Merged, false)]
    [InlineData(RunStatus.MergeFailed, false)]
    [InlineData(RunStatus.AssembleReady, false)]
    public async Task TerminalizationWinsAtPersistenceBoundary_RejectsBothEvents(RunStatus status, bool hasLocalEntry)
    {
        var (run, store, stream) = await CreateAsync();
        var paused = new PausingPreviewEventStream(stream);
        var streams = new RunStreamStore(paused);
        var entry = hasLocalEntry ? streams.Create(run.Id.ToString(), run.SubmittingUser) : null;
        var append = streams.TryRecordPreviewReadyAsync(
            run.Id.ToString(), new { ready = true }, store, CancellationToken.None);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await store.UpdateStatusAsync(run.Id, status, DateTimeOffset.UtcNow);
        paused.Resume.SetResult();

        (await append.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        entry?.GetSnapshotSince(0).Events.Should().BeEmpty();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
        if (!hasLocalEntry)
            streams.Get(run.Id.ToString()).Should().BeNull();
    }

    [Fact]
    public async Task SecondEventFails_BatchRollsBackAndNeverMirrorsReady()
    {
        var (run, store, stream) = await CreateAsync();
        var entry = new RunStreamStore(stream).Create(run.Id.ToString(), run.SubmittingUser);

        var append = () => entry.TryRecordPreviewReadyAsync(
            new FailingSecondPayload(), store, CancellationToken.None);

        await append.Should().ThrowAsync<InvalidOperationException>();
        entry.GetSnapshotSince(0).Events.Should().BeEmpty();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
        (await entry.TryRecordPreviewReadyAsync(new { ready = true }, store, CancellationToken.None)).Should().BeTrue();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Select(e => e.Sequence).Should().Equal(1, 2);
    }

    [Theory]
    [InlineData("review")]
    [InlineData("merge")]
    [InlineData("delete")]
    [InlineData("assemble")]
    public async Task SqlitePublicationClaim_BlocksEveryTerminalTransition(string transition)
    {
        var (run, store, stream) = await CreateAsync();
        await store.UpdateStatusAsync(run.Id,
            transition == "assemble" ? RunStatus.InProgress
                : transition == "merge" ? RunStatus.Merging : RunStatus.AwaitingReview, null);
        var guarded = (RunActiveClaimGuardedRunStore)store;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var append = guarded.TryWhileRunActiveAsync(run.Id, async () =>
        {
            entered.SetResult();
            await resume.Task;
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task terminal = transition switch
        {
            "review" => store.TryTransitionReviewAsync(run.Id, RunStatus.Declined, DateTimeOffset.UtcNow, "declined"),
            "merge" => store.CompleteMergingAsync(run.Id, RunStatus.Merged, DateTimeOffset.UtcNow, "merged"),
            "assemble" => store.SetAssembleReadyAsync(
                run.Id, "tree", "worktree", "diff", 1, DateTimeOffset.UtcNow),
            _ => store.DeleteAsync(run.Id),
        };
        try
        {
            terminal.IsCompleted.Should().BeFalse("terminal transitions must acquire the publication claim");
        }
        finally
        {
            resume.TrySetResult();
        }
        (await append).Should().BeTrue();
        await terminal.WaitAsync(TimeSpan.FromSeconds(5));
        var entry = new RunStreamStore(stream).Create(run.Id.ToString(), run.SubmittingUser);
        (await entry.TryRecordPreviewReadyAsync(new { }, store, CancellationToken.None)).Should().BeFalse();
        (await stream.GetPersistedEventsAsync(run.Id.ToString())).Should().BeEmpty();
    }

    private async Task<(Run Run, IRunStore Store, IRunEventStream Stream)> CreateAsync()
    {
        Directory.CreateDirectory(_directory);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Path"] = Path.Combine(_directory, "runs.db"),
        }).Build();
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={SqliteMemoryDbPathResolver.Resolve(configuration)}").Options;
        using (var db = new MemoryDbContext(options))
            await db.Database.EnsureCreatedAsync();
        var runDb = new SqliteDb(configuration);
        await runDb.EnsureCreatedAsync();
        var store = new RunActiveClaimGuardedRunStore(new SqliteRunStore(runDb), new RunActiveClaimGuard());
        var stream = new SqliteRunEventStream(configuration);
        var run = new Run
        {
            Id = RunId.New(),
            RepositoryPath = ".",
            OriginatingBranch = "dev",
            ModelSource = ModelSource.GitHubCopilot,
            Task = "conditional event regression",
            SubmittingUser = "owner",
            Status = RunStatus.InProgress,
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.InsertAsync(run);
        return (run, store, stream);
    }

    private sealed class FailingSecondPayload
    {
        private int _reads;
        public string Value => Interlocked.Increment(ref _reads) == 2
            ? throw new InvalidOperationException("second event rejected")
            : "ready";
    }

    public void Dispose()
    {
        // Close only these fixture-specific pools, never pools used by concurrent tests.
        foreach (var file in Directory.EnumerateFiles(_directory, "*.db"))
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = file,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                    Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
                    Pooling = true,
                }.ToString());
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
            using var efConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}");
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(efConnection);
        }
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
