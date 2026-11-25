extern alias GatewayHost;
extern alias ProjectsConfig;

using Agentweaver.Abstractions;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using GatewayHost::Agentweaver.Gateway;
using ProjectsConfig::Agentweaver.Projects.Config;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayModelProviderDraftTests
{
    private static readonly (string Method, string Path, string OperationId, string OwnerPath)[] DraftRoutes =
    [
        ("GET", "/api/v1/platform/byok-deployment-bindings", "readPlatformByokDeploymentBindings", "/api/platform/byok-deployment-bindings"),
        ("GET", "/api/v1/platform/byok-providers", "listPlatformByokProviderDrafts", "/api/platform/byok-providers"),
        ("GET", "/api/v1/platform/byok-providers/{providerId}", "readPlatformByokProviderDraft", "/api/platform/byok-providers/{providerId}"),
        ("POST", "/api/v1/platform/byok-providers", "createPlatformByokProviderDraft", "/api/platform/byok-providers"),
        ("PUT", "/api/v1/platform/byok-providers/{providerId}", "updatePlatformByokProviderDraft", "/api/platform/byok-providers/{providerId}"),
        ("POST", "/api/v1/platform/byok-providers/{providerId}/activate", "activatePlatformByokProviderDraft", "/api/platform/byok-providers/{providerId}/activate"),
        ("POST", "/api/v1/platform/byok-providers/deactivate", "deactivatePlatformByokProviderDraft", "/api/platform/byok-providers/deactivate"),
        ("GET", "/api/v1/projects/{projectId}/byok-providers", "listProjectByokProviderDrafts", "/api/projects/{projectId}/byok-providers"),
        ("GET", "/api/v1/projects/{projectId}/byok-providers/{providerId}", "readProjectByokProviderDraft", "/api/projects/{projectId}/byok-providers/{providerId}"),
        ("POST", "/api/v1/projects/{projectId}/byok-providers", "createProjectByokProviderDraft", "/api/projects/{projectId}/byok-providers"),
        ("PUT", "/api/v1/projects/{projectId}/byok-providers/{providerId}", "updateProjectByokProviderDraft", "/api/projects/{projectId}/byok-providers/{providerId}"),
    ];

    [Fact]
    public async Task CredentialPutStreamsOctetStreamRejectsUnsupportedMediaTypeAndAbortsOnPostHeaderError()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        string? forwardedContentType = null;
        string? forwardedIfMatch = null;
        string? forwardedIdempotency = null;
        byte[]? forwardedBytes = null;
        var throwAfterHeaders = false;

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            async (owner, req, ct) =>
            {
                if (owner != GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker)
                    throw new InvalidOperationException($"Unexpected owner {owner}.");

                if (throwAfterHeaders)
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(new FailingMidStream()),
                    };
                }

                forwardedContentType = req.Content?.Headers.ContentType?.MediaType;
                forwardedIfMatch = req.Headers.TryGetValues("If-Match", out var ifm) ? string.Join(",", ifm) : null;
                forwardedIdempotency = req.Headers.TryGetValues("Idempotency-Key", out var idem) ? string.Join(",", idem) : null;
                forwardedBytes = req.Content is null ? null : await req.Content.ReadAsByteArrayAsync(ct);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"operationId":"11111111-1111-1111-1111-111111111111","status":"published","scope":"platform","projectId":null,"providerId":"byok-1"}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            });

        using var openApiResp = await gateway.Client.GetAsync("/openapi/v1.json");
        openApiResp.EnsureSuccessStatusCode();
        using var openApi = JsonDocument.Parse(await openApiResp.Content.ReadAsStringAsync());
        var putOp = openApi.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/platform/byok-providers/{providerId}/credential")
            .GetProperty("put");
        var reqContent = putOp.GetProperty("requestBody").GetProperty("content");
        Assert.True(reqContent.TryGetProperty("application/json", out _));
        Assert.Equal("binary", reqContent.GetProperty("application/octet-stream").GetProperty("schema").GetProperty("format").GetString());
        var paramNames = putOp.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToArray();
        Assert.Contains("If-Match", paramNames);
        Assert.Contains("Idempotency-Key", paramNames);

        var bearer = CreateToken(signingKey);

        // Unsupported media type returns 415 before calling Broker.
        using (var badMedia = new HttpRequestMessage(HttpMethod.Put, "/api/v1/platform/byok-providers/byok-1/credential"))
        {
            badMedia.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            badMedia.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            badMedia.Content = new StringContent("plain", Encoding.UTF8, "text/plain");
            using var badMediaResp = await gateway.Client.SendAsync(badMedia);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, badMediaResp.StatusCode);
            Assert.Null(forwardedBytes);
        }

        // Oversized application/octet-stream returns 413 without buffering >8192 bytes.
        using (var oversized = new HttpRequestMessage(HttpMethod.Put, "/api/v1/platform/byok-providers/byok-1/credential"))
        {
            oversized.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            oversized.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            oversized.Content = new ByteArrayContent(new byte[8193]);
            oversized.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var oversizedResp = await gateway.Client.SendAsync(oversized);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResp.StatusCode);
        }

        // Valid UTF-8 octet-stream forwards headers and exact bytes with no-store.
        var utf8Bytes = Encoding.UTF8.GetBytes("sk-gw-\u03C0-\U0001F511");
        using (var valid = new HttpRequestMessage(HttpMethod.Put, "/api/v1/platform/byok-providers/byok-1/credential"))
        {
            valid.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            valid.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            valid.Headers.TryAddWithoutValidation("If-Match", "2");
            valid.Headers.Add("Idempotency-Key", "11111111-1111-1111-1111-111111111111");
            valid.Content = new ByteArrayContent(utf8Bytes);
            valid.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var validResp = await gateway.Client.SendAsync(valid);
            Assert.Equal(HttpStatusCode.OK, validResp.StatusCode);
            Assert.True(validResp.Headers.CacheControl?.NoStore);
            Assert.Equal("application/octet-stream", forwardedContentType);
            Assert.Equal("2", forwardedIfMatch);
            Assert.Equal("11111111-1111-1111-1111-111111111111", forwardedIdempotency);
            Assert.Equal(utf8Bytes, forwardedBytes);
        }

        // Failure after response headers start aborts cleanly instead of throwing from WriteProblemAsync.
        throwAfterHeaders = true;
        using (var midFail = new HttpRequestMessage(HttpMethod.Put, "/api/v1/platform/byok-providers/byok-1/credential"))
        {
            midFail.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            midFail.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            midFail.Content = new ByteArrayContent(utf8Bytes);
            midFail.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            await Assert.ThrowsAnyAsync<Exception>(() => gateway.Client.SendAsync(midFail));
        }
    }

    private sealed class FailingMidStream : Stream
    {
        private bool _sentFirst;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Simulated mid-stream failure.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentFirst)
            {
                _sentFirst = true;
                buffer.Span[0] = (byte)'{';
                return ValueTask.FromResult(1);
            }
            throw new IOException("Simulated mid-stream failure.");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CredentialOperationStatusRoutesExposeOpenApiContractsAndReturn503WhenOwnerUnavailable()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (owner, _, _) =>
            {
                if (owner == GatewayHost::Agentweaver.Gateway.GatewayOwner.IdentityBroker)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = new StringContent("{\"code\":\"credential_publication_unavailable\"}", Encoding.UTF8, "application/json"),
                    };
                    return Task.FromResult(response);
                }
                throw new InvalidOperationException($"Unexpected owner {owner}.");
            });

        using var openApiResponse = await gateway.Client.GetAsync("/openapi/v1.json");
        openApiResponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        var statusRoutes = new[]
        {
            ("/api/v1/platform/byok-providers/{providerId}/credential/operations/{operationId}",
                "readPlatformByokProviderCredentialOperation",
                "/api/platform/byok-providers/{providerId}/credential/operations/{operationId:guid}"),
            ("/api/v1/projects/{projectId}/byok-providers/{providerId}/credential/operations/{operationId}",
                "readProjectByokProviderCredentialOperation",
                "/api/projects/{projectId}/byok-providers/{providerId}/credential/operations/{operationId:guid}"),
        };

        foreach (var (path, operationId, ownerPath) in statusRoutes)
        {
            Assert.True(paths.TryGetProperty(path, out var pathItem), $"Missing {path}");
            var operation = pathItem.GetProperty("get");
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.Equal("IdentityBroker", operation.GetProperty("x-agentweaver-owner").GetString());
            Assert.True(operation.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
            Assert.Equal(ownerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            var responses = operation.GetProperty("responses");
            Assert.True(responses.GetProperty("200").GetProperty("headers").TryGetProperty("Cache-Control", out _));
            foreach (var status in new[] { "400", "401", "403", "404", "409", "503" })
            {
                var content = responses.GetProperty(status).GetProperty("content");
                Assert.True(content.TryGetProperty("application/problem+json", out _));
            }
        }

        var statusSchema = schemas.GetProperty("ModelProviderCredentialOperationStatusResponse");
        Assert.False(statusSchema.GetProperty("additionalProperties").GetBoolean());
        var props = statusSchema.GetProperty("properties");
        var statuses = props.GetProperty("status").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(
            new[] { "pending", "publication_pending", "published", "quarantined", "cancelled", "disable_pending", "disabled" },
            statuses);
        Assert.False(props.TryGetProperty("credentialVersion", out _));
        Assert.False(props.TryGetProperty("credentialRevision", out _));
        Assert.False(props.TryGetProperty("configurationHash", out _));

        var bearer = CreateToken(signingKey);
        using var outageReq = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/v1/platform/byok-providers/byok-1/credential/operations/{Guid.NewGuid():D}");
        outageReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        outageReq.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        using var outageResp = await gateway.Client.SendAsync(outageReq);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, outageResp.StatusCode);
        Assert.True(outageResp.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task DraftRoutesExposeTypedOpenApiContractsAndNoLifecycleOperations()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call owners."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");

        foreach (var route in DraftRoutes)
        {
            Assert.True(paths.TryGetProperty(route.Path, out var pathItem),
                $"Missing {route.Path}: {string.Join(", ", paths.EnumerateObject().Select(item => item.Name))}");
            var operation = pathItem.GetProperty(route.Method.ToLowerInvariant());
            Assert.Equal(route.OperationId, operation.GetProperty("operationId").GetString());
            Assert.Equal("Projects", operation.GetProperty("x-agentweaver-owner").GetString());
            Assert.True(operation.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
            Assert.Equal(route.OwnerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            Assert.True(operation.GetProperty("responses").GetProperty("200")
                .GetProperty("headers").TryGetProperty("Cache-Control", out _));
            var responses = operation.GetProperty("responses");
            foreach (var status in new[] { "400", "401", "403", "409", "503" })
            {
                var content = responses.GetProperty(status).GetProperty("content");
                Assert.True(content.TryGetProperty("application/json", out _));
                Assert.True(content.TryGetProperty("application/problem+json", out _));
            }
            if (route.OperationId is "readPlatformByokProviderDraft" or "activatePlatformByokProviderDraft" or
                "readProjectByokProviderDraft" or
                "listProjectByokProviderDrafts" or "createProjectByokProviderDraft" or
                "updateProjectByokProviderDraft")
                Assert.True(responses.TryGetProperty("404", out _));
            if (route.Method is "POST" or "PUT")
            {
                var content = responses.GetProperty("413").GetProperty("content");
                Assert.True(content.TryGetProperty("application/problem+json", out _));
                Assert.True(content.TryGetProperty("application/json", out _));
                Assert.True(responses.GetProperty("415").GetProperty("content")
                    .TryGetProperty("application/json", out _));
            }
        }

        Assert.False(schemas.GetProperty("ModelProviderDraftSaveRequest")
            .GetProperty("additionalProperties").GetBoolean());
        var saveProperties = schemas.GetProperty("ModelProviderDraftSaveRequest")
            .GetProperty("properties");
        Assert.Contains(saveProperties.GetProperty("rate_card_id").GetProperty("anyOf")
            .EnumerateArray(), item => item.GetProperty("type").GetString() == "null");
        var platformCreate = schemas.GetProperty("ModelProviderDraftPlatformCreateRequest");
        Assert.False(platformCreate.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schemas.GetProperty("ModelProviderDraft").GetProperty("properties").TryGetProperty("egress", out _));
        Assert.True(schemas.TryGetProperty("ModelProviderEgressStatus", out var egressStatusSchema));
        var destStateEnum = egressStatusSchema.GetProperty("properties")
            .GetProperty("destinationState").GetProperty("anyOf")[0].GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["requested"], destStateEnum);
        var platformEgressDestSchema = schemas.GetProperty("PlatformEgressDestination").GetProperty("properties");
        Assert.Equal(
            ["requested"],
            platformEgressDestSchema.GetProperty("state").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            ["modelProvider"],
            platformEgressDestSchema.GetProperty("origin").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Equal(
            ["platform", "project"],
            platformEgressDestSchema.GetProperty("scope").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.True(paths.TryGetProperty("/api/v1/platform/egress/destinations", out var egressDestPath));
        var egressOp = egressDestPath.GetProperty("get");
        Assert.Equal("listPlatformEgressDestinations", egressOp.GetProperty("operationId").GetString());
        Assert.Equal("Environment", egressOp.GetProperty("x-agentweaver-owner").GetString());
        Assert.True(egressOp.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
        Assert.True(egressOp.GetProperty("responses").GetProperty("200")
            .GetProperty("headers").TryGetProperty("Cache-Control", out _));
        Assert.True(egressOp.GetProperty("responses").TryGetProperty("503", out _));
        var reference = platformCreate.GetProperty("properties").GetProperty("reference");
        Assert.Equal("string", reference.GetProperty("type").GetString());
        Assert.Equal(1, reference.GetProperty("minLength").GetInt32());
        Assert.Equal(256, reference.GetProperty("maxLength").GetInt32());
        var pattern = reference.GetProperty("pattern").GetString()!;
        foreach (var value in new[] { "A", "A_b", "A.B-1", new string('a', 256) })
            Assert.Matches(pattern, value);
        foreach (var value in new[] { "", "_a", "a_", "a__b", "a\n", "a:b", new string('a', 257) })
            Assert.DoesNotMatch(pattern, value);
        Assert.DoesNotContain("reference", platformCreate.GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()));
        var target = platformCreate.GetProperty("properties").GetProperty("targetInstallationId");
        Assert.Equal("uuid", target.GetProperty("format").GetString());
        var targetPattern = target.GetProperty("pattern").GetString()!;
        Assert.Matches(targetPattern, "00112233-4455-4677-8899-aabbccddeeff");
        foreach (var invalid in new[]
        {
            "00000000-0000-0000-0000-000000000000",
            "00112233-4455-4677-8899-AABBCCDDEEFF",
            "00112233-4455-4677-8899-aabbccddeeff\n",
        })
            Assert.DoesNotMatch(targetPattern, invalid);
        var dependent = platformCreate.GetProperty("dependentRequired");
        Assert.Equal("targetInstallationId", Assert.Single(dependent.GetProperty("reference").EnumerateArray()).GetString());
        Assert.Equal("reference", Assert.Single(dependent.GetProperty("targetInstallationId").EnumerateArray()).GetString());
        foreach (var name in new[] { "ModelProviderDraftSaveRequest", "ModelProviderDraftProjectCreateRequest" })
            Assert.False(schemas.GetProperty(name).GetProperty("properties").TryGetProperty("targetInstallationId", out _));
        Assert.False(schemas.GetProperty("ModelProviderDraftSaveRequest")
            .GetProperty("properties").TryGetProperty("reference", out _));
        Assert.False(schemas.GetProperty("ModelProviderDraftProjectCreateRequest")
            .GetProperty("properties").TryGetProperty("reference", out _));
        Assert.Equal(0, platformCreate.GetProperty("properties").GetProperty("expectedRevision")
            .GetProperty("const").GetInt32());
        Assert.Equal(0, schemas.GetProperty("ModelProviderDraftProjectCreateRequest")
            .GetProperty("properties").GetProperty("expectedRevision").GetProperty("const").GetInt32());
        Assert.Equal(1, schemas.GetProperty("ModelProviderDraftSaveRequest")
            .GetProperty("properties").GetProperty("expectedRevision").GetProperty("minimum").GetInt32());
        foreach (var route in DraftRoutes.Where(item => item.Method is "POST" or "PUT"))
        {
            var schemaRef = paths.GetProperty(route.Path).GetProperty(route.Method.ToLowerInvariant())
                .GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString();
            Assert.Equal(route.OperationId switch
            {
                "createPlatformByokProviderDraft" => "#/components/schemas/ModelProviderDraftPlatformCreateRequest",
                "createProjectByokProviderDraft" => "#/components/schemas/ModelProviderDraftProjectCreateRequest",
                "activatePlatformByokProviderDraft" => "#/components/schemas/ActivatePlatformModelProviderRequest",
                "deactivatePlatformByokProviderDraft" => "#/components/schemas/DeactivatePlatformModelProviderRequest",
                _ => "#/components/schemas/ModelProviderDraftSaveRequest",
            }, schemaRef);
        }
        Assert.True(schemas.TryGetProperty("ModelProviderDraft", out var draftSchema),
            string.Join(", ", schemas.EnumerateObject().Select(schema => schema.Name)));
        Assert.False(draftSchema
            .GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("string", draftSchema.GetProperty("properties").GetProperty("reference")
            .GetProperty("type").GetString());
        Assert.DoesNotContain("reference", draftSchema.GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("hasCredential",
            draftSchema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name));
        Assert.Contains("rate_card_id", draftSchema.GetProperty("required").EnumerateArray()
            .Select(property => property.GetString()));
        Assert.Contains("rate_card_version", draftSchema.GetProperty("required").EnumerateArray()
            .Select(property => property.GetString()));
        Assert.DoesNotContain("credentialVersion",
            draftSchema.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name));
        Assert.Equal(
            ["Draft", "Available"],
            draftSchema.GetProperty("properties").GetProperty("state")
                .GetProperty("enum").EnumerateArray()
                .Select(value => value.GetString()!).ToArray());
        Assert.True(paths.TryGetProperty("/api/v1/platform/byok-providers/{providerId}/activate", out _));
        Assert.False(paths.TryGetProperty("/api/v1/platform/byok-providers/{providerId}", out var item) &&
            item.TryGetProperty("delete", out _));

        var pricing = paths
            .GetProperty("/api/v1/platform/model-pricing/rate-cards/{id}/{version}")
            .GetProperty("put").GetProperty("description").GetString()!;
        Assert.Contains("without JavaScript number conversion", pricing);
        Assert.Contains("new version instead of overwriting", pricing);
    }

    [Fact]
    public async Task AvailableProviderStatePassesThroughGatewayDraftRead()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string body =
            """{"id":"provider-a","name":"Customer model","type":"azure","base_url":"https://provider.example","model":"deployment-1","wire_api":null,"azure_api_version":null,"reasoning_efforts":["low"],"prompt_capacity_tokens":null,"scope":"platform","projectId":null,"state":"Available","revision":2,"configurationHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","has_api_key":false,"is_active":false,"hasCredential":true}""";
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DraftOwnerHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
            })),
            (_, _, _) => throw new InvalidOperationException("Draft metadata must call Projects."));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/platform/byok-providers/provider-a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Available", result.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task ProjectDetailDraftReadPassesThroughSerializedEgressStatus()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        const string body =
            """{"id":"provider-a","name":"Customer model","type":"openai","base_url":"https://api.openai.com/v1","model":"gpt-5","wire_api":null,"azure_api_version":null,"reasoning_efforts":["low"],"prompt_capacity_tokens":null,"rate_card_id":null,"rate_card_version":null,"scope":"project","projectId":"project-a","state":"Draft","revision":1,"configurationHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","has_api_key":false,"is_active":false,"hasCredential":false,"egress":{"host":"api.openai.com","port":443,"destinationState":"requested","canApprove":false,"deliveryStatus":"delivered"}}""";
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DraftOwnerHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
            })),
            (_, _, _) => throw new InvalidOperationException("Draft metadata must call Projects."));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/projects/project-a/byok-providers/provider-a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var egress = result.RootElement.GetProperty("egress");
        Assert.Equal("api.openai.com", egress.GetProperty("host").GetString());
        Assert.Equal(443, egress.GetProperty("port").GetInt32());
        Assert.Equal("requested", egress.GetProperty("destinationState").GetString());
        Assert.False(egress.GetProperty("canApprove").GetBoolean());
        Assert.Equal("delivered", egress.GetProperty("deliveryStatus").GetString());
    }

    [Theory]
    [InlineData("/api/v1/platform/byok-deployment-bindings", "/api/platform/byok-deployment-bindings")]
    [InlineData("/api/v1/platform/byok-providers?pageSize=1", "/api/platform/byok-providers?pageSize=1")]
    [InlineData("/api/v1/platform/byok-providers/provider-a", "/api/platform/byok-providers/provider-a")]
    [InlineData("/api/v1/projects/project-a/byok-providers?afterProviderId=one", "/api/projects/project-a/byok-providers?afterProviderId=one")]
    [InlineData("/api/v1/projects/project-a/byok-providers/provider-a", "/api/projects/project-a/byok-providers/provider-a")]
    public async Task BodyFreeDraftGetsForwardBearerTenantAndNoStore(
        string publicPath,
        string expectedOwnerPath)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var calls = 0;
        GatewayOwner observedOwner = default;
        string? observedPath = null;
        string? observedBearer = null;
        string? observedTenant = null;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, () => new DraftOwnerHandler((request, _) =>
            {
                calls++;
                observedOwner = GatewayOwner.Projects;
                observedPath = request.RequestUri!.PathAndQuery;
                observedBearer = request.Headers.Authorization?.Parameter;
                observedTenant = request.Headers.GetValues("X-Agentweaver-Tenant").Single();
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"providers":[],"nextAfterProviderId":null}""",
                        Encoding.UTF8, "application/json"),
                };
                response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                return Task.FromResult(response);
            }),
            (_, _, _) => throw new InvalidOperationException("Draft metadata must call Projects."));
        using var request = new HttpRequestMessage(HttpMethod.Get, publicPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        using var response = await gateway.Client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, calls);
        Assert.Equal(GatewayOwner.Projects, observedOwner);
        Assert.Equal(expectedOwnerPath, observedPath);
        Assert.Equal(bearer, observedBearer);
        Assert.Equal("tenant-a", observedTenant);
    }

    [Theory]
    [InlineData("/api/v1/platform/byok-deployment-bindings")]
    [InlineData("/api/v1/platform/byok-providers")]
    [InlineData("/api/v1/platform/byok-providers/provider-a")]
    [InlineData("/api/v1/projects/project-a/byok-providers")]
    [InlineData("/api/v1/projects/project-a/byok-providers/provider-a")]
    public async Task EveryDraftGetRejectsFixedLengthChunkedAndHeaderlessBodiesBeforeOwner(string path)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, () => new DraftOwnerHandler((_, _) =>
            {
                ownerCalls++;
                throw new InvalidOperationException("A Draft GET body must not reach Projects.");
            }),
            (_, _, _) => throw new InvalidOperationException("Draft GETs must use the Projects handler."));

        foreach (var bodyKind in new[] { "fixed", "chunked", "headerless" })
        {
            using var request = CreateBodyRequest(path, bearer, bodyKind);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("model_provider_draft_invalid_request",
                body.RootElement.GetProperty("title").GetString());
            Assert.Equal(0, ownerCalls);
        }
    }

    [Theory]
    [InlineData("POST", "/api/v1/platform/byok-providers")]
    [InlineData("PUT", "/api/v1/projects/project-a/byok-providers/provider-a")]
    public async Task DraftSaveBodyLimitRejectsOversizedBodiesBeforeProjects(
        string method,
        string path)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var projectCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DraftOwnerHandler((_, _) =>
            {
                projectCalls++;
                throw new InvalidOperationException("An oversized Draft save must not reach Projects.");
            }),
            (_, _, _) => throw new InvalidOperationException("Draft saves must use Projects."));
        using var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent(
                new string('x', 32 * 1024 + 1), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(0, projectCalls);
    }

    [Fact]
    public async Task DraftOwnerResponseLimitRejectsOversizedRows()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        var projectCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DraftOwnerHandler((_, _) =>
            {
                projectCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        new string('x', 32 * 1024 + 1), Encoding.UTF8, "application/json"),
                });
            }),
            (_, _, _) => throw new InvalidOperationException("Draft requests must use Projects."));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/platform/byok-providers/provider-a");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(1, projectCalls);
    }

    [Theory]
    [InlineData("/api/v1/platform/byok-deployment-bindings", 4 * 1024 * 1024, HttpStatusCode.OK)]
    [InlineData("/api/v1/platform/byok-deployment-bindings", 4 * 1024 * 1024 + 1, HttpStatusCode.BadGateway)]
    [InlineData("/api/v1/platform/byok-providers", 4 * 1024 * 1024, HttpStatusCode.OK)]
    [InlineData("/api/v1/platform/byok-providers", 4 * 1024 * 1024 + 1, HttpStatusCode.BadGateway)]
    [InlineData("/api/v1/platform/byok-providers/provider-a", 32 * 1024, HttpStatusCode.OK)]
    [InlineData("/api/v1/platform/byok-providers/provider-a", 32 * 1024 + 1, HttpStatusCode.BadGateway)]
    [InlineData("/api/v1/projects/project-a/byok-providers", 4 * 1024 * 1024, HttpStatusCode.OK)]
    [InlineData("/api/v1/projects/project-a/byok-providers", 4 * 1024 * 1024 + 1, HttpStatusCode.BadGateway)]
    [InlineData("/api/v1/projects/project-a/byok-providers/provider-a", 32 * 1024, HttpStatusCode.OK)]
    [InlineData("/api/v1/projects/project-a/byok-providers/provider-a", 32 * 1024 + 1, HttpStatusCode.BadGateway)]
    public async Task DraftGetsBufferActualChunkedOwnerResponseBeforeHeaders(string path, int size, HttpStatusCode status)
    {
        var expectedBody = "{\"value\":\"" + new string('x', size - 12) + "\"}";
        var bytes = Encoding.UTF8.GetBytes(expectedBody);
        Assert.Equal(size, bytes.Length);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var owner = builder.Build();
        owner.Run(async context =>
        {
            context.Response.ContentType = "application/json";
            for (var sent = 0; sent < size;)
            {
                var count = Math.Min(16 * 1024, size - sent);
                await context.Response.Body.WriteAsync(bytes.AsMemory(sent, count));
                await context.Response.Body.FlushAsync();
                sent += count;
            }
        });
        await owner.StartAsync();
        var address = new Uri(owner.Urls.Single());
        var chunked = false;
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key, () => new ObservedChunkedHandler(() => chunked = true),
            (_, _, _) => throw new InvalidOperationException("The read must call Projects."),
            projectsOwner: address);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(chunked);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        if (status == HttpStatusCode.BadGateway)
        {
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var problem = JsonDocument.Parse(body);
            Assert.Equal("owner_response_too_large", problem.RootElement.GetProperty("title").GetString());
        }
        else
        {
            Assert.Equal(expectedBody, body);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "model_provider_draft_not_found")]
    [InlineData(HttpStatusCode.UnsupportedMediaType, "model_provider_draft_unsupported_media_type")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "model_provider_draft_unavailable")]
    public async Task DraftOwnerErrorStatusesAndBodiesPassThrough(
        HttpStatusCode status,
        string code)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var bearer = CreateToken(key);
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new DraftOwnerHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent($$"""{"code":"{{code}}"}""", Encoding.UTF8, "application/json"),
            })),
            (_, _, _) => throw new InvalidOperationException("Draft requests must use Projects."));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/platform/byok-providers")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(code, JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("code").GetString());
    }

    private static HttpRequestMessage CreateBodyRequest(string path, string bearer, string bodyKind)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        switch (bodyKind)
        {
            case "fixed":
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                break;
            case "chunked":
                request.Version = HttpVersion.Version11;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Content = new LengthlessContent();
                break;
            default:
                request.Version = HttpVersion.Version20;
                request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                request.Content = new LengthlessContent();
                Assert.Null(request.Content.Headers.ContentLength);
                Assert.False(request.Headers.Contains("Transfer-Encoding"));
                break;
        }
        return request;
    }

    private static string CreateToken(SecurityKey key)
    {
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "provider-draft-caller"), new Claim("scope", "api.read projects.admin")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class LengthlessContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes("{}")).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class DraftOwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ObservedChunkedHandler(Action observed) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            Assert.True(response.Headers.TransferEncodingChunked);
            Assert.Null(response.Content.Headers.ContentLength);
            observed();
            return response;
        }
    }
}

[Collection("IdentityBrokerPostgres")]
public sealed partial class GatewayModelProviderDraftPostgresTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task GatewayRawReferencePinSurvivesProxyAndRejectsAnotherProjectsDatabase()
    {
        var firstConnection = await postgres.CreateMigratedDatabaseAsync();
        var secondConnection = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        await using var first = await ProjectsConfigResourceServer.StartAsync(
            firstConnection, signingKey, enableProviderReferences: true);
        await using var second = await ProjectsConfigResourceServer.StartAsync(
            secondConnection, signingKey, enableProviderReferences: true);
        var tenant = "pin-tenant-" + Guid.NewGuid().ToString("N");
        var actor = "pin-actor-" + Guid.NewGuid().ToString("N");
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        async Task<(DbContextOptions<ProjectsConfigDbContext> Options, string Id, Guid GrantId, long Revision)> PrepareAsync(
            ProjectsConfigResourceServer owner)
        {
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(owner.PrivilegedFixtureDataSource, config => config.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            var membership = await authority.GrantMembershipAsync(issuer, actor, tenant, "fixture");
            var grant = await authority.AssignRoleAsync(membership.MembershipId, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
            await using var connection = await owner.PrivilegedFixtureDataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection);
            return (options, ((Guid)(await read.ExecuteScalarAsync())!).ToString("D"), grant.AssignmentId, grant.Revision);
        }
        var a = await PrepareAsync(first);
        var b = await PrepareAsync(second);
        Assert.NotEqual(a.Id, b.Id);
        var bearer = CreateToken(signingKey, actor, "api.read projects.admin");
        await using var gatewayA = await GatewayResourceServer.StartAsync(
            signingKey, first.CreateHandler, (_, _, _) => throw new InvalidOperationException());
        await using var gatewayB = await GatewayResourceServer.StartAsync(
            signingKey, second.CreateHandler, (_, _, _) => throw new InvalidOperationException());
        string readTarget;
        using (var contextRequest = OwnerRequest(
            HttpMethod.Get, "/api/v1/authorization/target-context", bearer, tenant))
        using (var contextResponse = await gatewayA.Client.SendAsync(contextRequest))
        {
            Assert.Equal(HttpStatusCode.OK, contextResponse.StatusCode);
            Assert.True(contextResponse.Headers.CacheControl?.NoStore);
            using var context = JsonDocument.Parse(await contextResponse.Content.ReadAsStringAsync());
            readTarget = context.RootElement.GetProperty("installationId").GetString()!;
            Assert.Equal(a.Id, readTarget);
        }
        await using (var db = new ProjectsConfigDbContext(a.Options))
        {
            Assert.Equal(0, await db.ModelProviderDrafts.CountAsync());
            Assert.Equal(0, await db.ModelProviderReferenceReservations.CountAsync());
        }
        var operation = Guid.NewGuid();
        var id = "byok-" + operation.ToString("N");
        var reference = "Gateway-" + operation.ToString("N");
        var body = SaveBody(operation, 0, "Gateway pin");
        string Pinned(string target) => body[..^1] +
            $",\"reference\":\"{reference}\",\"targetInstallationId\":\"{target}\"}}";
        async Task AssertReplyAsync(
            GatewayResourceServer gateway, HttpMethod method, string path, string json,
            HttpStatusCode expected, string code)
        {
            using var request = OwnerRequest(method, path, bearer, tenant, json);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(expected, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, result.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(id, result.RootElement.GetRawText());
        }
        const string platformPath = "/api/v1/platform/byok-providers";
        foreach (var suffix in new[]
        {
            $",\"reference\":\"{reference}\"}}",
            $",\"reference\":\"{reference}\",\"targetInstallationId\":null}}",
            $",\"reference\":\"{reference}\",\"targetInstallationId\":\"not-a-uuid\"}}",
            $",\"reference\":\"{reference}\",\"targetInstallationId\":\"00000000-0000-0000-0000-000000000000\"}}",
            $",\"reference\":\"{reference}\",\"targetInstallationId\":42}}",
            $",\"reference\":\"{reference}\",\"targetInstallationId\":\"AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA\"}}",
            $",\"reference\":\"{reference}\",\"targetInstallationId\":\"{a.Id}\",\"targetInstallationId\":\"{a.Id}\"}}",
            $",\"targetInstallationId\":\"{a.Id}\"}}",
        })
            await AssertReplyAsync(gatewayA, HttpMethod.Post, platformPath, body[..^1] + suffix,
                HttpStatusCode.BadRequest, "model_provider_draft_invalid_request");
        await AssertReplyAsync(gatewayB, HttpMethod.Post, platformPath, Pinned(readTarget),
            HttpStatusCode.Conflict, "target_installation_mismatch");
        await using (var db = new ProjectsConfigDbContext(a.Options))
        {
            Assert.False(await db.ModelProviderDrafts.AnyAsync(row => row.ProviderId == id));
            Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == operation));
            Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(row => row.OperationId == operation));
        }
        await using (var db = new ProjectsConfigDbContext(b.Options))
        {
            Assert.False(await db.ModelProviderDrafts.AnyAsync(row => row.ProviderId == id));
            Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == operation));
            Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(row => row.OperationId == operation));
        }
        using (var create = OwnerRequest(HttpMethod.Post, platformPath, bearer, tenant, Pinned(readTarget)))
        using (var response = await gatewayA.Client.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(id, result.RootElement.GetProperty("id").GetString());
            Assert.Equal(reference, result.RootElement.GetProperty("reference").GetString());
        }
        using (var create = OwnerRequest(HttpMethod.Post, platformPath, bearer, tenant, Pinned(b.Id)))
        using (var response = await gatewayB.Client.SendAsync(create))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertReplyAsync(gatewayB, HttpMethod.Post, platformPath, Pinned(a.Id),
            HttpStatusCode.Conflict, "target_installation_mismatch");
        await using (var disabled = await ProjectsConfigResourceServer.StartAsync(firstConnection, signingKey))
        await using (var disabledGateway = await GatewayResourceServer.StartAsync(
            signingKey, disabled.CreateHandler, (_, _, _) => throw new InvalidOperationException()))
        {
            using (var replay = OwnerRequest(HttpMethod.Post, platformPath, bearer, tenant, Pinned(a.Id)))
            using (var response = await disabledGateway.Client.SendAsync(replay))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(id, result.RootElement.GetProperty("id").GetString());
                Assert.Equal(1, result.RootElement.GetProperty("revision").GetInt64());
            }
            var freshOperation = Guid.NewGuid();
            var freshBody = SaveBody(freshOperation, 0, "Disabled")[..^1] +
                $",\"reference\":\"Disabled-{freshOperation:N}\",\"targetInstallationId\":\"{a.Id}\"}}";
            await AssertReplyAsync(disabledGateway, HttpMethod.Post, platformPath, freshBody,
                HttpStatusCode.ServiceUnavailable, "model_provider_draft_unavailable");
        }
        await AssertReplyAsync(gatewayA, HttpMethod.Put, platformPath + "/" + id,
            SaveBody(Guid.NewGuid(), 1, "update")[..^1] + $",\"targetInstallationId\":\"{a.Id}\"}}",
            HttpStatusCode.BadRequest, "model_provider_draft_invalid_request");
        var boundBearer = CreateToken(signingKey, actor, "api.read projects.admin", projectId: "project-a");
        using (var projectRequest = OwnerRequest(HttpMethod.Post,
            "/api/v1/projects/project-a/byok-providers", boundBearer, tenant, Pinned(a.Id)))
        using (var response = await gatewayA.Client.SendAsync(projectRequest))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using (var db = new ProjectsConfigDbContext(a.Options))
        {
            var reservation = await db.ModelProviderReferenceReservations.AsNoTracking()
                .SingleAsync(row => row.OperationId == operation);
            Assert.Equal(Guid.Parse(a.Id), reservation.InstallationId);
            Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(row => row.OperationId == operation));
        }
        await using (var db = new ProjectsConfigDbContext(b.Options))
            Assert.Equal(Guid.Parse(b.Id), (await db.ModelProviderReferenceReservations.AsNoTracking()
                .SingleAsync(row => row.OperationId == operation)).InstallationId);
        var authorityA = new ProjectsConfigPrivilegedAuthorityStore(a.Options, TimeProvider.System);
        var otherActor = "other-pin-" + Guid.NewGuid().ToString("N");
        var otherMembership = await authorityA.GrantMembershipAsync(issuer, otherActor, tenant, "fixture");
        await authorityA.AssignRoleAsync(otherMembership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        using (var other = OwnerRequest(HttpMethod.Post, platformPath,
            CreateToken(signingKey, otherActor, "api.read projects.admin"), tenant, Pinned(a.Id)))
        using (var response = await gatewayA.Client.SendAsync(other))
        {
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("model_provider_draft_conflict",
                JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
        }
        await authorityA.RevokeRoleAssignmentAsync(a.GrantId, a.Revision, "fixture");
        await AssertReplyAsync(gatewayA, HttpMethod.Post, platformPath, Pinned(b.Id),
            HttpStatusCode.Forbidden, "forbidden");
        await using (var db = new ProjectsConfigDbContext(a.Options))
            Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(row => row.OperationId == operation));
    }

    [Fact]
    public async Task GatewayModelProviderDraftPostgresTestsCreatesAndReadsTheCommittedRevision()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        await using var projects = await ProjectsConfigResourceServer.StartAsync(connectionString, signingKey);
        var tenantId = "provider-draft-tenant-" + Guid.NewGuid().ToString("N");
        var subject = "provider-draft-admin-" + Guid.NewGuid().ToString("N");
        var dbOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, options => options.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(dbOptions, TimeProvider.System);
        var membership = await authorityStore.GrantMembershipAsync(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri, subject, tenantId, "fixture");
        await authorityStore.AssignRoleAsync(
            membership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        var bearer = CreateToken(signingKey, subject, "api.read projects.admin");
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey, projects.CreateHandler,
            (_, _, _) => throw new InvalidOperationException("Draft metadata must call Projects."));

        using var create = OwnerRequest(
            HttpMethod.Post, "/api/v1/platform/byok-providers", bearer, tenantId,
            SaveBody(Guid.NewGuid(), 0, "Hosted model"));
        using var created = await gateway.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.True(created.Headers.CacheControl?.NoStore);
        using var saved = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        using var openApiResponse = await gateway.Client.GetAsync("/openapi/v1.json");
        using var openApi = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync());
        var schemas = openApi.RootElement.GetProperty("components").GetProperty("schemas");
        var schema = schemas.GetProperty("ModelProviderDraft");
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("rate_card_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, saved.RootElement.GetProperty("rate_card_version").ValueKind);
        Assert.True(GatewayBacklogOpenApiTests.MatchesSchema(saved.RootElement, schema, schemas));
        var mutated = System.Text.Json.Nodes.JsonNode.Parse(schema.GetRawText())!;
        mutated["properties"]!.AsObject().Remove("rate_card_id");
        using var brokenSchema = JsonDocument.Parse(mutated.ToJsonString());
        Assert.False(GatewayBacklogOpenApiTests.MatchesSchema(saved.RootElement, brokenSchema.RootElement, schemas));
        var providerId = saved.RootElement.GetProperty("id").GetString();
        Assert.StartsWith("byok-", providerId);
        Assert.Equal(1, saved.RootElement.GetProperty("revision").GetInt64());

        using var read = OwnerRequest(
            HttpMethod.Get, "/api/v1/platform/byok-providers?pageSize=1", bearer, tenantId);
        using var listed = await gateway.Client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.True(listed.Headers.CacheControl?.NoStore);
        using var page = JsonDocument.Parse(await listed.Content.ReadAsStringAsync());
        var row = Assert.Single(page.RootElement.GetProperty("providers").EnumerateArray());
        Assert.Equal(providerId, row.GetProperty("id").GetString());
        Assert.Equal(1, row.GetProperty("revision").GetInt64());
        Assert.Equal("Draft", row.GetProperty("state").GetString());
        Assert.False(row.GetProperty("hasCredential").GetBoolean());
    }

    [Fact]
    public async Task RestrictedOwnerDraftRoutesUseRealBearerAuthAndRecheckAuthority()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var logs = new DraftCapturedLogs();
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, signingKey, loggerProvider: logs);
        var tenantId = "draft-owner-tenant-" + Guid.NewGuid().ToString("N");
        var suffix = Guid.NewGuid().ToString("N");
        var adminSubject = "draft-owner-platform-" + suffix;
        var revokedSubject = "draft-owner-revoked-" + suffix;
        var ownerSubject = "draft-owner-project-" + suffix;
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config => config.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var platformMembership = await authorityStore.GrantMembershipAsync(issuer, adminSubject, tenantId, "fixture");
        var platformGrant = await authorityStore.AssignRoleAsync(
            platformMembership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        var revokedMembership = await authorityStore.GrantMembershipAsync(issuer, revokedSubject, tenantId, "fixture");
        await authorityStore.AssignRoleAsync(
            revokedMembership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        var projectMembership = await authorityStore.GrantMembershipAsync(issuer, ownerSubject, tenantId, "fixture");
        await authorityStore.AssignRoleAsync(
            projectMembership.MembershipId, ProjectAuthorityResourceType.Tenant, tenantId,
            ProjectAuthorityRole.TenantAdmin, "fixture");

        var tenantAdminToken = CreateToken(signingKey, ownerSubject, "api.read projects.admin");
        string projectId;
        using (var createProject = OwnerRequest(
            HttpMethod.Post, "/api/projects/", tenantAdminToken, tenantId, """{"name":"Draft project"}"""))
        using (var response = await projects.Client.SendAsync(createProject))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            projectId = document.RootElement.GetProperty("projectId").GetString()!;
        }
        var projectToken = CreateToken(
            signingKey, ownerSubject, "api.read projects.admin", projectId: projectId);
        var platformToken = CreateToken(signingKey, adminSubject, "api.read projects.admin");
        var platformRoute = "/api/platform/byok-providers";
        var projectRoute = $"/api/projects/{projectId}/byok-providers";

        var platformOperation = Guid.NewGuid();
        var platformBody = SaveBody(platformOperation, 0, "Platform model");
        using (var create = OwnerRequest(HttpMethod.Post, platformRoute, platformToken, tenantId, platformBody))
        using (var response = await projects.Client.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("byok-" + platformOperation.ToString("N"),
                saved.RootElement.GetProperty("id").GetString());
            Assert.Equal(1, saved.RootElement.GetProperty("revision").GetInt64());
        }
        using (var read = OwnerRequest(
            HttpMethod.Get, platformRoute + "/byok-" + platformOperation.ToString("N"), platformToken, tenantId))
        using (var response = await projects.Client.SendAsync(read))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var list = OwnerRequest(HttpMethod.Get, platformRoute + "?pageSize=10", platformToken, tenantId))
        using (var response = await projects.Client.SendAsync(list))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var update = OwnerRequest(
            HttpMethod.Put,
            platformRoute + "/byok-" + platformOperation.ToString("N"),
            platformToken, tenantId, SaveBody(Guid.NewGuid(), 1, "Platform update")))
        using (var response = await projects.Client.SendAsync(update))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, saved.RootElement.GetProperty("revision").GetInt64());
        }

        var projectOperation = Guid.NewGuid();
        var projectBody = SaveBody(projectOperation, 0, "Project model");
        using (var create = OwnerRequest(HttpMethod.Post, projectRoute, projectToken, tenantId, projectBody))
        using (var response = await projects.Client.SendAsync(create))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var saved = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("project", saved.RootElement.GetProperty("scope").GetString());
        }
        var projectProviderId = "byok-" + projectOperation.ToString("N");
        using (var read = OwnerRequest(
            HttpMethod.Get, projectRoute + "/" + projectProviderId, projectToken, tenantId))
        using (var response = await projects.Client.SendAsync(read))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var list = OwnerRequest(HttpMethod.Get, projectRoute + "?pageSize=10", projectToken, tenantId))
        using (var response = await projects.Client.SendAsync(list))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(projectProviderId, Assert.Single(page.RootElement.GetProperty("providers")
                .EnumerateArray()).GetProperty("id").GetString());
        }
        using (var update = OwnerRequest(
            HttpMethod.Put, projectRoute + "/" + projectProviderId, projectToken, tenantId,
            SaveBody(Guid.NewGuid(), 1, "Project update")))
        using (var response = await projects.Client.SendAsync(update))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var wrongScope = OwnerRequest(
            HttpMethod.Get, projectRoute + "/byok-" + platformOperation.ToString("N"), projectToken, tenantId))
        using (var response = await projects.Client.SendAsync(wrongScope))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var missing = OwnerRequest(
            HttpMethod.Get, projectRoute + "/not-found", projectToken, tenantId))
        using (var response = await projects.Client.SendAsync(missing))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var missingProjectId = "missing-" + suffix[..16];
        var platformBoundMissingToken = CreateToken(
            signingKey, adminSubject, "api.read projects.admin", projectId: missingProjectId);
        using (var missingList = OwnerRequest(
            HttpMethod.Get, $"/api/projects/{missingProjectId}/byok-providers",
            platformBoundMissingToken, tenantId))
        using (var response = await projects.Client.SendAsync(missingList))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var missingSave = OwnerRequest(
            HttpMethod.Post, $"/api/projects/{missingProjectId}/byok-providers",
            platformBoundMissingToken, tenantId, SaveBody(Guid.NewGuid(), 0, "Missing project")))
        using (var response = await projects.Client.SendAsync(missingSave))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using (var missingUpdate = OwnerRequest(
            HttpMethod.Put, $"/api/projects/{missingProjectId}/byok-providers/provider-a",
            platformBoundMissingToken, tenantId, SaveBody(Guid.NewGuid(), 1, "Missing project")))
        using (var response = await projects.Client.SendAsync(missingUpdate))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var beforeDeniedRequests = await DraftRevisionCountAsync(options);
        var deniedRequests = new (HttpRequestMessage Request, HttpStatusCode Status)[]
        {
            (OwnerRequest(HttpMethod.Get, platformRoute, CreateToken(
                signingKey, adminSubject, "api.read"), tenantId), HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, platformRoute, CreateToken(
                signingKey, adminSubject, "api.read projects.admin", projectId: projectId), tenantId),
                HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, platformRoute, platformToken, "foreign-tenant"),
                HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, platformRoute, CreateToken(
                signingKey, adminSubject, "api.read projects.admin", purpose: "test-purpose"), tenantId),
                HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, projectRoute, CreateToken(
                signingKey, ownerSubject, "api.read projects.admin"), tenantId), HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, projectRoute, CreateToken(
                signingKey, ownerSubject, "api.read projects.admin", projectId: "other-project"), tenantId),
                HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, projectRoute, CreateToken(
                signingKey, ownerSubject, "api.read projects.admin", projectId: projectId, runId: "run-a"), tenantId),
                HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, projectRoute, CreateToken(
                signingKey, ownerSubject, "api.read projects.admin", projectId: projectId,
                purpose: "test-purpose"), tenantId), HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, projectRoute, CreateToken(
                signingKey, ownerSubject, "api.read", projectId: projectId), tenantId),
                HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, projectRoute, projectToken, "foreign-tenant"), HttpStatusCode.Forbidden),
            (OwnerRequest(HttpMethod.Get, platformRoute, CreateToken(
                signingKey, adminSubject, "api.read projects.admin", audience: "https://wrong.test"), tenantId),
                HttpStatusCode.Unauthorized),
            (OwnerRequest(HttpMethod.Get, projectRoute, CreateToken(
                signingKey, ownerSubject, "api.read projects.admin", audience: "https://wrong.test",
                projectId: projectId), tenantId), HttpStatusCode.Unauthorized),
        };
        foreach (var (request, expectedStatus) in deniedRequests)
        {
            using (request)
            using (var response = await projects.Client.SendAsync(request))
            {
                Assert.Equal(expectedStatus, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
        }
        Assert.Equal(beforeDeniedRequests, await DraftRevisionCountAsync(options));

        var revokedPlatformOperation = Guid.NewGuid();
        var revokedPlatformBody = SaveBody(revokedPlatformOperation, 0, "Revoked replay");
        using (var create = OwnerRequest(
            HttpMethod.Post, platformRoute, platformToken, tenantId, revokedPlatformBody))
        using (var response = await projects.Client.SendAsync(create))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await authorityStore.RevokeRoleAssignmentAsync(
            platformGrant.AssignmentId, platformGrant.Revision, adminSubject);
        using (var replay = OwnerRequest(
            HttpMethod.Post, platformRoute, platformToken, tenantId, revokedPlatformBody))
        using (var response = await projects.Client.SendAsync(replay))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var revokedOperation = Guid.NewGuid();
        var revokedBody = SaveBody(revokedOperation, 0, "Membership replay");
        var revokedToken = CreateToken(signingKey, revokedSubject, "api.read projects.admin");
        using (var create = OwnerRequest(HttpMethod.Post, platformRoute, revokedToken, tenantId, revokedBody))
        using (var response = await projects.Client.SendAsync(create))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await authorityStore.RevokeMembershipAsync(
            revokedMembership.MembershipId, revokedMembership.Revision, "fixture");
        using (var replay = OwnerRequest(HttpMethod.Post, platformRoute, revokedToken, tenantId, revokedBody))
        using (var response = await projects.Client.SendAsync(replay))
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RestrictedOwnerRejectsAndRedactsDraftHttpFailuresForBothScopes()
    {
        var connectionString = await postgres.CreateMigratedDatabaseAsync();
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var logs = new DraftCapturedLogs();
        await using var projects = await ProjectsConfigResourceServer.StartAsync(
            connectionString, signingKey, loggerProvider: logs);
        var tenantId = "draft-errors-tenant-" + Guid.NewGuid().ToString("N");
        var suffix = Guid.NewGuid().ToString("N");
        var issuer = new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(projects.PrivilegedFixtureDataSource, config => config.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
        var platformSubject = "draft-errors-platform-" + suffix;
        var platformMembership = await authorityStore.GrantMembershipAsync(
            issuer, platformSubject, tenantId, "fixture");
        await authorityStore.AssignRoleAsync(
            platformMembership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        var ownerSubject = "draft-errors-owner-" + suffix;
        var ownerMembership = await authorityStore.GrantMembershipAsync(issuer, ownerSubject, tenantId, "fixture");
        await authorityStore.AssignRoleAsync(
            ownerMembership.MembershipId, ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin, "fixture");
        var ownerToken = CreateToken(signingKey, ownerSubject, "api.read projects.admin");
        string projectId;
        using (var createProject = OwnerRequest(
            HttpMethod.Post, "/api/projects/", ownerToken, tenantId, """{"name":"Draft error project"}"""))
        using (var response = await projects.Client.SendAsync(createProject))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            projectId = document.RootElement.GetProperty("projectId").GetString()!;
        }
        var routes = new[]
        {
            (Path: "/api/platform/byok-providers", Token: CreateToken(
                signingKey, platformSubject, "api.read projects.admin")),
            (Path: $"/api/projects/{projectId}/byok-providers", Token: CreateToken(
                signingKey, ownerSubject, "api.read projects.admin", projectId: projectId)),
        };
        var disclosureSentinels = new List<string>();
        var before = await DraftRevisionCountAsync(options);

        foreach (var route in routes)
        {
            foreach (var bearer in new string?[] { null, "invalid-bearer" })
            {
                var sentinel = "draft-unauthorized-" + Guid.NewGuid().ToString("N");
                var rawRequest = "raw-request-" + Guid.NewGuid().ToString("N");
                disclosureSentinels.Add(sentinel);
                var requestBody = $$"""{"name":"{{sentinel}}","raw":"{{rawRequest}}"}""";
                using var request = new HttpRequestMessage(HttpMethod.Post, route.Path)
                {
                    Content = new StringContent(
                        requestBody,
                        Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-Agentweaver-Tenant", tenantId);
                if (bearer is not null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                using var response = await projects.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString());
                AssertDraftErrorRedacted(await response.Content.ReadAsStringAsync(),
                    sentinel, rawRequest, requestBody);
            }

            var malformedSentinel = "draft-malformed-json-" + Guid.NewGuid().ToString("N");
            var malformedRawRequest = "raw-request-" + Guid.NewGuid().ToString("N");
            disclosureSentinels.Add(malformedSentinel);
            var secretSentinel = "draft-secret-" + Guid.NewGuid().ToString("N");
            var secretRawRequest = "raw-request-" + Guid.NewGuid().ToString("N");
            disclosureSentinels.Add(secretSentinel);
            var unsupportedSentinel = "draft-unsupported-media-" + Guid.NewGuid().ToString("N");
            var unsupportedRawRequest = "raw-request-" + Guid.NewGuid().ToString("N");
            disclosureSentinels.Add(unsupportedSentinel);
            var failureCases = new[]
            {
                (Body: "{\"sentinel\":\"" + malformedSentinel + "\",\"raw\":\"" + malformedRawRequest + "\"",
                    Sentinel: malformedSentinel, RawRequest: malformedRawRequest,
                    ContentType: "application/json", Status: HttpStatusCode.BadRequest,
                    Code: "model_provider_draft_invalid_request"),
                (Body: $$"""{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":0,"name":"x","name":"{{secretSentinel}}","raw":"{{secretRawRequest}}","type":"openai","base_url":"https://api.openai.com/v1","model":"m"}""",
                    Sentinel: secretSentinel, RawRequest: secretRawRequest,
                    ContentType: "application/json", Status: HttpStatusCode.BadRequest,
                    Code: "model_provider_draft_invalid_request"),
                (Body: $$"""{"operationId":"ad451e41-9b48-412f-99dc-ad6a6cd4080c","expectedRevision":0,"name":"{{secretSentinel}}","raw":"{{secretRawRequest}}","type":"openai","base_url":"https://api.openai.com/v1","model":"m","vault_uri":"{{secretSentinel}}"}""",
                    Sentinel: secretSentinel, RawRequest: secretRawRequest,
                    ContentType: "application/json", Status: HttpStatusCode.BadRequest,
                    Code: "model_provider_draft_invalid_request"),
                (Body: $$"""{"sentinel":"{{unsupportedSentinel}}","raw":"{{unsupportedRawRequest}}"}""",
                    Sentinel: unsupportedSentinel, RawRequest: unsupportedRawRequest,
                    ContentType: "text/plain", Status: HttpStatusCode.UnsupportedMediaType,
                    Code: "model_provider_draft_unsupported_media_type"),
            };
            foreach (var item in failureCases)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, route.Path)
                {
                    Content = new StringContent(item.Body, Encoding.UTF8, item.ContentType),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", route.Token);
                request.Headers.Add("X-Agentweaver-Tenant", tenantId);
                using var response = await projects.Client.SendAsync(request);
                Assert.Equal(item.Status, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Equal(item.Code, JsonDocument.Parse(responseBody).RootElement.GetProperty("code").GetString());
                AssertDraftErrorRedacted(responseBody, item.Sentinel, item.RawRequest, item.Body);
            }

            var path = route.Path;
            foreach (var bodyKind in new[] { "fixed", "chunked", "headerless" })
            {
                var sentinel = $"draft-oversized-{bodyKind}-" + Guid.NewGuid().ToString("N");
                disclosureSentinels.Add(sentinel);
                using var request = OversizedOwnerSave(path, route.Token, tenantId, bodyKind, sentinel);
                using var response = await projects.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Equal("request_body_too_large",
                    JsonDocument.Parse(responseBody)
                        .RootElement.GetProperty("code").GetString());
                AssertDraftErrorRedacted(responseBody, sentinel);
            }
        }

        Assert.Equal(before, await DraftRevisionCountAsync(options));
        await using (var transportLimitedOwner = await ProjectsConfigResourceServer.StartAsync(
            connectionString, signingKey, useKestrel: true, loggerProvider: logs))
        {
            foreach (var route in routes)
            {
                var sentinel = "draft-kestrel-oversized-" + Guid.NewGuid().ToString("N");
                disclosureSentinels.Add(sentinel);
                using var request = OversizedOwnerSave(
                    route.Path, route.Token, tenantId, "chunked", sentinel);
                using var response = await transportLimitedOwner.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                var responseBody = await response.Content.ReadAsStringAsync();
                Assert.Equal("request_body_too_large",
                    JsonDocument.Parse(responseBody).RootElement.GetProperty("code").GetString());
                AssertDraftErrorRedacted(responseBody, sentinel);
            }
        }
        Assert.Equal(before, await DraftRevisionCountAsync(options));
        foreach (var sentinel in disclosureSentinels)
            Assert.DoesNotContain(sentinel, logs.Text);
        Assert.DoesNotContain("raw-request-", logs.Text);
        Assert.DoesNotContain("vault_uri", logs.Text);
        Assert.DoesNotContain("\"operationId\"", logs.Text);
        Assert.DoesNotContain("JsonException", logs.Text);
    }

    private static async Task<int> DraftRevisionCountAsync(DbContextOptions<ProjectsConfigDbContext> options)
    {
        await using var db = new ProjectsConfigDbContext(options);
        return await db.ModelProviderDraftRevisions.CountAsync();
    }

    private static HttpRequestMessage OversizedOwnerSave(
        string path,
        string bearer,
        string tenantId,
        string bodyKind,
        string sentinel)
    {
        var body = sentinel + new string('x', 32 * 1024 + 1 - sentinel.Length);
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", tenantId);
        if (bodyKind == "fixed")
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        else
        {
            request.Content = new LengthlessContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Version = bodyKind == "chunked" ? HttpVersion.Version11 : HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            Assert.Null(request.Content.Headers.ContentLength);
            Assert.False(request.Headers.Contains("Transfer-Encoding"));
        }
        return request;
    }

    private static void AssertDraftErrorRedacted(string responseBody, params string[] protectedValues)
    {
        foreach (var value in protectedValues)
            Assert.DoesNotContain(value, responseBody);
        Assert.DoesNotContain("raw-request-", responseBody);
        Assert.DoesNotContain("JsonException", responseBody);
        Assert.DoesNotContain("BytePositionInLine", responseBody);
        Assert.DoesNotContain("LineNumber", responseBody);
    }

    private static string CreateToken(
        SecurityKey key,
        string subject,
        string scopes,
        string audience = "https://api.test",
        string? projectId = null,
        string? runId = null,
        string? purpose = null,
        string? tenantAssertion = null)
    {
        var claims = new List<Claim>
        {
            new("sub", subject),
            new("scope", scopes),
        };
        if (projectId is not null)
            claims.Add(new Claim("project_id", projectId));
        if (runId is not null)
            claims.Add(new Claim("run_id", runId));
        if (purpose is not null)
            claims.Add(new Claim("purpose", purpose));
        if (tenantAssertion is not null)
            claims.Add(new Claim("tenant_id", tenantAssertion));
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpRequestMessage OwnerRequest(
        HttpMethod method,
        string path,
        string bearer,
        string tenantId,
        string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", tenantId);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static string SaveBody(Guid operationId, long revision, string name) =>
        $$"""{"operationId":"{{operationId:D}}","expectedRevision":{{revision}},"name":"{{name}}","type":"openai","base_url":"https://api.openai.com/v1","model":"test-model"}""";

    private sealed class LengthlessContent(string value) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(Encoding.UTF8.GetBytes(value)).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class DraftCapturedLogs : ILoggerProvider
    {
        private readonly StringBuilder _text = new();
        public string Text => _text.ToString();
        public ILogger CreateLogger(string categoryName) => new CapturedLogger(_text);
        public void Dispose() { }

        private sealed class CapturedLogger(StringBuilder text) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (text)
                {
                    text.AppendLine(formatter(state, exception));
                    if (exception is not null)
                        text.AppendLine(exception.ToString());
                }
            }
        }
    }
}
