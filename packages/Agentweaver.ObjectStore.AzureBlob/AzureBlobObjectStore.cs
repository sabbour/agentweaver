using Agentweaver.Abstractions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Agentweaver.ObjectStore.AzureBlob;

/// <summary>A platform-owned container; provisioning and authorization are outside this adapter.</summary>
public sealed class AzureBlobObjectStore : IObjectStore
{
    private readonly BlobContainerClient _container;

    public AzureBlobObjectStore(BlobContainerClient container)
    {
        _container = container ?? throw new ArgumentNullException(nameof(container));
        if (string.IsNullOrWhiteSpace(container.Name))
            throw new ArgumentException("A named Blob container is required.", nameof(container));
    }

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
