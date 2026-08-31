using System.Collections.Concurrent;
using Agentweaver.Api.Auth;
using Agentweaver.Api.Memory;
using Agentweaver.Api.Webhooks;
using Agentweaver.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Agentweaver.Api.Sandbox;

/// <summary>
/// Holds minted repository credentials in API memory until the owning run releases its pod.
/// The registry has no command inputs and does not persist credentials.
/// </summary>
public sealed class RunRepositoryCredentialRegistry
{
    internal static readonly TimeSpan InitialRevocationRetryDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan MaximumRevocationRetryDelay = TimeSpan.FromMinutes(1);

    private readonly IRunRepositoryCredentialMinter _credentialMinter;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, RepositoryCredential> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RetainedRevocation> _retainedRevocations =
        new(StringComparer.Ordinal);
    private readonly object _mintLockGate = new();
    private readonly Dictionary<string, RunCredentialLock> _mintLocks = new(StringComparer.Ordinal);

    internal int ActiveRunLockCount
    {
        get
        {
            lock (_mintLockGate)
                return _mintLocks.Count;
        }
    }

    /// <summary>
    /// Returns this replica's locally minted credential owners. The access tokens stay
    /// private to this registry; callers receive only run identifiers for liveness reconciliation.
    /// </summary>
    internal IReadOnlyList<string> GetActiveCredentialRunIds() => _entries.Keys.ToArray();

    public RunRepositoryCredentialRegistry(IServiceScopeFactory scopeFactory)
        : this(new RunRepositoryCredentialMinter(scopeFactory))
    {
    }

    internal RunRepositoryCredentialRegistry(
        IRunRepositoryCredentialMinter credentialMinter,
        TimeProvider? timeProvider = null)
    {
        _credentialMinter = credentialMinter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string?> MintAsync(string runId, CancellationToken ct = default)
    {
        using var mintLockLease = AcquireMintLock(runId);
        await mintLockLease.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_retainedRevocations.TryGetValue(runId, out var retained))
            {
                if (retained.ExpiresAt > now)
                    return null;

                _retainedRevocations.TryRemove(runId, out _);
            }

            if (_entries.TryGetValue(runId, out var current))
            {
                if (current.ExpiresAt > now)
                    return null;
                _entries.TryRemove(runId, out _);
            }

            var minted = await _credentialMinter.MintAsync(runId, ct).ConfigureAwait(false);
            if (minted is null || minted.ExpiresAt <= _timeProvider.GetUtcNow())
                return null;

            _entries[runId] = minted;
            return minted.AccessToken;
        }
        finally
        {
            mintLockLease.Semaphore.Release();
        }
    }

    public async Task RevokeAsync(string? runId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        using var mintLockLease = AcquireMintLock(runId);
        await mintLockLease.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (_retainedRevocations.TryGetValue(runId, out var retained))
            {
                if (retained.ExpiresAt <= now)
                {
                    _retainedRevocations.TryRemove(runId, out _);
                    _entries.TryRemove(runId, out _);
                }
                return;
            }

            if (!_entries.TryGetValue(runId, out var entry))
                return;
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(runId, out _);
                return;
            }

            await RevokeAndRemoveAsync(runId, entry.AccessToken, entry.ExpiresAt, ct).ConfigureAwait(false);
        }
        finally
        {
            mintLockLease.Semaphore.Release();
        }
    }

    /// <summary>
    /// Retries failed repository-token revocations that are due, including those whose owning
    /// SandboxClaim was already deleted. The expiry is an absolute stop: expired tokens are removed
    /// without another provider call.
    /// </summary>
    internal async Task<IReadOnlyList<FailedRepositoryCredentialRevocation>> RetryFailedRevocationsAsync(
        CancellationToken ct = default)
    {
        var failures = new List<FailedRepositoryCredentialRevocation>();
        foreach (var runId in _retainedRevocations.Keys)
        {
            ct.ThrowIfCancellationRequested();

            using var mintLockLease = AcquireMintLock(runId);
            await mintLockLease.Semaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_retainedRevocations.TryGetValue(runId, out var retained))
                    continue;

                var now = _timeProvider.GetUtcNow();
                if (retained.ExpiresAt <= now)
                {
                    _retainedRevocations.TryRemove(runId, out _);
                    _entries.TryRemove(runId, out _);
                    continue;
                }

                if (retained.NextAttemptAt > now)
                    continue;

                try
                {
                    await _credentialMinter.RevokeAsync(retained.AccessToken, ct).ConfigureAwait(false);
                    _retainedRevocations.TryRemove(runId, out _);
                    _entries.TryRemove(runId, out _);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    RetainFailedRevocation(runId, retained.AccessToken, retained.ExpiresAt);
                    failures.Add(new FailedRepositoryCredentialRevocation(runId, ex));
                }
            }
            finally
            {
                mintLockLease.Semaphore.Release();
            }
        }

        return failures;
    }

    /// <summary>
    /// Revokes credentials this replica minted for runs whose durable run or SandboxClaim state has
    /// become terminal or disappeared. Failed provider revocations remain in the in-memory retry
    /// set until expiry, regardless of whether another replica deleted the claim that prompted this
    /// reconciliation.
    /// </summary>
    internal async Task<IReadOnlyList<FailedRepositoryCredentialRevocation>> ReconcileTerminalOrGoneAsync(
        IReadOnlySet<string> terminalOrGoneRunIds,
        CancellationToken ct = default)
    {
        var failures = new List<FailedRepositoryCredentialRevocation>();
        var activeRunIds = _entries.Keys.ToArray();
        foreach (var runId in activeRunIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!terminalOrGoneRunIds.Contains(runId))
            {
                await RemoveExpiredEntryAsync(runId, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                await RevokeAsync(runId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add(new FailedRepositoryCredentialRevocation(runId, ex));
            }
        }

        failures.AddRange(await RetryFailedRevocationsAsync(ct).ConfigureAwait(false));
        return failures;
    }

    private async Task RemoveExpiredEntryAsync(string runId, CancellationToken ct)
    {
        using var mintLockLease = AcquireMintLock(runId);
        await mintLockLease.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(runId, out var entry) &&
                entry.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                _entries.TryRemove(runId, out _);
            }
        }
        finally
        {
            mintLockLease.Semaphore.Release();
        }
    }

    private RunCredentialLockLease AcquireMintLock(string runId)
    {
        lock (_mintLockGate)
        {
            if (!_mintLocks.TryGetValue(runId, out var mintLock))
            {
                mintLock = new RunCredentialLock();
                _mintLocks.Add(runId, mintLock);
            }

            mintLock.ActiveOperations++;
            return new RunCredentialLockLease(this, runId, mintLock);
        }
    }

    private void ReleaseMintLock(string runId, RunCredentialLock mintLock)
    {
        lock (_mintLockGate)
        {
            if (mintLock.ActiveOperations <= 0)
                throw new InvalidOperationException("Run credential lock lease was released more than once.");

            mintLock.ActiveOperations--;
            if (mintLock.ActiveOperations != 0 ||
                _entries.ContainsKey(runId) ||
                _retainedRevocations.ContainsKey(runId) ||
                !_mintLocks.TryGetValue(runId, out var current) ||
                !ReferenceEquals(current, mintLock))
            {
                return;
            }

            _mintLocks.Remove(runId);
            mintLock.Dispose();
        }
    }

    private async Task RevokeAndRemoveAsync(
        string runId,
        string accessToken,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        try
        {
            await _credentialMinter.RevokeAsync(accessToken, ct).ConfigureAwait(false);
            _retainedRevocations.TryRemove(runId, out _);
            _entries.TryRemove(runId, out _);
        }
        catch
        {
            RetainFailedRevocation(runId, accessToken, expiresAt);
            throw;
        }
    }

    private void RetainFailedRevocation(string runId, string accessToken, DateTimeOffset expiresAt)
    {
        var failures = _retainedRevocations.TryGetValue(runId, out var retained)
            ? retained.FailureCount + 1
            : 1;
        var retryDelay = CalculateRetryDelay(failures);
        _retainedRevocations[runId] = new RetainedRevocation(
            accessToken,
            expiresAt,
            failures,
            _timeProvider.GetUtcNow().Add(retryDelay));
        _entries.TryRemove(runId, out _);
    }

    private static TimeSpan CalculateRetryDelay(int failureCount)
    {
        var multiplier = 1L << Math.Min(Math.Max(failureCount - 1, 0), 6);
        var ticks = Math.Min(
            InitialRevocationRetryDelay.Ticks * multiplier,
            MaximumRevocationRetryDelay.Ticks);
        return TimeSpan.FromTicks(ticks);
    }

    private sealed record RetainedRevocation(
        string AccessToken,
        DateTimeOffset ExpiresAt,
        int FailureCount,
        DateTimeOffset NextAttemptAt);

    private sealed class RunCredentialLock : IDisposable
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ActiveOperations { get; set; }

        public void Dispose() => Semaphore.Dispose();
    }

    private sealed class RunCredentialLockLease(
        RunRepositoryCredentialRegistry registry,
        string runId,
        RunCredentialLock mintLock) : IDisposable
    {
        private RunRepositoryCredentialRegistry? _registry = registry;

        public SemaphoreSlim Semaphore => mintLock.Semaphore;

        public void Dispose()
        {
            var registry = Interlocked.Exchange(ref _registry, null);
            registry?.ReleaseMintLock(runId, mintLock);
        }
    }
}

