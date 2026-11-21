using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;
using Agentweaver.Secrets.AzureKeyVault;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace Agentweaver.Identity.Broker;

internal interface IModelProviderByokVault
{
    bool IsConfigured { get; }

    Task<SecretRef> WriteVersionAsync(
        string secretName,
        SecretCredential credential,
        Guid operationId,
        Guid credentialSlotId,
        CancellationToken cancellationToken);

    Task<SecretRef?> VerifyRecoveryVersionAsync(
        string secretName,
        string secretVersion,
        Guid operationId,
        Guid credentialSlotId,
        CancellationToken cancellationToken);
}

internal sealed class AzureKeyVaultByokVaultClient : IModelProviderByokVault
{
    internal const string OperationIdTag = "agentweaver_operation_id";
    internal const string CredentialSlotIdTag = "agentweaver_credential_slot_id";
    private readonly SecretClient? _client;

    internal AzureKeyVaultByokVaultClient(
        ModelProviderCredentialVaults vaults,
        TokenCredential credential)
    {
        ArgumentNullException.ThrowIfNull(vaults);
        ArgumentNullException.ThrowIfNull(credential);
        if (vaults.Byok is not null)
        {
            var options = new SecretClientOptions
            {
                Retry = { MaxRetries = 0 },
                Diagnostics = { IsLoggingContentEnabled = false },
            };
            _client = new SecretClient(vaults.Byok.VaultUri, credential, options);
        }
    }

    public bool IsConfigured => _client is not null;

    public async Task<SecretRef> WriteVersionAsync(
        string secretName,
        SecretCredential credential,
        Guid operationId,
        Guid credentialSlotId,
        CancellationToken cancellationToken)
    {
        if (_client is null)
            throw ModelProviderCredentialException.VaultUnavailable();
        ArgumentNullException.ThrowIfNull(credential);
        if (string.IsNullOrEmpty(secretName) || secretName.Length > 127 ||
            secretName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw ModelProviderCredentialException.InvalidRequest();
        if (operationId == Guid.Empty || credentialSlotId == Guid.Empty)
            throw ModelProviderCredentialException.InvalidRequest();

        cancellationToken.ThrowIfCancellationRequested();
        if (!credential.IsUsable())
            throw ModelProviderCredentialException.InvalidRequest();

        var secret = new KeyVaultSecret(secretName, credential.GetValue())
        {
            Properties =
            {
                Enabled = true,
                ExpiresOn = credential.ExpiresAt,
            }
        };
        secret.Properties.Tags[OperationIdTag] = operationId.ToString("D");
        secret.Properties.Tags[CredentialSlotIdTag] = credentialSlotId.ToString("D");

        var written = (await _client.SetSecretAsync(secret, cancellationToken).ConfigureAwait(false)).Value;
        if (written is null || written.Name != secretName ||
            string.IsNullOrWhiteSpace(written.Properties.Version))
            throw new InvalidOperationException("Key Vault did not return a secret version.");

        cancellationToken.ThrowIfCancellationRequested();
        if (!credential.IsUsable())
            throw new InvalidOperationException("Credential expired during write verification.");

        var stored = (await _client.GetSecretAsync(
            secretName, written.Properties.Version, cancellationToken).ConfigureAwait(false)).Value;
        if (stored is null ||
            stored.Name != secretName ||
            stored.Properties.Version != written.Properties.Version ||
            stored.Properties.Enabled != true ||
            stored.Value is null ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(secret.Value)),
                SHA256.HashData(Encoding.UTF8.GetBytes(stored.Value))))
            throw new InvalidOperationException("Key Vault readback verification failed.");

        return new SecretRef(secretName, written.Properties.Version);
    }

    public async Task<SecretRef?> VerifyRecoveryVersionAsync(
        string secretName,
        string secretVersion,
        Guid operationId,
        Guid credentialSlotId,
        CancellationToken cancellationToken)
    {
        if (_client is null)
            throw ModelProviderCredentialException.VaultUnavailable();

        try
        {
            var stored = (await _client.GetSecretAsync(secretName, secretVersion, cancellationToken)
                .ConfigureAwait(false)).Value;
            if (stored is null ||
                stored.Properties.Enabled != true ||
                !string.Equals(stored.Name, secretName, StringComparison.Ordinal) ||
                !string.Equals(stored.Properties.Version, secretVersion, StringComparison.Ordinal) ||
                string.IsNullOrEmpty(stored.Value) ||
                !stored.Properties.Tags.TryGetValue(OperationIdTag, out var taggedOp) ||
                !string.Equals(taggedOp, operationId.ToString("D"), StringComparison.Ordinal) ||
                !stored.Properties.Tags.TryGetValue(CredentialSlotIdTag, out var taggedSlot) ||
                !string.Equals(taggedSlot, credentialSlotId.ToString("D"), StringComparison.Ordinal))
                return null;

            return new SecretRef(secretName, secretVersion);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 403)
        {
            return null;
        }
    }
}
