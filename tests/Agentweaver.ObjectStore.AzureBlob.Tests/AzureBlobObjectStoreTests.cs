using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Xml.Linq;
using Agentweaver.Abstractions;
using Agentweaver.ObjectStore.AzureBlob;
using Azure;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Xunit;

namespace Agentweaver.ObjectStore.AzureBlob.Tests;

public sealed class AzureBlobObjectStoreTests
{
    private static (AzureBlobObjectStore Store, BlobTransport Transport) Create()
    {
        var transport = new BlobTransport();
        var options = new BlobClientOptions { Transport = new HttpClientTransport(transport) };
        options.Retry.MaxRetries = 0;
        return (new AzureBlobObjectStore(new BlobContainerClient(
            new Uri("https://example.blob.core.windows.net/platform-objects"), options)), transport);
    }

    [Fact]
    public async Task StreamsBinaryDataWithoutOwningInputAndReportsLength()
    {
        var (store, transport) = Create();
        var key = new ObjectKey("checkpoints/run-42/payload");
        var bytes = new byte[] { 0, 255, 17, 0, 128, 42 };
        using var input = new MemoryStream(bytes);
        await store.WriteAsync(key, input);
        Assert.True(input.CanRead);
        Assert.Equal("/platform-objects/checkpoints/run-42/payload", transport.LastPath);
        Assert.Equal("*", transport.LastIfNoneMatch);
        using (var read = await store.ReadAsync(key))
        {
            Assert.NotNull(read);
            Assert.Equal(bytes.Length, read.Length);
            using var output = new MemoryStream();
            await read.Content.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
            read.Dispose();
            Assert.Throws<ObjectDisposedException>(() => read.Content.ReadByte());
        }

        Assert.True(await store.DeleteAsync(key));
        Assert.False(await store.DeleteAsync(key));
        Assert.Null(await store.ReadAsync(key));
    }

    [Fact]
    public async Task AcceptsNonSeekableUploadStreams()
    {
        var (store, transport) = Create();
        using var source = new ForwardOnlyStream(new byte[] { 0, 0, 255, 9 });
        var key = new ObjectKey("logs/forward-only");
        await store.WriteAsync(key, source);
        Assert.True(source.CanRead);
        Assert.Equal("*", transport.LastCommitIfNoneMatch);
        using var download = await store.ReadAsync(key);
        Assert.NotNull(download);
        using var destination = new MemoryStream();
        await download.Content.CopyToAsync(destination);
        Assert.Equal(new byte[] { 0, 0, 255, 9 }, destination.ToArray());
    }

