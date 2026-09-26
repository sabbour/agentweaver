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
            CREATE TABLE IF NOT EXISTS "WorkPlans" (
                "Id" INTEGER NOT NULL PRIMARY KEY,
                "Status" TEXT NOT NULL,
                "ParentResumeState" TEXT NULL,
                "ParentResumeResultJson" TEXT NULL,
                "UpdatedAt" TEXT NOT NULL
            );
            INSERT OR REPLACE INTO "WorkPlans"
                ("Id", "Status", "ParentResumeState", "ParentResumeResultJson", "UpdatedAt")
            VALUES (1, 'complete', 'ready', '{}', '2026-09-26 00:00:00');
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
        replayed.Should().OnlyContain(e => e.TimestampUtc != default && e.TimestampUtc.Offset == TimeSpan.Zero,
            "direct durable appends must stamp every event with a UTC capture time");
    }

    [Fact]
    public async Task AppendIdempotentAsync_AcrossRestart_PersistsOneLogicalEvent()
    {
        const string runId = "run-idempotent";
        const string eventIdentity = "workflow-child-work-ready:request:hash";
        var producer = new SqliteRunEventStream(_config);
        var first = await producer.AppendWorkflowChildWorkReadyAsync(
            1,
            runId,
            eventIdentity,
            new RunEvent(0, EventTypes.WorkflowStep, new { status = "child_work_ready", attempt = 1 }));

        var afterRestart = new SqliteRunEventStream(_config);
        var duplicate = await afterRestart.AppendWorkflowChildWorkReadyAsync(
            1,
            runId,
            eventIdentity,
            new RunEvent(0, EventTypes.WorkflowStep, new { status = "child_work_ready", attempt = 2 }));

        duplicate.Should().NotBeNull();
        first.Should().NotBeNull();
        duplicate!.Sequence.Should().Be(first!.Sequence);
        var persisted = await afterRestart.GetPersistedEventsAsync(runId);
        persisted.Should().ContainSingle();
        persisted[0].Sequence.Should().Be(first.Sequence);
        System.Text.Json.JsonSerializer.Serialize(persisted[0].Payload).Should().Contain("\"attempt\":1");
    }

    [Fact]
    public async Task AppendIdempotentAsync_ConcurrentDuplicateCalls_PersistOneLogicalEvent()
    {
        const string runId = "run-idempotent-concurrent";
        const string eventIdentity = "workflow-child-work-ready:request:concurrent";
        var stream = new SqliteRunEventStream(_config);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(attempt =>
            Task.Run(() => stream.AppendWorkflowChildWorkReadyAsync(
                1,
                runId,
                eventIdentity,
                new RunEvent(0, EventTypes.WorkflowStep, new { status = "child_work_ready", attempt })))));

        results.Should().NotContainNulls();
        results.Select(result => result!.Sequence).Should()
            .OnlyContain(sequence => sequence == results[0]!.Sequence);
        (await stream.GetPersistedEventsAsync(runId)).Should().ContainSingle();
    }

    [Fact]
    public async Task AppendWorkflowChildWorkReadyAsync_SuppressedPlan_DoesNotPersistEvent()
    {
        const string runId = "run-suppressed-ready";
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "memory.db")}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE "WorkPlans"
                SET "Status" = 'cancelled', "ParentResumeState" = 'suppressed'
                WHERE "Id" = 1;
                """;
            command.ExecuteNonQuery();
        }
        var stream = new SqliteRunEventStream(_config);

        var appended = await stream.AppendWorkflowChildWorkReadyAsync(
            1,
            runId,
            "workflow-child-work-ready:cancelled",
            new RunEvent(0, EventTypes.WorkflowStep, new { status = "child_work_ready" }));

        appended.Should().BeNull();
        (await stream.GetPersistedEventsAsync(runId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReconnectAfterReopen_ReplaysHistoricalTerminalButClosesOnlyCurrentLifecycle()
    {
        const string runId = "reopened-run";
        CreateRunRow(runId, "failed", 1);
        var first = new SqliteRunEventStream(_config);
        await first.AppendAsync(runId, new RunEvent(0, EventTypes.RunFailed, new { reason = "old" }));

        // Simulate a revision after the stream was evicted and the process restarted.
        UpdateRunRow(runId, "in_progress", 2);
        var restarted = new SqliteRunEventStream(_config);
        var observed = new List<RunEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in restarted.SubscribeAsync(runId, ct: cancellation.Token))
                observed.Add(evt);
        }, cancellation.Token);

        await Task.Delay(100, cancellation.Token);
        subscriber.IsCompleted.Should().BeFalse("the generation-one terminal is historical evidence only");

        await restarted.AppendAsync(runId, new RunEvent(0, "agent.message.delta", new { delta = "continued" }));
        UpdateRunRow(runId, "completed", 2);
        await restarted.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { result = "current" }));
        await subscriber;

        observed.Select(evt => evt.Type).Should().Equal(
            EventTypes.RunFailed, "agent.message.delta", EventTypes.RunCompleted);
        observed.Count(evt => evt.Type == EventTypes.RunCompleted).Should().Be(1);
    }

    [Fact]
    public async Task ReconnectAfterTerminalOutboxWindow_WaitsForCurrentProjectedTerminalSequence()
    {
        const string runId = "reopened-terminal-outbox-window";
        CreateRunRow(runId, "failed", 1);
        var first = new SqliteRunEventStream(_config);
        await first.AppendTerminalOutcomeAsync(runId, TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "old" }, DateTimeOffset.UtcNow, 1));

        UpdateRunRow(runId, "completed", 2);
        CreateUnprojectedTerminalOutcome(runId, 2, "completed", EventTypes.RunCompleted, new { result = "current" });

        var restarted = new SqliteRunEventStream(_config);
        var observed = new List<RunEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in restarted.SubscribeAsync(runId, ct: cancellation.Token))
                observed.Add(evt);
        }, cancellation.Token);

        await WaitUntilAsync(
            () => observed.Count == 1,
            TimeSpan.FromSeconds(5),
            "the historical lifecycle terminal should be replayed");
        subscriber.IsCompleted.Should().BeFalse(
            "the terminal status has no current-generation projected event yet");

        await restarted.AppendTerminalOutcomeAsync(runId, TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "current" }, DateTimeOffset.UtcNow, 2));
        await subscriber;

        observed.Select(evt => evt.Type).Should().Equal(EventTypes.RunFailed, EventTypes.RunCompleted);
        observed.Count(evt => evt.Type == EventTypes.RunCompleted).Should().Be(1);
    }

    [Fact]
    public async Task SteeringRedirect_DoesNotTerminateReplayOrLiveTail()
    {
        const string runId = "redirect-run";
        CreateRunRow(runId, "in_progress", 1);
        var stream = new SqliteRunEventStream(_config);
        var observed = new List<RunEvent>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriber = Task.Run(async () =>
        {
            await foreach (var evt in stream.SubscribeAsync(runId, ct: cancellation.Token))
                observed.Add(evt);
        }, cancellation.Token);

        await stream.AppendAsync(runId, new RunEvent(
            0, EventTypes.RunCancelled, new { reason = "steering_redirect", directiveId = 7 }));
        await stream.AppendAsync(runId, new RunEvent(0, "agent.message.delta", new { delta = "redirected" }));
        await Task.Delay(100, cancellation.Token);
        subscriber.IsCompleted.Should().BeFalse();

        UpdateRunRow(runId, "completed", 1);
        await stream.AppendAsync(runId, new RunEvent(0, EventTypes.RunCompleted, new { result = "done" }));
        await subscriber;

        observed.Select(evt => evt.Type).Should().Equal(
            EventTypes.RunCancelled, "agent.message.delta", EventTypes.RunCompleted);
    }

    private void CreateRunRow(string runId, string status, int generation)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "agentweaver.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS runs (run_id TEXT PRIMARY KEY, status TEXT NOT NULL, lifecycle_generation INTEGER NOT NULL);"
            + " INSERT INTO runs (run_id, status, lifecycle_generation) VALUES ($runId, $status, $generation);";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    private void CreatePostTerminalAppendTrigger()
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "memory.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TRIGGER append_after_terminal_projection
            AFTER INSERT ON "RunEvents"
            WHEN NEW."EventType" = 'run.completed'
            BEGIN
                INSERT INTO "RunEvents" ("RunId", "Sequence", "EventType", "PayloadJson", "CreatedAt")
                VALUES (NEW."RunId", NEW."Sequence" + 1, 'race.post-claim-append', '{}', NEW."CreatedAt");
            END;
            """;
        command.ExecuteNonQuery();
    }

    private void UpdateRunRow(string runId, string status, int generation)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "agentweaver.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status = $status, lifecycle_generation = $generation WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    private void CreateUnprojectedTerminalOutcome(
        string runId,
        int generation,
        string status,
        string eventType,
        object payload)
    {
        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "agentweaver.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS terminal_run_outcomes (
                run_id TEXT NOT NULL,
                lifecycle_generation INTEGER NOT NULL,
                status TEXT NOT NULL,
                event_type TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                projected_at TEXT NULL,
                PRIMARY KEY (run_id, lifecycle_generation)
            );
            INSERT INTO terminal_run_outcomes (
                run_id, lifecycle_generation, status, event_type, payload_json, occurred_at)
            VALUES ($runId, $generation, $status, $eventType, $payload, $occurredAt);
            """;
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$eventType", eventType);
        command.Parameters.AddWithValue("$payload", System.Text.Json.JsonSerializer.Serialize(payload));
        command.Parameters.AddWithValue("$occurredAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
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
    public async Task AppendTerminalOutcome_IdenticalPayloadAcrossGenerations_PersistsOnePerGeneration()
    {
        var runId = "run-sqlite-reopened-identical-payload";
        var stream = new SqliteRunEventStream(_config);
        var payload = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "retriable_failure" }, DateTimeOffset.UtcNow, 1);

        await stream.AppendTerminalOutcomeAsync(runId, payload);
        await stream.AppendTerminalOutcomeAsync(runId, payload with { ExpectedLifecycleGeneration = 2 });
        await stream.AppendTerminalOutcomeAsync(runId, payload with { ExpectedLifecycleGeneration = 2 });

        var events = await new SqliteRunEventStream(_config).GetPersistedEventsAsync(runId);
        events.Where(evt => evt.Type == EventTypes.RunFailed).Should().HaveCount(2);
    }

    [Fact]
    public async Task DuplicateProviderTerminal_ReconnectsAtLinkedCurrentGenerationSequence()
    {
        const string runId = "run-sqlite-duplicate-provider";
        CreateRunRow(runId, "completed", 2);
        var producer = new SqliteRunEventStream(_config);
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "outbox" }, DateTimeOffset.UtcNow, 2);
        var canonical = await producer.AppendTerminalOutcomeAsync(runId, outcome);

        (await producer.TryLinkTerminalOutcomeAsync(
            runId, outcome, canonical)).Should().BeTrue();

        var restarted = new SqliteRunEventStream(_config);
        var replayed = await ReplayWithTimeoutAsync(restarted, runId);
        replayed.Should().ContainSingle().Which.Sequence.Should().Be(canonical.Sequence);
        replayed.Should().ContainSingle().Which.Type.Should().Be(EventTypes.RunCompleted);

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "memory.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT event_sequence FROM terminal_run_outcome_projections WHERE run_id = $runId AND lifecycle_generation = 2;";
        command.Parameters.AddWithValue("$runId", runId);
        Convert.ToInt32(command.ExecuteScalar()).Should().Be(canonical.Sequence);
    }

    [Fact]
    public async Task DuplicateProviderTerminal_DoesNotClaimUnmappedSameTypeEvent()
    {
        const string runId = "run-sqlite-duplicate-provider-unmapped";
        var stream = new SqliteRunEventStream(_config);
        var historical = await stream.AppendTerminalOutcomeAsync(runId, TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "same" }, DateTimeOffset.UtcNow, 1));
        var outcome = TerminalRunOutcome.Create(
            RunStatus.Failed, EventTypes.RunFailed, new { reason = "same" }, DateTimeOffset.UtcNow, 2);

        (await stream.TryLinkTerminalOutcomeAsync(runId, outcome, historical)).Should().BeFalse(
            "a generation-one projection cannot establish generation-two ownership");

        using var connection = new SqliteConnection($"Data Source={Path.Combine(_dir, "memory.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM terminal_run_outcome_projections WHERE run_id = $runId;";
        command.Parameters.AddWithValue("$runId", runId);
        Convert.ToInt32(command.ExecuteScalar()).Should().Be(1);
    }

    [Fact]
    public async Task AppendTerminalOutcome_PostClaimAppend_PublishesClaimedTerminalSequenceToTail()
    {
        const string runId = "run-terminal-projection-sequence";
        CreateRunRow(runId, "completed", 1);
        var stream = new SqliteRunEventStream(_config);
        CreatePostTerminalAppendTrigger();

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscription = stream.SubscribeAsync(runId, ct: cancellation.Token)
            .GetAsyncEnumerator(cancellation.Token);
        var next = subscription.MoveNextAsync().AsTask();

        var published = await stream.AppendTerminalOutcomeAsync(runId, TerminalRunOutcome.Create(
            RunStatus.Completed, EventTypes.RunCompleted, new { result = "done" }, DateTimeOffset.UtcNow, 1));
        (await next).Should().BeTrue();

        var persisted = await stream.GetPersistedEventsAsync(runId);
        var terminal = persisted.Should().ContainSingle(evt => evt.Type == EventTypes.RunCompleted).Subject;
        terminal.Sequence.Should().Be(1);
        persisted.Should().ContainSingle(evt => evt.Type == "race.post-claim-append").Which.Sequence.Should().Be(2);
        published.Sequence.Should().Be(terminal.Sequence);
        subscription.Current.Type.Should().Be(EventTypes.RunCompleted);
        subscription.Current.Sequence.Should().Be(terminal.Sequence);
        (await subscription.MoveNextAsync()).Should().BeFalse(
            "the tail must stop at the exact current-generation terminal winner");
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
