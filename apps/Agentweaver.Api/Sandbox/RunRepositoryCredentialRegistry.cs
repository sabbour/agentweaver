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
    private readonly IRunRepositoryCredentialMinter _credentialMinter;
    private readonly ConcurrentDictionary<string, RepositoryCredential> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mintLocks = new(StringComparer.Ordinal);

    public RunRepositoryCredentialRegistry(IServiceScopeFactory scopeFactory)
        : this(new RunRepositoryCredentialMinter(scopeFactory))
    {
    }

    internal RunRepositoryCredentialRegistry(IRunRepositoryCredentialMinter credentialMinter) =>
        _credentialMinter = credentialMinter;

    public async Task<string?> MintAsync(string runId, CancellationToken ct = default)
    {
        var mintLock = _mintLocks.GetOrAdd(runId, static _ => new SemaphoreSlim(1, 1));
        await mintLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(runId, out var current))
            {
                if (current.ExpiresAt > DateTimeOffset.UtcNow)
                    return null;
                _entries.TryRemove(runId, out _);
            }

            var minted = await _credentialMinter.MintAsync(runId, ct).ConfigureAwait(false);
            if (minted is null || minted.ExpiresAt <= DateTimeOffset.UtcNow)
                return null;

            _entries[runId] = minted;
            return minted.AccessToken;
        }
        finally
        {
            mintLock.Release();
        }
    }

    public async Task RevokeAsync(string? runId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return;

        var mintLock = _mintLocks.GetOrAdd(runId, static _ => new SemaphoreSlim(1, 1));
        await mintLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(runId, out var entry))
                return;

            if (entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _entries.TryRemove(runId, out _);
                return;
            }

            await _credentialMinter.RevokeAsync(entry.AccessToken, ct).ConfigureAwait(false);
            _entries.TryRemove(runId, out _);
        }
        finally
        {
            mintLock.Release();
        }
    }
}

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
        var persistence = scope.ServiceProvider.GetRequiredService<TwoAppPersistenceStore>();
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
