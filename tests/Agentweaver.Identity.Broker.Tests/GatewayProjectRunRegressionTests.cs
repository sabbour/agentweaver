using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>Guards the Gateway catalog, OpenAPI, CORS, and body-limit contract for interactive runs.</summary>
public sealed class GatewayProjectRunRegressionTests
{
    [Fact]
    public async Task ProjectRunListOpenApiDescribesCallerFilterAndSummaryFields()
    {
        await using var gateway = await StartAsync();
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs").GetProperty("get");
        Assert.Equal("listProjectRuns", operation.GetProperty("operationId").GetString());
        var startedBy = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "startedBy");
        Assert.Equal(new[] { "me" }, startedBy.GetProperty("schema").GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("exact caller identity", operation.GetProperty("description").GetString(),
            StringComparison.OrdinalIgnoreCase);

        var summary = root.GetProperty("components").GetProperty("schemas").GetProperty("ProjectRunSummary");
        var required = summary.GetProperty("required").EnumerateArray()
            .Select(value => value.GetString()).ToArray();
        Assert.Contains("workflowId", required);
        Assert.Contains("needsInputCount", required);
        var properties = summary.GetProperty("properties");
        var workflowId = properties.GetProperty("workflowId").GetProperty("anyOf").EnumerateArray().ToArray();
        Assert.Equal("string", workflowId[0].GetProperty("type").GetString());
        Assert.Equal("null", workflowId[1].GetProperty("type").GetString());
        Assert.Equal("integer", properties.GetProperty("needsInputCount").GetProperty("type").GetString());
        Assert.Equal("int64", properties.GetProperty("needsInputCount").GetProperty("format").GetString());
    }

    [Theory]
    [InlineData("/api/v1/projects/{projectId}/runs", "createProjectRun", "201",
        "CreateProjectRunRequest", "CreateProjectRunResponse")]
    [InlineData("/api/v1/projects/{projectId}/runs/{runId}/start", "startProjectRun", "200",
        null, "StartProjectRunResponse")]
    [InlineData("/api/v1/projects/{projectId}/runs/{runId}/cancel", "cancelProjectRun", "200",
        "CancelProjectRunRequest", "CancelProjectRunResponse")]
    [InlineData("/api/v1/projects/{projectId}/runs/{runId}/messages", "sendProjectRunMessage", "200",
        "ProjectRunMessageRequest", "ProjectRunMessageReceipt")]
    public async Task ServedOpenApiDescribesInteractiveRunOperations(
        string path, string operationId, string status, string? requestSchema, string responseSchema)
    {
        await using var gateway = await StartAsync();
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var operation = document.RootElement.GetProperty("paths").GetProperty(path).GetProperty("post");
        Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
        Assert.Equal("Orchestrator", operation.GetProperty("x-agentweaver-owner").GetString());
        Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant");
        var body = operation.GetProperty("requestBody");
        if (requestSchema is null)
            Assert.Equal(JsonValueKind.Null, body.ValueKind);
        else
            Assert.Equal($"#/components/schemas/{requestSchema}", body.GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        if (operationId is "createProjectRun" or "cancelProjectRun" or "sendProjectRunMessage")
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "Idempotency-Key" &&
                parameter.GetProperty("required").GetBoolean());
        Assert.Equal($"#/components/schemas/{responseSchema}", operation.GetProperty("responses")
            .GetProperty(status).GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString());

        // The persisted first message is never echoed by create or start.
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.False(schemas.GetProperty(responseSchema).GetProperty("properties")
            .TryGetProperty("firstMessage", out _));
    }

    [Fact]
    public async Task HistoricalTurnReadUsesEventsOwnerAndExposesOnlyTextAndIdentity()
    {
        await using var gateway = await StartAsync();
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var operation = root.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs/{runId}/sessions/{sessionId}/turns/{eventId}")
            .GetProperty("get");
        Assert.Equal("readSessionTurnContent", operation.GetProperty("operationId").GetString());
        Assert.Equal("Events", operation.GetProperty("x-agentweaver-owner").GetString());
        var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(parameters, parameter => parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant");
        Assert.DoesNotContain(parameters, parameter => parameter.GetProperty("name").GetString() == "kind");
        var properties = root.GetProperty("components").GetProperty("schemas")
            .GetProperty("SessionTurnContent").GetProperty("properties");
        Assert.Equal(new[] { "eventId", "identity", "text" }, properties.EnumerateObject().Select(p => p.Name));
    }

    [Theory]
    [InlineData("/api/v1/projects/p/runs", true)]
    [InlineData("/api/v1/projects/p/runs/r/cancel", true)]
    [InlineData("/api/v1/projects/p/runs/r/messages", true)]
    [InlineData("/api/v1/projects/p/runs/r/start", false)]
    public async Task BrowserPreflightAllowsIdempotencyKeyOnlyForRetryableRunWrites(string path, bool allowed)
    {
        await using var gateway = await StartAsync();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, path);
        preflight.Headers.TryAddWithoutValidation("Origin", "https://web.test");
        preflight.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        preflight.Headers.TryAddWithoutValidation(
            "Access-Control-Request-Headers", "authorization,content-type,x-agentweaver-tenant,idempotency-key");
        using var response = await gateway.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var headers = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Equal(allowed, headers.Contains("idempotency-key", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateRunAllowsTheFirstMessageBodyLimit()
    {
        await using var gateway = await StartAsync();
        var endpoint = Assert.Single(gateway.Endpoints, endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "createProjectRun");
        Assert.Equal(256 * 1024, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    [Fact]
    public async Task RunMessagesAllowTheBoundedUtf8BodyLimit()
    {
        await using var gateway = await StartAsync();
        var endpoint = Assert.Single(gateway.Endpoints, endpoint =>
            endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "sendProjectRunMessage");
        Assert.Equal(256 * 1024, endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);
    }

    private static async Task<GatewayResourceServer> StartAsync()
    {
        using var rsa = RSA.Create(2048);
        return await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa.ExportParameters(true)),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("Contract checks must not call an owner."));
    }
}
