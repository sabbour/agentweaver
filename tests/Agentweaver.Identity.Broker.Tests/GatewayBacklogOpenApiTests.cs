extern alias GatewayHost;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayBacklogOpenApiTests
{
    [Fact]
    public async Task IssueSyncRoutesExposeSafeTypedSettingsAndGrantReceiptsNotTokens()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("readIssueSync", paths.GetProperty("/api/v1/projects/{projectId}/issue-sync")
            .GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("configureIssueSync", paths.GetProperty("/api/v1/projects/{projectId}/issue-sync")
            .GetProperty("put").GetProperty("operationId").GetString());
        Assert.Equal("createIssueSyncGrant", paths.GetProperty("/api/v1/projects/{projectId}/issue-sync/grants")
            .GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("revokeIssueSyncGrant",
            paths.GetProperty("/api/v1/projects/{projectId}/issue-sync/grants/{grantId}/revoke")
                .GetProperty("post").GetProperty("operationId").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("#/components/schemas/IssueSyncSettings", schemas.GetProperty("ConfigureIssueSyncRequest")
            .GetProperty("properties").GetProperty("settings").GetProperty("$ref").GetString());
        Assert.False(schemas.GetProperty("IssueSyncGrantReceipt").GetProperty("properties")
            .TryGetProperty("accessToken", out _));
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.StartsWith("/internal/", StringComparison.Ordinal));
    }

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

    [Fact]
    public async Task ProjectSummaryExposesScopedEffectiveRoles()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal("listProjects",
            paths.GetProperty("/api/v1/projects").GetProperty("get").GetProperty("operationId").GetString());
        Assert.Equal("getProject",
            paths.GetProperty("/api/v1/projects/{projectId}").GetProperty("get").GetProperty("operationId").GetString());

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var summary = schemas.GetProperty("ProjectSummary");
        Assert.Contains(summary.GetProperty("required").EnumerateArray(),
            item => item.GetString() == "effectiveRoles");
        Assert.Equal("#/components/schemas/EffectiveProjectRole",
            summary.GetProperty("properties").GetProperty("effectiveRoles")
                .GetProperty("items").GetProperty("$ref").GetString());

        var effectiveRole = schemas.GetProperty("EffectiveProjectRole");
        Assert.Equal(new[] { "resourceType", "resourceId", "role" },
            effectiveRole.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(new[] { "tenant", "project" },
            effectiveRole.GetProperty("properties").GetProperty("resourceType")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal(new[] { "tenantAdmin", "owner", "contributor", "viewer", "orchestrator" },
            effectiveRole.GetProperty("properties").GetProperty("role")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    [Fact]
    public async Task ProjectConfigurationChoicesRouteAndSchemaMatchTheOwnerContract()
    {
        var route = Assert.Single(
            GatewayRouteCatalog.Routes,
            candidate => candidate.OperationId == "getProjectConfigurationChoices");
        Assert.Equal("GET", route.Method);
        Assert.Equal(GatewayOwner.Projects, route.Owner);
        Assert.Equal(
            "/api/v1/projects/{projectId}/configuration/choices",
            route.PublicPath);
        Assert.Equal(
            "/api/projects/{projectId}/configuration/choices",
            route.OwnerPath);
        Assert.Empty(route.QueryParameters);
        Assert.True(route.ForwardTenantSelector);
        Assert.False(route.HasJsonBody);

        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var operation = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/configuration/choices")
            .GetProperty("get");
        Assert.Equal("getProjectConfigurationChoices", operation.GetProperty("operationId").GetString());
        Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant");
        Assert.Equal(
            "#/components/schemas/ProjectConfigurationChoices",
            operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Contains(operation.GetProperty("responses").GetProperty("200")
            .GetProperty("headers").EnumerateObject(), header => header.Name == "Cache-Control");
        Assert.Contains("must not include query parameters",
            operation.GetProperty("responses").GetProperty("400").GetProperty("description").GetString()!,
            StringComparison.OrdinalIgnoreCase);

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Contains("platform_runtime_unconfigured",
            schemas.GetProperty("ProjectModelChoices").GetProperty("properties")
                .GetProperty("unavailableReason").GetProperty("anyOf")[0].GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()));
        var projectConfiguration = schemas.GetProperty("ProjectConfiguration").GetProperty("properties");
        Assert.Contains("run selection ignores stored overrides",
            projectConfiguration.GetProperty("providerOverrides").GetProperty("description").GetString());
        Assert.Contains("run selection ignores stored overrides",
            projectConfiguration.GetProperty("orderedProviderOverrides").GetProperty("description").GetString());
    }
}
