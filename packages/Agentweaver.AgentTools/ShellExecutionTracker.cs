namespace Agentweaver.AgentTools;

/// <summary>
/// Immutable active-command metadata that watchdogs and future heartbeat emitters can observe
/// without receiving the command text.
/// </summary>
public sealed record ShellExecutionSnapshot(
    string ToolCallId,
    string CommandHash,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline,
    long Generation = 0);

/// <summary>Atomic observation used by watchdogs to wait for active-shell lifecycle changes.</summary>
public sealed record ShellExecutionObservation(
    long Version,
    ShellExecutionSnapshot? ActiveExecution);

/// <summary>
/// Single-flight shell gate with observable active-command timing. It does not create heartbeat or
/// timeout policy; callers can reuse <see cref="ActiveExecution"/> to implement those policies.
/// </summary>
public sealed class ShellExecutionTracker : IDisposable
{
    public enum ObservedExecutionState
    {
        Idle,
        Running,
        Terminating,
        Fenced,
    }

    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _sync = new();
    private ShellExecutionSnapshot? _activeExecution;
    private bool _activeExecutionOwnsSingleFlight;
    private long _version;
    private long _observedGeneration;
    private ObservedExecutionState _observedState;
    private TaskCompletionSource _changed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public ShellExecutionSnapshot? ActiveExecution
    {
        get
        {
            lock (_sync)
                return _activeExecution;
        }
    }

    /// <summary>
    /// Starts a new SDK-owned turn generation. Lifecycle callbacks from previous generations are
    /// rejected rather than being allowed to mutate this turn's shell slot.
    /// </summary>
    public long BeginObservedTurn()
    {
        TaskCompletionSource changed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observedGeneration++;
            _activeExecution = null;
            _activeExecutionOwnsSingleFlight = false;
            _observedState = ObservedExecutionState.Idle;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return _observedGeneration;
    }

    public async Task<IDisposable> EnterAsync(
        string commandHash,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        lock (_sync)
            ObjectDisposedException.ThrowIf(_disposed, this);
        await _singleFlight.WaitAsync(ct).ConfigureAwait(false);

        var startedAt = DateTimeOffset.UtcNow;
        var deadline = timeout > TimeSpan.Zero ? startedAt.Add(timeout) : DateTimeOffset.MaxValue;
        var snapshot = new ShellExecutionSnapshot(commandHash, commandHash, startedAt, deadline);
        TaskCompletionSource changed;
        lock (_sync)
        {
            if (_disposed)
            {
                _singleFlight.Release();
                throw new ObjectDisposedException(nameof(ShellExecutionTracker));
            }
            _activeExecution = snapshot;
            _activeExecutionOwnsSingleFlight = true;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return new Lease(this, snapshot);
    }

    /// <summary>
    /// Starts observing a shell owned by the Copilot SDK. Unlike <see cref="EnterAsync"/>, this
    /// does not acquire the custom-tool single-flight semaphore because the SDK already owns the
    /// execution. Returns false if a different shell is already active.
    /// </summary>
    public bool TryStartObservedExecution(
        string toolCallId,
        string commandHash,
        TimeSpan hardTimeout)
        => TryStartObservedExecution(toolCallId, commandHash, hardTimeout, _observedGeneration);

    /// <summary>Starts a shell only when its callback belongs to the current, unfenced turn.</summary>
    public bool TryStartObservedExecution(
        string toolCallId,
        string commandHash,
        TimeSpan hardTimeout,
        long generation)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (generation != _observedGeneration || _observedState == ObservedExecutionState.Fenced ||
                _observedState == ObservedExecutionState.Terminating)
                return false;
            if (_activeExecution is not null)
                return _observedState == ObservedExecutionState.Running &&
                       string.Equals(_activeExecution.ToolCallId, toolCallId, StringComparison.Ordinal) &&
                       _activeExecution.Generation == generation;

            var startedAt = DateTimeOffset.UtcNow;
            var deadline = hardTimeout > TimeSpan.Zero
                ? startedAt.Add(hardTimeout)
                : DateTimeOffset.MaxValue;
            _activeExecution = new ShellExecutionSnapshot(
                toolCallId,
                commandHash,
                startedAt,
                deadline,
                generation);
            _activeExecutionOwnsSingleFlight = false;
            _observedState = ObservedExecutionState.Running;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return true;
    }

    /// <summary>Completes an SDK-owned shell when its matching tool lifecycle event arrives.</summary>
    public bool CompleteObservedExecution(string toolCallId)
        => CompleteObservedExecution(toolCallId, _observedGeneration);

