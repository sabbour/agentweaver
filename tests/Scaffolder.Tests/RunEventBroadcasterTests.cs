using FluentAssertions;
using Scaffolder.Api.Infrastructure;
using Scaffolder.Api.Streaming;
using Scaffolder.Domain;
using Scaffolder.Tests.Helpers;

namespace Scaffolder.Tests.Streaming;

/// <summary>
/// Verifies FR-021 SSE fan-out, resume (backfill-then-live), and deduplication
/// semantics. All tests use a real temp SQLite database and the real broadcaster.
/// </summary>
public sealed class RunEventBroadcasterTests : IAsyncLifetime
{
    private TestSqliteDb _testDb = null!;
    private SqliteEventStore _store = null!;
    private RunEventBroadcaster _broadcaster = null!;

    public async Task InitializeAsync()
    {
        _testDb = await TestSqliteDb.CreateAsync();
        _store = new SqliteEventStore(_testDb.Db);
        _broadcaster = new RunEventBroadcaster(_store);
    }

    public async Task DisposeAsync() => await _testDb.DisposeAsync();

    private async Task<RunEvent> AppendAndPublish(RunId runId, string type = EventType.AgentMessage)
    {
        var persisted = await _store.AppendAsync(new RunEvent
        {
            RunId = runId,
            Sequence = 0,
            Type = type,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = "{}"
        });
        _broadcaster.Publish(persisted);
        return persisted;
    }

    [Fact]
    public async Task Subscribe_ReceivesPublishedEvents()
    {
        var runId = RunId.New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<RunEvent>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in _broadcaster.SubscribeAsync(runId, afterSequence: 0, cts.Token))
            {
                received.Add(evt);
                if (received.Count == 3) cts.Cancel();
            }
        });

        // Give subscriber time to attach.
        await Task.Delay(50);

        await AppendAndPublish(runId);
        await AppendAndPublish(runId);
        await AppendAndPublish(runId);

        try { await subscribeTask; } catch (OperationCanceledException) { }

        received.Should().HaveCount(3);
        received.Select(e => e.Sequence).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Subscribe_WithLastSeenSequence_BackfillsThenLive()
    {
        var runId = RunId.New();

        // Pre-publish 5 events to the store (and broadcaster, though no subscriber yet).
        for (var i = 0; i < 5; i++)
        {
            await AppendAndPublish(runId);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<RunEvent>();

        var subscribeTask = Task.Run(async () =>
        {
            // Subscribe after sequence 3 — expect backfill of 4,5 then live event 6.
            await foreach (var evt in _broadcaster.SubscribeAsync(runId, afterSequence: 3, cts.Token))
            {
                received.Add(evt);
                if (received.Count == 3) cts.Cancel();
            }
        });

        // Wait for subscriber to attach and complete backfill.
        await Task.Delay(100);

        // Publish one more live event.
        await AppendAndPublish(runId);

        try { await subscribeTask; } catch (OperationCanceledException) { }

        received.Should().HaveCount(3, because: "backfill gives events 4 and 5; live gives event 6");
        received.Select(e => e.Sequence).Should().BeEquivalentTo(new[] { 4, 5, 6 });
    }

    [Fact]
    public async Task TwoSubscribers_BothReceiveSameEvents()
    {
        var runId = RunId.New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var subscriber1 = new List<RunEvent>();
        var subscriber2 = new List<RunEvent>();

        var t1 = Task.Run(async () =>
        {
            await foreach (var evt in _broadcaster.SubscribeAsync(runId, afterSequence: 0, cts.Token))
            {
                subscriber1.Add(evt);
                if (subscriber1.Count == 3) break;
            }
        });

        var t2 = Task.Run(async () =>
        {
            await foreach (var evt in _broadcaster.SubscribeAsync(runId, afterSequence: 0, cts.Token))
            {
                subscriber2.Add(evt);
                if (subscriber2.Count == 3) break;
            }
        });

        await Task.Delay(100);

        await AppendAndPublish(runId);
        await AppendAndPublish(runId);
        await AppendAndPublish(runId);

        await Task.WhenAll(t1, t2);

        subscriber1.Should().HaveCount(3);
        subscriber2.Should().HaveCount(3);
        subscriber1.Select(e => e.Sequence).Should().BeEquivalentTo(
            subscriber2.Select(e => e.Sequence),
            because: "both subscribers must observe the same events at the same sequence (SC-006)");
    }

    [Fact]
    public async Task Complete_CausesSubscribersToFinish()
    {
        var runId = RunId.New();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = new List<RunEvent>();
        var subscribeTask = Task.Run(async () =>
        {
            await foreach (var evt in _broadcaster.SubscribeAsync(runId, afterSequence: 0, cts.Token))
            {
                received.Add(evt);
            }
        });

        await Task.Delay(50);
        await AppendAndPublish(runId);
        _broadcaster.Complete(runId);

        await subscribeTask;

        received.Should().HaveCount(1, because: "one event was published before completion");
    }

    [Fact]
    public async Task Deduplication_SkipsAlreadySeenSequence()
    {
        var runId = RunId.New();

        // Pre-store events 1 and 2.
        await AppendAndPublish(runId);
        await AppendAndPublish(runId);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<RunEvent>();

        var subscribeTask = Task.Run(async () =>
        {
            // afterSequence=1 means backfill gives event 2; subsequent live gives event 3.
            await foreach (var evt in _broadcaster.SubscribeAsync(runId, afterSequence: 1, cts.Token))
            {
                received.Add(evt);
                if (received.Count == 2) cts.Cancel();
            }
        });

        await Task.Delay(50);

        // Publish event 3 live; event 2 is already in the store so backfill handles it.
        await AppendAndPublish(runId);

        try { await subscribeTask; } catch (OperationCanceledException) { }

        received.Should().HaveCount(2);
        // Sequences must be unique — event 2 must not appear twice.
        received.Select(e => e.Sequence).Should().OnlyHaveUniqueItems(
            because: "deduplication must prevent the same sequence from being delivered twice");
        received.Select(e => e.Sequence).Should().BeInAscendingOrder();
    }
}
