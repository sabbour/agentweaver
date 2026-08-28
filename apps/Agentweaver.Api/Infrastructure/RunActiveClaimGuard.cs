namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// In-process, per-run mutual exclusion used only when a run's status and its durable
/// tool-approval policy events cannot share a single ACID transaction. On the default SQLite
/// deployment, run records live in a separate SQLite database (<see cref="SqliteRunStore"/>,
/// its own connection) from the RunEvents/policy store (EF Core <c>MemoryDbContext</c>), so no
/// single transaction can span both. This guard closes that gap by serializing the interval
/// between reading a run's active status and either committing or evaluating a durable approval
/// policy against every guarded run-store status transition for the same run (see
/// <see cref="RunActiveClaimGuardedRunStore"/>), so terminalization cannot land in that window.
///
/// This mirrors the established precedent in <c>Agentweaver.Api.Git.RepositoryMergeLock</c>:
/// Postgres deployments get true cross-replica atomicity (a single transaction with
/// <c>FOR UPDATE</c>); local/dev SQLite -- which this codebase already treats as single-process,
/// per that lock's own semaphore fallback -- gets a process-wide async lock instead. Only one
/// process ever opens a given SQLite deployment's database files, so an in-process lock is a
/// complete fix here, not a partial workaround.
///
/// Keyed by run id. An entry is retained while it has either a holder or a waiter, then removed
/// and disposed. Registry changes are synchronized separately from the per-run waits, preserving
/// concurrency between different runs.
/// </summary>
public sealed class RunActiveClaimGuard
{
    private readonly object _registryGate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal int EntryCount
    {
        get
        {
            lock (_registryGate)
                return _entries.Count;
        }
    }

    /// <summary>
    /// Acquires the exclusive claim for <paramref name="runId"/>, waiting if another operation
    /// (a policy grant/evaluation or a guarded status transition) currently holds it. Dispose the
    /// returned handle to release the claim.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(Domain.RunId runId, CancellationToken ct)
    {
        var key = runId.ToString();
        Entry entry;
        lock (_registryGate)
        {
            if (!_entries.TryGetValue(key, out entry!))
                _entries.Add(key, entry = new Entry());
            entry.Users++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry);
            throw;
        }
    }

    private void ReleaseReference(string key, Entry entry)
    {
        lock (_registryGate)
        {
            if (--entry.Users != 0)
                return;

            if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
            {
                _entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    private sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;
    }

    private sealed class Releaser(RunActiveClaimGuard owner, string key, Entry entry) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                entry.Semaphore.Release();
                owner.ReleaseReference(key, entry);
            }
            return ValueTask.CompletedTask;
        }
    }
}
