namespace Agentweaver.AgentTools;

/// <summary>
/// Immutable active-command metadata that watchdogs and future heartbeat emitters can observe
/// without receiving the command text.
/// </summary>
public sealed record ShellExecutionSnapshot(
    string CommandHash,
    DateTimeOffset StartedAt,
    DateTimeOffset Deadline);

/// <summary>
/// Single-flight shell gate with observable active-command timing. It does not create heartbeat or
/// timeout policy; callers can reuse <see cref="ActiveExecution"/> to implement those policies.
/// </summary>
public sealed class ShellExecutionTracker : IDisposable
{
    private readonly SemaphoreSlim _singleFlight = new(1, 1);
    private readonly object _sync = new();
    private ShellExecutionSnapshot? _activeExecution;
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
        var snapshot = new ShellExecutionSnapshot(commandHash, startedAt, startedAt.Add(timeout));
        lock (_sync)
        {
            if (_disposed)
            {
                _singleFlight.Release();
                throw new ObjectDisposedException(nameof(ShellExecutionTracker));
            }
            _activeExecution = snapshot;
        }
        return new Lease(this, snapshot);
    }

    private void Exit(ShellExecutionSnapshot snapshot)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_activeExecution, snapshot))
                _activeExecution = null;
        }
        _singleFlight.Release();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
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
