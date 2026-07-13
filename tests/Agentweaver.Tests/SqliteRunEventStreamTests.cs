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

    [Fact]
    public async Task SubscribeAsync_AfterLateAppendFollowingTerminal_DrainsPersistedDiagnosticsThenCompletes()
    {
        var runId = "run-late-assembly";
        var stream = new SqliteRunEventStream(_config);

        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.RunAssembleReady, new { }));
        await stream.CompleteAsync(runId);
        await stream.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorAssemblyFailed, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            detail = "outer (inner: real configure 500)",
        }));

        var afterRestart = new SqliteRunEventStream(_config);
        var replayed = await ReplayWithTimeoutAsync(afterRestart, runId);

        replayed.Select(e => e.Sequence).Should().Equal(1, 2);
        replayed.Select(e => e.Type).Should().Equal(EventTypes.RunAssembleReady, EventTypes.CoordinatorAssemblyFailed);
        System.Text.Json.JsonSerializer.Serialize(replayed[^1].Payload).Should().Contain("real configure 500");
    }

    [Fact]
    public async Task SubscribeAsync_PersistedCoordinatorAssemblyFailedAfterRestart_CompletesWithoutRunTerminal()
    {
        var runId = "run-assembly-failed-terminal";
        var producer = new SqliteRunEventStream(_config);
        await producer.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorAssemblyFailed, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
        }));

        var afterRestart = new SqliteRunEventStream(_config);
        var replayed = await ReplayWithTimeoutAsync(afterRestart, runId);

        replayed.Should().ContainSingle();
        replayed[0].Type.Should().Be(EventTypes.CoordinatorAssemblyFailed);
    }

    [Fact]
    public async Task SubscribeAsync_PersistedRetryableAssemblyBlocked_StaysOpenForRecoveredEvent()
    {
        var runId = "run-retryable-blocked-replay";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorAssemblyBlocked, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            retryable = true,
        }));

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

        await WaitUntilAsync(() => received.Any(e => e.Type == EventTypes.CoordinatorAssemblyBlocked),
            TimeSpan.FromSeconds(5), "subscriber should replay the blocked event");
        consume.IsCompleted.Should().BeFalse("retryable assembly_blocked must not terminate the subscriber");

        await stream.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorRecovered, new { reason = "rearmed" }));
        await consume;

        received.Select(e => e.Type).Should().ContainInOrder(
            EventTypes.CoordinatorAssemblyBlocked,
            EventTypes.CoordinatorRecovered);
    }

    [Fact]
    public async Task SubscribeAsync_LiveRetryableAssemblyBlocked_StaysOpenForRecoveredEvent()
    {
        var runId = "run-retryable-blocked-live";
        var stream = new SqliteRunEventStream(_config);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var subscription = stream.SubscribeAsync(runId, 0, cts.Token)
            .GetAsyncEnumerator(cts.Token);

        var blockedMove = subscription.MoveNextAsync().AsTask();
        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.CoordinatorAssemblyBlocked, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
            retryable = true,
        }));
        (await blockedMove).Should().BeTrue();
        subscription.Current.Type.Should().Be(EventTypes.CoordinatorAssemblyBlocked);

        var recoveredMove = subscription.MoveNextAsync().AsTask();
        recoveredMove.IsCompleted.Should().BeFalse(
            "live retryable assembly_blocked must not close the stream");
        await stream.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorRecovered, new { reason = "rearmed" }));
        (await recoveredMove).Should().BeTrue();
        subscription.Current.Type.Should().Be(EventTypes.CoordinatorRecovered);
    }

    [Fact]
    public async Task AppendAsync_PostTerminalAgentMessageDelta_IsNotPersisted()
    {
        var runId = "run-postterminal-delta";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.RunAssembleReady, new { }));
        await stream.CompleteAsync(runId);
        // A straggling streaming delta arriving after the terminal must be dropped — never persisted,
        // so it can never resurrect/re-drive a completed run (#239 companion hardening).
        await stream.AppendAsync(runId, new RunEvent(2, EventTypes.AgentMessageDelta, new { delta = "late" }));

        var afterRestart = new SqliteRunEventStream(_config);
        var replayed = await ReplayWithTimeoutAsync(afterRestart, runId);

        replayed.Select(e => e.Sequence).Should().Equal(1);
        replayed.Select(e => e.Type).Should().Equal(EventTypes.RunAssembleReady);
    }

    [Fact]
    public async Task AppendAsync_PostTerminalDiagnostic_StillPersists()
    {
        var runId = "run-postterminal-diag";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(1, EventTypes.RunAssembleReady, new { }));
        await stream.CompleteAsync(runId);
        // Regression lock: ONLY agent.message.delta is dropped post-terminal — a diagnostic terminal
        // (coordinator.assembly_failed) MUST still persist + replay for the durable audit trail.
        await stream.AppendAsync(runId, new RunEvent(2, EventTypes.CoordinatorAssemblyFailed, new
        {
            reason = "build_test_infra_agenthost_launch_failed",
        }));

        var afterRestart = new SqliteRunEventStream(_config);
        var replayed = await ReplayWithTimeoutAsync(afterRestart, runId);

        replayed.Select(e => e.Sequence).Should().Equal(1, 2);
        replayed.Select(e => e.Type).Should().Equal(
            EventTypes.RunAssembleReady, EventTypes.CoordinatorAssemblyFailed);
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

    private static async Task<List<RunEvent>> ReplayWithTimeoutAsync(IRunEventStream stream, string runId)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var replayed = new List<RunEvent>();
        await foreach (var evt in stream.SubscribeAsync(runId, 0, cts.Token))
            replayed.Add(evt);
        return replayed;
    }
}
