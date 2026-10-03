using Agentweaver.Abstractions;
using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;

namespace Agentweaver.Secrets.AzureKeyVault;

public sealed class AzureKeyVaultConfiguration
{
    private static readonly string[] VaultDnsSuffixes =
        [".vault.azure.net", ".vault.usgovcloudapi.net", ".vault.azure.cn"];

    public AzureKeyVaultConfiguration(Uri vaultUri)
    {
        ArgumentNullException.ThrowIfNull(vaultUri);
        var name = VaultDnsSuffixes
            .Where(suffix => vaultUri.IsAbsoluteUri &&
                vaultUri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .Select(suffix => vaultUri.Host[..^suffix.Length])
            .FirstOrDefault();
        if (!vaultUri.IsAbsoluteUri || vaultUri.Scheme != Uri.UriSchemeHttps ||
            !vaultUri.IsDefaultPort || vaultUri.AbsolutePath != "/" ||
            vaultUri.UserInfo.Length != 0 || vaultUri.Query.Length != 0 ||
            vaultUri.Fragment.Length != 0 || name is null ||
            name.Length is < 3 or > 24 || !char.IsAsciiLetter(name[0]) ||
            !char.IsAsciiLetterOrDigit(name[^1]) || name.Contains("--", StringComparison.Ordinal) ||
            name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("An HTTPS Azure Key Vault root URI is required.", nameof(vaultUri));

        VaultUri = vaultUri;
    }

    public Uri VaultUri { get; }
}

public enum AzureKeyVaultSecretFailure
{
    NotFound,
    Disabled,
    AccessDenied,
    ServiceFailure,
    InvalidValue,
}

public sealed class AzureKeyVaultSecretException : Exception
{
    internal AzureKeyVaultSecretException(AzureKeyVaultSecretFailure failure, int status = 0)
        : base($"Azure Key Vault secret redemption failed ({failure}).")
    {
        Failure = failure;
        Status = status;
    }

    public AzureKeyVaultSecretFailure Failure { get; }
    public int Status { get; }
}

// Register only in the trusted control plane. The caller authorizes run and purpose.
public sealed class AzureKeyVaultSecretRedemption : ISecretRedemption, IDisposable
{
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(5);
    private readonly SecretClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<SecretCredential> _issued = [];
    private readonly object _sync = new();
    private bool _disposed;

    public AzureKeyVaultSecretRedemption(
        AzureKeyVaultConfiguration configuration,
        TokenCredential credential,
        SecretClientOptions? clientOptions = null,
        TimeProvider? timeProvider = null)
        : this(configuration, CreateClient(configuration, credential, clientOptions), timeProvider)
    {
    }

    public AzureKeyVaultSecretRedemption(
        AzureKeyVaultConfiguration configuration,
        SecretClient client,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (_client.VaultUri != configuration.VaultUri)
            throw new ArgumentException("The injected client must target the configured vault.", nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SecretCredential> RedeemAsync(
        SecretRedemptionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        if (request.Secret.Id.Length > 127 ||
            request.Secret.Id.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("The secret identifier is not an Azure Key Vault secret name.", nameof(request));

        KeyVaultSecret secret;
        try
        {
            secret = (await _client.GetSecretAsync(
                request.Secret.Id, request.Secret.Version, cancellationToken).ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var failure = error.Status switch
            {
                404 => AzureKeyVaultSecretFailure.NotFound,
                403 when string.Equals(error.ErrorCode, "SecretDisabled", StringComparison.OrdinalIgnoreCase)
                    => AzureKeyVaultSecretFailure.Disabled,
                403 => AzureKeyVaultSecretFailure.AccessDenied,
                _ => AzureKeyVaultSecretFailure.ServiceFailure,
            };
            throw new AzureKeyVaultSecretException(failure, error.Status);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        if (secret is null || secret.Properties.Enabled == false ||
            !string.Equals(secret.Name, request.Secret.Id, StringComparison.Ordinal) ||
            !string.Equals(secret.Properties.Version, request.Secret.Version, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(secret.Value) ||
            secret.Properties.NotBefore is { } notBefore && notBefore > now ||
            secret.Properties.ExpiresOn is { } expiredAt && expiredAt <= now)
            throw new AzureKeyVaultSecretException(
                secret?.Properties.Enabled == false
                    ? AzureKeyVaultSecretFailure.Disabled
                    : AzureKeyVaultSecretFailure.InvalidValue);

        var expiresAt = now.Add(CredentialLifetime);
        if (secret.Properties.ExpiresOn is { } vaultExpiry && vaultExpiry < expiresAt)
            expiresAt = vaultExpiry;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _issued.RemoveWhere(credential => credential.ExpiresAt <= now);
            var issued = new SecretCredential(secret.Value, expiresAt, _timeProvider);
            _issued.Add(issued);
            return issued;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var credential in _issued)
                credential.Invalidate();
            _issued.Clear();
        }
    }

    private static SecretClient CreateClient(
        AzureKeyVaultConfiguration configuration, TokenCredential credential, SecretClientOptions? options)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(credential);
        if (options?.Diagnostics.IsLoggingContentEnabled == true)
            throw new ArgumentException("Secret response content logging must be disabled.", nameof(options));
        return new SecretClient(configuration.VaultUri, credential, options);
    }
}