    /// <summary>Completes only the matching running generation.</summary>
    public bool CompleteObservedExecution(string toolCallId, long generation)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (generation != _observedGeneration ||
                _observedState != ObservedExecutionState.Running ||
                _activeExecution is null ||
                _activeExecutionOwnsSingleFlight ||
                !string.Equals(_activeExecution.ToolCallId, toolCallId, StringComparison.Ordinal) ||
                _activeExecution.Generation != generation)
            {
                return false;
            }

            _activeExecution = null;
            _observedState = ObservedExecutionState.Idle;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return true;
    }

    /// <summary>Returns an atomic snapshot and version for heartbeat/watchdog observation.</summary>
    public ShellExecutionObservation Observe()
    {
        lock (_sync)
            return new ShellExecutionObservation(_version, _activeExecution);
    }

    /// <summary>Completes when the active execution differs from the supplied observation.</summary>
    public Task WaitForChangeAsync(long observedVersion, CancellationToken ct = default)
    {
        Task changed;
        lock (_sync)
        {
            if (_version != observedVersion)
                return Task.CompletedTask;
            changed = _changed.Task;
        }
        return changed.WaitAsync(ct);
    }

    /// <summary>
    /// Marks a matching shell as terminating. A second shell cannot start until it is fenced.
    /// </summary>
    public bool TryBeginObservedTermination(ShellExecutionSnapshot snapshot)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (!MatchesObservedExecutionLocked(snapshot) ||
                _observedState != ObservedExecutionState.Running)
                return false;

            _observedState = ObservedExecutionState.Terminating;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return true;
    }

    /// <summary>
    /// Releases a terminated shell slot while fencing its generation so late SDK callbacks cannot
    /// re-arm the tracker. A subsequent <see cref="BeginObservedTurn"/> opens a new generation.
    /// </summary>
    public bool FenceObservedExecution(ShellExecutionSnapshot snapshot)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (!MatchesObservedExecutionLocked(snapshot))
                return false;

            _activeExecution = null;
            _observedState = ObservedExecutionState.Fenced;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return true;
    }

    /// <summary>Reports whether callbacks for the supplied generation are permanently fenced.</summary>
    public bool IsObservedGenerationFenced(long generation)
    {
        lock (_sync)
            return generation == _observedGeneration && _observedState == ObservedExecutionState.Fenced;
    }

    /// <summary>Clears a matching SDK-owned observation after a faulted/cancelled turn.</summary>
    public void ClearObservedExecution(long generation)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (generation != _observedGeneration || _activeExecution is null ||
                _activeExecutionOwnsSingleFlight || _activeExecution.Generation != generation)
                return;
            _activeExecution = null;
            _observedState = ObservedExecutionState.Idle;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
    }

    /// <summary>Clears the current observed generation for legacy callers.</summary>
    public void ClearObservedExecution() => ClearObservedExecution(_observedGeneration);

    private bool MatchesObservedExecutionLocked(ShellExecutionSnapshot snapshot) =>
        _activeExecution is not null &&
        !_activeExecutionOwnsSingleFlight &&
        _activeExecution.Generation == snapshot.Generation &&
        _activeExecution.Generation == _observedGeneration &&
        string.Equals(_activeExecution.ToolCallId, snapshot.ToolCallId, StringComparison.Ordinal) &&
        string.Equals(_activeExecution.CommandHash, snapshot.CommandHash, StringComparison.Ordinal) &&
        _activeExecution.StartedAt == snapshot.StartedAt;

    private void Exit(ShellExecutionSnapshot snapshot)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (ReferenceEquals(_activeExecution, snapshot))
            {
                _activeExecution = null;
                _activeExecutionOwnsSingleFlight = false;
                _observedState = ObservedExecutionState.Idle;
                changed = AdvanceVersionLocked();
            }
        }
        changed?.TrySetResult();
        _singleFlight.Release();
    }

    public void Dispose()
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            _activeExecution = null;
            _activeExecutionOwnsSingleFlight = false;
            _observedState = ObservedExecutionState.Fenced;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
    }

    private TaskCompletionSource AdvanceVersionLocked()
    {
        _version++;
        var changed = _changed;
        _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return changed;
    }

    private sealed class Lease(
        ShellExecutionTracker owner,
        ShellExecutionSnapshot snapshot) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Exit(snapshot);
        }
    }
}
