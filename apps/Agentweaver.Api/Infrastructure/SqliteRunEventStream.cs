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

        // The RunEvents table lives in memory.db (EF Core MemoryDbContext), a separate file from
        // the main agentweaver.db. Resolve the same path Program.cs uses to register the context.
        var basePath = configuration["Database:Path"] is string p && !string.IsNullOrWhiteSpace(p)
            ? Path.GetDirectoryName(Path.GetFullPath(p))!
            : AppPaths.DataDirectory;
        Directory.CreateDirectory(basePath);
        var memoryDbPath = Path.Combine(basePath, "memory.db");

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = memoryDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    /// <inheritdoc />
    public ValueTask AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
    {
        if (_completedRuns.ContainsKey(runId))
        {
            _logger?.LogWarning("Discarding event {EventType} for completed run {RunId}", evt.Type, runId);
            return ValueTask.CompletedTask;
        }

        // Layer 1: synchronous, durable write-through BEFORE the channel publish so the event is
        // crash-safe before any live subscriber observes it. Honors a pre-assigned sequence when
        // present (idempotent via the unique (RunId, Sequence) index), otherwise assigns MAX+1.
        var sequence = WriteThrough(runId, evt, ct);

        // Layer 2: publish to the live channel. TryWrite never blocks; if the bounded channel is
        // full (slow/absent consumer) the live copy is dropped — it stays durable in SQLite.
        var stamped = evt.Sequence == sequence ? evt : new RunEvent(sequence, evt.Type, evt.Payload);
        lock (_channelsGate)
        {
            if (_completedRuns.ContainsKey(runId))
            {
                _logger?.LogWarning(
                    "Run {RunId} completed while appending event {EventType}; durable event {Sequence} will not resurrect live channel",
                    runId, evt.Type, sequence);
                return ValueTask.CompletedTask;
            }

            var channel = _channels.GetOrAdd(runId, _ => CreateChannel());
            channel.Writer.TryWrite(stamped);
        }

        return ValueTask.CompletedTask;
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
        foreach (var evt in LoadFromSequence(runId, fromSequence, ct))
        {
            yield return evt;
            lastReplayed = evt.Sequence;
            if (TerminalTypes.Contains(evt.Type))
                yield break; // Completed run: replay history, then terminate cleanly.
        }

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

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=2000;";
            pragma.ExecuteNonQuery();
        }

        var payloadJson = JsonSerializer.Serialize(evt.Payload);
        var createdAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

        if (evt.Sequence > 0)
        {
            // Caller-assigned sequence: insert idempotently. A duplicate (RunId, Sequence) is a
            // no-op (the row is already durable), e.g. when a terminal backfill re-persists events.
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO "RunEvents" ("RunId", "Sequence", "EventType", "PayloadJson", "CreatedAt")
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

    /// <summary>Synchronously loads persisted events with Sequence &gt; <paramref name="fromSequence"/>.</summary>
    private List<RunEvent> LoadFromSequence(string runId, int fromSequence, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var events = new List<RunEvent>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT "Sequence", "EventType", "PayloadJson"
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
            events.Add(new RunEvent(sequence, type, payload));
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
