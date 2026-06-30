using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

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
/// <para>Sequence assignment uses a serializable-read transaction so that two concurrent
/// <see cref="AppendAsync"/> calls targeting the same run cannot collide on the unique
/// <c>(RunId, Sequence)</c> index. On constraint violation the transaction is retried once.</para>
/// </summary>
public sealed class EfRunEventStream : IRunEventStream
{
    private const int MaxSequenceRetries = 3;
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
    public async ValueTask AppendAsync(string runId, RunEvent evt, CancellationToken ct = default)
    {
        if (_completedRuns.ContainsKey(runId))
        {
            _logger?.LogWarning("Discarding event {EventType} for completed run {RunId}", evt.Type, runId);
            return;
        }

        var sequence = await WriteThroughAsync(runId, evt, ct).ConfigureAwait(false);

        _ = sequence;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        string runId, int fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var lastSeen = fromSequence;
        while (!ct.IsCancellationRequested)
        {
            var emitted = false;
            await foreach (var evt in LoadFromSequenceAsync(runId, lastSeen, ct).ConfigureAwait(false))
            {
                emitted = true;
                yield return evt;
                lastSeen = evt.Sequence;
                if (TerminalTypes.Contains(evt.Type))
                    yield break;
            }

            if (!emitted && _completedRuns.ContainsKey(runId))
                yield break;

            if (!emitted)
                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(string runId, CancellationToken ct = default)
    {
        _completedRuns[runId] = 0;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Durably writes the event to the <c>RunEvents</c> table and returns the assigned sequence.
    /// Uses a serializable transaction to atomically compute MAX(Sequence)+1 for the run.
    /// On unique constraint violation (race between concurrent appends) retries up to
    /// <see cref="MaxSequenceRetries"/> times.
    /// </summary>
    private async Task<int> WriteThroughAsync(string runId, RunEvent evt, CancellationToken ct)
    {
        var payloadJson = JsonSerializer.Serialize(evt.Payload);

        for (var attempt = 0; attempt < MaxSequenceRetries; attempt++)
        {
            await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await using var tx = await db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct).ConfigureAwait(false);

            try
            {
                int sequence;

                if (evt.Sequence > 0)
                {
                    // Caller-assigned: use it; skip on duplicate (idempotent).
                    var exists = await db.RunEvents
                        .AnyAsync(e => e.RunId == runId && e.Sequence == evt.Sequence, ct)
                        .ConfigureAwait(false);
                    if (exists)
                        return evt.Sequence;

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
                    CreatedAt = DateTime.UtcNow,
                });

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return sequence;
            }
            catch (DbUpdateException) when (attempt < MaxSequenceRetries - 1)
            {
                // Unique constraint violation (concurrent appends) — rollback and retry.
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                _logger?.LogDebug("RunEvent sequence conflict for run {RunId}, attempt {Attempt}", runId, attempt + 1);
            }
        }

        throw new InvalidOperationException(
            $"Failed to assign a unique RunEvent sequence for run '{runId}' after {MaxSequenceRetries} attempts.");
    }

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
            yield return new RunEvent(row.Sequence, row.EventType, payload);
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