    [Fact]
    public async Task CreateOnlyNeverOverwritesEvenOnRetry()
    {
        var (store, _) = Create();
        var key = new ObjectKey("artifacts/immutable");
        await store.WriteAsync(key, new MemoryStream([1, 2, 3]));
        var exception = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.WriteAsync(key, new MemoryStream([9])));
        Assert.Equal(412, exception.Status);
        using var forward = new ForwardOnlyStream([7, 8]);
        Assert.Equal(412, (await Assert.ThrowsAsync<RequestFailedException>(
            () => store.WriteAsync(key, forward))).Status);
        using var read = await store.ReadAsync(key);
        Assert.NotNull(read);
        Assert.Equal(3, read.Length);
        Assert.Equal(1, read.Content.ReadByte());
    }

    [Fact]
    public async Task FailsClosedOnInvalidInputsAndCancellation()
    {
        Assert.Throws<ArgumentException>(() => new ObjectKey("../escape"));
        Assert.Throws<ArgumentException>(() => new ObjectKey("a//b"));
        Assert.Throws<ArgumentException>(() => new ObjectKey("a\\b"));
        Assert.Throws<ArgumentException>(() => new ObjectKey(" "));
        Assert.Throws<ArgumentException>(() => new ObjectKey("/root"));
        Assert.Throws<ArgumentException>(() => new ObjectKey("end/"));
        Assert.Throws<ArgumentException>(() => new ObjectKey(new string('a', 1025)));
        Assert.Throws<ArgumentException>(() => new ObjectKey("a\nb"));
        Assert.Throws<ArgumentNullException>(() => new AzureBlobObjectStore(null!));
        Assert.Throws<ArgumentException>(() => new AzureBlobObjectStore(
            new BlobContainerClient(new Uri("https://example.blob.core.windows.net/"))));
        var (store, transport) = Create();
        var key = new ObjectKey("valid");
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(default, new MemoryStream()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => store.WriteAsync(key, null!));
        var closed = new MemoryStream();
        closed.Dispose();
        await Assert.ThrowsAsync<ArgumentException>(() => store.WriteAsync(key, closed));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteAsync(key, new MemoryStream(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(key, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAsync(key, cancelled.Token));
        Assert.Equal(0, transport.CallCount);
    }

    [Fact]
    public async Task CancelsAnInFlightDownload()
    {
        var (store, transport) = Create();
        transport.PauseRequests = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = store.ReadAsync(new ObjectKey("logs/pending"), cancellation.Token);
        await transport.EnteredRequest.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task PropagatesServiceAndTransportFailures()
    {
        var (store, transport) = Create();
        var key = new ObjectKey("failure");
        transport.FailureStatus = HttpStatusCode.Forbidden;
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(
            () => store.ReadAsync(key))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(
            () => store.WriteAsync(key, new MemoryStream([1])))).Status);
        Assert.Equal(403, (await Assert.ThrowsAsync<RequestFailedException>(
            () => store.DeleteAsync(key))).Status);
        transport.FailureStatus = null;
        transport.MissingContainer = true;
        Assert.Equal("ContainerNotFound", (await Assert.ThrowsAsync<RequestFailedException>(
            () => store.ReadAsync(key))).ErrorCode);
        Assert.Equal("ContainerNotFound", (await Assert.ThrowsAsync<RequestFailedException>(
            () => store.DeleteAsync(key))).ErrorCode);
        transport.MissingContainer = false;
        transport.ThrowTransport = true;
        var transportFailure = await Assert.ThrowsAsync<RequestFailedException>(() => store.ReadAsync(key));
        Assert.IsType<HttpRequestException>(transportFailure.InnerException);
    }

    private sealed class BlobTransport : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, byte[]> _objects = new();
        private readonly ConcurrentDictionary<string, byte[]> _blocks = new();
        public string? LastPath { get; private set; }
        public string? LastIfNoneMatch { get; private set; }
        public string? LastCommitIfNoneMatch { get; private set; }
        public int CallCount { get; private set; }
        public HttpStatusCode? FailureStatus { get; set; }
        public bool ThrowTransport { get; set; }
        public bool MissingContainer { get; set; }
        public bool PauseRequests { get; set; }
        public TaskCompletionSource EnteredRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastPath = request.RequestUri!.AbsolutePath;
            LastIfNoneMatch = request.Headers.IfNoneMatch.FirstOrDefault()?.ToString();
            EnteredRequest.TrySetResult();
            if (PauseRequests) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowTransport) throw new HttpRequestException("Transport unavailable");
            if (FailureStatus is { } status) return Error(status, "AuthorizationFailure");
            if (MissingContainer) return Error(HttpStatusCode.NotFound, "ContainerNotFound");
            var path = LastPath;
            if (request.Method == HttpMethod.Put)
            {
                var query = request.RequestUri.Query;
                if (query.Contains("comp=blocklist", StringComparison.Ordinal))
                {
                    LastCommitIfNoneMatch = LastIfNoneMatch;
                    if (LastIfNoneMatch != "*") return Error(HttpStatusCode.BadRequest, "MissingCondition");
                    if (_objects.ContainsKey(path)) return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                    var xml = XDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                    var blockIds = xml.Descendants().Where(element => element.Name.LocalName is "Latest" or "Uncommitted" or "Committed")
                        .Select(element => element.Value);
                    var committed = blockIds.SelectMany(id => _blocks[path + ":" + id]).ToArray();
                    return _objects.TryAdd(path, committed) ? Success(HttpStatusCode.Created) :
                        Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                }
                if (query.Contains("comp=block", StringComparison.Ordinal))
                {
                    var id = Uri.UnescapeDataString(query.TrimStart('?').Split('&')
                        .Single(part => part.StartsWith("blockid=", StringComparison.Ordinal)).Split('=', 2)[1]);
                    _blocks[path + ":" + id] = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                    return Success(HttpStatusCode.Created);
                }
                if (LastIfNoneMatch != "*") return Error(HttpStatusCode.BadRequest, "MissingCondition");
                if (_objects.ContainsKey(path)) return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                var bytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                if (!_objects.TryAdd(path, bytes)) return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
                return Success(HttpStatusCode.Created);
            }
            if (request.Method == HttpMethod.Get)
            {
                if (!_objects.TryGetValue(path, out var bytes)) return Error(HttpStatusCode.NotFound, "BlobNotFound");
                var response = Success(HttpStatusCode.OK);
                response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response.Content.Headers.ContentLength = bytes.Length;
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                response.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
                return response;
            }
            if (request.Method == HttpMethod.Delete)
                return _objects.TryRemove(path, out _) ? Success(HttpStatusCode.Accepted) :
                    Error(HttpStatusCode.NotFound, "BlobNotFound");
            return Error(HttpStatusCode.BadRequest, "UnexpectedRequest");
        }

        private static HttpResponseMessage Success(HttpStatusCode status)
        {
            var response = new HttpResponseMessage(status);
            response.Headers.TryAddWithoutValidation("x-ms-request-id", "fake-request");
            response.Headers.ETag = new EntityTagHeaderValue("\"etag\"");
            response.Content = new ByteArrayContent([]);
            return response;
        }

        private static HttpResponseMessage Error(HttpStatusCode status, string code)
        {
            var response = Success(status);
            response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
            return response;
        }
    }

    private sealed class ForwardOnlyStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content);
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
