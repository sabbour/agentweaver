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
    /// Maximum number of events retained per run. Prevents unbounded memory growth
    /// for pathologically long runs. Once the cap is reached, new events are dropped.
    /// </summary>
    public const int MaxEventsPerRun = 10_000;

    /// <summary>
    /// The submitting user who owns this run. Used to authorize stream access for
    /// in-progress runs where the persistent Run record might not yet be fetched.
    /// </summary>
    public string Owner { get; }

    private readonly List<RunEvent> _history = [];
    private bool _isCompleted;
    private bool _isAwaitingReview;
    private bool _evicted;
    private int _generation = 1;
    private DateTimeOffset _lastActiveAt = DateTimeOffset.UtcNow;
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile TaskCompletionSource _eventSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunStreamEntry(string owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
    }

    /// <summary>
    /// The last time an event was recorded into this entry. Updated atomically on every
    /// <see cref="Record"/> and <see cref="RecordNext"/> call so the eviction sweep uses
    /// real activity time rather than immutable creation time. This prevents sweeping an
    /// entry that is still actively written to during a long-lived revision cycle.
    /// </summary>
    public DateTimeOffset LastActiveAt
    {
        get { lock (_lock) return _lastActiveAt; }
    }

    public bool IsCompleted
    {
        get { lock (_lock) return _isCompleted; }
    }

    /// <summary>
    /// True once the agent has finished and the run is waiting for a human review decision.
    /// AwaitingReview entries are exempt from the stale-in-progress eviction sweep (A1).
    /// </summary>
    public bool IsAwaitingReview
    {
        get { lock (_lock) return _isAwaitingReview; }
    }

    public int Generation
    {
        get { lock (_lock) return _generation; }
    }

    /// <summary>
    /// Marks this entry as awaiting a review decision. Must be called before emitting
    /// review.requested so the stale sweep cannot evict the entry while it waits (A1).
    /// </summary>
    public void MarkAwaitingReview()
    {
        lock (_lock) _isAwaitingReview = true;
    }

    /// <summary>
    /// Clears the awaiting-review flag after a request-changes decision so the entry
    /// is treated as live again (in_progress) and is eligible for normal eviction.
    /// Also refreshes <see cref="_lastActiveAt"/> under the same lock so the entry is
    /// not immediately eligible for stale eviction if it sat awaiting review longer than
    /// <c>maxInProgressAge</c>. Returns <see langword="false"/> if the entry was already
    /// evicted (caller should treat this as a no-op but may log a warning).
    /// </summary>
    public bool ClearAwaitingReview()
    {
        lock (_lock)
        {
            _lastActiveAt = DateTimeOffset.UtcNow;
            _isAwaitingReview = false;
            return !_evicted;
        }
    }

    /// <summary>
    /// Atomically checks whether this entry is stale and, if so, marks it evicted so that
    /// subsequent <see cref="Record"/>/<see cref="RecordNext"/> calls are no-ops. Returns
    /// <see langword="true"/> only if the entry was actually marked by this call, in which case
    /// the caller must remove it from the store dictionary. Eliminates the TOCTOU window
    /// between the stale predicate and <see cref="ConcurrentDictionary{TKey,TValue}.TryRemove"/>
    /// (Fix 2 / A1): once this method returns <see langword="true"/>, no concurrent writer can
    /// resurface the entry.
    /// </summary>
    public bool TryMarkEvicted(DateTimeOffset cutoff)
    {
        lock (_lock)
        {
            if (_evicted || _isCompleted || _isAwaitingReview || _lastActiveAt >= cutoff)
                return false;
            _evicted = true;
            return true;
        }
    }

    public int BumpGeneration()
    {
        lock (_lock)
            return ++_generation;
    }

    /// <summary>
    /// Atomically checks whether this entry is still live and, if so, increments the
    /// generation counter. Returns <c>(true, newGeneration)</c> when the entry is live,
    /// or <c>(false, currentGeneration)</c> when the entry has already been marked evicted
    /// so the caller can detect the dead entry and recreate a fresh one before starting a
    /// new revision cycle. This prevents a revision from silently writing to an evicted
    /// entry whose <see cref="Record"/>/<see cref="RecordNext"/> calls are all no-ops.
    /// </summary>
    public (bool Success, int Generation) TryBumpGeneration()
    {
        lock (_lock)
        {
            if (_evicted) return (false, _generation);
            return (true, ++_generation);
        }
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
    /// </summary>
    public void RecordNext(string type, object payload)
    {
        TaskCompletionSource? previous;
        lock (_lock)
        {
            if (_evicted) return;                   // Fix 2: no-op on evicted entries
            _lastActiveAt = DateTimeOffset.UtcNow;  // Fix 1: refresh before cap check
            if (_history.Count >= MaxEventsPerRun) return;
            var seq = _history.Count == 0 ? 1 : _history[^1].Sequence + 1;
            _history.Add(new RunEvent(seq, type, payload));
            previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        previous.TrySetResult();
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
            if (_evicted) return;                   // Fix 2: no-op on evicted entries
            _lastActiveAt = DateTimeOffset.UtcNow;  // Fix 1: refresh before cap check
            if (_history.Count >= MaxEventsPerRun) return;
            _history.Add(evt);
            previous = Interlocked.Exchange(ref _eventSignal, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        previous.TrySetResult();
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
}

public sealed class RunStreamStore
{
    // Retain a bounded number of finished runs so late or reconnecting clients can replay the
    // full recorded event sequence (Principle V) rather than a single collapsed message.
    private const int MaxRetainedCompleted = 256;

    private readonly TimeSpan _maxInProgressAge;

    private readonly ConcurrentDictionary<string, (RunStreamEntry Entry, DateTimeOffset CreatedAt)> _entries = new();
    private readonly ConcurrentQueue<string> _completedOrder = new();

    /// <summary>Production constructor — uses a 2-hour stale-entry threshold.</summary>
    public RunStreamStore() : this(TimeSpan.FromHours(2)) { }

    /// <summary>
    /// Test constructor allowing a custom stale-entry threshold so eviction behaviour
    /// can be exercised without waiting two hours.
    /// </summary>
    public RunStreamStore(TimeSpan maxInProgressAge)
    {
        _maxInProgressAge = maxInProgressAge;
    }

    public RunStreamEntry Create(string runId, string owner)
    {
        var entry = new RunStreamEntry(owner);
        _entries[runId] = (entry, DateTimeOffset.UtcNow);
        return entry;
    }

    public RunStreamEntry? Get(string runId) =>
        _entries.TryGetValue(runId, out var pair) ? pair.Entry : null;

    /// <summary>
    /// Removes a run's stream entry from the store. Used by
    /// <see cref="Runs.RunOrchestrator.StartRevisionAsync"/> to discard an evicted entry
    /// before creating a fresh one so the new entry is visible to <see cref="Get"/>.
    /// </summary>
    public void Remove(string runId) => _entries.TryRemove(runId, out _);

    /// <summary>
    /// Marks a run's stream as finished and retains its recorded history for replay, evicting the
    /// oldest completed runs once the retention bound is exceeded. Also evicts stale in-progress
    /// entries that exceeded the maximum age.
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

        // Evict stale in-progress entries that likely represent leaked runs.
        // AwaitingReview entries are exempt: they are waiting for a human decision
        // and must not be evicted while that decision is pending (A1).
        // Merging entries inherit this exemption: MarkAwaitingReview() is set before
        // the approve flow enters the Merging state, so IsAwaitingReview is already
        // true for any in-flight merge. No separate flag is needed.
        // LastActiveAt (updated on every Record/RecordNext) is used instead of the
        // immutable CreatedAt so a long-lived revision cycle — where the entry was
        // created hours ago but is still actively written to — is not evicted while
        // the watch loop is still streaming events to connected clients.
        var cutoff = DateTimeOffset.UtcNow - _maxInProgressAge;
        foreach (var kvp in _entries)
        {
            if (kvp.Value.Entry.TryMarkEvicted(cutoff))
                _entries.TryRemove(kvp.Key, out _);
        }
    }
}
