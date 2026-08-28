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
public sealed class RunRepositoryCredentialRegistry(IServiceScopeFactory scopeFactory)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mintLocks = new(StringComparer.Ordinal);

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

            using var scope = scopeFactory.CreateScope();
            var persistence = scope.ServiceProvider.GetRequiredService<TwoAppPersistenceStore>();
            var snapshot = (await persistence.GetCapabilitySnapshotsAsync(runId, ct).ConfigureAwait(false))
                .SingleOrDefault(x => x.Purpose == GitHubCapabilityPurpose.UnattendedRepository);
            if (snapshot is null)
                return null;

            Entry? minted = null;
            var outcome = await scope.ServiceProvider.GetRequiredService<GitHubCapabilityBroker>()
                .TryUseRepositoryCredentialAsync(
                    new SnapshotRef(snapshot.SnapshotRef),
                    DateTimeOffset.UtcNow,
                    (token, expiresAt) =>
                    {
                        minted = new Entry(token, expiresAt);
                        return Task.CompletedTask;
                    },
                    ct).ConfigureAwait(false);
            if (outcome != GitHubCapabilityBrokerOutcome.Issued || minted is null)
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
            if (!_entries.TryRemove(runId, out var entry))
                return;

            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<RepoAppInstallationTokenService>()
                .RevokeRepositoryTokenAsync(entry.AccessToken, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Token expiry bounds a failed best-effort revoke.
        }
        finally
        {
            mintLock.Release();
        }
    }

    private sealed record Entry(string AccessToken, DateTimeOffset ExpiresAt);
}
