extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
        string? observedProjectsPath = null;
        string? observedProjectsBearer = null;
        string? observedProjectsTenant = null;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(request =>
            {
                observedProjectsPath = request.RequestUri!.PathAndQuery;
                observedProjectsBearer = request.Headers.Authorization?.Parameter;
                observedProjectsTenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
                return NoContentNoStore();
            }),
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
        Assert.Equal("/api/authorization/platform/model-pricing-rate-cards", observedProjectsPath);
        Assert.Equal(bearer, observedProjectsBearer);
        Assert.Equal("tenant-1", observedProjectsTenant);
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
        var projectsCalls = 0;
        var eventsCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ =>
            {
                projectsCalls++;
                throw new InvalidOperationException("Invalid request must not reach Projects.");
            }),
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
        Assert.Equal(0, projectsCalls);
        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task Enforces64KiBBoundAndJsonContentTypeBeforeCallingEvents()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var projectsCalls = 0;
        var eventsCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ =>
            {
                projectsCalls++;
                throw new InvalidOperationException("Rejected request must not reach Projects.");
            }),
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

        Assert.Equal(0, projectsCalls);
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
            () => new DelegateHandler(_ => NoContentNoStore()),
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
            () => new DelegateHandler(_ => NoContentNoStore()),
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
            () => new DelegateHandler(_ => NoContentNoStore()),
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

    [Theory]
    [InlineData("api.read", null, null, null, null, "caller_scope_invalid")]
    [InlineData("projects.admin", null, null, null, null, "caller_scope_invalid")]
    [InlineData("api.read projects.admin", "not-a-guid", null, null, null, "caller_scope_invalid")]
    [InlineData("api.read projects.admin", null, "proj-1", null, null, "caller_binding_mismatch")]
    [InlineData("api.read projects.admin", null, null, "run-1", null, "caller_binding_mismatch")]
    [InlineData("api.read projects.admin", null, null, null, "credential-publication", "caller_binding_mismatch")]
    public async Task RejectsInvalidTokenShapeScopesAndBindingsBeforeCallingProjectsOrEvents(
        string scopes,
        string? subject,
        string? projectId,
        string? runId,
        string? purpose,
        string expectedCode)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var projectsCalls = 0;
        var eventsCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ =>
            {
                projectsCalls++;
                throw new InvalidOperationException("Projects must not be called.");
            }),
            (_, _, _) =>
            {
                eventsCalls++;
                throw new InvalidOperationException("Events must not be called.");
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CreateToken(key, scopes, subject, projectId, runId, purpose));

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, projectsCalls);
        Assert.Equal(0, eventsCalls);
    }

    [Theory]
    [InlineData("tenant/bad")]
    [InlineData("tenant bad")]
    [InlineData("   ")]
    [InlineData("tenant,1")]
    public async Task RejectsInvalidTenantHeaderBeforeCallingProjectsOrEvents(string tenant)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var projectsCalls = 0;
        var eventsCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ =>
            {
                projectsCalls++;
                throw new InvalidOperationException("Projects must not be called.");
            }),
            (_, _, _) =>
            {
                eventsCalls++;
                throw new InvalidOperationException("Events must not be called.");
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));
        request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", tenant);

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("projects_authorization_tenant_invalid", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, projectsCalls);
        Assert.Equal(0, eventsCalls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, true, HttpStatusCode.Forbidden, "projects_authorization_denied")]
    [InlineData(HttpStatusCode.Forbidden, true, HttpStatusCode.Forbidden, "projects_authorization_denied")]
    [InlineData(HttpStatusCode.NotFound, true, HttpStatusCode.Forbidden, "projects_authorization_denied")]
    [InlineData(HttpStatusCode.Found, true, HttpStatusCode.BadGateway, "projects_authorization_redirect_rejected")]
    [InlineData(HttpStatusCode.OK, true, HttpStatusCode.BadGateway, "projects_authorization_unavailable")]
    [InlineData(HttpStatusCode.ServiceUnavailable, true, HttpStatusCode.BadGateway, "projects_authorization_unavailable")]
    [InlineData(HttpStatusCode.NoContent, false, HttpStatusCode.BadGateway, "projects_authorization_cache_policy_invalid")]
    public async Task FailsClosedWithoutRelayingProjectsBodyOrCallingEvents(
        HttpStatusCode projectsStatus,
        bool includeNoStore,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var eventsCalls = 0;
        const string dependencySecretBody = """{"detail":"internal-projects-body-must-not-relay"}""";
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DelegateHandler(_ =>
            {
                var response = new HttpResponseMessage(projectsStatus);
                if (projectsStatus != HttpStatusCode.NoContent)
                    response.Content = new StringContent(dependencySecretBody, Encoding.UTF8, "application/problem+json");
                if (includeNoStore)
                    response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }),
            (_, _, _) =>
            {
                eventsCalls++;
                throw new InvalidOperationException("Events must not be called when precheck fails.");
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put, "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("internal-projects-body-must-not-relay", body, StringComparison.Ordinal);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(expectedCode, problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task Http11DeniedUploadReturns403BeforeCallerSendsBodyAndDoesNotReachEvents()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var eventsCalls = 0;

        await using var server = await StartHttp11GatewayAsync(
            key,
            () => new DelegateHandler(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("""{"detail":"do-not-relay"}""", Encoding.UTF8, "application/problem+json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return response;
            }),
            (_, _, _) =>
            {
                eventsCalls++;
                throw new InvalidOperationException("Denied upload must not reach Events.");
            });

        var endpoint = server.Client.BaseAddress!;
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(endpoint.Host, endpoint.Port);
        await using var stream = tcp.GetStream();

        // Send only HTTP/1.1 headers with a declared 100-byte body; do not send any body bytes.
        var headers =
            $"PUT /api/v1/platform/model-pricing/rate-cards/manual-card/v1 HTTP/1.1\r\n" +
            $"Host: {endpoint.Authority}\r\n" +
            $"Authorization: Bearer {bearer}\r\n" +
            $"X-Agentweaver-Tenant: tenant-1\r\n" +
            $"Content-Type: application/json\r\n" +
            $"Content-Length: 100\r\n" +
            $"\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(headers);
        await stream.WriteAsync(headerBytes);
        await stream.FlushAsync();

        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var statusLine = await reader.ReadLineAsync(readTimeout.Token);
        Assert.Equal("HTTP/1.1 403 Forbidden", statusLine);

        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(readTimeout.Token);
            if (string.IsNullOrEmpty(line))
                break;
            var colon = line.IndexOf(':');
            Assert.True(colon > 0, $"Unexpected header line: {line}");
            responseHeaders[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        Assert.Equal("no-store", responseHeaders["Cache-Control"]);
        var contentLength = int.Parse(responseHeaders["Content-Length"]);
        var bodyBuffer = new char[contentLength];
        var totalRead = 0;
        while (totalRead < contentLength)
        {
            var read = await reader.ReadAsync(bodyBuffer.AsMemory(totalRead, contentLength - totalRead), readTimeout.Token);
            Assert.True(read > 0);
            totalRead += read;
        }
        var body = new string(bodyBuffer);
        Assert.DoesNotContain("do-not-relay", body, StringComparison.Ordinal);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("projects_authorization_denied", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, eventsCalls);
    }

    [Fact]
    public async Task Http11AuthorizedUploadForwardsBearerTenantAndBodyToEvents()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string canonical = """{"runtimeRateCardSchemaVersion":1}""";
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()}\"";

        string? observedProjectsTenant = null;
        string? observedEventsTenant = null;
        string? observedEventsBearer = null;
        string? observedEventsBody = null;

        await using var server = await StartHttp11GatewayAsync(
            key,
            () => new DelegateHandler(request =>
            {
                observedProjectsTenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
                return NoContentNoStore();
            }),
            async (_, request, cancellationToken) =>
            {
                observedEventsTenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
                observedEventsBearer = request.Headers.Authorization?.Parameter;
                observedEventsBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var response = new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(canonical, Encoding.UTF8, "application/json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                response.Headers.ETag = new EntityTagHeaderValue(etag);
                return response;
            });

        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            "/api/v1/platform/model-pricing/rate-cards/manual-card/v1")
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new StringContent(canonical, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");

        using var response = await server.Client.SendAsync(request).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(etag, response.Headers.ETag?.ToString());
        Assert.Equal("tenant-1", observedProjectsTenant);
        Assert.Equal("tenant-1", observedEventsTenant);
        Assert.Equal(bearer, observedEventsBearer);
        Assert.Equal(canonical, observedEventsBody);
        Assert.Equal(canonical, await response.Content.ReadAsStringAsync());
    }

    private static HttpResponseMessage NoContentNoStore() =>
        new(HttpStatusCode.NoContent)
        {
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
        };

    private static string CreateToken(
        SecurityKey key,
        string scopes = "api.read projects.admin",
        string? subject = null,
        string? projectId = null,
        string? runId = null,
        string? purpose = null)
    {
        var credentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256);
        var claims = new List<Claim>
        {
            new("sub", subject ?? Guid.NewGuid().ToString("D")),
            new("scope", scopes),
        };
        if (projectId is not null)
            claims.Add(new Claim("project_id", projectId));
        if (runId is not null)
            claims.Add(new Claim("run_id", runId));
        if (purpose is not null)
            claims.Add(new Claim("purpose", purpose));
        var token = new JwtSecurityToken(
            issuer: new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience: "https://api.test",
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static async Task<Http11GatewayHost> StartHttp11GatewayAsync(
        SecurityKey signingKey,
        Func<HttpMessageHandler> projectsHandlerFactory,
        Func<GatewayOwner, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> eventsHandler)
    {
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer);
        const string audience = "https://api.test";
        var options = new GatewayOptions(
            issuer,
            audience,
            new Uri("https://projects.test/"),
            new Uri("https://orchestrator.test/"),
            new Uri("https://knowledge.test/"),
            new Uri("https://events.test/"),
            new Uri("https://identity-broker.test/"),
            new Uri("https://web.test/"),
            TimeSpan.FromSeconds(10));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        builder.Logging.ClearProviders();
        var requestContext = new HttpContextAccessor();
        builder.Services.AddSingleton<IHttpContextAccessor>(requestContext);
        GatewayEndpoints.SuppressTargetContextRequestLogs(builder.Services, requestContext);
        builder.Services.AddSingleton(options);
        GatewayWebCors.AddGatewayWebCors(builder.Services, options);
        builder.Services.AddHttpClient(nameof(GatewayOwner.Projects), client =>
                client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(projectsHandlerFactory);
        foreach (var owner in Enum.GetValues<GatewayOwner>().Where(owner => owner != GatewayOwner.Projects))
        {
            builder.Services.AddHttpClient(owner.ToString(), client =>
                    client.Timeout = Timeout.InfiniteTimeSpan)
                .ConfigurePrimaryHttpMessageHandler(() => new DelegateHandler((request, ct) =>
                    eventsHandler(owner, request, ct)));
        }
        builder.Services.AddSingleton<GatewayOwnerClient>();
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(authentication =>
            {
                authentication.MapInboundClaims = false;
                authentication.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = issuer.AbsoluteUri,
                    ValidateAudience = true,
                    ValidAudience = audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    RequireSignedTokens = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };
            });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, GatewayAuthorizationResultHandler>();

        var app = builder.Build();
        app.UseRouting();
        GatewayEndpoints.UseTargetContextGuard(app);
        app.UseCors();
        app.UseAuthentication();
        app.UseAuthorization();
        GatewayEndpoints.MapGatewayEndpoints(app);
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.Single();
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new Http11GatewayHost(app, client);
    }

    private sealed class Http11GatewayHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client => client;

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> syncHandler)
            : this((request, _) => Task.FromResult(syncHandler(request)))
        {
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
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
