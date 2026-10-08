using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayKnowledgeOpenApiRegressionTests
{
    private static readonly string[] KnowledgeWriteOperationIds =
    [
        "createKnowledgeRecord",
        "updateKnowledgeRecord",
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
