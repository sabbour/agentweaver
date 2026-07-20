using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// EF Core / PostgreSQL implementation of <see cref="IRunEventStream"/>.
///
/// <para>Postgres is the cross-replica relay: every append is durable before acknowledgement, and
/// subscribers poll the shared <c>RunEvents</c> table from their cursor. This intentionally avoids
/// per-pod channels for live delivery so a browser connected to replica B observes events written by
/// a run executing on replica A without sticky sessions.</para>
///
/// <para>Write path:
/// <list type="number">
///   <item><b>Durable layer</b> — every <see cref="AppendAsync"/> writes to <c>RunEvents</c> via
///   EF (a <c>MemoryDbContext</c> factory-created context per call) before acknowledging.</item>
/// </list></para>
///
/// <para>Sequence assignment is server-authoritative: PostgreSQL appends take a per-run
/// <c>pg_advisory_xact_lock</c> (hash derived in SQL from runId) and allocate <c>MAX+1</c> inside
/// that transaction, so concurrent replicas cannot collide on <c>(RunId, Sequence)</c>.</para>
/// </summary>
public sealed class EfRunEventStream : IRunEventStream
{
    private const int MaxWriteAttempts = 4;
    private const string RunEventSequenceConstraintName = "IX_RunEvents_RunId_Sequence";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

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

    private readonly IDbContextFactory<MemoryDbContext> _factory;
    private readonly ConcurrentDictionary<string, byte> _completedRuns = new();
    private readonly ILogger<EfRunEventStream>? _logger;

