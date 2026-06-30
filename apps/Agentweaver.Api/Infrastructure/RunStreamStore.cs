using System.Collections.Concurrent;
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
            return _history.Count == 0 ? 1 : _history[^1].Sequence + 1;
    }

    /// <summary>
    /// Atomically allocates the next sequence number and records the event under a
    /// single lock acquisition, preventing a race between <see cref="NextSequence"/>
    /// and <see cref="Record"/>. Use this when the caller does not already hold the
    /// lock (e.g. recovery paths that emit a single synthetic event).
    /// Returns the monotonic sequence assigned to the recorded event.
    /// </summary>
    public int RecordNext(string type, object payload)
    {
        TaskCompletionSource? previous;
        int seq;
        lock (_lock)
        {
            seq = _history.Count == 0 ? 1 : _history[^1].Sequence + 1;
            _history.Add(new RunEvent(seq, type, payload));
            previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        previous.TrySetResult();
        PersistBestEffort(new RunEvent(seq, type, payload));
        return seq;
    }

    /// <summary>
    /// Records an event into the history and wakes all clients currently blocked in
    /// <see cref="WaitForChangeAsync"/>. Called by the orchestrator's recording writer.
    /// </summary>
    public void Record(RunEvent evt)
    {
        TaskCompletionSource? previous;
        lock (_lock)
        {
            _history.Add(evt);
            previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        previous.TrySetResult();
        PersistBestEffort(evt);
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
        catch
        {
            // Best-effort mirror only. Terminal backfill paths reconcile any missed events.
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
