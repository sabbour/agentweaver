using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
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
    }

    /// <inheritdoc />
    public ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
    {
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

        if (ShouldStopAfterReplayBatch(replayBatch))
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
            if (TerminalTypes.Contains(evt.Type))
                yield break;
        }
    }

    private static bool ShouldStopAfterReplayBatch(IReadOnlyList<RunEvent> events)
    {
        var terminalIndex = -1;
        for (var i = 0; i < events.Count; i++)
        {
            if (TerminalTypes.Contains(events[i].Type))
                terminalIndex = i;
        }

        if (terminalIndex < 0)
            return false;

        return true;
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
            events.Add(new RunEvent(sequence, type, payload, new DateTimeOffset(createdAt)));
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
