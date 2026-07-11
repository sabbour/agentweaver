using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Agentweaver.Api.Memory;

namespace Agentweaver.Api.Coordinator;

/// <summary>
/// Cross-process advisory lock that serializes integration-branch builds per project repository.
///
/// The physical git repo at <c>/workspace/{projectId}/.git</c> is shared by every run in a project
/// (Azure Files SMB in the cloud). <see cref="Git.WorktreeManager.BuildIntegrationBranch"/> deletes
/// and recreates the integration ref on each rebuild, so two builds racing the same repo produce a
/// <c>LibGit2Sharp.LockedFileException</c> (ref-lock contention) or a null-ref while the ref is being
/// swapped. Both the dispatch dependency-base rebuild and the collective-assembly integration build
/// take this lock, keyed by <b>projectId</b> (repo granularity, not per-run), around the build.
///
/// Backed by the <c>IntegrationBuildLocks</c> table (one row per project) claimed with a conditional
/// UPSERT and released by the holder in a finally. Identical SQL runs on SQLite (local/tests) and
/// Postgres (staging) — both support <c>INSERT ... ON CONFLICT ... DO UPDATE ... WHERE</c>. This is a
/// DB lock rather than a named OS mutex (which would not span pods) or an SMB/git file lock (the very
/// substrate that fails under the race). A per-acquisition <see cref="IntegrationBuildLockRecord.OwnerToken"/>
/// fences release so a lock stolen after the stale TTL is never released by the crashed original
/// holder, and the stale TTL guarantees a crashed holder never deadlocks the project.
/// </summary>
public sealed class IntegrationBuildLock
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IntegrationBuildLock> _logger;
    private readonly string _myPodId;
    private readonly TimeSpan _staleTtl;

    public IntegrationBuildLock(
        IServiceScopeFactory scopeFactory,
        ILogger<IntegrationBuildLock> logger,
        IConfiguration? configuration = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        _myPodId = configuration?.GetValue<string>("App:PodId")
                   ?? Environment.GetEnvironmentVariable("HOSTNAME")
                   ?? Environment.MachineName;

        // A build normally finishes in seconds (in-memory libgit2 tree merges). The stale TTL only
        // exists so a crashed holder's row is reclaimable; keep it comfortably above the worst-case
        // build so a live holder is never stolen from mid-build (default 300 s).
        var staleSecs = configuration?.GetValue("Coordinator:IntegrationBuildLockStaleTtlSeconds", 300) ?? 300;
        _staleTtl = TimeSpan.FromSeconds(Math.Max(30, staleSecs));
    }

    /// <summary>
    /// Resolves the lock key for a project. Prefers the authoritative project id; falls back to the
    /// normalized repository path (<c>/workspace/{projectId}</c>, one-to-one with the repo) when the
    /// project id is not carried on the context. Both integration-build callers (dispatch dependency-base
    /// rebuild and collective assembly) MUST derive the key the same way so they serialize together.
    /// </summary>
    public static string ResolveProjectKey(string? projectId, string? repositoryPath)
    {
        if (!string.IsNullOrWhiteSpace(projectId))
            return projectId;
        return (repositoryPath ?? string.Empty).Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>
    /// Acquires the per-project build lock, polling until it is free (or reclaimable as stale) or
    /// <paramref name="timeout"/> elapses. Returns a handle whose disposal releases the lock, or
    /// <c>null</c> if the lock could not be taken within the timeout (the caller then skips or defers
    /// its build). Reclaims a lock older than the configured stale TTL so a crashed holder never
    /// deadlocks the project.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string projectId, TimeSpan timeout, CancellationToken ct)
    {
        var token = Guid.NewGuid().ToString("N");
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (true)
        {
            if (await TryClaimAsync(projectId, token, ct).ConfigureAwait(false))
                return new Handle(this, projectId, token);

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogWarning(
                    "Integration build lock: could not acquire lock for project {ProjectId} within {TimeoutSeconds}s (held by a peer build)",
                    projectId, timeout.TotalSeconds);
                return null;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Conditional-claim UPSERT: insert the lock row if absent, or steal it only when the current
    /// holder's row is older than the stale threshold. Returns true only for the single winner (the
    /// statement affects one row); a fresh, peer-held lock leaves the WHERE false and affects zero
    /// rows. Identical SQL on SQLite and Postgres.
    /// </summary>
    private async Task<bool> TryClaimAsync(string projectId, string token, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var staleThreshold = now - _staleTtl;

        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "IntegrationBuildLocks" ("ProjectId", "OwnerToken", "OwnerPodId", "AcquiredAt")
            VALUES ({projectId}, {token}, {_myPodId}, {now})
            ON CONFLICT ("ProjectId") DO UPDATE
               SET "OwnerToken" = {token},
                   "OwnerPodId" = {_myPodId},
                   "AcquiredAt" = {now}
             WHERE "IntegrationBuildLocks"."AcquiredAt" < {staleThreshold}
            """, ct).ConfigureAwait(false);

        return rows > 0;
    }

    /// <summary>
    /// Releases the lock. The DELETE is fenced on <see cref="IntegrationBuildLockRecord.OwnerToken"/>
    /// so a holder whose lock was stolen after the stale TTL cannot delete the new holder's row.
    /// </summary>
    private async Task ReleaseAsync(string projectId, string token)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                DELETE FROM "IntegrationBuildLocks"
                 WHERE "ProjectId" = {projectId} AND "OwnerToken" = {token}
                """, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A leaked row is self-healing: the stale TTL lets the next builder reclaim it.
            _logger.LogWarning(ex,
                "Integration build lock: failed to release lock for project {ProjectId}; it will be reclaimed after the stale TTL",
                projectId);
        }
    }

    private sealed class Handle(IntegrationBuildLock owner, string projectId, string token) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                await owner.ReleaseAsync(projectId, token).ConfigureAwait(false);
        }
    }
}
