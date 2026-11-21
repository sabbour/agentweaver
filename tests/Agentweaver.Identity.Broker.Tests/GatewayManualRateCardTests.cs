extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayManualRateCardTests
{
    [Fact]
    public async Task OpenApiExposesManualRateCardWriteRouteWithBoundsAndStatuses()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call owners."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var pathItem = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/platform/model-pricing/rate-cards/{id}/{version}");
        var operation = pathItem.GetProperty("put");
        Assert.Equal("savePlatformManualRuntimeRateCard", operation.GetProperty("operationId").GetString());
        Assert.Equal("Events", operation.GetProperty("x-agentweaver-owner").GetString());
        Assert.Equal("/api/platform/model-pricing/rate-cards/{id}/{version}",
            operation.GetProperty("x-agentweaver-owner-path").GetString());
        Assert.True(operation.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
        Assert.Equal(64 * 1024, operation.GetProperty("x-agentweaver-max-request-body-bytes").GetInt64());
        Assert.Equal(256 * 1024, operation.GetProperty("x-agentweaver-max-response-bytes").GetInt64());

        var responses = operation.GetProperty("responses");
        foreach (var status in new[] { "200", "201", "400", "401", "403", "409", "413", "415", "503" })
            Assert.True(responses.TryGetProperty(status, out _), $"Missing status {status}");
        Assert.True(responses.GetProperty("200").GetProperty("headers").TryGetProperty("Cache-Control", out _));
        Assert.True(responses.GetProperty("201").GetProperty("headers").TryGetProperty("Cache-Control", out _));
    }

    [Fact]
    public async Task ProxiesPutToEventsForwardsBearerAndTenantAndExposesEtagAndNoStore()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string canonical = """{"runtimeRateCardSchemaVersion":1}""";
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}\"";

        GatewayOwner? observedOwner = null;
        string? observedPath = null;
        string? observedBearer = null;
        string? observedTenant = null;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (owner, request, _) =>
            {
                observedOwner = owner;
                observedPath = request.RequestUri!.PathAndQuery;
                observedBearer = request.Headers.Authorization?.Parameter;
                observedTenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(canonical, Encoding.UTF8, "application/json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                response.Headers.ETag = new EntityTagHeaderValue(etag);
                return Task.FromResult(response);
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(GatewayOwner.Events, observedOwner);
        Assert.Equal("/api/platform/model-pricing/rate-cards/manual-card/v1", observedPath);
        Assert.Equal(bearer, observedBearer);
        Assert.Equal("tenant-1", observedTenant);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(etag, response.Headers.ETag?.ToString());
        Assert.Equal(canonical, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/v1/platform/model-pricing/rate-cards/-bad/v1")]
    [InlineData("/api/v1/platform/model-pricing/rate-cards/card%2F1/v1")]
    [InlineData("/api/v1/platform/model-pricing/rate-cards/card-1/v1?x=1")]
    public async Task RejectsInvalidKeysEncodedAliasesAndQueriesBeforeCallingEvents(string path)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var eventsCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                eventsCalls++;
                throw new InvalidOperationException("Invalid request must not reach Events.");
            });

        using var request = new HttpRequestMessage(HttpMethod.Put, path)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task Enforces64KiBBoundAndJsonContentTypeBeforeCallingEvents()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var eventsCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                eventsCalls++;
                throw new InvalidOperationException("Rejected request must not reach Events.");
            });

        // Unsupported media type -> 415
        using (var wrongType = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/card-1/v1")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        })
        {
            wrongType.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await gateway.Client.SendAsync(wrongType);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }

        // Declared > 64 KiB -> 413
        var oversized = new string(' ', 64 * 1024 + 1);
        using (var declaredLarge = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/card-1/v1")
        {
            Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
        })
        {
            declaredLarge.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            using var response = await gateway.Client.SendAsync(declaredLarge);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task StreamedOverflowReturns413WithoutBufferingWholeUpload()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var ownerReadCompleted = false;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            async (_, request, cancellationToken) =>
            {
                await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                ownerReadCompleted = true;
                return new HttpResponseMessage(HttpStatusCode.Created);
            });

        var oversized = new string(' ', 64 * 1024 + 1);
        using var streamedLarge = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/card-1/v1")
        {
            Content = new StreamContent(new NonSeekableStream(Encoding.UTF8.GetBytes(oversized))),
        };
        streamedLarge.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        streamedLarge.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        using var response = await gateway.Client.SendAsync(streamedLarge);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.False(ownerReadCompleted);
    }

    [Fact]
    public async Task ValidSlowUploadReachesOwnerBeforeLastChunk()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string canonical = """{"runtimeRateCardSchemaVersion":1}""";
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}\"";

        var firstChunk = Encoding.UTF8.GetBytes("""{"runtimeRateCardSchemaVersion":""");
        var lastChunk = Encoding.UTF8.GetBytes("""1}""");
        var allowLastChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerReceivedFirstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            async (_, request, cancellationToken) =>
            {
                await using var bodyStream = await request.Content!.ReadAsStreamAsync(cancellationToken);
                using var received = new MemoryStream();
                var buffer = new byte[1024];
                while (true)
                {
                    var read = await bodyStream.ReadAsync(buffer, cancellationToken);
                    if (read == 0)
                        break;
                    await received.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    if (received.Length > 0)
                        ownerReceivedFirstChunk.TrySetResult();
                }

                Assert.Equal(canonical, Encoding.UTF8.GetString(received.ToArray()));
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(canonical, Encoding.UTF8, "application/json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                response.Headers.ETag = new EntityTagHeaderValue(etag);
                return response;
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Content = new StreamContent(new TwoChunkSlowStream(firstChunk, lastChunk, allowLastChunk.Task)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        var responseTask = gateway.Client.SendAsync(request);
        await ownerReceivedFirstChunk.Task.WaitAsync(TimeSpan.FromSeconds(5));
        allowLastChunk.TrySetResult();

        using var response = await responseTask;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(etag, response.Headers.ETag?.ToString());
        Assert.Equal(canonical, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EventsCanRejectAuthorityBeforeReadingStreamedBody()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var allowLastChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"code":"forbidden"}""", Encoding.UTF8, "application/problem+json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return Task.FromResult(response);
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Content = new StreamContent(new TwoChunkSlowStream(
                Encoding.UTF8.GetBytes("{"),
                Encoding.UTF8.GetBytes("}"),
                allowLastChunk.Task)),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        // Gateway returns Events' 403 while the upload stream is still blocked before its last chunk.
        using var response = await gateway.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        allowLastChunk.TrySetResult();
    }

    private static string CreateToken(SecurityKey key)
    {
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityToken(
            issuer: new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience: "https://api.test",
            claims:
            [
                new Claim("sub", Guid.NewGuid().ToString("D")),
                new Claim("scope", "api.read projects.admin"),
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }

    private sealed class TwoChunkSlowStream(byte[] firstChunk, byte[] lastChunk, Task allowLastChunk) : Stream
    {
        private int _stage;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_stage == 0)
            {
                _stage = 1;
                firstChunk.CopyTo(buffer);
                return firstChunk.Length;
            }
            if (_stage == 1)
            {
                await allowLastChunk.WaitAsync(cancellationToken).ConfigureAwait(false);
                _stage = 2;
                lastChunk.CopyTo(buffer);
                return lastChunk.Length;
            }
            return 0;
        }
    }
}
