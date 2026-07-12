namespace Agentweaver.AgentTools;

/// <summary>
/// Immutable active-command metadata that watchdogs and future heartbeat emitters can observe
/// without receiving the command text.
/// </summary>
public sealed record ShellExecutionSnapshot(
    string ToolCallId,
    string CommandHash,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline);

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
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _sync = new();
    private ShellExecutionSnapshot? _activeExecution;
    private bool _activeExecutionOwnsSingleFlight;
    private long _version;
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
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeExecution is not null)
                return string.Equals(_activeExecution.ToolCallId, toolCallId, StringComparison.Ordinal);

            var startedAt = DateTimeOffset.UtcNow;
            var deadline = hardTimeout > TimeSpan.Zero
                ? startedAt.Add(hardTimeout)
                : DateTimeOffset.MaxValue;
            _activeExecution = new ShellExecutionSnapshot(
                toolCallId,
                commandHash,
                startedAt,
                deadline);
            _activeExecutionOwnsSingleFlight = false;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
        return true;
    }

    /// <summary>Completes an SDK-owned shell when its matching tool lifecycle event arrives.</summary>
    public bool CompleteObservedExecution(string toolCallId)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (_activeExecution is null ||
                _activeExecutionOwnsSingleFlight ||
                !string.Equals(_activeExecution.ToolCallId, toolCallId, StringComparison.Ordinal))
            {
                return false;
            }

            _activeExecution = null;
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

    /// <summary>Clears an SDK-owned observation after a faulted/cancelled turn.</summary>
    public void ClearObservedExecution()
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (_activeExecution is null || _activeExecutionOwnsSingleFlight)
                return;
            _activeExecution = null;
            changed = AdvanceVersionLocked();
        }
        changed.TrySetResult();
    }

    private void Exit(ShellExecutionSnapshot snapshot)
    {
        TaskCompletionSource? changed = null;
        lock (_sync)
        {
            if (ReferenceEquals(_activeExecution, snapshot))
            {
                _activeExecution = null;
                _activeExecutionOwnsSingleFlight = false;
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
