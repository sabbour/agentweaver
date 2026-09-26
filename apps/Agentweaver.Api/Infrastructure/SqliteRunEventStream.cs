using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.AgentRuntime.Workflow;
using Agentweaver.Api.Coordinator;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Domain;
using Microsoft.Data.Sqlite;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Two-layer <see cref="IRunEventStream"/> implementation.
///
/// <para><b>Layer 1 — SQLite write-through (durability).</b> Every <see cref="AppendAsync"/> writes
/// the event row to the <c>RunEvents</c> table (in <c>memory.db</c>, shape frozen by migration
/// <c>20260616063937_AddRunEvents</c>) synchronously, in WAL mode, before the append is acknowledged.
/// Replay is therefore always complete after a crash/restart.</para>
///
/// <para><b>Layer 2 — in-process channel (low-latency fan-out).</b> Each active run has one bounded
/// <see cref="Channel{T}"/>. After the durable write, the event is published to the channel so live
/// subscribers tail it without polling. The channel is bounded (capacity 1000); when a slow/absent
/// consumer fills it, surplus live copies are dropped — they remain durable in SQLite and a
/// reconnecting subscriber recovers them via replay.</para>
///
/// <para><see cref="SubscribeAsync"/> performs the standard <b>replay-then-tail</b> pattern: it
/// replays persisted rows from the cursor, then tails the channel, skipping any event already seen
/// during replay so the hand-off is gapless and duplicate-free.</para>
/// </summary>
public sealed class SqliteRunEventStream : IRunEventStream
{
    private const int ChannelCapacity = 1000;

    private static readonly HashSet<string> TerminalTypes = new(StringComparer.Ordinal)
    {
        EventTypes.RunCompleted,
        EventTypes.RunFailed,
        EventTypes.RunCancelled,
        EventTypes.MergeCompleted,
        EventTypes.MergeFailed,
        EventTypes.ReviewDeclined,
        EventTypes.RunAssembleReady,
        EventTypes.CoordinatorAssemblyFailed,
    };

