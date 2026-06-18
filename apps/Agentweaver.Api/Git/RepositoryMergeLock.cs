using System.Collections.Concurrent;

namespace Agentweaver.Api.Git;

/// <summary>
/// Process-wide, per-repository semaphore that serializes concurrent approve
/// requests for the same repository. Prevents the TOCTOU window between the
/// status check and the DB CAS when two concurrent approvals race on different
/// runs in the same repository (MF2).
///
/// Keyed by canonical repository path. A single <see cref="SemaphoreSlim(1,1)"/>
/// is created per path on first access and reused thereafter.
/// </summary>
public sealed class RepositoryMergeLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tries to acquire the per-repository lock within <paramref name="timeout"/>.
    /// Returns a disposable handle that releases the semaphore on disposal, or
    /// null when the timeout expires (caller should return 409 retriable).
    /// </summary>
    public async Task<IDisposable?> TryAcquireAsync(
        string canonicalRepoPath,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var semaphore = _locks.GetOrAdd(canonicalRepoPath, _ => new SemaphoreSlim(1, 1));

        if (!await semaphore.WaitAsync(timeout, ct).ConfigureAwait(false))
            return null;

        return new SemaphoreReleaser(semaphore);
    }

    private sealed class SemaphoreReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                semaphore.Release();
        }
    }
}
