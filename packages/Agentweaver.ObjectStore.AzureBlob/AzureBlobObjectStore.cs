using Agentweaver.Abstractions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Agentweaver.ObjectStore.AzureBlob;

/// <summary>
/// Stores platform artifacts in a supplied Azure Blob container. Container provisioning and
/// authorization remain the responsibility of the hosting service.
/// </summary>
public sealed class AzureBlobObjectStore : IObjectStore
{
    private readonly BlobContainerClient _container;

    /// <summary>Creates an adapter for an already provisioned, named Blob container.</summary>
    /// <param name="container">The Azure SDK container client used for all object operations.</param>
    /// <exception cref="ArgumentNullException">The client is null.</exception>
    /// <exception cref="ArgumentException">The client has no container name.</exception>
    public AzureBlobObjectStore(BlobContainerClient container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        if (string.IsNullOrWhiteSpace(container.Name))
            throw new ArgumentException("A named Blob container is required.", nameof(container));
    }

    /// <summary>Creates a blob only if the key does not already exist.</summary>
    /// <param name="key">The object key, used as the blob name.</param>
    /// <param name="content">Readable object content.</param>
    /// <param name="cancellationToken">Cancels the Azure request.</param>
    /// <exception cref="ArgumentException">The key is invalid or the stream is unreadable.</exception>
    /// <exception cref="RequestFailedException">Azure rejects the upload, including an existing key.</exception>
    public async Task WriteAsync(ObjectKey key, Stream content, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);
        if (!content.CanRead) throw new ArgumentException("Content must be readable.", nameof(content));
        cancellationToken.ThrowIfCancellationRequested();
        await _container.GetBlobClient(key.Value).UploadAsync(content,
            new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Opens an object stream, or returns null only when the blob does not exist.</summary>
    /// <param name="key">The object key to read.</param>
    /// <param name="cancellationToken">Cancels the Azure request.</param>
    /// <returns>A disposable stream and its length, or null when the blob is absent.</returns>
    /// <exception cref="RequestFailedException">Azure fails for a reason other than a missing blob.</exception>
    public async Task<ObjectRead?> ReadAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var download = await _container.GetBlobClient(key.Value)
                .DownloadStreamingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var result = download.Value;
            return new ObjectRead(result.Content, result.Details.ContentLength, result.Dispose);
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound")
        {
            return null;
        }
    }

    /// <summary>Deletes an object and reports whether the blob existed.</summary>
    /// <param name="key">The object key to delete.</param>
    /// <param name="cancellationToken">Cancels the Azure request.</param>
    /// <returns>True when Azure deleted the blob; false when it was already absent.</returns>
    /// <exception cref="RequestFailedException">Azure fails for a reason other than a missing blob.</exception>
    public async Task<bool> DeleteAsync(ObjectKey key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _container.GetBlobClient(key.Value)
                .DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound")
        {
            return false;
        }
    }

    private static void ValidateKey(ObjectKey key)
    {
        if (key.Value is null) throw new ArgumentException("A valid object key is required.", nameof(key));
    }
}