    public EfRunEventStream(IDbContextFactory<MemoryDbContext> factory, ILogger<EfRunEventStream>? logger = null)
    {
        _factory = factory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<int> AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
    {
        // #239 companion hardening: once a run is completed, drop streaming AgentMessageDelta events —
        // a straggling delta arriving after the terminal must never re-persist and re-drive the run.
        // ONLY agent.message.delta is dropped; every terminal/diagnostic/final-message/tool/usage/
        // subtask/topology event still persists post-terminal (durable audit + gapless replay).
        if (_completedRuns.ContainsKey(runId) && evt.Type == EventTypes.AgentMessageDelta)
            return 0;

        var sequence = await WriteThroughAsync(runId, evt, ct).ConfigureAwait(false);

        if (_completedRuns.ContainsKey(runId))
        {
            _logger?.LogWarning(
                "Persisted late event {EventType} for completed run {RunId}; subscribers must replay from durable store",
                evt.Type, runId);
        }

        return sequence;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        string runId, int fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastSeen = fromSequence;
        while (!ct.IsCancellationRequested)
        {
            var batch = new List<RunEvent>();
            await foreach (var evt in LoadFromSequenceAsync(runId, lastSeen, ct).ConfigureAwait(false))
                batch.Add(evt);

            foreach (var evt in batch)
            {
                yield return evt;
                lastSeen = evt.Sequence;
            }

            if (ShouldStopAfterReplayBatch(batch))
                yield break;

            if (batch.Count == 0 && _completedRuns.ContainsKey(runId))
                yield break;

            if (batch.Count == 0)
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
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
        _completedRuns[runId] = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Durably writes the event to the <c>RunEvents</c> table and returns the assigned sequence.
    /// PostgreSQL writes acquire a per-run advisory transaction lock on the server and perform
    /// MAX+1 allocation inside that lock, giving deterministic cross-replica sequence assignment.
    /// Retries are bounded and restricted to explicit transient/conflict SQLSTATEs.
    /// </summary>
    /// <inheritdoc />
    public async Task<IReadOnlyList<RunEvent>> GetPersistedEventsAsync(string runId, int fromSequence = 0, CancellationToken ct = default)
    {
        var events = new List<RunEvent>();
        await foreach (var evt in LoadFromSequenceAsync(runId, fromSequence, ct).ConfigureAwait(false))
            events.Add(evt);
        return events;
    }

    private async Task<int> WriteThroughAsync(string runId, RunEvent evt, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(evt.Payload);
        // Prefer the event's own TimestampUtc (stamped by RunStreamStore.RecordNext/Record) over
        // DateTime.UtcNow so CreatedAt reflects "when it happened", not "when it was persisted".
        var timestampUtc = evt.TimestampUtc == default ? DateTimeOffset.UtcNow : evt.TimestampUtc;
        var explicitSequence = evt.Sequence > 0;

        for (var attempt = 1; attempt <= MaxWriteAttempts; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.ReadCommitted, ct).ConfigureAwait(false);

            try
            {
                await AcquireRunWriteLockAsync(db, runId, ct).ConfigureAwait(false);

                int sequence;

                if (explicitSequence)
                {
                    var existing = await db.RunEvents.AsNoTracking()
                        .Where(e => e.RunId == runId && e.Sequence == evt.Sequence)
                        .Select(e => new ExistingRunEvent(e.EventType, e.PayloadJson))
                        .SingleOrDefaultAsync(ct)
                        .ConfigureAwait(false);

                    if (existing is not null)
                    {
                        EnsureExplicitSequenceMatches(
                            runId,
                            evt.Sequence,
                            evt.Type,
                            payloadJson,
                            existing.EventType,
                            existing.PayloadJson);
                        return evt.Sequence;
                    }

                    sequence = evt.Sequence;
                }
                else
                {
                    // Auto-assign the next monotonic sequence for this run.
                    var max = await db.RunEvents
                        .Where(e => e.RunId == runId)
                        .Select(e => (int?)e.Sequence)
                        .MaxAsync(ct)
                        .ConfigureAwait(false);
                    sequence = (max ?? 0) + 1;
                }

                db.RunEvents.Add(new RunEventRecord
                {
                    RunId = runId,
                    Sequence = sequence,
                    EventType = evt.Type,
                    PayloadJson = payloadJson,
                    CreatedAt = timestampUtc.UtcDateTime,
                });

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return sequence;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ShouldRetryWrite(ex, attempt, out var sqlState))
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                var delay = ComputeRetryDelay(attempt);
                _logger?.LogWarning(
                    "Retrying RunEvent append for run {RunId} after SQLSTATE {SqlState} " +
                    "(attempt {Attempt}/{MaxAttempts}, delay {DelayMs}ms)",
                    runId, sqlState, attempt, MaxWriteAttempts, (int)delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Failed to durably append RunEvent for run '{runId}' after {MaxWriteAttempts} attempts.");
    }

    private static async Task AcquireRunWriteLockAsync(MemoryDbContext db, string runId, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return;

        // Bound lock wait so a stuck writer cannot block appenders indefinitely.
        await db.Database.ExecuteSqlRawAsync(
            "SET LOCAL lock_timeout = '2000ms';",
            ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended({0}, 0));",
            new object[] { runId },
            ct).ConfigureAwait(false);
    }

    private static bool ShouldRetryWrite(
        Exception ex,
        int attempt,
        out string sqlState)
    {
        sqlState = string.Empty;
        if (attempt >= MaxWriteAttempts)
            return false;

        var postgres = ExtractPostgresException(ex);
        if (postgres is null)
            return false;

        sqlState = postgres.SqlState ?? string.Empty;
        if (sqlState is PostgresErrorCodes.SerializationFailure
            or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.LockNotAvailable)
        {
            return true;
        }

        if (sqlState == PostgresErrorCodes.UniqueViolation
            && string.Equals(postgres.ConstraintName, RunEventSequenceConstraintName, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static PostgresException? ExtractPostgresException(Exception ex) =>
        ex switch
        {
            PostgresException pg => pg,
            DbUpdateException { InnerException: PostgresException pg } => pg,
            _ => null,
        };

    private static TimeSpan ComputeRetryDelay(int attempt)
    {
        var boundedJitterMs = Random.Shared.Next(5, 30);
        var baseDelayMs = 20 * attempt;
        return TimeSpan.FromMilliseconds(baseDelayMs + boundedJitterMs);
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

    private sealed record ExistingRunEvent(string EventType, string PayloadJson);

    private async IAsyncEnumerable<RunEvent> LoadFromSequenceAsync(
        string runId, int fromSequence, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await db.RunEvents
            .Where(e => e.RunId == runId && e.Sequence > fromSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            var payload = DeserializePayload(runId, row.Sequence, row.EventType, row.PayloadJson);
            // Restore the persisted append-time timestamp so a replayed run's timeline matches
            // when the event actually happened, not the moment of replay.
            var createdAtUtc = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc);
            yield return new RunEvent(row.Sequence, row.EventType, payload, new DateTimeOffset(createdAtUtc));
        }
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
