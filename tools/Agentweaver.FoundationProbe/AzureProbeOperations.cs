extern alias AzureIdentity;

using Agentweaver.Abstractions;
using Agentweaver.ObjectStore.AzureBlob;
using Agentweaver.Persistence.Postgres;
using Agentweaver.Secrets.AzureKeyVault;
using Azure.Core;
using Azure.Storage.Blobs;
using Npgsql;
using NpgsqlTypes;
using WorkloadIdentityCredentialOptions = AzureIdentity::Azure.Identity.WorkloadIdentityCredentialOptions;

namespace Agentweaver.FoundationProbe;

internal sealed class AzureProbeOperations : IProbeOperations, IAsyncDisposable
{
    private const string PostgresTokenScope = "https://ossrdbms-aad.database.windows.net/.default";
    private const string SecretPurpose = "acceptance.foundation-probe";
    private readonly ProbeTarget _target;
    private readonly AzureKeyVaultSecretRedemption _secretRedemption;
    private readonly BlobContainerClient _container;
    private readonly AzureBlobObjectStore _objectStore;
    private readonly OwnedBlobProbe _blobProbe;
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresOutbox _outbox;
    private readonly PostgresProbe _postgresProbe;

    public AzureProbeOperations(ProbeTarget target, TokenCredential credential, string tokenFilePath)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        ArgumentNullException.ThrowIfNull(credential);
        if (!Path.IsPathFullyQualified(tokenFilePath))
            throw new ProbeException("workload_identity_configuration_mismatch");

        _secretRedemption = AzureKeyVaultSecretRedemption.CreateWithWorkloadIdentity(
            new AzureKeyVaultConfiguration(new Uri(target.FoundationResources.VaultUri)),
            new WorkloadIdentityCredentialOptions
            {
                TenantId = target.TenantId,
                ClientId = target.FoundationProbeIdentity.ClientId,
                TokenFilePath = tokenFilePath,
            });
        _container = new BlobContainerClient(new Uri(target.FoundationResources.BlobContainerUri), credential);
        _objectStore = new AzureBlobObjectStore(_container);
        _blobProbe = new OwnedBlobProbe(_container, _objectStore);
        _dataSource = CreatePostgresDataSource(target, credential);
        _outbox = new PostgresOutbox(_dataSource, target.Runtime.SchemaName);
        _postgresProbe = new PostgresProbe(_dataSource, _outbox);
    }

    public async Task<KeyVaultEvidence> RedeemKeyVaultAsync(
        ProbeTarget target, string nonce, CancellationToken cancellationToken)
    {
        EnsureTarget(target);
        var request = new SecretRedemptionRequest(
            new SecretRef(target.Runtime.KeyVaultSecretName, target.Runtime.KeyVaultSecretVersion),
            SecretPurpose,
            nonce);
        var credential = await _secretRedemption.RedeemAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            return new KeyVaultEvidence(
                target.FoundationResources.KeyVaultId,
                target.FoundationResources.VaultUri,
                request.Secret.Id,
                request.Secret.Version,
                Redeemed: true);
        }
        finally
        {
            credential.Invalidate();
        }
    }

    public async Task<BlobEvidence> RunBlobRoundTripAsync(
        ProbeTarget target, ProbeSource source, string nonce, CancellationToken cancellationToken)
    {
        EnsureTarget(target);
        return await _blobProbe.RunAsync(target, source, nonce, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PostgresEvidence> WritePostgresEffectsAsync(
        ProbeTarget target, ProbeSource source, string nonce, CancellationToken cancellationToken)
    {
        EnsureTarget(target);
        return await _postgresProbe.RunAsync(target, source, nonce, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _secretRedemption.Dispose();
        await _dataSource.DisposeAsync().ConfigureAwait(false);
    }

    internal static NpgsqlDataSource CreatePostgresDataSource(
        ProbeTarget target,
        TokenCredential credential,
        bool pooling = true,
        SslMode sslMode = SslMode.VerifyFull,
        int port = 5432)
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = target.FoundationResources.PostgresHost,
            Port = port,
            Database = target.Runtime.DatabaseName,
            Username = target.Runtime.DatabaseRole,
            SslMode = sslMode,
            Pooling = pooling,
            ApplicationName = "foundation-probe",
            Timeout = 20,
            CommandTimeout = 20,
        };
        var builder = new NpgsqlDataSourceBuilder(connection.ConnectionString);
        builder.UsePasswordProvider(
            _ => throw new InvalidOperationException("Synchronous PostgreSQL token acquisition is disabled."),
            (_, cancellationToken) => GetPostgresTokenAsync(credential, cancellationToken));
        return builder.Build();
    }

    internal static async ValueTask<string> GetPostgresTokenAsync(
        TokenCredential credential, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([PostgresTokenScope]), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token))
            throw new ProbeException("postgres_token_unavailable");
        return token.Token;
    }

    private void EnsureTarget(ProbeTarget target)
    {
        if (!ReferenceEquals(target, _target))
            throw new ProbeException("target_changed");
    }
}
