using Agentweaver.Abstractions;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using System.Security.Cryptography;
using System.Text;

namespace Agentweaver.Secrets.AzureKeyVault;

/// <summary>
/// Writes short-lived credentials to Azure Key Vault and returns the exact created secret
/// version only after reading it back and comparing its value and metadata.
/// </summary>
public sealed class AzureKeyVaultSecretVersionWriter : ISecretVersionWriter
{
    private readonly SecretClient _client;

    /// <summary>
    /// Creates a writer that disables SDK retries and content logging, preventing an
    /// ambiguous retry from creating multiple secret versions.
    /// </summary>
    /// <param name="configuration">The validated vault root URI.</param>
    /// <param name="credential">The Azure credential used for Key Vault access.</param>
    /// <param name="options">Optional SDK options subject to the safe-write checks.</param>
    /// <exception cref="ArgumentException">Options enable content logging or automatic retries.</exception>
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

    /// <summary>
    /// Creates one enabled, expiring secret version and returns its exact version reference
    /// only after confirming that Key Vault stored the same value and expiry.
    /// </summary>
    /// <param name="secretId">The native Key Vault secret name.</param>
    /// <param name="credential">The secret material and its requested expiry.</param>
    /// <param name="cancellationToken">Cancels the write or read-back verification.</param>
    /// <returns>A reference containing the exact created secret version.</returns>
    /// <exception cref="AzureKeyVaultSecretException">The credential or stored version is invalid, or Azure denies/fails the request.</exception>
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