    private static readonly IReadOnlyDictionary<string, Type> PayloadTypes = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [EventTypes.WorkflowGraph] = typeof(GraphDescriptor),
        [EventTypes.CoordinatorGraph] = typeof(GraphDescriptor),
    };

    private readonly string _connectionString;
    private readonly string _runConnectionString;
    private readonly ConcurrentDictionary<string, Channel<RunEvent>> _channels = new();
    private readonly ConcurrentDictionary<string, byte> _completedRuns = new();
    private readonly object _channelsGate = new();
    private readonly ILogger<SqliteRunEventStream>? _logger;

    public SqliteRunEventStream(IConfiguration configuration, ILogger<SqliteRunEventStream>? logger = null)
    {
        _logger = logger;

        // The RunEvents table lives in the companion SQLite file used by MemoryDbContext. Resolve
        // the exact same path Program.cs uses so test hosts with distinct Database:Path values do
        // not collide on a process-wide temp\memory.db sidecar.
        var memoryDbPath = SqliteMemoryDbPathResolver.Resolve(configuration);
        Directory.CreateDirectory(Path.GetDirectoryName(memoryDbPath)!);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = memoryDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        _runConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = configuration["Database:Path"] ?? Path.Combine(AppPaths.DataDirectory, "agentweaver.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        EnsureTerminalOutcomeProjectionTable();
        EnsureRunEventIdentityColumn();
    }

    /// <inheritdoc />
    public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
    {
        evt = StampTimestamp(StructuredRunFailureTerminal.NormalizeFailure(evt));
        // #239 companion hardening: once a run is completed, drop streaming AgentMessageDelta events —
        // a straggling delta arriving after the terminal must never re-persist and re-drive the run.
        // ONLY agent.message.delta is dropped; every terminal/diagnostic/final-message/tool/usage/
        // subtask/topology event still persists post-terminal (durable audit + gapless replay).
        if (_completedRuns.ContainsKey(runId) && evt.Type == EventTypes.AgentMessageDelta)
            return ValueTask.FromResult(0);

        // Layer 1: synchronous, durable write-through BEFORE the channel publish so the event is
        // crash-safe before any live subscriber observes it. Honors a pre-assigned sequence when
        // present (idempotent via the unique (RunId, Sequence) index), otherwise assigns MAX+1.
        var sequence = WriteThrough(runId, evt, ct);

        if (_completedRuns.ContainsKey(runId))
        {
            _logger?.LogWarning(
                "Persisted late event {EventType} for completed run {RunId}; live channel remains closed",
                evt.Type, runId);
            return ValueTask.FromResult(sequence);
        }

        // Layer 2: publish to the live channel. TryWrite never blocks; if the bounded channel is
        // full (slow/absent consumer) the live copy is dropped — it stays durable in SQLite.
        var stamped = evt.Sequence == sequence ? evt : new RunEvent(sequence, evt.Type, evt.Payload, evt.TimestampUtc);
        lock (_channelsGate)
        {
            if (_completedRuns.ContainsKey(runId))
            {
                _logger?.LogWarning(
                    "Run {RunId} completed while appending event {EventType}; durable event {Sequence} will not resurrect live channel",
                    runId, evt.Type, sequence);
                return ValueTask.FromResult(sequence);
            }

            var channel = _channels.GetOrAdd(runId, _ => CreateChannel());
            channel.Writer.TryWrite(stamped);
        }

        return ValueTask.FromResult(sequence);
    }

    public Task<RunEvent> AppendIdentifiedAsync(
        string runId,
        string eventIdentity,
        RunEvent evt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventIdentity);
        ct.ThrowIfCancellationRequested();
        evt = StampTimestamp(StructuredRunFailureTerminal.NormalizeFailure(evt));

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var prior = connection.CreateCommand();
        prior.Transaction = tx;
        prior.CommandText =
            """
            SELECT "Sequence", "EventType", "PayloadJson", "CreatedAt"
            FROM "RunEvents"
            WHERE "RunId" = $runId AND "EventIdentity" = $eventIdentity;
            """;
        prior.Parameters.AddWithValue("$runId", runId);
        prior.Parameters.AddWithValue("$eventIdentity", eventIdentity);
        using (var priorReader = prior.ExecuteReader())
        {
            if (priorReader.Read())
            {
                var priorSequence = priorReader.GetInt32(0);
                var priorEventType = priorReader.GetString(1);
                if (!string.Equals(priorEventType, evt.Type, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Run event identity '{eventIdentity}' for run '{runId}' is already bound to event type '{priorEventType}'.");
                var priorEvent = new RunEvent(
                    priorSequence,
                    priorEventType,
                    DeserializePayload(runId, priorSequence, priorEventType, priorReader.GetString(2)),
                    DateTimeOffset.Parse(priorReader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                priorReader.Dispose();
                tx.Commit();
                return Task.FromResult(priorEvent);
            }
        }

        using var append = connection.CreateCommand();
        append.Transaction = tx;
        append.CommandText =
            """
            INSERT OR IGNORE INTO "RunEvents"
                ("RunId", "Sequence", "EventIdentity", "EventType", "PayloadJson", "CreatedAt")
            SELECT $runId, COALESCE(MAX("Sequence"), 0) + 1, $eventIdentity, $type, $payload, $createdAt
            FROM "RunEvents" WHERE "RunId" = $runId
            RETURNING "Sequence";
            """;
        append.Parameters.AddWithValue("$runId", runId);
        append.Parameters.AddWithValue("$eventIdentity", eventIdentity);
        append.Parameters.AddWithValue("$type", evt.Type);
        append.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(evt.Payload));
        append.Parameters.AddWithValue("$createdAt",
            evt.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        var inserted = append.ExecuteScalar();
        if (inserted is not null)
        {
            var assignedSequence = Convert.ToInt32(inserted, CultureInfo.InvariantCulture);
            tx.Commit();
            var recorded = evt with { Sequence = assignedSequence };
            PublishDurableEvent(runId, recorded);
            return Task.FromResult(recorded);
        }

        using var existing = connection.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText =
            """
            SELECT "Sequence", "EventType", "PayloadJson", "CreatedAt"
            FROM "RunEvents"
            WHERE "RunId" = $runId AND "EventIdentity" = $eventIdentity;
            """;
        existing.Parameters.AddWithValue("$runId", runId);
        existing.Parameters.AddWithValue("$eventIdentity", eventIdentity);
        using var reader = existing.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException(
                $"Run event identity '{eventIdentity}' for run '{runId}' was not persisted after a duplicate append.");
        var sequence = reader.GetInt32(0);
        var eventType = reader.GetString(1);
        if (!string.Equals(eventType, evt.Type, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Run event identity '{eventIdentity}' for run '{runId}' is already bound to event type '{eventType}'.");
        var persisted = new RunEvent(
            sequence,
            eventType,
            DeserializePayload(runId, sequence, eventType, reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        reader.Dispose();
        tx.Commit();
        return Task.FromResult(persisted);
    }

    public Task<RunEvent?> AppendWorkflowChildWorkReadyAsync(
        int workPlanId,
        string runId,
        string eventIdentity,
        RunEvent evt,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventIdentity);
        ct.ThrowIfCancellationRequested();
        evt = StampTimestamp(StructuredRunFailureTerminal.NormalizeFailure(evt));

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var claim = connection.CreateCommand();
        claim.Transaction = tx;
        claim.CommandText =
            """
            UPDATE "WorkPlans"
            SET "UpdatedAt" = "UpdatedAt"
            WHERE "Id" = $workPlanId
              AND "Status" = $complete
              AND "ParentResumeState" = $ready
              AND "ParentResumeResultJson" IS NOT NULL;
            """;
        claim.Parameters.AddWithValue("$workPlanId", workPlanId);
        claim.Parameters.AddWithValue("$complete", WorkPlanStatus.Complete);
        claim.Parameters.AddWithValue("$ready", Workflows.WorkflowChildWorkResumeStates.Ready);
        var eligible = claim.ExecuteNonQuery();

        using var prior = connection.CreateCommand();
        prior.Transaction = tx;
        prior.CommandText =
            """
            SELECT "Sequence", "EventType", "PayloadJson", "CreatedAt"
            FROM "RunEvents"
            WHERE "RunId" = $runId AND "EventIdentity" = $eventIdentity;
            """;
        prior.Parameters.AddWithValue("$runId", runId);
        prior.Parameters.AddWithValue("$eventIdentity", eventIdentity);
        using (var priorReader = prior.ExecuteReader())
        {
            if (priorReader.Read())
            {
                var priorSequence = priorReader.GetInt32(0);
                var priorEventType = priorReader.GetString(1);
                if (!string.Equals(priorEventType, evt.Type, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"Run event identity '{eventIdentity}' for run '{runId}' is already bound to event type '{priorEventType}'.");
                var priorEvent = new RunEvent(
                    priorSequence,
                    priorEventType,
                    DeserializePayload(runId, priorSequence, priorEventType, priorReader.GetString(2)),
                    DateTimeOffset.Parse(priorReader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
                priorReader.Dispose();
                tx.Commit();
                return Task.FromResult<RunEvent?>(priorEvent);
            }
        }

        if (eligible == 0)
        {
            tx.Commit();
            return Task.FromResult<RunEvent?>(null);
        }

        using var append = connection.CreateCommand();
        append.Transaction = tx;
        append.CommandText =
            """
            INSERT OR IGNORE INTO "RunEvents"
                ("RunId", "Sequence", "EventIdentity", "EventType", "PayloadJson", "CreatedAt")
            SELECT $runId, COALESCE(MAX("Sequence"), 0) + 1, $eventIdentity, $type, $payload, $createdAt
            FROM "RunEvents" WHERE "RunId" = $runId
            RETURNING "Sequence";
            """;
        append.Parameters.AddWithValue("$runId", runId);
        append.Parameters.AddWithValue("$eventIdentity", eventIdentity);
        append.Parameters.AddWithValue("$type", evt.Type);
        append.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(evt.Payload));
        append.Parameters.AddWithValue("$createdAt",
            evt.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        var inserted = append.ExecuteScalar();
        if (inserted is not null)
        {
            var assignedSequence = Convert.ToInt32(inserted, CultureInfo.InvariantCulture);
            tx.Commit();
            var recorded = evt with { Sequence = assignedSequence };
            PublishDurableEvent(runId, recorded);
            return Task.FromResult<RunEvent?>(recorded);
        }

        using var existing = connection.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText =
            """
            SELECT "Sequence", "EventType", "PayloadJson", "CreatedAt"
            FROM "RunEvents"
            WHERE "RunId" = $runId AND "EventIdentity" = $eventIdentity;
            """;
        existing.Parameters.AddWithValue("$runId", runId);
        existing.Parameters.AddWithValue("$eventIdentity", eventIdentity);
        using var reader = existing.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException(
                $"Run event identity '{eventIdentity}' for run '{runId}' was not persisted after a duplicate append.");
        var sequence = reader.GetInt32(0);
        var eventType = reader.GetString(1);
        if (!string.Equals(eventType, evt.Type, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Run event identity '{eventIdentity}' for run '{runId}' is already bound to event type '{eventType}'.");
        var persisted = new RunEvent(
            sequence,
            eventType,
            DeserializePayload(runId, sequence, eventType, reader.GetString(2)),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        reader.Dispose();
        tx.Commit();
        return Task.FromResult<RunEvent?>(persisted);
    }

    public Task<RunEvent> AppendTerminalOutcomeAsync(
        string runId,
        TerminalRunOutcome outcome,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var evt = StampTimestamp(outcome.ToRunEvent());
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var claim = connection.CreateCommand();
        claim.Transaction = tx;
        claim.CommandText =
            "INSERT OR IGNORE INTO terminal_run_outcome_projections (run_id, lifecycle_generation) VALUES ($runId, $generation);";
        claim.Parameters.AddWithValue("$runId", runId);
        claim.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        if (claim.ExecuteNonQuery() == 0)
        {
            using var existing = connection.CreateCommand();
            existing.Transaction = tx;
            existing.CommandText =
                """
                SELECT event."Sequence", event."CreatedAt"
                  FROM terminal_run_outcome_projections projection
                  JOIN "RunEvents" event
                    ON event."RunId" = projection.run_id
                   AND event."Sequence" = projection.event_sequence
                 WHERE projection.run_id = $runId
                   AND projection.lifecycle_generation = $generation
                   AND event."EventType" = $type;
                """;
            existing.Parameters.AddWithValue("$runId", runId);
            existing.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
            existing.Parameters.AddWithValue("$type", outcome.EventType);
            using var reader = existing.ExecuteReader();
            if (!reader.Read())
                throw new InvalidOperationException(
                    $"Terminal outcome projection claim exists without its event for run {runId} generation {outcome.ExpectedLifecycleGeneration}.");
            tx.Commit();
            var persistedExisting = new RunEvent(
                reader.GetInt32(0), evt.Type, evt.Payload,
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            PublishDurableEvent(runId, persistedExisting);
            return Task.FromResult(persistedExisting);
        }

        using var append = connection.CreateCommand();
        append.Transaction = tx;
        append.CommandText =
            """
            INSERT INTO "RunEvents" ("RunId", "Sequence", "EventType", "PayloadJson", "CreatedAt")
            SELECT $runId, COALESCE(MAX("Sequence"), 0) + 1, $type, $payload, $createdAt
            FROM "RunEvents" WHERE "RunId" = $runId
            RETURNING "Sequence";
            """;
        append.Parameters.AddWithValue("$runId", runId);
        append.Parameters.AddWithValue("$type", evt.Type);
        append.Parameters.AddWithValue("$payload", outcome.Payload.GetRawText());
        append.Parameters.AddWithValue("$createdAt",
            evt.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
        var terminalSequence = Convert.ToInt32(append.ExecuteScalar(), CultureInfo.InvariantCulture);
        using var projected = connection.CreateCommand();
        projected.Transaction = tx;
        projected.CommandText =
            """
            UPDATE terminal_run_outcome_projections
               SET event_sequence = $sequence
             WHERE run_id = $runId AND lifecycle_generation = $generation;
            """;
        projected.Parameters.AddWithValue("$runId", runId);
        projected.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        projected.Parameters.AddWithValue("$sequence", terminalSequence);
        projected.ExecuteNonQuery();
        tx.Commit();
        var persisted = evt with { Sequence = terminalSequence };
        PublishDurableEvent(runId, persisted);
        return Task.FromResult(persisted);
    }

    public Task<bool> TryLinkTerminalOutcomeAsync(
        string runId,
        TerminalRunOutcome outcome,
        RunEvent canonicalEvent,
        CancellationToken ct = default)
    {
        if (canonicalEvent.Sequence <= 0 || canonicalEvent.Type != outcome.EventType)
            return Task.FromResult(false);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var tx = connection.BeginTransaction();
        using var existing = connection.CreateCommand();
        existing.Transaction = tx;
        existing.CommandText =
            """
            SELECT 1
            FROM terminal_run_outcome_projections projection
            JOIN "RunEvents" event
              ON event."RunId" = projection.run_id
             AND event."Sequence" = projection.event_sequence
            WHERE projection.run_id = $runId
              AND projection.lifecycle_generation = $generation
              AND projection.event_sequence = $sequence
              AND event."EventType" = $type;
            """;
        existing.Parameters.AddWithValue("$runId", runId);
        existing.Parameters.AddWithValue("$generation", outcome.ExpectedLifecycleGeneration);
        existing.Parameters.AddWithValue("$sequence", canonicalEvent.Sequence);
        existing.Parameters.AddWithValue("$type", canonicalEvent.Type);
        if (existing.ExecuteScalar() is null)
            return Task.FromResult(false);
        tx.Commit();
        return Task.FromResult(true);
    }

    public async Task<IReadOnlyList<RunEvent>> AppendWhileRunActiveAsync(
        string runId, IReadOnlyList<RunEvent> events, IRunStore runStore, CancellationToken ct = default)
    {
        if (RunStoreChain.Find<RunActiveClaimGuardedRunStore>(runStore) is not { } guarded)
            throw new InvalidOperationException("Conditional SQLite events require the guarded run store.");

        var recorded = new List<RunEvent>();
        await guarded.TryWhileRunActiveAsync(RunId.Parse(runId), () =>
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var tx = connection.BeginTransaction();
            foreach (var rawEvent in events)
            {
                ct.ThrowIfCancellationRequested();
                var evt = StructuredRunFailureTerminal.NormalizeFailure(rawEvent);
                evt = StampTimestamp(evt);
                using var cmd = connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO "RunEvents" ("RunId", "Sequence", "EventType", "PayloadJson", "CreatedAt")
                    SELECT $runId, COALESCE(MAX("Sequence"), 0) + 1, $type, $payload, $createdAt
                    FROM "RunEvents" WHERE "RunId" = $runId
                    RETURNING "Sequence";
                    """;
                cmd.Parameters.AddWithValue("$runId", runId);
                cmd.Parameters.AddWithValue("$type", evt.Type);
                cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(evt.Payload));
                cmd.Parameters.AddWithValue("$createdAt",
                    evt.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture));
                recorded.Add(evt with { Sequence = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) });
            }
            ct.ThrowIfCancellationRequested();
            tx.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);

        lock (_channelsGate)
        {
            if (recorded.Count > 0 && !_completedRuns.ContainsKey(runId))
            {
                var channel = _channels.GetOrAdd(runId, _ => CreateChannel());
                foreach (var evt in recorded)
                    channel.Writer.TryWrite(evt);
            }
        }
        return recorded;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        string runId, int fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // 1. Get or create the channel BEFORE reading from the DB. Any append that lands after this
        //    point publishes to the channel; anything before is caught by the replay below — so the
        //    replay/tail hand-off has no gap.
        Channel<RunEvent>? channel;
        lock (_channelsGate)
        {
            channel = _completedRuns.ContainsKey(runId)
                ? null
                : _channels.GetOrAdd(runId, _ => CreateChannel());
        }

        // 2. Replay persisted events from the cursor.
        var lastReplayed = fromSequence;
        var replayBatch = LoadFromSequence(runId, fromSequence, ct).ToList();
        foreach (var evt in replayBatch)
        {
            yield return evt;
            lastReplayed = evt.Sequence;
        }

        if (ShouldStopAfterReplayBatch(
            replayBatch,
            lastReplayed,
            GetCurrentLifecycleTerminalProjection(runId)))
            yield break; // Completed/parked run: drain durable diagnostics, then terminate cleanly.

        if (channel is null)
            yield break;

        // 3. Tail the live channel, skipping anything already delivered during replay. ReadAllAsync
        //    completes when the channel is completed via CompleteAsync (or ct is cancelled).
        await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (evt.Sequence <= lastReplayed)
                continue;
            yield return evt;
            lastReplayed = evt.Sequence;
            if (ShouldStopAfterReplayBatch(
                [evt],
                lastReplayed,
                GetCurrentLifecycleTerminalProjection(runId)))
                yield break;
        }
    }

    private static bool ShouldStopAfterReplayBatch(
        IReadOnlyList<RunEvent> events,
        int lastDeliveredSequence,
        CurrentLifecycleTerminalProjection? currentLifecycle)
    {
        if (currentLifecycle is not null)
        {
            if (!currentLifecycle.IsTerminal)
                return false;

            if (currentLifecycle.EventSequence is int eventSequence)
                return lastDeliveredSequence >= eventSequence;

            if (currentLifecycle.HasTerminalOutboxOutcome)
                return false;
        }

        var terminalIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (RunEventTerminality.IsTerminal(events[i]))
                terminalIndex = i;
        }

        if (terminalIndex < 0)
            return false;

        return true;
    }

    private void PublishDurableEvent(string runId, RunEvent evt)
    {
        lock (_channelsGate)
        {
            if (!_completedRuns.ContainsKey(runId)
                && _channels.TryGetValue(runId, out var channel))
            {
                channel.Writer.TryWrite(evt);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(string runId, CancellationToken ct = default)
    {
        lock (_channelsGate)
        {
            _completedRuns[runId] = 0;
            if (_channels.TryRemove(runId, out var channel))
                channel.Writer.TryComplete();
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(string runId, int fromSequence = 0, CancellationToken ct = default)
    {
        IReadOnlyList<RunEvent> events = LoadFromSequence(runId, fromSequence, ct);
        return Task.FromResult(events);
    }

    /// <inheritdoc />
    public Task<DateTimeOffset?> GetLastEventTimestampAsync(string runId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT MAX("CreatedAt") FROM "RunEvents" WHERE "RunId" = $runId;
            """;
        cmd.Parameters.AddWithValue("$runId", runId);

        var raw = cmd.ExecuteScalar();
        if (raw is null || raw is DBNull)
            return Task.FromResult<DateTimeOffset?>(null);

        var parsed = raw switch
        {
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            string s when DateTime.TryParse(
                s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)),
            _ => (DateTimeOffset?)null,
        };

        return Task.FromResult(parsed);
    }

    private static Channel<RunEvent> CreateChannel() =>
        Channel.CreateBounded<RunEvent>(new BoundedChannelOptions(ChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    private void EnsureTerminalOutcomeProjectionTable()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS terminal_run_outcome_projections (
                run_id TEXT NOT NULL,
                lifecycle_generation INTEGER NOT NULL,
                event_sequence INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (run_id, lifecycle_generation)
            );
            """;
        command.ExecuteNonQuery();
        command.CommandText =
            "ALTER TABLE terminal_run_outcome_projections ADD COLUMN event_sequence INTEGER NOT NULL DEFAULT 0;";
        try { command.ExecuteNonQuery(); }
        catch (SqliteException) { }
    }

    private void EnsureRunEventIdentityColumn()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE \"RunEvents\" ADD COLUMN \"EventIdentity\" TEXT NULL;";
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1) { }
        command.CommandText =
            """
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_RunEvents_RunId_EventIdentity"
            ON "RunEvents" ("RunId", "EventIdentity")
            WHERE "EventIdentity" IS NOT NULL;
            """;
        command.ExecuteNonQuery();
    }

    private CurrentLifecycleTerminalProjection? GetCurrentLifecycleTerminalProjection(string runId)
    {
        try
        {
            using var connection = new SqliteConnection(_runConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT status, lifecycle_generation FROM runs WHERE run_id = $runId;";
            command.Parameters.AddWithValue("$runId", runId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return null;

            var status = reader.GetString(0);
            if (status is not ("merged" or "declined" or "failed" or "completed"
                or "merge_failed" or "assemble_ready" or "cancelled"))
            {
                return new CurrentLifecycleTerminalProjection(false, null);
            }

            var lifecycleGeneration = reader.GetInt32(1);
            reader.Dispose();
            using var eventConnection = new SqliteConnection(_connectionString);
            eventConnection.Open();
            using var projection = eventConnection.CreateCommand();
            projection.CommandText =
                """
                SELECT event_sequence
                FROM terminal_run_outcome_projections
                WHERE run_id = $runId AND lifecycle_generation = $generation;
                """;
            projection.Parameters.AddWithValue("$runId", runId);
            projection.Parameters.AddWithValue("$generation", lifecycleGeneration);
            var eventSequence = projection.ExecuteScalar();
            var hasTerminalOutboxOutcome = false;
            if (eventSequence is null or DBNull)
            {
                try
                {
                    command.CommandText =
                        """
                        SELECT 1
                        FROM terminal_run_outcomes
                        WHERE run_id = $runId AND lifecycle_generation = $generation
                        LIMIT 1;
                        """;
                    command.Parameters.Clear();
                    command.Parameters.AddWithValue("$runId", runId);
                    command.Parameters.AddWithValue("$generation", lifecycleGeneration);
                    hasTerminalOutboxOutcome = command.ExecuteScalar() is not null;
                }
                catch (SqliteException)
                {
                    // Older local stores did not persist terminal-outcome outbox rows.
                }
            }
            return new CurrentLifecycleTerminalProjection(
                true,
                eventSequence is null or DBNull ? null : Convert.ToInt32(eventSequence, CultureInfo.InvariantCulture),
                hasTerminalOutboxOutcome);
        }
        catch (SqliteException)
        {
            // Retain legacy event-only behavior where no run database is available.
            return null;
        }
    }

    private sealed record CurrentLifecycleTerminalProjection(
        bool IsTerminal,
        int? EventSequence,
        bool HasTerminalOutboxOutcome = false);

    /// <summary>
    /// Synchronous durable insert into the RunEvents table. Returns the sequence assigned to the
    /// row. WAL mode and a busy timeout are applied per connection (cheap with pooling).
    /// </summary>
    private int WriteThrough(string runId, RunEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        try
        {
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
            pragma.CommandText = "PRAGMA busy_timeout=2000;";
            pragma.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Failed to apply SQLite run-event PRAGMAs; continuing with defaults");
        }

        var payloadJson = JsonSerializer.Serialize(evt.Payload);
        // Prefer the event's own TimestampUtc (stamped by RunStreamStore.RecordNext/Record at the
        // moment of append) over DateTime.UtcNow here, so the durable CreatedAt column reflects
        // "when it happened" rather than "when it was persisted" for callers that route through
        // the stream store. Falls back to now for events constructed without a timestamp.
        var timestampUtc = evt.TimestampUtc == default ? DateTimeOffset.UtcNow : evt.TimestampUtc;
        var createdAt = timestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        if (evt.Sequence > 0)
        {
            var existing = LoadExistingExplicitEvent(connection, runId, evt.Sequence);
            if (existing is not null)
            {
                EnsureExplicitSequenceMatches(
                    runId,
                    evt.Sequence,
                    evt.Type,
                    payloadJson,
                    existing.Value.EventType,
                    existing.Value.PayloadJson);
                return evt.Sequence;
            }

            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO "RunEvents" ("RunId", "Sequence", "EventType", "PayloadJson", "CreatedAt")
                    VALUES ($runId, $seq, $type, $payload, $createdAt);
                    """;
                cmd.Parameters.AddWithValue("$runId", runId);
                cmd.Parameters.AddWithValue("$seq", evt.Sequence);
                cmd.Parameters.AddWithValue("$type", evt.Type);
                cmd.Parameters.AddWithValue("$payload", payloadJson);
                cmd.Parameters.AddWithValue("$createdAt", createdAt);
                cmd.ExecuteNonQuery();
                return evt.Sequence;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // Concurrent explicit-sequence append: resolve idempotency against the durable row.
                existing = LoadExistingExplicitEvent(connection, runId, evt.Sequence);
                if (existing is not null)
                {
                    EnsureExplicitSequenceMatches(
                        runId,
                        evt.Sequence,
                        evt.Type,
                        payloadJson,
                        existing.Value.EventType,
                        existing.Value.PayloadJson);
                    return evt.Sequence;
                }

                throw;
            }
        }

        // Auto-assign the next monotonic sequence for this run. The MAX+1 select and insert run in
        // one statement so concurrent appends cannot collide on the unique (RunId, Sequence) index.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO "RunEvents" ("RunId", "Sequence", "EventType", "PayloadJson", "CreatedAt")
                SELECT $runId, COALESCE(MAX("Sequence"), 0) + 1, $type, $payload, $createdAt
                FROM "RunEvents" WHERE "RunId" = $runId
                RETURNING "Sequence";
                """;
            cmd.Parameters.AddWithValue("$runId", runId);
            cmd.Parameters.AddWithValue("$type", evt.Type);
            cmd.Parameters.AddWithValue("$payload", payloadJson);
            cmd.Parameters.AddWithValue("$createdAt", createdAt);
            var result = cmd.ExecuteScalar();
            return Convert.ToInt32(result, CultureInfo.InvariantCulture);
        }
    }

    private static RunEvent StampTimestamp(RunEvent evt) =>
        evt.TimestampUtc == default
            ? evt with { TimestampUtc = DateTimeOffset.UtcNow }
            : evt with { TimestampUtc = evt.TimestampUtc.ToUniversalTime() };

    private static (string EventType, string PayloadJson)? LoadExistingExplicitEvent(
        SqliteConnection connection,
        string runId,
        int sequence)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT "EventType", "PayloadJson"
            FROM "RunEvents"
            WHERE "RunId" = $runId AND "Sequence" = $seq
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$seq", sequence);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return (reader.GetString(0), reader.GetString(1));
    }

    private static void EnsureExplicitSequenceMatches(
        string runId,
        int sequence,
        string incomingType,
        string incomingPayloadJson,
        string persistedType,
        string persistedPayloadJson)
    {
        if (string.Equals(incomingType, persistedType, StringComparison.Ordinal)
            && PayloadsEquivalent(incomingPayloadJson, persistedPayloadJson))
        {
            return;
        }

        throw new RunEventSequenceCollisionException(
            $"RunEvent explicit sequence collision detected for run '{runId}' sequence {sequence}: " +
            "the existing durable event payload/type differs from the incoming event.");
    }

    private static bool PayloadsEquivalent(string leftJson, string rightJson)
    {
        if (string.Equals(leftJson, rightJson, StringComparison.Ordinal))
            return true;

        try
        {
            using var leftDoc = JsonDocument.Parse(leftJson);
            using var rightDoc = JsonDocument.Parse(rightJson);
            return JsonElement.DeepEquals(leftDoc.RootElement, rightDoc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Synchronously loads persisted events with Sequence &gt; <paramref name="fromSequence"/>.</summary>
    private List<RunEvent> LoadFromSequence(string runId, int fromSequence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var events = new List<RunEvent>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT "Sequence", "EventType", "PayloadJson", "CreatedAt"
            FROM "RunEvents"
            WHERE "RunId" = $runId AND "Sequence" > $from
            ORDER BY "Sequence";
            """;
        cmd.Parameters.AddWithValue("$runId", runId);
        cmd.Parameters.AddWithValue("$from", fromSequence);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var sequence = reader.GetInt32(0);
            var type = reader.GetString(1);
            var payloadJson = reader.GetString(2);
            var payload = DeserializePayload(runId, sequence, type, payloadJson);
            // Restore the persisted append-time timestamp so a replayed run's timeline matches
            // when the event actually happened, not the moment of replay.
            var createdAt = DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc);
            events.Add(StructuredRunFailureTerminal.NormalizeFailure(
                new RunEvent(sequence, type, payload, new DateTimeOffset(createdAt))));
        }

        return events;
    }

    private object DeserializePayload(string runId, int sequence, string type, string payloadJson)
    {
        try
        {
            if (PayloadTypes.TryGetValue(type, out var payloadType))
                return JsonSerializer.Deserialize(payloadJson, payloadType) ?? new { };

            return JsonSerializer.Deserialize<JsonElement>(payloadJson);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Corrupt RunEvents payload for run {RunId} sequence {Sequence}", runId, sequence);
            return new { error = "corrupt_payload", runId, sequence };
        }
    }
}
