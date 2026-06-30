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

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class TestMemoryDbContextFactory(DbContextOptions<MemoryDbContext> options)
        : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => new(options);
    }
}
