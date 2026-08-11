using Azure;
using Azure.Security.KeyVault.Secrets;

namespace Agentweaver.Api.Auth;

internal interface IKeyVaultSecretWriterClient
{
    Task<string> SetSecretAsync(string key, string value, CancellationToken ct);
    Task RecoverDeletedSecretAsync(string key, CancellationToken ct);
    Task<bool> IsSecretActiveAsync(string key, CancellationToken ct);
}

internal sealed class AzureKeyVaultSecretWriterClient(SecretClient client) : IKeyVaultSecretWriterClient
{
    public async Task<string> SetSecretAsync(string key, string value, CancellationToken ct)
    {
        var response = await client.SetSecretAsync(key, value, ct).ConfigureAwait(false);
        return response.Value.Properties.Version ?? string.Empty;
    }

    public async Task RecoverDeletedSecretAsync(string key, CancellationToken ct)
    {
        _ = await client.StartRecoverDeletedSecretAsync(key, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsSecretActiveAsync(string key, CancellationToken ct)
    {
        try
        {
            _ = await client.GetSecretAsync(key, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }
}

/// <summary>
/// Restores a soft-deleted Key Vault secret before replacing it with a fresh value.
/// Recovery keeps purge protection intact and hides Key Vault's deleted-but-recoverable
/// state behind <see cref="ISecretStore.SetSecretAsync"/>.
/// </summary>
internal sealed class KeyVaultRecoverableSecretWriter
{
    internal const int DefaultMaxPollAttempts = 60;
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IKeyVaultSecretWriterClient _client;
    private readonly int _maxPollAttempts;
    private readonly TimeSpan _pollInterval;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public KeyVaultRecoverableSecretWriter(
        IKeyVaultSecretWriterClient client,
        int maxPollAttempts = DefaultMaxPollAttempts,
        TimeSpan? pollInterval = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPollAttempts, 1);
        _client = client;
        _maxPollAttempts = maxPollAttempts;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _delay = delay ?? Task.Delay;
    }

    public async Task<string> SetSecretAsync(string key, string value, CancellationToken ct)
    {
        try
        {
            return await _client.SetSecretAsync(key, value, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // ObjectIsDeletedButRecoverable is the steady tombstone state. Key Vault can also
            // return a generic conflict while delete/recovery is still transitioning.
        }

        await StartRecoveryOrJoinConcurrentCreatorAsync(key, ct).ConfigureAwait(false);

        RequestFailedException? lastConflict = null;
        for (var attempt = 0; attempt < _maxPollAttempts; attempt++)
        {
            var isActive = await _client.IsSecretActiveAsync(key, ct).ConfigureAwait(false);
            if (isActive)
            {
                try
                {
                    return await _client.SetSecretAsync(key, value, ct).ConfigureAwait(false);
                }
                catch (RequestFailedException ex) when (ex.Status == 409)
                {
                    lastConflict = ex;
                    await StartRecoveryOrJoinConcurrentCreatorAsync(key, ct).ConfigureAwait(false);
                }
            }
            else
            {
                // A just-started delete can return 409 from Set before the tombstone is visible
                // to Recover. Retry recovery while polling so an immediate relaunch still heals.
                await StartRecoveryOrJoinConcurrentCreatorAsync(key, ct).ConfigureAwait(false);
            }

            if (attempt + 1 < _maxPollAttempts)
                await _delay(_pollInterval, ct).ConfigureAwait(false);
        }

        var timeout = TimeSpan.FromTicks(_pollInterval.Ticks * _maxPollAttempts);
        throw new TimeoutException(
            $"Key Vault secret '{key}' did not become writable within {timeout.TotalSeconds:0.###} seconds after recovery.",
            lastConflict);
    }

    private async Task StartRecoveryOrJoinConcurrentCreatorAsync(string key, CancellationToken ct)
    {
        try
        {
            await _client.RecoverDeletedSecretAsync(key, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 409)
        {
            // Another creator may already be recovering the deterministic key, or the key
            // became active between the failed write and this call. A 404 can occur briefly
            // while a delete is transitioning into the recoverable collection.
        }
    }
}
