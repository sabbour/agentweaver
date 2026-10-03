using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Agentweaver.Abstractions;
using Agentweaver.FoundationProbe;
using Agentweaver.ObjectStore.AzureBlob;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Xunit;

namespace Agentweaver.FoundationProbe.Tests;

public sealed class OwnedBlobProbeTests
{
    [Fact]
    public async Task CreatesReadsAndDeletesOnlyItsOwnedEtag()
    {
        var (probe, transport) = Create();
        var target = ProbeFixtures.Target();
        var nonce = new string('e', 32);

        var evidence = await probe.RunAsync(target, ProbeFixtures.Source, nonce, CancellationToken.None);

        Assert.True(evidence.CleanupConfirmed);
        Assert.Equal(nonce, evidence.OwnershipNonce);
        Assert.Equal($"foundation-probe/{nonce}/roundtrip.json", evidence.ObjectKey);
        Assert.Equal("*", transport.UploadIfNoneMatch);
        Assert.Equal("\"etag-1\"", transport.DeleteIfMatch);
        Assert.False(transport.Objects.ContainsKey("/platform-artifacts/" + evidence.ObjectKey));
        Assert.Equal(3, transport.MetadataAtUpload.Count);
        Assert.Equal(nonce, transport.MetadataAtUpload["probe_nonce"]);
    }

    [Fact]
    public async Task UncertainUploadUsesOwnershipMetadataAndConditionalEtagCleanup()
    {
        var (probe, transport) = Create();
        transport.FailAfterUpload = true;
        var nonce = new string('e', 32);

        var failure = await Assert.ThrowsAsync<ProbeException>(() =>
            probe.RunAsync(ProbeFixtures.Target(), ProbeFixtures.Source, nonce, CancellationToken.None));

        Assert.Equal("blob_operation_failed", failure.Code);
        Assert.Equal("\"etag-1\"", transport.DeleteIfMatch);
        Assert.Empty(transport.Objects);
    }

    [Fact]
    public async Task CollisionOrChangedOwnershipBlocksAcceptanceWithoutDeletingTheOtherBlob()
    {
        var (probe, transport) = Create();
        var nonce = new string('e', 32);
        transport.Seed("/platform-artifacts/foundation-probe/" + nonce + "/roundtrip.json",
            [1, 2, 3], new Dictionary<string, string> { ["probe_nonce"] = "another-owner" });

        var failure = await Assert.ThrowsAsync<ProbeException>(() =>
            probe.RunAsync(ProbeFixtures.Target(), ProbeFixtures.Source, nonce, CancellationToken.None));

        Assert.Equal("blob_operation_and_cleanup_failed", failure.Code);
        Assert.Null(transport.DeleteIfMatch);
        Assert.Single(transport.Objects);
    }

    [Fact]
    public async Task EtagChangeAndDeleteFailureBothBlockAcceptance()
    {
        var (changedProbe, changedTransport) = Create();
        changedTransport.ChangeEtagBeforeFirstProperties = true;
        var changed = await Assert.ThrowsAsync<ProbeException>(() =>
            changedProbe.RunAsync(ProbeFixtures.Target(), ProbeFixtures.Source,
                new string('e', 32), CancellationToken.None));
        Assert.Equal("blob_operation_and_cleanup_failed", changed.Code);
        Assert.Null(changedTransport.DeleteIfMatch);
        Assert.Single(changedTransport.Objects);

        var (failedCleanupProbe, failedCleanupTransport) = Create();
        failedCleanupTransport.FailDelete = true;
        var cleanup = await Assert.ThrowsAsync<ProbeException>(() =>
            failedCleanupProbe.RunAsync(ProbeFixtures.Target(), ProbeFixtures.Source,
                new string('f', 32), CancellationToken.None));
        Assert.Equal("blob_cleanup_failed", cleanup.Code);
        Assert.Single(failedCleanupTransport.Objects);
    }

    [Fact]
    public async Task CancellationDuringReadStillRunsBoundedConditionalCleanup()
    {
        var (probe, transport) = Create();
        transport.PauseDownload = true;
        using var cancellation = new CancellationTokenSource();
        var task = probe.RunAsync(ProbeFixtures.Target(), ProbeFixtures.Source,
            new string('e', 32), cancellation.Token);
        await transport.DownloadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        var failure = await Assert.ThrowsAsync<ProbeException>(() => task);

        Assert.Equal("blob_operation_failed", failure.Code);
        Assert.Equal("\"etag-1\"", transport.DeleteIfMatch);
        Assert.Empty(transport.Objects);
    }

    private static (OwnedBlobProbe Probe, BlobTransport Transport) Create(TimeSpan? cleanupTimeout = null)
    {
        var transport = new BlobTransport();
        var options = new BlobClientOptions { Transport = new HttpClientTransport(transport) };
        options.Retry.MaxRetries = 0;
        var container = new BlobContainerClient(
            new Uri("https://awv1p0blob.blob.core.windows.net/platform-artifacts"), options);
        return (new OwnedBlobProbe(container, new AzureBlobObjectStore(container), cleanupTimeout), transport);
    }

    private sealed class BlobTransport : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, BlobObject> _objects = new(StringComparer.Ordinal);
        private int _etagSequence;

