using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Agentweaver.Mcp.Tests;

public sealed class NativeRemoteMcpTransportTests
{
    private const string ProtocolVersion = "2025-06-18";
    private static readonly Uri Endpoint = new("https://remote-mcp.test/mcp");

    private static class ControlledProfile
    {
        public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
        public const int MaximumRequestBytes = 256 * 1024;
        public const int MaximumResponseBytes = 1024 * 1024;
        public const int MaximumSchemaBytes = 64 * 1024;
        public const int MaximumJsonDepth = 32;
        public const int MaximumSseEventBytes = 64 * 1024;
        public const int MaximumSseEvents = 256;
        public const int MaximumDiscoveryPages = 8;
        public const int MaximumTools = 256;
        public const int MaximumRequestsPerMinute = 60;
    }

    [Fact]
    public async Task NativeStreamableHttpHandshakeAndDiscoveryStayOnControlledEndpoint()
    {
        var server = new ControlledMcpHandler();
        using var httpClient = new HttpClient(server, disposeHandler: false)
        {
            Timeout = ControlledProfile.OperationTimeout,
        };
        var transport = CreateTransport(httpClient);

        try
        {
            await using var client = await McpClientFactory.CreateAsync(
                transport,
                CreateClientOptions(),
                NullLoggerFactory.Instance);

            var tools = await client.ListToolsAsync();

            var tool = Assert.Single(tools);
            Assert.Equal("controlled_echo", tool.Name);
            var requests = server.Requests.ToArray();
            Assert.Contains(requests, request => request.Operation == "initialize");
            Assert.Contains(requests, request => request.Operation == "notifications/initialized");
            Assert.Contains(requests, request => request.Operation == "listen");
            Assert.Contains(requests, request => request.Operation == "tools/list");
            Assert.All(requests, request =>
            {
                Assert.Equal(Endpoint, request.Endpoint);
                Assert.Equal(ProtocolVersion, request.ProtocolVersion);
                Assert.Equal(64, request.InputSha256.Length);
                Assert.InRange(request.BodyBytes, 0, ControlledProfile.MaximumRequestBytes);
                Assert.Equal(
                    request.Operation == "listen" ? HttpMethod.Get : HttpMethod.Post,
                    request.Method);
                Assert.False(request.HasAuthorization);
                Assert.False(request.HasCookie);
            });
            Assert.Equal(4, requests.Length);
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task MissingAppliedNetworkProofDoesNotReachNetworkHandler()
    {
        var network = new CountingNetworkHandler();
        var gate = new MissingAppliedProofHandler(network);
        using var httpClient = new HttpClient(gate, disposeHandler: false)
        {
            Timeout = ControlledProfile.OperationTimeout,
        };
        var transport = CreateTransport(httpClient);

        try
        {
            var failure = await Record.ExceptionAsync(async () =>
                await McpClientFactory.CreateAsync(
                    transport,
                    CreateClientOptions(),
                    NullLoggerFactory.Instance));

            Assert.NotNull(failure);
            var attempted = Assert.Single(gate.Attempts);
            Assert.Equal(HttpMethod.Post, attempted.Method);
            Assert.Equal(Endpoint, attempted.Endpoint);
            Assert.Equal(ProtocolVersion, attempted.ProtocolVersion);
            Assert.Equal("initialize", attempted.Operation);
            Assert.Equal(64, attempted.InputSha256.Length);
            Assert.False(attempted.HasAuthorization);
            Assert.False(attempted.HasCookie);
            Assert.Equal(0, network.SendCount);
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Fact]
    public async Task RequestAtMaximumPlusOneIsRejectedBeforeAnySend()
    {
        var network = new CountingNetworkHandler();
        var gate = new MissingAppliedProofHandler(network);
        using var httpClient = new HttpClient(gate, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new ByteArrayContent(new byte[ControlledProfile.MaximumRequestBytes + 1]),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Accept.Add(new("application/json"));
        request.Headers.Accept.Add(new("text/event-stream"));

        await Assert.ThrowsAsync<HttpRequestException>(() => httpClient.SendAsync(request));

        Assert.Empty(gate.Attempts);
        Assert.Equal(0, network.SendCount);
    }

    [Fact]
    public async Task NativeTransportDoesNotRetryAfterAnUncertainTimeout()
    {
        var network = new StallingNetworkHandler();
        using var httpClient = new HttpClient(network, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMilliseconds(150),
        };
        var transport = CreateTransport(httpClient);

        try
        {
            var failure = await Record.ExceptionAsync(async () =>
                await McpClientFactory.CreateAsync(
                    transport,
                    CreateClientOptions(),
                    NullLoggerFactory.Instance));

            Assert.NotNull(failure);
            Assert.Equal(1, network.SendCount);
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    private static SseClientTransport CreateTransport(HttpClient httpClient) =>
        new(
            new SseClientTransportOptions
            {
                Endpoint = Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = ControlledProfile.ConnectTimeout,
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);

    private static McpClientOptions CreateClientOptions() =>
        new()
        {
            ClientInfo = new Implementation
            {
                Name = "agentweaver-controlled-remote-mcp-test",
                Version = "1",
            },
            ProtocolVersion = ProtocolVersion,
            InitializationTimeout = ControlledProfile.OperationTimeout,
        };

    private static async Task<CapturedRequest> CaptureAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Contains("Authorization") ||
            request.Headers.Contains("Cookie") ||
            request.Headers.Contains("Proxy-Authorization"))
            throw new HttpRequestException("The controlled profile rejects forwarded credentials.");

        if (request.RequestUri is null || request.RequestUri != Endpoint)
            throw new HttpRequestException("The controlled profile rejected the request route.");

        if (request.Method == HttpMethod.Get)
        {
            var getVersionHeaders = request.Headers.TryGetValues("MCP-Protocol-Version", out var values)
                ? values.ToArray()
                : [];
            if (getVersionHeaders.Length != 1 ||
                getVersionHeaders[0] != ProtocolVersion ||
                !request.Headers.Accept.Any(header => header.MediaType == "text/event-stream") ||
                request.Content is not null)
                throw new HttpRequestException("The controlled profile rejected the server-stream request.");

            var getSessionId = request.Headers.TryGetValues("Mcp-Session-Id", out var getSessionValues)
                ? string.Join(",", getSessionValues)
                : null;
            return new CapturedRequest(
                request.Method,
                request.RequestUri,
                ProtocolVersion,
                "listen",
                Convert.ToHexString(SHA256.HashData([])),
                0,
                default,
                false,
                false,
                getSessionId);
        }

        if (request.Method != HttpMethod.Post ||
            request.Content?.Headers.ContentType?.MediaType != "application/json" ||
            !request.Headers.Accept.Any(header => header.MediaType == "application/json") ||
            !request.Headers.Accept.Any(header => header.MediaType == "text/event-stream"))
            throw new HttpRequestException("The controlled profile rejected the request method or media type.");

        var body = await ReadBoundedAsync(
            request.Content,
            ControlledProfile.MaximumRequestBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(
            body,
            new JsonDocumentOptions { MaxDepth = ControlledProfile.MaximumJsonDepth });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var jsonrpc) ||
            jsonrpc.GetString() != "2.0" ||
            !root.TryGetProperty("method", out var methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
            throw new HttpRequestException("The controlled profile requires one JSON-RPC request.");

        var operation = methodElement.GetString()!;
        var hasVersionHeader = request.Headers.TryGetValues("MCP-Protocol-Version", out var versionValues);
        var versionHeaders = hasVersionHeader ? versionValues!.ToArray() : [];
        string? protocolVersion;
        if (operation == "initialize")
        {
            if (!root.TryGetProperty("params", out var parameters) ||
                !parameters.TryGetProperty("protocolVersion", out var requestedVersion) ||
                requestedVersion.ValueKind != JsonValueKind.String)
                throw new HttpRequestException("The initialize request has no protocol version.");
            protocolVersion = requestedVersion.GetString();
            if (versionHeaders.Length > 1 ||
                (versionHeaders.Length == 1 && versionHeaders[0] != ProtocolVersion))
                throw new HttpRequestException("The initialize request used an unsupported protocol version.");
        }
        else
        {
            protocolVersion = versionHeaders.Length == 1 ? versionHeaders[0] : null;
        }

        if (protocolVersion != ProtocolVersion ||
            operation is not ("initialize" or "notifications/initialized" or "tools/list"))
            throw new HttpRequestException("The controlled profile rejected the protocol or operation.");

        var hasId = root.TryGetProperty("id", out var id);
        if ((operation == "notifications/initialized" && hasId) ||
            (operation != "notifications/initialized" && !hasId))
            throw new HttpRequestException("The JSON-RPC request has an invalid request identifier.");
        var requestId = hasId ? id.Clone() : default;

        var sessionId = request.Headers.TryGetValues("Mcp-Session-Id", out var postSessionValues)
            ? string.Join(",", postSessionValues)
            : null;
        if (sessionId?.Length > 512)
            throw new HttpRequestException("The MCP session identifier exceeds the local test bound.");

        return new CapturedRequest(
            request.Method,
            request.RequestUri,
            protocolVersion,
            operation,
            Convert.ToHexString(SHA256.HashData(body)),
            body.Length,
            requestId,
            request.Headers.Contains("Authorization"),
            request.Headers.Contains("Cookie"),
            sessionId);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent? content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content is null || content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maximumBytes)
            throw new HttpRequestException("The controlled profile rejected an oversized body.");

        await using var source = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maximumBytes, 4096));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await source.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                return buffer.ToArray();
            if (buffer.Length + read > maximumBytes)
                throw new HttpRequestException("The controlled profile rejected an oversized body.");
            buffer.Write(chunk, 0, read);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Endpoint,
        string ProtocolVersion,
        string Operation,
        string InputSha256,
        int BodyBytes,
        JsonElement Id,
        bool HasAuthorization,
        bool HasCookie,
        string? SessionId);

    private sealed class ControlledMcpHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        private int _discoveryPages;
        private int _toolCount;

        public CapturedRequest[] Requests => _requests.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_requests.Count >= ControlledProfile.MaximumRequestsPerMinute)
                throw new HttpRequestException("The controlled profile request rate was exceeded.");

            var captured = await CaptureAsync(request, cancellationToken);
            _requests.Enqueue(captured);
            if (captured.Operation == "tools/list" &&
                ++_discoveryPages > ControlledProfile.MaximumDiscoveryPages)
                throw new HttpRequestException("The controlled profile page limit was exceeded.");

            HttpResponseMessage response = captured.Operation switch
            {
                "initialize" => JsonResponse(captured.Id, new
                {
                    protocolVersion = ProtocolVersion,
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "controlled-remote-mcp", version = "1" },
                }),
                "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
                "tools/list" => SseToolsResponse(captured.Id),
                "listen" => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
                _ => throw new HttpRequestException("The controlled profile rejected the operation."),
            };

            if (response.Content.Headers.ContentLength is > ControlledProfile.MaximumResponseBytes)
            {
                response.Dispose();
                throw new HttpRequestException("The controlled profile response size was exceeded.");
            }
            return response;
        }

