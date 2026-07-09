using System.Collections.Concurrent;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Api.Memory;
using Agentweaver.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Tests.Runtime;

public sealed class EfRunEventStreamTests : IDisposable
{
    private readonly string _dir;
    private readonly DbContextOptions<MemoryDbContext> _options;

    public EfRunEventStreamTests()
    {
        _dir = Path.Combine(Environment.CurrentDirectory, ".test-artifacts", "ef-run-events-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "memory.db")}")
            .Options;

        using var db = new MemoryDbContext(_options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task SubscribeAsync_TailsEventsWrittenByAnotherStreamInstance()
    {
        var runId = "run-cross-replica";
        var producer = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var subscriber = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var received = new List<RunEvent>();
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in subscriber.SubscribeAsync(runId, 0, cts.Token))
                received.Add(evt);
        });

        await Task.Delay(300, cts.Token);
        await producer.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorStarted, new { goal = "build" }), cts.Token);
        await producer.AppendAsync(runId, new RunEvent(2, EventTypes.RunCompleted, new { result = "confirmed" }), cts.Token);

        await consume;
        received.Select(e => e.Sequence).Should().Equal(1, 2);
        received.Select(e => e.Type).Should().Equal(EventTypes.CoordinatorStarted, EventTypes.RunCompleted);
    }

    [Fact]
    public async Task RunStreamStore_RecordNext_MirrorsEventsToSharedStream()
    {
        var runId = "run-store-mirror";
        var producer = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var subscriber = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var store = new RunStreamStore(producer);

        var entry = store.Create(runId, "user-a");
        entry.RecordNext(EventTypes.CoordinatorOutcomeSpec, new { status = "awaiting_confirmation" });
        entry.RecordNext(EventTypes.RunCompleted, new { result = "confirmed" });

        var received = new List<RunEvent>();
        await foreach (var evt in subscriber.SubscribeAsync(runId, 0))
            received.Add(evt);

        received.Select(e => e.Sequence).Should().Equal(1, 2);
        received[0].Type.Should().Be(EventTypes.CoordinatorOutcomeSpec);
    }

    [Fact]
    public async Task SubscribeAsync_AfterLateAppendFollowingTerminal_DrainsPersistedDiagnosticsThenCompletes()
    {
        var runId = "run-late-assembly-ef";
        var producer = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        await producer.AppendAsync(runId, new RunEvent(1, EventTypes.RunAssembleReady, new { }));
        await producer.CompleteAsync(runId);
        await producer.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorAssemblyBlocked, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            detail = "outer (inner: real configure 500)",
        }));
        await producer.AppendAsync(runId, new RunEvent(3, EventTypes.CoordinatorAssemblyFailed, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            detail = "terminal after retries",
        }));

        var subscriber = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var replayed = await ReplayWithTimeoutAsync(subscriber, runId);

        replayed.Select(e => e.Sequence).Should().Equal(1, 2, 3);
        replayed.Select(e => e.Type).Should().ContainInOrder(
            EventTypes.RunAssembleReady,
            EventTypes.CoordinatorAssemblyBlocked,
            EventTypes.CoordinatorAssemblyFailed);
        System.Text.Json.JsonSerializer.Serialize(replayed[1].Payload).Should().Contain("real configure 500");
    }

    [Fact]
    public async Task SubscribeAsync_PersistedCoordinatorAssemblyFailedAfterRestart_CompletesWithoutRunTerminal()
    {
        var runId = "run-assembly-failed-terminal-ef";
        var producer = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        await producer.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorAssemblyFailed, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
        }));

        var subscriber = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var replayed = await ReplayWithTimeoutAsync(subscriber, runId);

        replayed.Should().ContainSingle();
        replayed[0].Type.Should().Be(EventTypes.CoordinatorAssemblyFailed);
    }

    [Fact]
    public async Task SubscribeAsync_PersistedRetryableAssemblyBlocked_StaysOpenForRecoveredEvent()
    {
        var runId = "run-retryable-blocked-replay-ef";
        var producer = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        await producer.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorAssemblyBlocked, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            retryable = true,
        }));

        var subscriber = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var received = new ConcurrentQueue<RunEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in subscriber.SubscribeAsync(runId, 0, cts.Token))
            {
                received.Enqueue(evt);
                if (evt.Type == EventTypes.CoordinatorRecovered)
                    break;
            }
        }, cts.Token);

        await WaitUntilAsync(() => received.Any(e => e.Type == EventTypes.CoordinatorAssemblyBlocked),
            TimeSpan.FromSeconds(5), "subscriber should replay the blocked event");
        consume.IsCompleted.Should().BeFalse("retryable assembly_blocked must not terminate the subscriber");

        await producer.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorRecovered, new { reason = "rearmed" }));
        await consume;

        received.Select(e => e.Type).Should().ContainInOrder(
            EventTypes.CoordinatorAssemblyBlocked,
            EventTypes.CoordinatorRecovered);
    }

    [Fact]
    public async Task SubscribeAsync_LiveRetryableAssemblyBlocked_StaysOpenForRecoveredEvent()
    {
        var runId = "run-retryable-blocked-live-ef";
        var stream = new EfRunEventStream(new TestMemoryDbContextFactory(_options));
        var received = new ConcurrentQueue<RunEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in stream.SubscribeAsync(runId, 0, cts.Token))
            {
                received.Enqueue(evt);
                if (evt.Type == EventTypes.CoordinatorRecovered)
                    break;
            }
        }, cts.Token);

        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorAssemblyBlocked, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            retryable = true,
        }));
        await WaitUntilAsync(() => received.Any(e => e.Type == EventTypes.CoordinatorAssemblyBlocked),
            TimeSpan.FromSeconds(5), "subscriber should receive the blocked event");
        consume.IsCompleted.Should().BeFalse("live retryable assembly_blocked must not close the stream");

        await stream.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorRecovered, new { reason = "rearmed" }));
        await consume;

        received.Select(e => e.Type).Should().ContainInOrder(
            EventTypes.CoordinatorAssemblyBlocked,
            EventTypes.CoordinatorRecovered);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }

    private static async Task<List<RunEvent>> ReplayWithTimeoutAsync(IRunEventStream stream, string runId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replayed = new List<RunEvent>();
        await foreach (var evt in stream.SubscribeAsync(runId, 0, cts.Token))
            replayed.Add(evt);
        return replayed;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        if (condition())
            return;

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
        while (!condition())
        {
            try
            {
                await timer.WaitForNextTickAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                break;
            }
        }

        condition().Should().BeTrue(because);
    }
}
