extern alias GatewayHost;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Orchestrator.Core;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
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
                .GetProperty("application/json").GetProperty("schema").GetProperty("oneOf")[0]
                .GetProperty("$ref").GetString());
        Assert.True(schemas.GetProperty("BacklogTaskSnapshot").GetProperty("properties")
            .TryGetProperty("repositoryIssue", out _));

        var state = paths.GetProperty("/api/v1/projects/{projectId}/backlog/tasks/{taskId}/state")
            .GetProperty("put");
        Assert.Equal("setProjectBacklogTaskState", state.GetProperty("operationId").GetString());
        Assert.Equal("#/components/schemas/SetBacklogTaskStateRequest",
            state.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        var dependencies = paths.GetProperty(
            "/api/v1/projects/{projectId}/backlog/tasks/{taskId}/dependencies/{prerequisiteTaskId}");
        Assert.Equal("addProjectBacklogDependency",
            dependencies.GetProperty("put").GetProperty("operationId").GetString());
        Assert.Equal("removeProjectBacklogDependency",
            dependencies.GetProperty("delete").GetProperty("operationId").GetString());
        Assert.Equal("#/components/schemas/BacklogGraphRevisionRequest",
            dependencies.GetProperty("delete").GetProperty("requestBody").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());

        var claim = paths.GetProperty(
            "/api/v1/projects/{projectId}/runs/{runId}/backlog/tasks/{taskId}/claim").GetProperty("post");
        Assert.Equal("claimBacklogTask", claim.GetProperty("operationId").GetString());
        Assert.Equal("#/components/schemas/BacklogTaskClaimReceipt",
            claim.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/BacklogIssues",
            claim.GetProperty("responses").GetProperty("409").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("oneOf")[0]
                .GetProperty("$ref").GetString());
        Assert.Contains("after the durable claim commits",
            claim.GetProperty("responses").GetProperty("503").GetProperty("description").GetString()!);
        foreach (var schemaName in new[]
        {
            "BacklogTaskReference",
            "BacklogTaskContent",
            "BacklogTaskSnapshot",
            "BacklogDependencyEdge",
            "BacklogGraph",
            "BacklogDependencyGraphSnapshot",
            "CaptureBacklogTaskRequest",
            "EditBacklogTaskContentRequest",
            "SetBacklogTaskStateRequest",
            "BacklogTaskRevisionRequest",
            "BacklogGraphRevisionRequest",
            "ClaimBacklogTaskRequest",
            "BacklogTaskClaimReceipt",
        })
            Assert.False(schemas.GetProperty(schemaName).GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task SerializedBacklogResponseMatchesItsStrictOpenApiSchema()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var openApiResponse = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
        using var document = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var backlogGraphSchema = schemas.GetProperty("BacklogGraph");

        var graph = BacklogDependencyGraph.Create(new BacklogDependencyGraphSnapshot(
            "project-a",
            5,
            [
                new BacklogTaskSnapshot(
                    new BacklogTaskReference("project-a", "feature"), 2, BacklogTaskState.Ready, Title: "Feature"),
                new BacklogTaskSnapshot(
                    new BacklogTaskReference("project-a", "foundation"), 1, BacklogTaskState.Backlog, Title: "Foundation"),
            ],
            [
                new BacklogDependencyEdge(
                    new BacklogTaskReference("project-a", "feature"),
                    new BacklogTaskReference("project-a", "foundation")),
            ]));
        Assert.True(graph.IsSuccess);

        var services = new ServiceCollection();
        services.ConfigureHttpJsonOptions(options =>
        {
            // Match the Orchestrator's successful Results.Ok JSON response settings.
            options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.SerializerOptions.MaxDepth = 16;
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        services.AddLogging();
        using var serviceProvider = services.BuildServiceProvider();
        using var serviceScope = serviceProvider.CreateScope();
        var httpContext = new DefaultHttpContext { RequestServices = serviceScope.ServiceProvider };
        await using var responseBody = new MemoryStream();
        httpContext.Response.Body = responseBody;
        await Results.Ok(graph.Value).ExecuteAsync(httpContext);
        responseBody.Position = 0;
        using var serializedResponse = await JsonDocument.ParseAsync(responseBody);

        Assert.True(
            MatchesSchema(serializedResponse.RootElement, backlogGraphSchema, schemas),
            "The serialized successful backlog response must conform to the strict BacklogGraph schema.");
    }

    private static bool MatchesSchema(JsonElement value, JsonElement schema, JsonElement schemas)
    {
        // Check the schema keywords used by the strict backlog response contracts.
        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string prefix = "#/components/schemas/";
            var name = reference.GetString();
            return name is not null && name.StartsWith(prefix, StringComparison.Ordinal) &&
                schemas.TryGetProperty(name[prefix.Length..], out var referencedSchema) &&
                MatchesSchema(value, referencedSchema, schemas);
        }

        if (schema.TryGetProperty("anyOf", out var alternatives))
            return alternatives.EnumerateArray().Any(alternative => MatchesSchema(value, alternative, schemas));
        if (!schema.TryGetProperty("type", out var type))
            return true;

        switch (type.GetString())
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object)
                    return false;
                if (schema.TryGetProperty("required", out var required) &&
                    required.EnumerateArray().Any(name => !value.TryGetProperty(name.GetString()!, out _)))
                    return false;
                var properties = schema.GetProperty("properties");
                foreach (var property in value.EnumerateObject())
                {
                    if (!properties.TryGetProperty(property.Name, out var propertySchema))
                    {
                        if (schema.TryGetProperty("additionalProperties", out var additionalProperties) &&
                            additionalProperties.ValueKind == JsonValueKind.False)
                            return false;
                        continue;
                    }
                    if (!MatchesSchema(property.Value, propertySchema, schemas))
                        return false;
                }
                return true;
            case "array":
                return value.ValueKind == JsonValueKind.Array &&
                    value.EnumerateArray().All(item => MatchesSchema(item, schema.GetProperty("items"), schemas));
            case "string":
                return value.ValueKind == JsonValueKind.String &&
                    (!schema.TryGetProperty("enum", out var values) ||
                     values.EnumerateArray().Any(candidate => candidate.GetString() == value.GetString()));
            case "integer":
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _);
            case "number":
                return value.ValueKind == JsonValueKind.Number;
            case "boolean":
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False;
            case "null":
                return value.ValueKind == JsonValueKind.Null;
            default:
                return false;
        }
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
        var paths = document.RootElement.GetProperty("paths");
        var operation = paths
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
        var modelChoices = schemas.GetProperty("ProjectModelChoices");
        Assert.Contains("model_capability_catalog_unavailable",
            modelChoices.GetProperty("properties")
                .GetProperty("unavailableReason").GetProperty("anyOf")[0].GetProperty("enum")
                .EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("savedReferences",
            modelChoices.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("Saved does not mean eligible",
            modelChoices.GetProperty("properties").GetProperty("savedReferences")
                .GetProperty("description").GetString());
        Assert.Equal(
            new string?[] { "state", "unavailableReason", "runtimeEligibility", "bindingsRevision", "defaultReasoningEffort", "choices", "savedReferences" },
            modelChoices.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("#/components/schemas/ProjectModelChoice",
            modelChoices.GetProperty("properties").GetProperty("choices").GetProperty("items")
                .GetProperty("$ref").GetString());
        var modelChoice = schemas.GetProperty("ProjectModelChoice").GetProperty("properties");
        Assert.Equal(new string?[] { "hostedCopilot", "byok" },
            modelChoice.GetProperty("sourceMode").GetProperty("enum").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        Assert.Equal(new string?[] { "modelBinding", "hostedModelCatalog", "verifiedAtRunStart" },
            modelChoice.GetProperty("reasoningEffortSource").GetProperty("enum").EnumerateArray()
                .Select(item => item.GetString()).ToArray());
        Assert.Contains("run-start verification",
            modelChoice.GetProperty("reasoningEfforts").GetProperty("description").GetString());
        // The choice item carries eligibility fields only; provider and credential data stay with AgentHost.
        Assert.Equal(["reasoningEffortSource", "reasoningEfforts", "reference", "sourceMode"],
            modelChoice.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray());

        var liveCatalogRoute = Assert.Single(
            GatewayRouteCatalog.Routes,
            candidate => candidate.OperationId == "getHostedModelReasoningCatalog");
        Assert.Equal("GET", liveCatalogRoute.Method);
        Assert.Equal(GatewayOwner.AgentHost, liveCatalogRoute.Owner);
        Assert.Equal(
            "/api/v1/projects/{projectId}/configuration/hosted-model-reasoning-catalog",
            liveCatalogRoute.PublicPath);
        Assert.Equal("/runtime/v1/projects/{projectId}/hosted-model-reasoning-catalog",
            liveCatalogRoute.OwnerPath);
        Assert.True(liveCatalogRoute.ForwardTenantSelector);
        var liveCatalogOperation = paths.GetProperty(liveCatalogRoute.PublicPath).GetProperty("get");
        Assert.Equal("getHostedModelReasoningCatalog",
            liveCatalogOperation.GetProperty("operationId").GetString());
        Assert.Equal("#/components/schemas/HostedModelReasoningCatalogV1",
            liveCatalogOperation.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Contains(liveCatalogOperation.GetProperty("responses").GetProperty("200")
            .GetProperty("headers").EnumerateObject(), header => header.Name == "Cache-Control");
        var catalogSchema = schemas.GetProperty("HostedModelReasoningCatalogV1");
        Assert.Contains("bindingsRevision",
            catalogSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("#/components/schemas/HostedModelReasoningModelV1",
            catalogSchema.GetProperty("properties").GetProperty("models")
                .GetProperty("additionalProperties").GetProperty("$ref").GetString());
        Assert.Equal(new string?[] { "available", "unavailable" },
            schemas.GetProperty("HostedModelReasoningModelV1").GetProperty("properties").GetProperty("state")
                .GetProperty("enum").EnumerateArray().Select(item => item.GetString()).ToArray());

        var projectConfiguration = schemas.GetProperty("ProjectConfiguration").GetProperty("properties");
        Assert.Contains("any non-empty value returns 400",
            projectConfiguration.GetProperty("providerOverrides").GetProperty("description").GetString());
        Assert.Contains("any non-empty value returns 400",
            projectConfiguration.GetProperty("orderedProviderOverrides").GetProperty("description").GetString());
    }
}
