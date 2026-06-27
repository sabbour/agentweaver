using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Runs;
using Agentweaver.Api.Runs.Graph;
using Agentweaver.Domain;
using Microsoft.EntityFrameworkCore;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// EF Core / PostgreSQL implementation of <see cref="IRunEventStream"/>.
///
/// <para>Mirrors <see cref="SqliteRunEventStream"/>'s two-layer design:
/// <list type="number">
///   <item><b>Durable layer</b> — every <see cref="AppendAsync"/> writes to <c>RunEvents</c> via
///   EF (a <c>MemoryDbContext</c> factory-created context per call) before acknowledging.</item>
///   <item><b>In-process channel</b> — same bounded <see cref="Channel{T}"/> fan-out, replay-then-tail
///   pattern as the SQLite implementation.</item>
/// </list></para>
///
/// <para>Sequence assignment uses a serializable-read transaction so that two concurrent
/// <see cref="AppendAsync"/> calls targeting the same run cannot collide on the unique
/// <c>(RunId, Sequence)</c> index. On constraint violation the transaction is retried once.</para>
/// </summary>
public sealed class EfRunEventStream : IRunEventStream
{
    private const int ChannelCapacity = 1000;
    private const int MaxSequenceRetries = 3;

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
    private readonly ConcurrentDictionary<string, Channel<RunEvent>> _channels = new();
    private readonly ConcurrentDictionary<string, byte> _completedRuns = new();
    private readonly object _channelsGate = new();
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

        var stamped = evt.Sequence == sequence ? evt : new RunEvent(sequence, evt.Type, evt.Payload);
        lock (_channelsGate)
        {
            if (_completedRuns.ContainsKey(runId))
            {
                _logger?.LogWarning(
                    "Run {RunId} completed while appending event {EventType}; durable event {Sequence} will not resurrect live channel",
                    runId, evt.Type, sequence);
                return;
            }

            var channel = _channels.GetOrAdd(runId, _ => CreateChannel());
            channel.Writer.TryWrite(stamped);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RunEvent> SubscribeAsync(
        string runId, int fromSequence = 0, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Channel<RunEvent>? channel;
        lock (_channelsGate)
        {
            channel = _completedRuns.ContainsKey(runId)
                ? null
                : _channels.GetOrAdd(runId, _ => CreateChannel());
        }

        var lastReplayed = fromSequence;
        await foreach (var evt in LoadFromSequenceAsync(runId, fromSequence, ct).ConfigureAwait(false))
        {
            yield return evt;
            lastReplayed = evt.Sequence;
            if (TerminalTypes.Contains(evt.Type))
                yield break;
        }

        if (channel is null)
            yield break;

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
