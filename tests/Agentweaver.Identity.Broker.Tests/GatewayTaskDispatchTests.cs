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

public sealed class GatewayTaskDispatchTests
{
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";
    private const string SessionId = "root-1";
    private const string TenantId = "tenant-1";
    private const string Audience = "https://api.test";
    private static string Issuer => new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;

    [Fact]
    public async Task DispatchRouteForwardsTheRunRequestAndPublishesItsEvidenceContract()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        var calls = new List<ForwardedCall>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            async (_, request, cancellationToken) =>
            {
                var call = new ForwardedCall(
                    request.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants)
                        ? tenants.Single()
                        : null,
                    await request.Content!.ReadAsStringAsync(cancellationToken));
                calls.Add(call);
                return JsonResponse(HttpStatusCode.OK, new
                {
                    workPlanId = "plan-1",
                    decisionStateVersion = 4,
                    checkpointRevision = 3,
                    dispatchedAssociationIds = new[] { "item-1" },
                    unavailableExecutorStepIds = Array.Empty<string>(),
                    failedDependencyIds = Array.Empty<string>(),
                    isComplete = true,
                    completedResults = new Dictionary<string, string> { ["item-1"] = "output" },
                    wallTimeLimitReached = false,
                    childRuntimeStartup = new Dictionary<string, object>(),
                    completionEvidence = Array.Empty<object>(),
                    taskCompletion = new
                    {
                        state = "completed",
                        taskId = "task-1",
                    },
                });
            });

        var route = Assert.Single(GatewayRouteCatalog.Routes,
            item => item.OperationId == "dispatchCoordinatorExecution");
        Assert.Equal("POST", route.Method);
        Assert.Equal(
            "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/dispatch",
            route.PublicPath);
        Assert.Equal(
            "/api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/dispatch",
            route.OwnerPath);
        Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
        Assert.True(route.HasJsonBody);
        Assert.True(route.ForwardTenantSelector);
        Assert.Equal(256 * 1024, route.MaximumRequestBodyBytes);
        Assert.Equal(256 * 1024L, gateway.GetRequestSizeLimit(route.PublicPath)?.MaxRequestBodySize);

        const string body = """{"expectedStateVersion":4,"workPlanId":"plan-1"}""";
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/projects/{ProjectId}/runs/{RunId}/coordination/sessions/{SessionId}/actions/dispatch")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", TenantId);

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(calls);
        Assert.Equal(HttpMethod.Post, calls[0].Method);
        Assert.Equal(
            $"/api/projects/{ProjectId}/runs/{RunId}/coordination/sessions/{SessionId}/actions/dispatch",
            calls[0].Path);
        Assert.Equal("Bearer " + bearer, calls[0].Authorization);
        Assert.Equal(TenantId, calls[0].Tenant);
        Assert.Equal(body, calls[0].Body);

        using var responseJson = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("task-1", responseJson.RootElement.GetProperty("taskCompletion")
            .GetProperty("taskId").GetString());
    }

    [Fact]
    public async Task DispatchOwnerCallUsesTheExtendedDeadline()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            async (_, _, cancellationToken) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
                return JsonResponse(HttpStatusCode.OK, new { isComplete = false });
            },
            ownerRequestTimeout: TimeSpan.FromMilliseconds(20));

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/projects/{ProjectId}/runs/{RunId}/coordination/sessions/{SessionId}/actions/dispatch")
        {
            Content = new StringContent(
                """{"expectedStateVersion":4,"workPlanId":"plan-1"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", TenantId);

        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task OpenApiDescribesDispatchCompletionEvidenceAndSpendBudgets()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var dispatch = document.RootElement.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/actions/dispatch")
            .GetProperty("post");
        Assert.Equal("dispatchCoordinatorExecution", dispatch.GetProperty("operationId").GetString());
        var bodyLimit = dispatch.GetProperty("responses").GetProperty("413");
        Assert.True(bodyLimit.GetProperty("content").TryGetProperty("application/problem+json", out _));
        Assert.False(bodyLimit.GetProperty("content").TryGetProperty("application/json", out _));
        var requestSchema = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty("MafExecutionDispatchRequest");
        Assert.False(requestSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("#/components/schemas/MafExecutionDispatchRequest",
            dispatch.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/MafExecutionDispatchResponse",
            dispatch.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.Equal("#/components/schemas/RuntimeChildTurnCompletionReceipt",
            schemas.GetProperty("MafExecutionDispatchResponse").GetProperty("properties")
                .GetProperty("completionEvidence").GetProperty("items").GetProperty("$ref").GetString());
        var taskCompletionRefs = schemas.GetProperty("MafExecutionDispatchResponse").GetProperty("properties")
            .GetProperty("taskCompletion").GetProperty("anyOf").EnumerateArray()
            .Where(item => item.TryGetProperty("$ref", out _))
            .Select(item => item.GetProperty("$ref").GetString());
        Assert.Contains("#/components/schemas/MafExecutionTaskCompletionEvidence", taskCompletionRefs);
        Assert.Equal("#/components/schemas/SpendBudget",
            schemas.GetProperty("CopilotRunLimits").GetProperty("properties")
                .GetProperty("spendBudgets").GetProperty("anyOf")[0]
                .GetProperty("items").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/SpendBudget",
            schemas.GetProperty("CopilotRunLimitOverrides").GetProperty("properties")
                .GetProperty("spendBudgets").GetProperty("anyOf")[0]
                .GetProperty("items").GetProperty("$ref").GetString());
        Assert.Contains("workItemBindingSha256",
            schemas.GetProperty("RuntimeChildTurnCompletionReceipt").GetProperty("properties")
                .EnumerateObject().Select(property => property.Name));
        Assert.Contains("#/components/schemas/RuntimeCleanupSummary",
            schemas.GetProperty("OwnerRunStatus").GetProperty("properties").GetProperty("runtimeCleanup")
                .GetProperty("anyOf").EnumerateArray()
                .Where(item => item.TryGetProperty("$ref", out _))
                .Select(item => item.GetProperty("$ref").GetString()));
    }

    private static string CreateBearer(SecurityKey signingKey)
    {
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            [
                new Claim("sub", "task-dispatch-actor"),
                new Claim("scope", "api.read projects.orchestrator"),
                new Claim("project_id", ProjectId),
                new Claim("run_id", RunId),
            ],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json"),
        };

    private sealed record ForwardedCall(
        HttpMethod Method,
        string Path,
        string? Authorization,
        string? Tenant,
        string Body);
}
