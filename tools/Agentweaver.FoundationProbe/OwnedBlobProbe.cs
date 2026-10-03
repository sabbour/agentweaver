using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.ObjectStore.AzureBlob;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Agentweaver.FoundationProbe;

internal sealed class OwnedBlobProbe
{
    private readonly BlobContainerClient _container;
    private readonly AzureBlobObjectStore _objectStore;
    private readonly TimeSpan _cleanupTimeout;

    public OwnedBlobProbe(
        BlobContainerClient container,
        AzureBlobObjectStore objectStore,
        TimeSpan? cleanupTimeout = null)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(25);
        if (_cleanupTimeout <= TimeSpan.Zero || _cleanupTimeout > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
    }

    public async Task<BlobEvidence> RunAsync(
        ProbeTarget target,
        ProbeSource source,
        string nonce,
        CancellationToken cancellationToken)
    {
        var key = new ObjectKey($"foundation-probe/{nonce}/roundtrip.json");
        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sourceSha = source.Sha,
            sourceTree = source.Tree,
            nonce,
        });
        var contentHash = SHA256.HashData(content);
        var contentSha = Convert.ToHexString(contentHash).ToLowerInvariant();
        var ownerMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["probe_nonce"] = nonce,
            ["source_sha"] = source.Sha,
            ["source_tree"] = source.Tree,
        };
        var blob = _container.GetBlobClient(key.Value);
        ETag? uploadEtag = null;
        Exception? operationFailure = null;
        Exception? cleanupFailure = null;
        var uploadAttempted = false;
        var cleanupConfirmed = false;

        try
        {
            uploadAttempted = true;
            using var input = new MemoryStream(content, writable: false);
            var upload = await blob.UploadAsync(
                input,
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                    Metadata = ownerMetadata,
                },
                cancellationToken).ConfigureAwait(false);
            uploadEtag = upload.Value.ETag;

            var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            VerifyBlobOwnership(properties.Value, ownerMetadata, uploadEtag);
            using var read = await _objectStore.ReadAsync(key, cancellationToken).ConfigureAwait(false)
                ?? throw new ProbeException("blob_readback_missing");
            if (read.Length != content.Length)
                throw new ProbeException("blob_readback_mismatch");
            using var downloaded = new MemoryStream();
            await read.Content.CopyToAsync(downloaded, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(downloaded.ToArray()), contentHash))
                throw new ProbeException("blob_readback_mismatch");
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        if (uploadAttempted)
        {
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(_cleanupTimeout);
                cleanupConfirmed = await DeleteOwnedBlobAsync(blob, ownerMetadata, uploadEtag, cleanupTimeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (operationFailure is not null && cleanupFailure is not null)
            throw new ProbeException("blob_operation_and_cleanup_failed",
                new AggregateException(operationFailure, cleanupFailure));
        if (operationFailure is not null)
            throw new ProbeException("blob_operation_failed", operationFailure);
        if (cleanupFailure is not null)
            throw new ProbeException("blob_cleanup_failed", cleanupFailure);
        if (!cleanupConfirmed || uploadEtag is null)
            throw new ProbeException("blob_cleanup_unconfirmed");

        return new BlobEvidence(
            target.FoundationResources.BlobContainerId,
            target.FoundationResources.BlobContainerUri,
            key.Value,
            nonce,
            uploadEtag.Value.ToString(),
            contentSha,
            CleanupConfirmed: true);
    }

    private static async Task<bool> DeleteOwnedBlobAsync(
        BlobClient blob,
        IReadOnlyDictionary<string, string> expectedMetadata,
        ETag? successfulUploadEtag,
        CancellationToken cancellationToken)
    {
        BlobProperties properties;
        try
        {
            properties = (await blob.GetPropertiesAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false)).Value;
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound")
        {
            return true;
        }

        VerifyBlobOwnership(properties, expectedMetadata, successfulUploadEtag);
        await blob.DeleteIfExistsAsync(
            DeleteSnapshotsOption.None,
            new BlobRequestConditions { IfMatch = properties.ETag },
            cancellationToken).ConfigureAwait(false);

        try
        {
            await blob.GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            throw new ProbeException("blob_cleanup_unconfirmed");
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound")
        {
            return true;
        }
    }

    private static void VerifyBlobOwnership(
        BlobProperties properties,
        IReadOnlyDictionary<string, string> expectedMetadata,
        ETag? successfulUploadEtag)
    {
        if (expectedMetadata.Any(pair =>
                !properties.Metadata.TryGetValue(pair.Key, out var actual) || actual != pair.Value))
            throw new ProbeException("blob_ownership_mismatch");
        if (successfulUploadEtag is { } expected && properties.ETag != expected)
            throw new ProbeException("blob_etag_changed");
    }
}
