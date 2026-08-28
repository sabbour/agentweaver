using System.Collections.Concurrent;

namespace Agentweaver.Api.Infrastructure;

/// <summary>
/// In-process, per-run mutual exclusion used only when a run's status and its durable
/// tool-approval policy events cannot share a single ACID transaction. On the default SQLite
/// deployment, run records live in a separate SQLite database (<see cref="SqliteRunStore"/>,
/// its own connection) from the RunEvents/policy store (EF Core <c>MemoryDbContext</c>), so no
/// single transaction can span both. This guard closes that gap by serializing the interval
/// between reading a run's active status and committing a durable approval policy against every
/// guarded run-store status transition for the same run (see
/// <see cref="RunActiveClaimGuardedRunStore"/>), so terminalization cannot land in that window.
///
/// This mirrors the established precedent in <c>Agentweaver.Api.Git.RepositoryMergeLock</c>:
/// Postgres deployments get true cross-replica atomicity (a single transaction with
/// <c>FOR UPDATE</c>); local/dev SQLite -- which this codebase already treats as single-process,
/// per that lock's own semaphore fallback -- gets a process-wide async lock instead. Only one
/// process ever opens a given SQLite deployment's database files, so an in-process lock is a
/// complete fix here, not a partial workaround.
///
/// Keyed by run id. Entries are never removed; the number of distinct runs a single process
/// ever handles is bounded and the per-entry cost (one <see cref="SemaphoreSlim"/>) is small.
/// </summary>
public sealed class RunActiveClaimGuard
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>
    /// Acquires the exclusive claim for <paramref name="runId"/>, waiting if another operation
    /// (a policy grant or a guarded status transition) currently holds it. Dispose the returned
    /// handle to release the claim.
    /// </summary>
    public async Task<IAsyncDisposable> AcquireAsync(Domain.RunId runId, CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(runId.ToString(), _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Releaser(semaphore);
    }

    private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
