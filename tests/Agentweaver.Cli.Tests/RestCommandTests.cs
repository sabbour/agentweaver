using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Agentweaver.Cli;

namespace Agentweaver.Cli.Tests;

public sealed class RestCommandTests
{
    [Fact]
    public async Task RawRequestUsesTenantPreflightWithoutOpenApiAndPreservesExplicitNullBody()
    {
        var handler = new RestHandler();
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var output = new StringWriter();
        var error = new StringWriter();

        var result = await CliApplication.RunAsync(
            Rest(
                "--method", "patch",
                "--url", "https://gateway.example/api/v1/uncatalogued?limit=1",
                "--body", "null",
                "--header", "X-Test=header-value"),
            new CliRuntimeOptions(HttpClient: http, Authentication: authentication, Output: output, Error: error));

        Assert.Equal(0, result);
        var raw = Assert.Single(handler.Requests, request => request.Path != "/api/v1/authorization/context");
        Assert.Equal("PATCH", raw.Method);
        Assert.Equal("/api/v1/uncatalogued", raw.Path);
        Assert.Equal("?limit=1", raw.Query);
        Assert.Equal("null", Encoding.UTF8.GetString(raw.Body!));
        Assert.Equal("application/json", raw.ContentType);
        Assert.Equal("header-value", raw.Headers["X-Test"]);
        Assert.Equal("primary-bearer", raw.Authorization);
        Assert.Equal("tenant-1", raw.Tenant);
        Assert.DoesNotContain(raw.Headers.Keys, name =>
            name.Equals("X-Agentweaver-Project-Authorization", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, request => request.Path == "/openapi/v1.json");
        Assert.Equal("/api/v1/authorization/context", handler.Requests[0].Path);
        Assert.Contains("result", output.ToString(), StringComparison.Ordinal);
        Assert.Empty(error.ToString());
        Assert.Equal(new[] { "primary-bearer" }, authentication.RequestedBearers);
    }

    [Fact]
    public async Task RawRootRelativeUrlResolvesOnLinuxAndPreservesQuery()
    {
        var handler = new RestHandler();
        using var http = new HttpClient(handler);

        var result = await CliApplication.RunAsync(
            Rest("--method", "GET", "--url", "/api/v1/uncatalogued?limit=1"),
            new CliRuntimeOptions(HttpClient: http, Authentication: new TestAuthentication()));

        Assert.Equal(0, result);
        var raw = Assert.Single(handler.Requests, request => request.Path != "/api/v1/authorization/context");
        Assert.Equal("/api/v1/uncatalogued", raw.Path);
        Assert.Equal("?limit=1", raw.Query);
        Assert.Equal("primary-bearer", raw.Authorization);
    }

    [Fact]
    public async Task RawRequestDistinguishesOmittedBodyFromUtf8FileBody()
    {
        var bodyPath = Path.GetFullPath($"rest-body-{Guid.NewGuid():N}.json");
        var bodyText = "{\r\n  \"message\": \"hello\"\r\n}\r\n";
        await File.WriteAllTextAsync(bodyPath, bodyText, new UTF8Encoding(false));
        try
        {
            var omittedHandler = new RestHandler();
            using var omittedHttp = new HttpClient(omittedHandler);
            Assert.Equal(0, await CliApplication.RunAsync(
                Rest("--method", "GET", "--url", "/api/v1/uncatalogued"),
                new CliRuntimeOptions(HttpClient: omittedHttp, Authentication: new TestAuthentication())));
            var omitted = Assert.Single(omittedHandler.Requests, request => request.Path != "/api/v1/authorization/context");
            Assert.Null(omitted.Body);

            var fileHandler = new RestHandler();
            using var fileHttp = new HttpClient(fileHandler);
            Assert.Equal(0, await CliApplication.RunAsync(
                Rest("--method", "POST", "--url", "/api/v1/uncatalogued", "--body", "@" + bodyPath),
                new CliRuntimeOptions(HttpClient: fileHttp, Authentication: new TestAuthentication())));
            var supplied = Assert.Single(fileHandler.Requests, request => request.Path != "/api/v1/authorization/context");
            Assert.Equal(Encoding.UTF8.GetBytes(bodyText), supplied.Body);
            Assert.Equal("application/json", supplied.ContentType);
        }
        finally
        {
            File.Delete(bodyPath);
        }
    }

    [Fact]
    public async Task RawRequestRejectsTwoMiBJsonBodyBeforeAuthentication()
    {
        var bodyPath = Path.GetFullPath($"rest-large-body-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(
            bodyPath,
            "{\"payload\":\"" + new string('x', 2 * 1024 * 1024) + "\"}",
            new UTF8Encoding(false));
        try
        {
            var handler = new RestHandler();
            using var http = new HttpClient(handler);
            var authentication = new TestAuthentication();
            var error = new StringWriter();

            var result = await CliApplication.RunAsync(
                Rest("--method", "POST", "--url", "/api/v1/uncatalogued", "--body", "@" + bodyPath),
                new CliRuntimeOptions(HttpClient: http, Authentication: authentication, Error: error));

            Assert.Equal(1, result);
            Assert.Empty(handler.Requests);
            Assert.Empty(authentication.RequestedBearers);
            Assert.Contains("maximum supported size", error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(bodyPath);
        }
    }

    [Theory]
    [InlineData("https://other.example/api/v1/call")]
    [InlineData("https://user@gateway.example/api/v1/call")]
    [InlineData("https://gateway.example/api/v1/call?access_token=secret")]
    [InlineData("https://gateway.example/api/v1/call?X-Amz-Signature=secret")]
    [InlineData("/api/v1/call#fragment")]
    [InlineData("//other.example/api/v1/call")]
    [InlineData("file:///api/v1/call")]
    public async Task RawRequestRejectsUnsafeUrlsBeforeAuthenticationOrNetwork(string url)
    {
        var handler = new RestHandler();
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var error = new StringWriter();

        var result = await CliApplication.RunAsync(
            Rest("--method", "GET", "--url", url),
            new CliRuntimeOptions(HttpClient: http, Authentication: authentication, Error: error));

        Assert.Equal(1, result);
        Assert.Empty(handler.Requests);
        Assert.Empty(authentication.RequestedBearers);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization=secret")]
    [InlineData("Host=other.example")]
    [InlineData("Proxy-Authorization=secret")]
    [InlineData("Content-Length=1")]
    [InlineData("X-Access-Key=secret")]
    [InlineData("Ocp-Apim-Subscription-Key=secret")]
    public async Task RawRequestRejectsReservedHeadersBeforeAuthentication(string header)
    {
        var handler = new RestHandler();
        using var http = new HttpClient(handler);
        var authentication = new TestAuthentication();
        var error = new StringWriter();

        var result = await CliApplication.RunAsync(
            Rest("--method", "GET", "--url", "/api/v1/call", "--header", header),
            new CliRuntimeOptions(HttpClient: http, Authentication: authentication, Error: error));

        Assert.Equal(1, result);
        Assert.Empty(handler.Requests);
        Assert.Empty(authentication.RequestedBearers);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawRequestRequiresCurrentTenantAuthorizationContextAndDoesNotDiscoverOpenApi()
    {
        var handler = new RestHandler();
        using var http = new HttpClient(handler);
        var error = new StringWriter();

        var result = await CliApplication.RunAsync(
            RestForTenant("other-tenant", "--method", "GET", "--url", "/api/v1/uncatalogued"),
            new CliRuntimeOptions(HttpClient: http, Authentication: new TestAuthentication(), Error: error));

        Assert.Equal(1, result);
        Assert.Single(handler.Requests);
        Assert.Equal("/api/v1/authorization/context", handler.Requests[0].Path);
        Assert.Equal("other-tenant", handler.Requests[0].Tenant);
        Assert.DoesNotContain("/openapi/v1.json", handler.Requests.Select(request => request.Path));
        Assert.Contains("does not match", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("application/pdf")]
    [InlineData(null)]
    public async Task RawOpaqueResponsesAreSavedByteForByteToNewFiles(string? contentType)
    {
        var outputPath = Path.GetFullPath($"rest-output-{Guid.NewGuid():N}.bin");
        var expected = new byte[] { 0, 1, 2, 255 };
        var handler = new RestHandler
        {
            RawResponse = Response(HttpStatusCode.OK, expected, contentType),
        };
        using var http = new HttpClient(handler);
        try
        {
            var output = new StringWriter();
            var result = await CliApplication.RunAsync(
                Rest("--method", "GET", "--url", "/api/v1/file", "--output", outputPath),
                new CliRuntimeOptions(HttpClient: http, Authentication: new TestAuthentication(), Output: output));

            Assert.Equal(0, result);
            Assert.Equal(expected, await File.ReadAllBytesAsync(outputPath));
            Assert.Contains("\"bytes\": 4", output.ToString(), StringComparison.Ordinal);

            var overwriteHandler = new RestHandler();
            using var overwriteHttp = new HttpClient(overwriteHandler);
            var overwriteResult = await CliApplication.RunAsync(
                Rest("--method", "GET", "--url", "/api/v1/file", "--output", outputPath),
                new CliRuntimeOptions(HttpClient: overwriteHttp, Authentication: new TestAuthentication()));
            Assert.Equal(1, overwriteResult);
            Assert.Empty(overwriteHandler.Requests);
            Assert.Equal(expected, await File.ReadAllBytesAsync(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RawOpaqueResponseRequiresOutput()
    {
        var binaryHandler = new RestHandler
        {
            RawResponse = Response(HttpStatusCode.OK, [1, 2, 3], "application/pdf"),
        };
        using var binaryHttp = new HttpClient(binaryHandler);
        var binaryError = new StringWriter();
        var binaryResult = await CliApplication.RunAsync(
            Rest("--method", "GET", "--url", "/api/v1/file"),
            new CliRuntimeOptions(HttpClient: binaryHttp, Authentication: new TestAuthentication(), Error: binaryError));

        Assert.Equal(1, binaryResult);
        Assert.Contains("specify a new path with --output", binaryError.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/json", "{\"ok\":true}")]
    [InlineData("application/problem+json", "{\"title\":\"problem\"}")]
    [InlineData("text/plain", "plain response")]
    [InlineData("text/event-stream", "data: visible\n\n")]
    public async Task RawOutputCannotBeUsedForRecognizedTextResponses(string contentType, string body)
    {
        var outputPath = Path.GetFullPath($"rest-text-{Guid.NewGuid():N}.bin");
        var handler = new RestHandler
        {
            RawResponse = Response(HttpStatusCode.OK, Encoding.UTF8.GetBytes(body), contentType),
        };
        using var http = new HttpClient(handler);
        var error = new StringWriter();
        try
        {
            var result = await CliApplication.RunAsync(
                Rest("--method", "GET", "--url", "/api/v1/data", "--output", outputPath),
                new CliRuntimeOptions(HttpClient: http, Authentication: new TestAuthentication(), Error: error));

            Assert.Equal(1, result);
            Assert.Contains("--output is only valid", error.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task HeadIgnoresLargeRepresentationLengthButStillBoundsActualBytesAndEmptyOutput()
    {
        var emptyHeadHandler = new RestHandler
        {
            RawResponse = EmptyResponse(HttpStatusCode.OK, 16 * 1024 * 1024),
        };
        using var emptyHeadHttp = new HttpClient(emptyHeadHandler);
        var headResult = await CliApplication.RunAsync(
            Rest("--method", "HEAD", "--url", "/api/v1/representation"),
            new CliRuntimeOptions(HttpClient: emptyHeadHttp, Authentication: new TestAuthentication()));
        Assert.Equal(0, headResult);

        var emptyOutputPath = Path.GetFullPath($"rest-empty-{Guid.NewGuid():N}.bin");
        var outputHandler = new RestHandler { RawResponse = EmptyResponse(HttpStatusCode.OK, 16 * 1024 * 1024) };
        using var outputHttp = new HttpClient(outputHandler);
        var outputError = new StringWriter();
        try
        {
            var outputResult = await CliApplication.RunAsync(
                Rest("--method", "HEAD", "--url", "/api/v1/representation", "--output", emptyOutputPath),
                new CliRuntimeOptions(HttpClient: outputHttp, Authentication: new TestAuthentication(), Error: outputError));
            Assert.Equal(1, outputResult);
            Assert.Contains("empty response", outputError.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(emptyOutputPath));
        }
        finally
        {
            if (File.Exists(emptyOutputPath))
                File.Delete(emptyOutputPath);
        }

        var oversizedHandler = new RestHandler
        {
            RawResponse = Response(HttpStatusCode.OK, new byte[8 * 1024 * 1024 + 1], "application/pdf"),
        };
        using var oversizedHttp = new HttpClient(oversizedHandler);
        var oversizedError = new StringWriter();
        var oversizedResult = await CliApplication.RunAsync(
            Rest("--method", "HEAD", "--url", "/api/v1/representation"),
            new CliRuntimeOptions(HttpClient: oversizedHttp, Authentication: new TestAuthentication(), Error: oversizedError));
        Assert.Equal(1, oversizedResult);
        Assert.Contains("response exceeded", oversizedError.ToString(), StringComparison.OrdinalIgnoreCase);

        var noContentHandler = new RestHandler
        {
            RawResponse = EmptyResponse(HttpStatusCode.NoContent, 16 * 1024 * 1024),
        };
        using var noContentHttp = new HttpClient(noContentHandler);
        Assert.Equal(0, await CliApplication.RunAsync(
            Rest("--method", "DELETE", "--url", "/api/v1/uncatalogued"),
            new CliRuntimeOptions(HttpClient: noContentHttp, Authentication: new TestAuthentication())));
    }

    [Fact]
    public async Task RawTextStatusErrorsAndOutputModeAreExplicit()
    {
        var deniedHandler = new RestHandler
        {
            RawResponse = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("denied", Encoding.UTF8, "text/plain"),
            },
        };
        using var deniedHttp = new HttpClient(deniedHandler);
        var output = new StringWriter();
        var deniedResult = await CliApplication.RunAsync(
            Rest("--method", "GET", "--url", "/api/v1/denied"),
            new CliRuntimeOptions(HttpClient: deniedHttp, Authentication: new TestAuthentication(), Output: output));
        Assert.Equal(1, deniedResult);
        Assert.Contains("denied", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("403", output.ToString(), StringComparison.Ordinal);

        var sseOutputPath = Path.GetFullPath($"rest-sse-{Guid.NewGuid():N}.bin");
        var sseHandler = new RestHandler
        {
            RawResponse = Response(HttpStatusCode.OK, "data: visible\n\n"u8.ToArray(), "text/event-stream"),
        };
        using var sseHttp = new HttpClient(sseHandler);
        var sseOutput = new StringWriter();
        var sseError = new StringWriter();
        try
        {
            var sseResult = await CliApplication.RunAsync(
                Rest("--method", "GET", "--url", "/api/v1/events", "--output", sseOutputPath),
                new CliRuntimeOptions(
                    HttpClient: sseHttp,
                    Authentication: new TestAuthentication(),
                    Output: sseOutput,
                    Error: sseError));

            Assert.Equal(1, sseResult);
            Assert.Contains("--output is only valid", sseError.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("data: visible", sseOutput.ToString(), StringComparison.Ordinal);
            Assert.False(File.Exists(sseOutputPath));
        }
        finally
        {
            if (File.Exists(sseOutputPath))
                File.Delete(sseOutputPath);
        }
    }

    [Fact]
    public async Task RawEventStreamStreamsRedactedChunksAndReturnsCancellationCode()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RestHandler
        {
            RawResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BlockingContent(
                    "text/event-stream",
                    "data: ?access_token=secret-",
                    "canary\n\n"),
            },
        };
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var error = new StringWriter();

        var running = CliApplication.RunAsync(
            Rest("--method", "GET", "--url", "/api/v1/events"),
            new CliRuntimeOptions(HttpClient: http, Authentication: new TestAuthentication(), Output: output, Error: error),
            cancellation.Token);

        try
        {
            await WaitForTextAsync(output, "[REDACTED]");
            Assert.DoesNotContain("secret-canary", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
        }
        Assert.Equal(130, await running);
        Assert.DoesNotContain("secret-canary", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RawEventStreamRedactsAccessAndSubscriptionKeysAcrossReadChunks()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new RestHandler
        {
            RawResponse = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new BlockingContent(
                    "text/event-stream",
                    "data: {\"accessKey\":\"split-access-",
                    "value\",\"subscriptionKey\":\"split-subscription-",
                    "value\"}\n\n"),
            },
        };
        using var http = new HttpClient(handler);
        var output = new StringWriter();
        var error = new StringWriter();
        var running = CliApplication.RunAsync(
            Rest("--method", "GET", "--url", "/api/v1/events"),
            new CliRuntimeOptions(HttpClient: http, Authentication: new TestAuthentication(), Output: output, Error: error),
            cancellation.Token);

        try
        {
            await WaitForTextAsync(output, "subscriptionKey");
            Assert.Contains("\"accessKey\":\"[REDACTED]\"", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("\"subscriptionKey\":\"[REDACTED]\"", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("split-access-value", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("split-subscription-value", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
        }

        Assert.Equal(130, await running);
        Assert.DoesNotContain("split-access-value", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("split-subscription-value", error.ToString(), StringComparison.Ordinal);
    }

    private static string[] Rest(params string[] options) => RestForTenant("tenant-1", options);

    private static string[] RestForTenant(string tenant, params string[] options) =>
    [
        "rest",
        .. options,
        "--gateway-url", "https://gateway.example/",
        "--broker-issuer", "https://identity.example/",
        "--client-id", "",
        "--api-audience", "https://gateway.example/",
        "--tenant-id", tenant,
        "--project-id", "",
        "--run-id", "",
    ];

    private static HttpResponseMessage Response(HttpStatusCode status, byte[] body, string? contentType)
    {
        var content = new ByteArrayContent(body);
        if (contentType is not null)
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(status) { Content = content };
    }

    private static HttpResponseMessage EmptyResponse(HttpStatusCode status, long? contentLength)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent([]) };
        if (contentLength is { } length)
            response.Content.Headers.ContentLength = length;
        return response;
    }

    private static async Task WaitForTextAsync(StringWriter writer, string expected)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!writer.ToString().Contains(expected, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.Contains(expected, writer.ToString(), StringComparison.Ordinal);
    }

    private sealed class TestAuthentication : ICliAuthentication
    {
        public List<string> RequestedBearers { get; } = [];

        public Task<CliAuthStatus> LoginAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "actor-1", identity));

        public Task<CliAuthStatus> StatusAsync(CliIdentity identity, CancellationToken ct) =>
            Task.FromResult(new CliAuthStatus(true, "actor-1", identity));

        public Task LogoutAsync(CliIdentity identity, CancellationToken ct) => Task.CompletedTask;

        public Task<string> GetBearerAsync(CliIdentity identity, InteractionPolicy interaction, CancellationToken ct)
        {
            Assert.Equal(InteractionPolicy.Never, interaction);
            RequestedBearers.Add("primary-bearer");
            return Task.FromResult("primary-bearer");
        }
    }

    private sealed class RestHandler : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];
        public HttpResponseMessage? RawResponse { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(
                item => item.Key,
                item => string.Join(",", item.Value),
                StringComparer.OrdinalIgnoreCase);
            Requests.Add(new RequestSnapshot(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                body,
                request.Content?.Headers.ContentType?.ToString(),
                request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants)
                    ? tenants.Single()
                    : null,
                headers));

            if (request.RequestUri.AbsolutePath == "/api/v1/authorization/context")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "contractVersion": 1,
                          "issuer": "https://identity.example/",
                          "actorId": "actor-1",
                          "tenantId": "tenant-1",
                          "membershipRevision": 1,
                          "boundProjectId": null,
                          "boundRunId": null,
                          "effectiveAuthority": []
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                };
            if (request.RequestUri.AbsolutePath == "/openapi/v1.json")
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return RawResponse ?? new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"result\":\"ok\"}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed record RequestSnapshot(
        string Method,
        string Path,
        string Query,
        byte[]? Body,
        string? ContentType,
        string? Authorization,
        string? Tenant,
        IReadOnlyDictionary<string, string> Headers);

    private sealed class BlockingContent : HttpContent
    {
        private readonly string[] _chunks;

        public BlockingContent(string mediaType, params string[] chunks)
        {
            _chunks = chunks;
            Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingReadStream(_chunks));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BlockingReadStream(string[] chunks) : Stream
    {
        private int _nextChunk;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_nextChunk >= chunks.Length)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            var bytes = Encoding.UTF8.GetBytes(chunks[_nextChunk++]);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
    }
}
