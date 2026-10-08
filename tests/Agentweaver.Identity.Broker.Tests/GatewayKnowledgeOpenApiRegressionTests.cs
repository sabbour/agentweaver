using System.Security.Cryptography;
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
