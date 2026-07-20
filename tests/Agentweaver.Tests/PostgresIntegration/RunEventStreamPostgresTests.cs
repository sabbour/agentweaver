using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Tests.PostgresIntegration;

[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class RunEventStreamPostgresTests(PostgresFixture pg)
{
    [PostgresRequiredFact]
    public async Task AppendAsync_ConcurrentSameRunAcrossIndependentStreams_AssignsUniqueContiguousSequences()
    {
        var runId = "run-events-pg-" + Guid.NewGuid().ToString("N");
        const int workerCount = 8;
        const int eventsPerWorker = 15;

        var providers = new List<ServiceProvider>();
        var streams = new List<EfRunEventStream>();
        try
        {
            for (var i = 0; i < workerCount; i++)
            {
                var services = new ServiceCollection();
                services.AddDbContextFactory<MemoryDbContext>(opts =>
                    opts.UseNpgsql(pg.ConnectionString,
                        n => n.MigrationsAssembly("Agentweaver.Api.Migrations.Postgres")));
                services.AddLogging();
                var provider = services.BuildServiceProvider();
                providers.Add(provider);
                streams.Add(new EfRunEventStream(provider.GetRequiredService<IDbContextFactory<MemoryDbContext>>()));
            }

            var start = new Barrier(workerCount);
            var workers = streams.Select((stream, worker) => Task.Run(async () =>
            {
                start.SignalAndWait();
                for (var index = 0; index < eventsPerWorker; index++)
                {
                    _ = await stream.AppendAsync(runId, new RunEvent(0, EventTypes.ToolCall, new
                    {
                        worker,
                        index,
                        requestId = $"{worker:D2}-{index:D2}",
                    })).ConfigureAwait(false);
                }
            })).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);

            var verifier = new EfRunEventStream(pg.Factory);
            _ = await verifier.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { })).ConfigureAwait(false);

            await using var db = await pg.CreateDbContextAsync().ConfigureAwait(false);
            var rows = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId)
                .OrderBy(e => e.Sequence)
                .ToListAsync()
                .ConfigureAwait(false);

            var expectedCount = (workerCount * eventsPerWorker) + 1;
            rows.Should().HaveCount(expectedCount);
            rows.Select(r => r.Sequence).Should().Equal(Enumerable.Range(1, expectedCount));
            rows.Select(r => r.Sequence).Distinct().Should().HaveCount(expectedCount);

            var replayed = new List<RunEvent>();
            await foreach (var evt in new EfRunEventStream(pg.Factory).SubscribeAsync(runId, 0))
                replayed.Add(evt);

            replayed.Should().HaveCount(expectedCount);
            replayed.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, expectedCount));
            replayed[^1].Type.Should().Be(EventTypes.RunCompleted);
        }
        finally
        {
            foreach (var provider in providers)
                provider.Dispose();
        }
    }

    [PostgresRequiredFact]
    public async Task AppendAsync_ExplicitSequence_DuplicateIdenticalEvent_IsIdempotent()
    {
        var runId = "run-events-pg-idempotent-" + Guid.NewGuid().ToString("N");
        var stream = new EfRunEventStream(pg.Factory);

        _ = await stream.AppendAsync(runId, new RunEvent(11, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        })).ConfigureAwait(false);

        _ = await stream.AppendAsync(runId, new RunEvent(11, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        })).ConfigureAwait(false);

        await using var db = await pg.CreateDbContextAsync().ConfigureAwait(false);
        var rows = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Sequence)
            .ToListAsync()
            .ConfigureAwait(false);

        rows.Should().ContainSingle();
        rows[0].Sequence.Should().Be(11);
        rows[0].EventType.Should().Be(EventTypes.ToolResult);
    }

    [PostgresRequiredFact]
    public async Task AppendAsync_ExplicitSequence_DuplicateDifferentPayload_Throws()
    {
        var runId = "run-events-pg-mismatch-" + Guid.NewGuid().ToString("N");
        var stream = new EfRunEventStream(pg.Factory);

        _ = await stream.AppendAsync(runId, new RunEvent(5, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        })).ConfigureAwait(false);

        var act = async () => _ = await stream.AppendAsync(runId, new RunEvent(5, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = false,
        })).ConfigureAwait(false);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit sequence collision*")
            .ConfigureAwait(false);
    }

    [PostgresRequiredFact]
    public async Task RecordNext_InterleavedWithDirectSequenceZeroAppend_PersistsBothEventsWithoutCollision()
    {
        var runId = "run-events-pg-mixed-" + Guid.NewGuid().ToString("N");
        const string entryEventType = "coordinator.topology";
        var inner = new EfRunEventStream(pg.Factory);
        var interleaved = new InterleavingRunEventStream(inner, entryEventType);
        var entry = new RunStreamEntry("owner", runId, interleaved);
        var start = new Barrier(2);

        var entryWrite = Task.Run(() =>
        {
            start.SignalAndWait();
            return entry.RecordNext(entryEventType, new
            {
                writer = "entry",
                marker = "entry-record-next",
            });
        });

        var directWrite = Task.Run(async () =>
        {
            start.SignalAndWait();
            await interleaved.EntryAppendIntercepted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            try
            {
                return await interleaved.AppendAsync(runId, new RunEvent(0, EventTypes.ToolCall, new
                {
                    writer = "direct",
                    marker = "direct-sequence-zero",
                })).ConfigureAwait(false);
            }
            finally
            {
                interleaved.ReleaseEntryAppend();
            }
        });

        var assigned = await Task.WhenAll(entryWrite, directWrite).ConfigureAwait(false);
        assigned[0].Should().NotBe(assigned[1]);

        await using var db = await pg.CreateDbContextAsync().ConfigureAwait(false);
        var rows = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == runId)
            .OrderBy(e => e.Sequence)
            .ToListAsync()
            .ConfigureAwait(false);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Sequence).Should().Equal(1, 2);
        rows.Count(r => r.EventType == entryEventType).Should().Be(1);
        rows.Count(r => r.EventType == EventTypes.ToolCall).Should().Be(1);
    }

    private sealed class InterleavingRunEventStream(IRunEventStream inner, string gateOnType) : IRunEventStream
    {
        private readonly TaskCompletionSource _entryAppendIntercepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseEntryAppend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _gateUsed;

        public TaskCompletionSource EntryAppendIntercepted => _entryAppendIntercepted;

        public async ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
        {
            if (evt.Sequence == 0
                && string.Equals(evt.Type, gateOnType, StringComparison.Ordinal)
                && Interlocked.Exchange(ref _gateUsed, 1) == 0)
            {
                _entryAppendIntercepted.TrySetResult();
                await _releaseEntryAppend.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return await inner.AppendAsync(runId, evt, ct).ConfigureAwait(false);
        }

        public IAsyncEnumerable<RunEvent> SubscribeAsync(string runId, int fromSequence = 0, CancellationToken ct = default) =>
            inner.SubscribeAsync(runId, fromSequence, ct);

        public ValueTask CompleteAsync(string runId, CancellationToken ct = default) =>
            inner.CompleteAsync(runId, ct);

        public Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(string runId, int fromSequence = 0, CancellationToken ct = default) =>
            inner.GetPersistedEventsAsync(runId, fromSequence, ct);

        public void ReleaseEntryAppend() => _releaseEntryAppend.TrySetResult();
    }
}