        public string? UploadIfNoneMatch { get; private set; }
        public string? DeleteIfMatch { get; private set; }
        public Dictionary<string, string> MetadataAtUpload { get; private set; } = [];
        public bool FailAfterUpload { get; set; }
        public bool FailDelete { get; set; }
        public bool ChangeEtagBeforeFirstProperties { get; set; }
        public bool PauseDownload { get; set; }
        public TaskCompletionSource DownloadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyDictionary<string, BlobObject> Objects => _objects;

        public void Seed(string path, byte[] bytes, Dictionary<string, string> metadata) =>
            _objects[path] = new BlobObject(bytes, metadata, NextEtag());

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Put)
                return await UploadAsync(request, path, cancellationToken);
            if (request.Method == HttpMethod.Head)
                return Properties(path);
            if (request.Method == HttpMethod.Get)
            {
                DownloadEntered.TrySetResult();
                if (PauseDownload)
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return Download(path);
            }
            if (request.Method == HttpMethod.Delete)
                return Delete(request, path);
            return Error(HttpStatusCode.BadRequest, "UnexpectedRequest");
        }

        private async Task<HttpResponseMessage> UploadAsync(
            HttpRequestMessage request, string path, CancellationToken cancellationToken)
        {
            UploadIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            var metadata = request.Headers
                .Where(header => header.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key["x-ms-meta-".Length..],
                    header => header.Value.First(), StringComparer.OrdinalIgnoreCase);
            MetadataAtUpload = metadata;
            var body = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            if (request.RequestUri!.Query.Contains("comp=blocklist", StringComparison.Ordinal))
            {
                var xml = XDocument.Parse(Encoding.UTF8.GetString(body));
                var blockIds = xml.Descendants()
                    .Where(element => element.Name.LocalName is "Latest" or "Uncommitted" or "Committed")
                    .Select(element => element.Value);
                body = blockIds.SelectMany(id => _objects[path + ":" + id].Content).ToArray();
            }
            else if (request.RequestUri.Query.Contains("comp=block", StringComparison.Ordinal))
            {
                var blockId = Uri.UnescapeDataString(request.RequestUri.Query.TrimStart('?').Split('&')
                    .Single(part => part.StartsWith("blockid=", StringComparison.Ordinal)).Split('=', 2)[1]);
                _objects[path + ":" + blockId] = new BlobObject(body, metadata, NextEtag());
                return Success(HttpStatusCode.Created, _objects[path + ":" + blockId]);
            }

            if (UploadIfNoneMatch != "*")
                return Error(HttpStatusCode.BadRequest, "MissingCreateCondition");
            if (_objects.ContainsKey(path))
                return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            var value = new BlobObject(body, metadata, NextEtag());
            if (!_objects.TryAdd(path, value))
                return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            if (FailAfterUpload)
                throw new HttpRequestException("Simulated lost upload response.");
            return Success(HttpStatusCode.Created, value);
        }

        private HttpResponseMessage Properties(string path)
        {
            if (!_objects.TryGetValue(path, out var value))
                return Error(HttpStatusCode.NotFound, "BlobNotFound");
            if (ChangeEtagBeforeFirstProperties)
            {
                ChangeEtagBeforeFirstProperties = false;
                value = value with { ETag = NextEtag() };
                _objects[path] = value;
            }
            return Success(HttpStatusCode.OK, value);
        }

        private HttpResponseMessage Download(string path)
        {
            if (!_objects.TryGetValue(path, out var value))
                return Error(HttpStatusCode.NotFound, "BlobNotFound");
            var response = Success(HttpStatusCode.OK, value);
            response.Content = new ByteArrayContent(value.Content);
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            response.Content.Headers.ContentLength = value.Content.Length;
            return response;
        }

        private HttpResponseMessage Delete(HttpRequestMessage request, string path)
        {
            DeleteIfMatch = request.Headers.IfMatch.FirstOrDefault()?.ToString();
            if (FailDelete)
                return Error(HttpStatusCode.InternalServerError, "InjectedCleanupFailure");
            if (!_objects.TryGetValue(path, out var value))
                return Error(HttpStatusCode.NotFound, "BlobNotFound");
            if (DeleteIfMatch != value.ETag)
                return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
            _objects.TryRemove(path, out _);
            return Success(HttpStatusCode.Accepted, value);
        }

        private HttpResponseMessage Success(HttpStatusCode status, BlobObject? value = null)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "test-request");
            response.Headers.TryAddWithoutValidation("x-ms-version", "2023-11-03");
            response.Headers.ETag = EntityTagHeaderValue.Parse(value?.ETag ?? "\"etag-0\"");
            response.Content = new ByteArrayContent([]);
            if (value is not null)
            {
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                response.Content.Headers.ContentLength = value.Content.Length;
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                foreach (var (key, metadata) in value.Metadata)
                    response.Headers.TryAddWithoutValidation($"x-ms-meta-{key}", metadata);
            }
            return response;
        }

        private HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = Success(status);
            response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
            return response;
        }

        private string NextEtag() => $"\"etag-{Interlocked.Increment(ref _etagSequence)}\"";

        internal sealed record BlobObject(byte[] Content, Dictionary<string, string> Metadata, string ETag);
    }
}
