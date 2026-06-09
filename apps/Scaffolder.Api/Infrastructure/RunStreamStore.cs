using System.Collections.Concurrent;
using Scaffolder.Domain;

namespace Scaffolder.Api.Infrastructure;

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
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource _completionSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile TaskCompletionSource _eventSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RunStreamEntry(string owner)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
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

    /// <summary>
    /// Marks this entry as awaiting a review decision. Must be called before emitting
    /// review.requested so the stale sweep cannot evict the entry while it waits (A1).
    /// </summary>
    public void MarkAwaitingReview()
    {
        lock (_lock) _isAwaitingReview = true;
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
    /// Records an event into the history and wakes all clients currently blocked in
    /// <see cref="WaitForChangeAsync"/>. Called by the orchestrator's recording writer.
    /// </summary>
    public void Record(RunEvent evt)
    {
        TaskCompletionSource? previous;
        lock (_lock)
        {
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

    /// <summary>
    /// Maximum age for in-progress entries before they are considered leaked and eligible for
    /// eviction. Prevents permanent memory leaks from runs that never complete.
    /// </summary>
    private static readonly TimeSpan MaxInProgressAge = TimeSpan.FromHours(2);

    private readonly ConcurrentDictionary<string, (RunStreamEntry Entry, DateTimeOffset CreatedAt)> _entries = new();
    private readonly ConcurrentQueue<string> _completedOrder = new();

    public RunStreamEntry Create(string runId, string owner)
    {
        var entry = new RunStreamEntry(owner);
        _entries[runId] = (entry, DateTimeOffset.UtcNow);
        return entry;
    }

    public RunStreamEntry? Get(string runId) =>
        _entries.TryGetValue(runId, out var pair) ? pair.Entry : null;

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
        var cutoff = DateTimeOffset.UtcNow - MaxInProgressAge;
        foreach (var kvp in _entries)
        {
            if (!kvp.Value.Entry.IsCompleted && !kvp.Value.Entry.IsAwaitingReview && kvp.Value.CreatedAt < cutoff)
                _entries.TryRemove(kvp.Key, out _);
        }
    }
}
