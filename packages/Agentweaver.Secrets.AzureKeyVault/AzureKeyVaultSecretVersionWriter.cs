using Agentweaver.Abstractions;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Secrets.AzureKeyVault;

public sealed class AzureKeyVaultSecretVersionWriter : ISecretVersionWriter
{
    private readonly SecretClient _client;

    public AzureKeyVaultSecretVersionWriter(
        AzureKeyVaultConfiguration configuration,
        TokenCredential credential,
        SecretClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credential);
        options ??= new SecretClientOptions { Retry = { MaxRetries = 0 } };
        if (options.Diagnostics.IsLoggingContentEnabled || options.Retry.MaxRetries != 0)
            throw new ArgumentException(
                "Credential version writes require disabled content logging and zero automatic retries.", nameof(options));
        _client = new SecretClient(configuration.VaultUri, credential, options);
    }

    public async Task<SecretRef> WriteVersionAsync(
        string secretId, SecretCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrEmpty(secretId) || secretId.Length > 127 ||
            secretId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("An exact native Key Vault secret name is required.", nameof(secretId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!credential.IsUsable())
            throw new AzureKeyVaultSecretException(AzureKeyVaultSecretFailure.InvalidValue);
        var secret = new KeyVaultSecret(secretId, credential.GetValue())
        {
            Properties = { Enabled = true, ExpiresOn = credential.ExpiresAt }
        };
        KeyVaultSecret written;
        KeyVaultSecret stored;
        try
        {
            written = (await _client.SetSecretAsync(secret, cancellationToken).ConfigureAwait(false)).Value;
            if (written is null || written.Name != secretId ||
                string.IsNullOrWhiteSpace(written.Properties.Version))
                throw new AzureKeyVaultSecretException(AzureKeyVaultSecretFailure.InvalidValue);
            cancellationToken.ThrowIfCancellationRequested();
            if (!credential.IsUsable())
                throw new AzureKeyVaultSecretException(AzureKeyVaultSecretFailure.InvalidValue);
            stored = (await _client.GetSecretAsync(
                secretId, written.Properties.Version, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AzureKeyVaultSecretException(
                error.Status == 403 ? AzureKeyVaultSecretFailure.AccessDenied : AzureKeyVaultSecretFailure.ServiceFailure,
                error.Status);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!credential.IsUsable() || stored is null || stored.Name != secretId ||
            stored.Properties.Version != written.Properties.Version || stored.Properties.Enabled != true ||
            stored.Properties.ExpiresOn?.ToUnixTimeSeconds() != secret.Properties.ExpiresOn?.ToUnixTimeSeconds() ||
            stored.Properties.ExpiresOn > credential.ExpiresAt ||
            stored.Value is null ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret.Value)),
                SHA256.HashData(Encoding.UTF8.GetBytes(stored.Value))))
            throw new AzureKeyVaultSecretException(AzureKeyVaultSecretFailure.InvalidValue);
        return new SecretRef(secretId, written.Properties.Version);
    }
}
