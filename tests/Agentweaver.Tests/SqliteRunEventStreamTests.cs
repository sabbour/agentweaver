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
    public async Task AppendAsync_ConcurrentSameRunAcrossInstances_AssignsUniqueContiguousSequences()
    {
        var runId = "run-sqlite-concurrency";
        const int workerCount = 2;
        const int eventsPerWorker = 4;
        var streams = Enumerable.Range(0, workerCount)
            .Select(_ => new SqliteRunEventStream(_config))
            .ToArray();
        var start = new Barrier(workerCount);

        var workers = streams.Select((stream, worker) => Task.Run(async () =>
        {
            start.SignalAndWait();
            for (var index = 0; index < eventsPerWorker; index++)
            {
                await stream.AppendAsync(runId, new RunEvent(0, EventTypes.ToolCall, new
                {
                    worker,
                    index,
                }));
            }
        })).ToArray();

        await Task.WhenAll(workers);

        var verifier = new SqliteRunEventStream(_config);
        await verifier.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { }));

        var replayed = await ReplayWithTimeoutAsync(verifier, runId);
        var expectedCount = (workerCount * eventsPerWorker) + 1;
        replayed.Should().HaveCount(expectedCount);
        replayed.Select(e => e.Sequence).Should().Equal(Enumerable.Range(1, expectedCount));
        replayed.Select(e => e.Sequence).Distinct().Should().HaveCount(expectedCount);
    }

    [Fact]
    public async Task RecordNext_InterleavedWithDirectSequenceZeroAppend_PersistsBothEventsWithoutCollision()
    {
        var runId = "run-sqlite-mixed";
        const string entryEventType = "coordinator.topology";
        var inner = new SqliteRunEventStream(_config);
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
            await interleaved.EntryAppendIntercepted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try
            {
                return await interleaved.AppendAsync(runId, new RunEvent(0, EventTypes.ToolCall, new
                {
                    writer = "direct",
                    marker = "direct-sequence-zero",
                }));
            }
            finally
            {
                interleaved.ReleaseEntryAppend();
            }
        });

        var assigned = await Task.WhenAll(entryWrite, directWrite);
        assigned[0].Should().NotBe(assigned[1]);

        var persisted = await new SqliteRunEventStream(_config).GetPersistedEventsAsync(runId, 0);
        persisted.Should().HaveCount(2);
        persisted.Select(e => e.Sequence).Should().Equal(1, 2);
        persisted.Count(e => e.Type == entryEventType).Should().Be(1);
        persisted.Count(e => e.Type == EventTypes.ToolCall).Should().Be(1);
    }

    [Fact]
    public async Task AppendAsync_ExplicitSequence_DuplicateDifferentPayload_Throws()
    {
        var runId = "run-explicit-mismatch-sqlite";
        var stream = new SqliteRunEventStream(_config);
        await stream.AppendAsync(runId, new RunEvent(7, EventTypes.ToolResult, new
        {
            toolName = "project_list",
            success = true,
        }));

        Func<Task> act = async () =>
        {
            _ = await stream.AppendAsync(runId, new RunEvent(7, EventTypes.ToolResult, new
            {
                toolName = "project_list",
                success = false,
            }));
        };

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*explicit sequence collision*");
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
