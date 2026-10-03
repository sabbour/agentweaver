using System.Text;
using Agentweaver.Abstractions;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Identity.Client;

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

public enum AzureKeyVaultWorkloadIdentityFailure
{
    TokenFileUnavailable,
    ExchangeRejected,
}

public sealed class AzureKeyVaultWorkloadIdentityException : Exception
{
    internal AzureKeyVaultWorkloadIdentityException(AzureKeyVaultWorkloadIdentityFailure failure, int status = 0)
        : base($"Azure Key Vault workload identity failed ({failure}).")
    {
        Failure = failure;
        Status = status;
    }

    public AzureKeyVaultWorkloadIdentityFailure Failure { get; }
    public int Status { get; }
}

// Register only in the trusted control plane. The caller authorizes run and purpose.
public sealed class AzureKeyVaultSecretRedemption : ISecretRedemption, IDisposable
{
    private static readonly TimeSpan CredentialLifetime = TimeSpan.FromMinutes(5);
    private readonly SecretClient _client;
    private readonly bool _usesWorkloadIdentity;
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

    private AzureKeyVaultSecretRedemption(
        AzureKeyVaultConfiguration configuration,
        WorkloadIdentityCredential credential,
        SecretClientOptions? clientOptions,
        TimeProvider? timeProvider)
        : this(configuration, (TokenCredential)credential, clientOptions, timeProvider)
    {
        _usesWorkloadIdentity = true;
    }

    public static AzureKeyVaultSecretRedemption CreateWithWorkloadIdentity(
        AzureKeyVaultConfiguration configuration,
        WorkloadIdentityCredentialOptions identityOptions,
        SecretClientOptions? clientOptions = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(identityOptions);
        if (string.IsNullOrWhiteSpace(identityOptions.TenantId))
            throw new ArgumentException("An explicit workload identity tenant is required.", nameof(identityOptions));
        if (string.IsNullOrWhiteSpace(identityOptions.ClientId))
            throw new ArgumentException("An explicit workload identity client is required.", nameof(identityOptions));
        if (string.IsNullOrWhiteSpace(identityOptions.TokenFilePath) ||
            !Path.IsPathFullyQualified(identityOptions.TokenFilePath))
            throw new ArgumentException("An absolute projected token file path is required.", nameof(identityOptions));
        if (identityOptions.Diagnostics.IsLoggingContentEnabled)
            throw new ArgumentException("Identity assertion content logging must be disabled.", nameof(identityOptions));
        if (clientOptions?.Diagnostics.IsLoggingContentEnabled == true)
            throw new ArgumentException("Secret response content logging must be disabled.", nameof(clientOptions));

        identityOptions.Transport = new RedactingIdentityTransport(
            identityOptions.Transport ?? HttpClientTransport.Shared);
        return new AzureKeyVaultSecretRedemption(
            configuration, new WorkloadIdentityCredential(identityOptions), clientOptions, timeProvider);
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
        catch (CredentialUnavailableException) when (_usesWorkloadIdentity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AzureKeyVaultWorkloadIdentityException(
                AzureKeyVaultWorkloadIdentityFailure.TokenFileUnavailable);
        }
        catch (AuthenticationFailedException error) when (_usesWorkloadIdentity)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cause = error as Exception;
            while (cause is not null && cause is not MsalServiceException)
                cause = cause.InnerException;
            throw new AzureKeyVaultWorkloadIdentityException(
                error.GetBaseException() is IOException or UnauthorizedAccessException
                    ? AzureKeyVaultWorkloadIdentityFailure.TokenFileUnavailable
                    : AzureKeyVaultWorkloadIdentityFailure.ExchangeRejected,
                (cause as MsalServiceException)?.StatusCode ?? 0);
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

    private sealed class RedactingIdentityTransport(HttpPipelineTransport inner) : HttpPipelineTransport
    {
        private static readonly byte[] SafeError = Encoding.UTF8.GetBytes(
            """{"error":"invalid_client","error_description":"Workload identity exchange rejected."}""");

        public override Request CreateRequest() => inner.CreateRequest();

        public override void Process(HttpMessage message)
        {
            inner.Process(message);
            Redact(message);
        }

        public override async ValueTask ProcessAsync(HttpMessage message)
        {
            await inner.ProcessAsync(message).ConfigureAwait(false);
            Redact(message);
        }

        private void Redact(HttpMessage message)
        {
            if (message.Response.Status < 400 || message.Request.Method != RequestMethod.Post)
                return;

            var raw = message.Response.ContentStream;
            message.Response.ContentStream = new MemoryStream(SafeError, writable: false);
            raw?.Dispose();
        }
    }
}
