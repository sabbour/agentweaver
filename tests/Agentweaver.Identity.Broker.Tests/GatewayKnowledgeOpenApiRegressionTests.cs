using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayKnowledgeOpenApiRegressionTests
{
    private static readonly string[] KnowledgeWriteOperationIds =
    [
        "createKnowledgeRecord",
        "updateKnowledgeRecord",
        "restoreKnowledgeRecord",
        "approveKnowledgeDecision",
        "importKnowledgeRecords",
        "promoteKnowledgeProposal",
        "rejectKnowledgeProposal",
    ];

    [Fact]
    public async Task ServedOpenApiRequiresIdempotencyKeyOnlyForKnowledgeWrites()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        foreach (var operationId in KnowledgeWriteOperationIds)
        {
            var operation = FindOperation(paths, operationId);
            var header = Assert.Single(
                operation.GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.Equal("header", header.GetProperty("in").GetString());
            Assert.True(header.GetProperty("required").GetBoolean());

            var schema = header.GetProperty("schema");
            Assert.Equal("string", schema.GetProperty("type").GetString());
            Assert.Equal(1, schema.GetProperty("minLength").GetInt32());
            Assert.Equal(128, schema.GetProperty("maxLength").GetInt32());
            Assert.Equal("^[A-Za-z0-9._:-]+$", schema.GetProperty("pattern").GetString());
        }

        var operationsWithIdempotencyKey = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths.EnumerateObject())
        foreach (var method in path.Value.EnumerateObject())
        {
            var operation = method.Value;
            if (HasIdempotencyKeyHeader(operation))
                operationsWithIdempotencyKey.Add(operation.GetProperty("operationId").GetString()!);
        }

        Assert.Equal(
            KnowledgeWriteOperationIds.OrderBy(id => id, StringComparer.Ordinal),
            operationsWithIdempotencyKey);
    }

    [Fact]
    public async Task KnowledgeLifecycleRoutesExposeStrictTransferContractsAndRequestBounds()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var paths = root.GetProperty("paths");
        var import = paths.GetProperty(
            "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/import")
            .GetProperty("post");
        Assert.Equal("importKnowledgeRecords", import.GetProperty("operationId").GetString());
        Assert.Equal("Knowledge", import.GetProperty("x-agentweaver-owner").GetString());
        Assert.Equal(1_048_576, import.GetProperty("x-agentweaver-max-request-body-bytes").GetInt32());
        Assert.Equal(1_048_576L, gateway.GetRequestSizeLimit(
            "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/import")?.MaxRequestBodySize);
        var importBody = import.GetProperty("requestBody");
        Assert.True(importBody.GetProperty("required").GetBoolean());
        Assert.Equal(
            "#/components/schemas/KnowledgeRecordTransferBundle",
            importBody.GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());

        var export = paths.GetProperty(
            "/api/v1/projects/{projectId}/runs/{runId}/agents/{agentId}/records/export")
            .GetProperty("get");
        Assert.Equal("exportKnowledgeRecords", export.GetProperty("operationId").GetString());
        Assert.Equal(
            "#/components/schemas/KnowledgeRecordTransferBundle",
            export.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.False(HasIdempotencyKeyHeader(export));

        var schemas = root.GetProperty("components").GetProperty("schemas");
        var decisionRecord = schemas.GetProperty("KnowledgeRecord");
        Assert.Contains("superseded",
            decisionRecord.GetProperty("properties").GetProperty("state").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.True(decisionRecord.GetProperty("additionalProperties").ValueKind == JsonValueKind.False);
        Assert.True(decisionRecord.GetProperty("properties").TryGetProperty("supersededByRecordId", out _));

        var revision = schemas.GetProperty("KnowledgeRecordRevision");
        var revisionProperties = revision.GetProperty("properties");
        Assert.Equal(JsonValueKind.Array, revisionProperties.GetProperty("reason").GetProperty("anyOf").ValueKind);
        Assert.True(revisionProperties.TryGetProperty("supersededByRecordId", out _));
        Assert.True(revisionProperties.TryGetProperty("sourceRunId", out _));
        Assert.True(revisionProperties.TryGetProperty("sourceSessionId", out _));
        Assert.True(revisionProperties.TryGetProperty("actorFingerprint", out _));
        Assert.True(revisionProperties.TryGetProperty("changeKind", out _));

        var bundle = schemas.GetProperty("KnowledgeRecordTransferBundle");
        Assert.Equal("agentweaver.knowledge-transfer.v1",
            bundle.GetProperty("properties").GetProperty("format").GetProperty("const").GetString());
        Assert.Equal(1, bundle.GetProperty("properties").GetProperty("schemaVersion")
            .GetProperty("const").GetInt32());
        Assert.Equal(25, bundle.GetProperty("properties").GetProperty("records")
            .GetProperty("maxItems").GetInt32());
        Assert.False(bundle.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schemas.GetProperty("KnowledgeRecordImportResult").GetProperty("properties")
            .TryGetProperty("isDuplicate", out _));
    }

    [Fact]
    public async Task SkillsMarketplaceOpenApiRequiresOnlyTheOwnerRevisionQueryParameters()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException(
                "The OpenAPI endpoint must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");

        AssertRevisionQuery(
            FindOperation(paths, "removeMarketplaceSource"),
            "expectedRevision",
            required: true);
        var browse = FindOperation(paths, "browseMarketplaceSource");
        AssertRevisionQuery(browse, "expectedSourceRevision", required: true);
        foreach (var optionalName in new[] { "query", "page", "pageSize" })
        {
            var parameter = Assert.Single(
                browse.GetProperty("parameters").EnumerateArray(),
                item => item.GetProperty("name").GetString() == optionalName);
            Assert.False(parameter.GetProperty("required").GetBoolean());
        }
        var existingProjectQuery = Assert.Single(
            FindOperation(paths, "getProject").GetProperty("parameters").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "runId");
        Assert.False(existingProjectQuery.GetProperty("required").GetBoolean());

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schemaName in new[]
        {
            "CreateMarketplaceSourceRequest",
            "UpdateMarketplaceSourceRequest",
            "SkillContentCandidateRequest",
            "ImportSkillContentRequest",
            "UpdateSkillAssignmentRequest",
        })
        {
            Assert.False(schemas.GetProperty(schemaName).GetProperty("additionalProperties").GetBoolean());
        }

        Assert.Equal(3L * 1024 * 1024, gateway.GetRequestSizeLimit("/api/v1/skills/preview")?.MaxRequestBodySize);
        Assert.Equal(
            3L * 1024 * 1024,
            gateway.GetRequestSizeLimit("/api/v1/projects/{projectId}/skills/import")?.MaxRequestBodySize);
        foreach (var operationId in new[] { "previewSkillContent", "importProjectSkill" })
        {
            Assert.Equal(
                3 * 1024 * 1024,
                FindOperation(paths, operationId)
                    .GetProperty("x-agentweaver-max-request-body-bytes").GetInt32());
        }
    }

    [Fact]
    public async Task SkillsMarketplaceRoutesForwardExactOwnerPathsBodiesBearerAndTenant()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var now = DateTime.UtcNow;
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "skills-marketplace-test-actor"), new Claim("scope", "api.read")],
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        var bearer = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
        var forwarded = new List<(string Method, string PathAndQuery, string? Authorization, string? Tenant, string Body)>();
        const string ownerResponse = "{\"owner\":\"response\"}";
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new RecordingOwnerHandler(async (request, cancellationToken) =>
            {
                var body = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
                forwarded.Add((
                    request.Method.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    tenantValues?.SingleOrDefault(),
                    body));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(ownerResponse, Encoding.UTF8, "application/json"),
                };
            }),
            (_, _, _) => throw new InvalidOperationException(
                "Skills and marketplace requests must use the Projects owner."));

        var ownerRequests = new (HttpMethod Method, string PublicPath, string OwnerPath, string Body)[]
        {
            (HttpMethod.Get,
                "/api/v1/projects/project-1/skill-marketplaces/sources",
                "/api/projects/project-1/skill-marketplaces/sources",
                string.Empty),
            (HttpMethod.Post,
                "/api/v1/projects/project-1/skill-marketplaces/sources",
                "/api/projects/project-1/skill-marketplaces/sources",
                "{\"repository\":\"example/skills\"}"),
            (HttpMethod.Put,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1",
                "/api/projects/project-1/skill-marketplaces/sources/source-1",
                "{\"expectedRevision\":4,\"repository\":\"example/skills\"}"),
            (HttpMethod.Delete,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1?expectedRevision=4",
                "/api/projects/project-1/skill-marketplaces/sources/source-1?expectedRevision=4",
                string.Empty),
            (HttpMethod.Get,
                "/api/v1/projects/project-1/skill-marketplaces/sources/source-1/browse?expectedSourceRevision=4&query=alpha&page=2&pageSize=5",
                "/api/projects/project-1/skill-marketplaces/sources/source-1/browse?expectedSourceRevision=4&query=alpha&page=2&pageSize=5",
                string.Empty),
            (HttpMethod.Post,
                "/api/v1/skills/preview",
                "/api/skills/preview",
                "{\"candidate\":{\"skillMarkdown\":\"IyBUZXN0\",\"resources\":[]}}"),
            (HttpMethod.Post,
                "/api/v1/projects/project-1/skills/import",
                "/api/projects/project-1/skills/import",
                "{\"idempotencyKey\":\"intent-1\",\"expectedContentDigest\":\"sha256:test\",\"candidate\":{\"skillMarkdown\":\"IyBUZXN0\",\"resources\":[]}}"),
            (HttpMethod.Put,
                "/api/v1/projects/project-1/skills/skill-1/assignment",
                "/api/projects/project-1/skills/skill-1/assignment",
                "{\"expectedProjectConfigurationRevision\":4,\"revision\":1,\"contentDigest\":\"sha256:test\",\"enabled\":true,\"order\":0,\"agentIds\":[\"agent-1\"]}"),
        };

        foreach (var ownerRequest in ownerRequests)
        {
            using var request = new HttpRequestMessage(ownerRequest.Method, ownerRequest.PublicPath);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.TryAddWithoutValidation("X-Agentweaver-Tenant", "tenant-1");
            if (ownerRequest.Body.Length > 0)
                request.Content = new StringContent(ownerRequest.Body, Encoding.UTF8, "application/json");

            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal(ownerResponse, await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        Assert.Equal(ownerRequests.Length, forwarded.Count);
        for (var index = 0; index < ownerRequests.Length; index++)
        {
            Assert.Equal(ownerRequests[index].Method.Method, forwarded[index].Method);
            Assert.Equal(ownerRequests[index].OwnerPath, forwarded[index].PathAndQuery);
            Assert.Equal("Bearer " + bearer, forwarded[index].Authorization);
            Assert.Equal("tenant-1", forwarded[index].Tenant);
            Assert.Equal(ownerRequests[index].Body, forwarded[index].Body);
        }
    }

    private static void AssertRevisionQuery(JsonElement operation, string name, bool required)
    {
        var parameter = Assert.Single(
            operation.GetProperty("parameters").EnumerateArray(),
            item => item.GetProperty("name").GetString() == name);
        Assert.Equal("query", parameter.GetProperty("in").GetString());
        Assert.Equal(required, parameter.GetProperty("required").GetBoolean());
        var schema = parameter.GetProperty("schema");
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.Equal("int64", schema.GetProperty("format").GetString());
    }

    private sealed class RecordingOwnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private static JsonElement FindOperation(JsonElement paths, string operationId)
    {
        var matches = new List<JsonElement>();
        foreach (var path in paths.EnumerateObject())
        foreach (var method in path.Value.EnumerateObject())
        {
            if (method.Value.TryGetProperty("operationId", out var id) &&
                id.GetString() == operationId)
            {
                matches.Add(method.Value);
            }
        }

        return Assert.Single(matches);
    }

    private static bool HasIdempotencyKeyHeader(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters) &&
        parameters.EnumerateArray().Any(parameter =>
            parameter.GetProperty("name").GetString() == "Idempotency-Key");
}
