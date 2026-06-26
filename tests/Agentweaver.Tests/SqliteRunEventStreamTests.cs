using System.Collections.Concurrent;
using FluentAssertions;
using Agentweaver.Api.Infrastructure;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Agentweaver.Tests.Runtime;

/// <summary>
/// Validates the two-layer <see cref="SqliteRunEventStream"/>: synchronous SQLite write-through
/// (Layer 1, durable across "restart") + in-process channel tailing (Layer 2), and the gapless,
/// duplicate-free replay-then-tail hand-off of <see cref="IRunEventStream.SubscribeAsync"/>.
/// </summary>
public sealed class SqliteRunEventStreamTests : IDisposable
{
    private readonly string _dir;
    private readonly IConfiguration _config;

    public SqliteRunEventStreamTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "aw-evtstream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        // SqliteRunEventStream derives memory.db from the directory of Database:Path.
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Path"] = Path.Combine(_dir, "agentweaver.db"),
            })
            .Build();

        CreateRunEventsTable(Path.Combine(_dir, "memory.db"));
    }

    private static void CreateRunEventsTable(string memoryDbPath)
    {
        using var conn = new SqliteConnection($"Data Source={memoryDbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "RunEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_RunEvents" PRIMARY KEY AUTOINCREMENT,
                "RunId" TEXT NOT NULL,
                "Sequence" INTEGER NOT NULL,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RunEvents_RunId_Sequence" ON "RunEvents" ("RunId", "Sequence");
            """;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Replay_ReturnsFullDurableHistory_AfterSimulatedRestart()
    {
        var runId = "run-1";
        var producer = new SqliteRunEventStream(_config);
        await producer.AppendAsync(runId, new RunEvent(1, "agent.message.delta", new { delta = "a" }));
        await producer.AppendAsync(runId, new RunEvent(2, "agent.message.delta", new { delta = "b" }));
        await producer.AppendAsync(runId, new RunEvent(3, EventTypes.RunCompleted, new { }));

        // Simulate a process restart: drop all in-memory channel state, keep the SQLite file.
        var afterRestart = new SqliteRunEventStream(_config);

        var replayed = new List<RunEvent>();
        await foreach (var evt in afterRestart.SubscribeAsync(runId, 0))
            replayed.Add(evt);

        replayed.Select(e => e.Sequence).Should().Equal(1, 2, 3);
        replayed[^1].Type.Should().Be(EventTypes.RunCompleted);
    }

    [Fact]
    public async Task Subscribe_FromCursor_ReturnsOnlyNewerEvents_NoDuplicate()
    {
        var runId = "run-2";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, "a", new { }));
        await stream.AppendAsync(runId, new RunEvent(2, "b", new { }));
        await stream.AppendAsync(runId, new RunEvent(3, EventTypes.RunFailed, new { }));

        var seen = new List<int>();
        await foreach (var evt in stream.SubscribeAsync(runId, fromSequence: 1))
            seen.Add(evt.Sequence);

        seen.Should().Equal(2, 3);
    }

    [Fact]
    public async Task ReplayThenTail_DeliversLiveEvents_GaplessAcrossBoundary()
    {
        var runId = "run-3";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, "a", new { }));
        await stream.AppendAsync(runId, new RunEvent(2, "b", new { }));

        var received = new ConcurrentQueue<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var consume = Task.Run(async () =>
        {
            await foreach (var evt in stream.SubscribeAsync(runId, 0, cts.Token))
            {
                received.Enqueue(evt.Sequence);
                if (evt.Type == EventTypes.RunCompleted) break;
            }
        });

        await WaitUntilAsync(() => received.Count >= 2, TimeSpan.FromSeconds(5),
            "subscriber should replay existing events before live appends");
        await stream.AppendAsync(runId, new RunEvent(3, "c", new { }));
        await stream.AppendAsync(runId, new RunEvent(4, EventTypes.RunCompleted, new { }));

        await consume;

        received.ToArray().Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task CompleteAsync_ClosesChannel_SubscriberCompletes()
    {
        var runId = "run-4";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, "a", new { }));

        var received = new ConcurrentQueue<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var consume = Task.Run(async () =>
        {
            await foreach (var evt in stream.SubscribeAsync(runId, 0, cts.Token))
                received.Enqueue(evt.Sequence);
        });

        await WaitUntilAsync(() => received.Count >= 1, TimeSpan.FromSeconds(5),
            "subscriber should replay the initial event before completion");
        await stream.AppendAsync(runId, new RunEvent(2, "b", new { }));
        await stream.CompleteAsync(runId);

        await consume; // Should complete (not hang) once the channel is closed.
        received.ToArray().Should().Equal(1, 2);
    }

    [Fact]
    public async Task AppendAsync_IsIdempotent_OnDuplicateSequence()
    {
        var runId = "run-5";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.RunCompleted, new { }));
        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.RunCompleted, new { })); // duplicate (RunId, Sequence)

        var afterRestart = new SqliteRunEventStream(_config);
        var replayed = new List<RunEvent>();
        await foreach (var evt in afterRestart.SubscribeAsync(runId, 0))
            replayed.Add(evt);

        replayed.Should().HaveCount(1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort; pooled handles may linger */ }
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