internal sealed record FailedRepositoryCredentialRevocation(string RunId, Exception Exception);

/// <summary>
/// The registry's credential-only dependency. It has no knowledge of git, gh, command text, or
/// sandbox execution; it only mints from the run's fenced repository snapshot and revokes the
/// provider credential.
/// </summary>
internal interface IRunRepositoryCredentialMinter
{
    Task<RepositoryCredential?> MintAsync(string runId, CancellationToken ct);
    Task RevokeAsync(string accessToken, CancellationToken ct);
}

internal sealed class RunRepositoryCredentialMinter(IServiceScopeFactory scopeFactory)
    : IRunRepositoryCredentialMinter
{
    public async Task<RepositoryCredential?> MintAsync(string runId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var persistence = scope.ServiceProvider.GetRequiredService<GitHubConnectionsPersistenceStore>();
        var snapshot = (await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false))
            .SingleOrDefault(x => x.Purpose == GitHubCapabilityPurpose.UnattendedRepository);
        if (snapshot is null)
            return null;

        RepositoryCredential? minted = null;
        var outcome = await scope.ServiceProvider.GetRequiredService<GitHubCapabilityBroker>()
            .TryUseRepositoryCredentialAsync(
                new SnapshotRef(snapshot.SnapshotRef),
                DateTimeOffset.UtcNow,
                (token, expiresAt) =>
                {
                    minted = new RepositoryCredential(token, expiresAt);
                    return Task.CompletedTask;
                },
                ct).ConfigureAwait(false);
        return outcome == GitHubCapabilityBrokerOutcome.Issued ? minted : null;
    }

    public async Task RevokeAsync(string accessToken, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<RepoAppInstallationTokenService>()
            .RevokeRepositoryTokenAsync(accessToken, ct).ConfigureAwait(false);
    }
}

internal sealed record RepositoryCredential(string AccessToken, DateTimeOffset ExpiresAt);
