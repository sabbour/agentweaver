using System.Collections.Concurrent;
using System.Text.Json;
using Agentweaver.Domain;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// Snapshot returned by <see cref="RunStreamEntry.GetSnapshotSince"/> — provides the event
/// list and completion flag atomically under one lock acquisition so callers never observe a
/// stale completion state relative to the returned events.
/// </summary>
public readonly record struct StreamSnapshot(IReadOnlyList<RunEvent> Events, bool IsCompleted);

public sealed class RunStreamEntry
{
    private static readonly IComparer<RunEvent> SequenceComparer =
        Comparer<RunEvent>.Create((left, right) => left.Sequence.CompareTo(right.Sequence));

    /// <summary>
    /// The submitting user who owns this run. Used to authorize stream access for
    /// in-progress runs where the persistent Run record might not yet be fetched.
    /// </summary>
    public string Owner { get; }
    private readonly string _runId;
    private readonly IRunEventStream? _eventStream;

    private readonly List<RunEvent> _history = [];
    private bool _isCompleted;
    private bool _isAwaitingReview;
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile TaskCompletionSource _eventSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunStreamEntry(string owner, string runId = "", IRunEventStream? eventStream = null)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _runId = runId;
        _eventStream = eventStream;
    }

    public bool IsCompleted
    {
        get { lock (_lock) return _isCompleted; }
    }

    /// <summary>
    /// True once the agent has finished and the run is waiting for a human review decision.
    /// Used by the SSE loop to close the stream at the review gate.
    /// </summary>
    public bool IsAwaitingReview
    {
        get { lock (_lock) return _isAwaitingReview; }
    }

    /// <summary>
    /// Marks this entry as awaiting a review decision. Called before emitting review.requested
    /// so the SSE loop can detect the gate and close the stream for the client.
    /// </summary>
    public void MarkAwaitingReview()
    {
        lock (_lock) _isAwaitingReview = true;
    }

    /// <summary>
    /// Clears the awaiting-review flag after a request-changes decision so the entry
    /// is treated as live again (in_progress) and the SSE loop resumes streaming.
    /// </summary>
    public void ClearAwaitingReview()
    {
        lock (_lock) _isAwaitingReview = false;
    }

    /// <summary>
    /// Returns the next monotonic sequence number for an event to be appended by
    /// the orchestrator or review endpoint AFTER IAgentRunner.ExecuteAsync has returned.
    /// Must be called under _lock to preserve total ordering (A2 / FR-019).
    /// </summary>
    public int NextSequence()
    {
        lock (_lock)
            return NextInMemorySequenceLocked();
    }

    /// <summary>
    /// Records an event and returns its sequence. When a durable event stream is configured, sequence
    /// assignment is delegated to that shared stream (Sequence=0 => provider assigns MAX+1) so direct
    /// and entry writers share one authority. Without a durable stream, falls back to deterministic
    /// in-memory allocation.
    /// </summary>
    public int RecordNext(string type, object payload)
    {
        return RecordNext(type, _ => payload);
    }

    /// <summary>
    /// Records an event with a payload factory. When a durable stream is configured, the
    /// <paramref name="payloadFactory"/> receives the in-memory next-sequence hint while the durable
    /// stream remains the authority for the assigned sequence returned by this method.
    /// </summary>
    public int RecordNext(string type, Func<int, object> payloadFactory)
    {
        ArgumentNullException.ThrowIfNull(payloadFactory);

        var timestampUtc = DateTimeOffset.UtcNow;
        RunEvent recorded;
        TaskCompletionSource? previous = null;
        var added = false;

        if (HasDurableSequenceAuthority)
        {
            // Durable sequence authority: ask the shared event stream to allocate MAX+1 (Sequence=0)
            // so direct writers and entry writers cannot race each other with local sequence guesses.
            var sequenceHint = NextSequence();
            var payload = payloadFactory(sequenceHint);
            var assignedSequence = _eventStream!
                .AppendAsync(_runId, new RunEvent(0, type, payload, timestampUtc))
                .AsTask().GetAwaiter().GetResult();
            if (assignedSequence <= 0)
                return 0;

            recorded = new RunEvent(assignedSequence, type, payload, timestampUtc);

            lock (_lock)
            {
                added = TryInsertOrValidateLocked(recorded);
                if (added)
                    previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }
        else
        {
            lock (_lock)
            {
                var sequence = NextInMemorySequenceLocked();
                var payload = payloadFactory(sequence);
                recorded = new RunEvent(sequence, type, payload, timestampUtc);
                added = TryInsertOrValidateLocked(recorded);
                if (added)
                    previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            PersistBestEffort(recorded);
        }

        if (added)
            previous!.TrySetResult();

        return recorded.Sequence;
    }

    /// <summary>
    /// Records an event into the history and wakes all clients currently blocked in
    /// <see cref="WaitForChangeAsync"/>. Called by the orchestrator's recording writer.
    /// Always re-stamps <see cref="RunEvent.TimestampUtc"/> to the moment of append — the correct
    /// "when it happened" semantic — so callers do not need to set it themselves.
    /// </summary>
    public void Record(RunEvent evt)
    {
        var stamped = evt with { TimestampUtc = DateTimeOffset.UtcNow };
        RunEvent recorded;
        TaskCompletionSource? previous = null;
        var added = false;

        if (HasDurableSequenceAuthority)
        {
            // Explicit Sequence>0 preserves historical/backfill intent; Sequence<=0 requests
            // provider-assigned MAX+1 for live writers.
            var durableCandidate = stamped.Sequence > 0
                ? stamped
                : stamped with { Sequence = 0 };
            var assignedSequence = _eventStream!
                .AppendAsync(_runId, durableCandidate)
                .AsTask().GetAwaiter().GetResult();
            if (assignedSequence <= 0)
                return;

            recorded = stamped with { Sequence = assignedSequence };

            lock (_lock)
            {
                added = TryInsertOrValidateLocked(recorded);
                if (added)
                    previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
        }
        else
        {
            recorded = stamped;
            lock (_lock)
            {
                added = TryInsertOrValidateLocked(recorded);
                if (added)
                    previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            PersistBestEffort(recorded);
        }

        if (added)
            previous!.TrySetResult();
    }

    /// <summary>
    /// Atomically retrieves all events with Sequence greater than <paramref name="lastSeen"/>
    /// together with the current completion state. This eliminates the race between reading
    /// events and checking whether more may arrive.
    /// </summary>
    public StreamSnapshot GetSnapshotSince(int lastSeen)
    {
        lock (_lock)
        {
            var events = _history.Where(e => e.Sequence > lastSeen).ToList();
            return new StreamSnapshot(events, _isCompleted);
        }
    }

    /// <summary>
    /// Returns true if any recorded event has the specified <paramref name="type"/>.
    /// Used by the SSE loop to detect reconnects at or after a known event type so
    /// the stream can break immediately instead of polling indefinitely.
    /// </summary>
    public bool HasEventType(string type)
    {
        lock (_lock)
            return _history.Any(e => string.Equals(e.Type, type, StringComparison.Ordinal));
    }

    public void MarkCompleted()
    {
        lock (_lock) _isCompleted = true;
        _completionSignal.TrySetResult();
    }

    /// <summary>
    /// Waits until a new event is recorded, completion is signaled, the ~1 s timeout elapses,
    /// or <paramref name="ct"/> is triggered — whichever comes first.
    /// Clients poll with <see cref="GetSnapshotSince"/> after this returns.
    /// </summary>
    public async Task WaitForChangeAsync(CancellationToken ct)
    {
        var eventTask = _eventSignal.Task;
        var completionTask = _completionSignal.Task;
        var timeout = Task.Delay(TimeSpan.FromSeconds(1), ct);

        await Task.WhenAny(eventTask, completionTask, timeout).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
    }

    private void PersistBestEffort(RunEvent evt)
    {
        if (_eventStream is null || string.IsNullOrWhiteSpace(_runId))
            return;

        try
        {
            _eventStream.AppendAsync(_runId, evt).AsTask().GetAwaiter().GetResult();
        }
        catch (RunEventSequenceCollisionException)
        {
            // Distinct-payload collisions on an explicit sequence are data-integrity faults; never
            // mask them as success. Callers decide how to surface/fail the run.
            throw;
        }
        catch
        {
            // Best-effort mirror only. Terminal backfill paths reconcile any missed events.
        }
    }

    private bool HasDurableSequenceAuthority =>
        _eventStream is not null && !string.IsNullOrWhiteSpace(_runId);

    private int NextInMemorySequenceLocked() =>
        _history.Count == 0 ? 1 : _history[^1].Sequence + 1;

    private bool TryInsertOrValidateLocked(RunEvent candidate)
    {
        var index = _history.BinarySearch(candidate, SequenceComparer);
        if (index >= 0)
        {
            if (EventsEquivalent(_history[index], candidate))
                return false;

            throw new RunEventSequenceCollisionException(
                $"RunStreamEntry sequence collision detected for run '{_runId}' sequence {candidate.Sequence}: " +
                "the existing in-memory event payload/type differs from the incoming event.");
        }

        _history.Insert(~index, candidate);
        return true;
    }

    private static bool EventsEquivalent(RunEvent existing, RunEvent candidate)
    {
        if (!string.Equals(existing.Type, candidate.Type, StringComparison.Ordinal))
            return false;

        if (ReferenceEquals(existing.Payload, candidate.Payload))
            return true;

        var leftJson = JsonSerializer.Serialize(existing.Payload);
        var rightJson = JsonSerializer.Serialize(candidate.Payload);
        if (string.Equals(leftJson, rightJson, StringComparison.Ordinal))
            return true;

        try
        {
            using var left = JsonDocument.Parse(leftJson);
            using var right = JsonDocument.Parse(rightJson);
            return JsonElement.DeepEquals(left.RootElement, right.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

public sealed class RunStreamStore
{
    // Retain a bounded number of finished runs so late or reconnecting clients can replay the
    // full recorded event sequence (Principle V) rather than a single collapsed message.
    private const int MaxRetainedCompleted = 256;

    private readonly ConcurrentDictionary<string, (RunStreamEntry Entry, DateTimeOffset CreatedAt)> _entries = new();
    private readonly ConcurrentQueue<string> _completedOrder = new();
    private readonly IRunEventStream? _eventStream;

    public RunStreamStore(IRunEventStream? eventStream = null)
    {
        _eventStream = eventStream;
    }

    public RunStreamEntry Create(string runId, string owner)
    {
        var entry = new RunStreamEntry(owner, runId, _eventStream);
        _entries[runId] = (entry, DateTimeOffset.UtcNow);
        return entry;
    }

    public RunStreamEntry? Get(string runId) =>
        _entries.TryGetValue(runId, out var pair) ? pair.Entry : null;

    /// <summary>
    /// Removes a run's stream entry from the store.
    /// </summary>
    public void Remove(string runId) => _entries.TryRemove(runId, out _);

    /// <summary>
    /// Marks a run's stream as finished and retains its recorded history for replay, evicting the
    /// oldest completed runs once the retention bound is exceeded.
    /// </summary>
    public void Complete(string runId)
    {
        if (!_entries.TryGetValue(runId, out var pair)) return;

        pair.Entry.MarkCompleted();
        _completedOrder.Enqueue(runId);

        // Evict oldest completed entries beyond bound.
        while (_completedOrder.Count > MaxRetainedCompleted && _completedOrder.TryDequeue(out var oldest))
        {
            if (_entries.TryGetValue(oldest, out var oldestPair) && oldestPair.Entry.IsCompleted)
                _entries.TryRemove(oldest, out _);
        }
    }
}
