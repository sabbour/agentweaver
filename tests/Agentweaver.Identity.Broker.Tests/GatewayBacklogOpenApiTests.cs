using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayBacklogOpenApiTests
{
    [Fact]
    public async Task ProjectBacklogRoutesHaveTypedRequestAndResponseSchemas()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("readProjectBacklog", paths.GetProperty("/api/v1/projects/{projectId}/backlog")
            .GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("captureBacklogTask", paths.GetProperty("/api/v1/projects/{projectId}/backlog/tasks")
            .GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("editBacklogTaskContent",
            paths.GetProperty("/api/v1/projects/{projectId}/backlog/tasks/{taskId}/content")
                .GetProperty("put").GetProperty("operationId").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("#/components/schemas/BacklogTaskContent", schemas.GetProperty("CaptureBacklogTaskRequest")
            .GetProperty("properties").GetProperty("content").GetProperty("$ref").GetString());
        Assert.Contains(schemas.GetProperty("EditBacklogTaskContentRequest").GetProperty("required")
            .EnumerateArray(), item => item.GetString() == "expectedTaskRevision");
        Assert.Equal("#/components/schemas/BacklogIssues",
            paths.GetProperty("/api/v1/projects/{projectId}/backlog/tasks").GetProperty("post")
                .GetProperty("responses").GetProperty("409").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.True(schemas.GetProperty("BacklogTaskSnapshot").GetProperty("properties")
            .TryGetProperty("repositoryIssue", out _));
    }
}