        private static HttpResponseMessage JsonResponse(JsonElement id, object result)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id,
                result,
            });
            return Response(HttpStatusCode.OK, bytes, "application/json");
        }

        private HttpResponseMessage SseToolsResponse(JsonElement id)
        {
            var tool = new
            {
                name = "controlled_echo",
                description = "A local-only protocol fixture.",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false,
                },
            };
            var schemaBytes = JsonSerializer.SerializeToUtf8Bytes(tool.inputSchema).Length;
            if (schemaBytes > ControlledProfile.MaximumSchemaBytes)
                throw new HttpRequestException("The controlled profile schema bound was exceeded.");

            _toolCount += 1;
            if (_toolCount > ControlledProfile.MaximumTools)
                throw new HttpRequestException("The controlled profile tool count was exceeded.");

            var data = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id,
                result = new { tools = new[] { tool } },
            });
            var eventBytes = Encoding.UTF8.GetByteCount(data);
            if (eventBytes > ControlledProfile.MaximumSseEventBytes ||
                ControlledProfile.MaximumSseEvents < 1)
                throw new HttpRequestException("The controlled profile SSE or schema bound was exceeded.");

            var payload = Encoding.UTF8.GetBytes($"event: message\ndata: {data}\n\n");
            if (payload.Length > ControlledProfile.MaximumResponseBytes)
                throw new HttpRequestException("The controlled profile response size was exceeded.");
            return Response(HttpStatusCode.OK, payload, "text/event-stream");
        }

        private static HttpResponseMessage Response(
            HttpStatusCode statusCode,
            byte[] body,
            string contentType)
        {
            using var document = JsonDocument.Parse(
                contentType == "text/event-stream"
                    ? ExtractSseData(body)
                    : body,
                new JsonDocumentOptions { MaxDepth = ControlledProfile.MaximumJsonDepth });
            return new HttpResponseMessage(statusCode)
            {
                Content = new ByteArrayContent(body)
                {
                    Headers = { ContentType = new(contentType) },
                },
            };
        }

        private static byte[] ExtractSseData(byte[] payload)
        {
            var text = Encoding.UTF8.GetString(payload);
            var prefix = "data:";
            var start = text.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
                throw new HttpRequestException("The controlled SSE response has no data event.");
            start += prefix.Length;
            if (start < text.Length && text[start] == ' ')
                start++;
            var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
            if (end < 0)
                throw new HttpRequestException("The controlled SSE response is not terminated.");
            return Encoding.UTF8.GetBytes(text[start..end]);
        }
    }

    private sealed class MissingAppliedProofHandler(HttpMessageHandler network)
        : DelegatingHandler(network)
    {
        private readonly ConcurrentQueue<CapturedRequest> _attempts = new();

        public CapturedRequest[] Attempts => _attempts.ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _attempts.Enqueue(await CaptureAsync(request, cancellationToken));
            throw new HttpRequestException("Remote MCP is blocked: no current applied L7 proof.");
        }
    }

    private sealed class CountingNetworkHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            throw new InvalidOperationException("The no-proof handler must not delegate.");
        }
    }

    private sealed class StallingNetworkHandler : HttpMessageHandler
    {
        private int _sendCount;

        public int SendCount => Volatile.Read(ref _sendCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _sendCount);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
