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

public sealed class GatewayWorkflowGenerationTests
{
    private const string ProjectId = "project-1";
    private const string RunId = "run-1";
    private const string SessionId = "root-1";
    private const string TenantId = "tenant-1";
    private const string ActorId = "workflow-generation-actor";
    private const string Audience = "https://api.test";
    private static string Issuer => new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri;

    [Fact]
    public async Task WorkflowGenerationRoutesAndOpenApiKeepTheDualCredentialBoundaryOnPost()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new CallbackHandler((_, _) => throw new InvalidOperationException("OpenAPI must not call Projects.")),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));

        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var expectedRoutes = new[]
        {
            ("POST", "/api/v1/projects/{projectId}/workflows/generate",
                "/api/projects/{projectId}/workflows/generate", "generateProjectWorkflow", 202),
            ("GET", "/api/v1/projects/{projectId}/workflows/generation-jobs/{jobId}",
                "/api/projects/{projectId}/workflows/generation-jobs/{jobId:guid}",
                "readProjectWorkflowGenerationJob", 200),
            ("GET", "/api/v1/projects/{projectId}/workflows/generation-jobs/{jobId}/result",
                "/api/projects/{projectId}/workflows/generation-jobs/{jobId:guid}/result",
                "readProjectWorkflowGenerationResult", 200),
        };
        foreach (var (method, publicPath, ownerPath, operationId, successStatus) in expectedRoutes)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == operationId);
            Assert.Equal(method, route.Method);
            Assert.Equal(publicPath, route.PublicPath.Replace(":guid", "", StringComparison.Ordinal));
            Assert.Equal(ownerPath, route.OwnerPath);
            Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.Equal(operationId == "generateProjectWorkflow", route.HasJsonBody);

            var operation = paths.GetProperty(publicPath).GetProperty(method.ToLowerInvariant());
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.Equal("Orchestrator", operation.GetProperty("x-agentweaver-owner").GetString());
            Assert.Equal(ownerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.False(Assert.Single(parameters, item =>
                item.GetProperty("name").GetString() == "X-Agentweaver-Tenant")
                .GetProperty("required").GetBoolean());
            var projectAuthorization = parameters.SingleOrDefault(item =>
                item.GetProperty("name").GetString() == "X-Agentweaver-Project-Authorization");
            var idempotency = parameters.SingleOrDefault(item =>
                item.GetProperty("name").GetString() == "Idempotency-Key");
            Assert.Equal(operationId == "generateProjectWorkflow", projectAuthorization.ValueKind != JsonValueKind.Undefined);
            Assert.Equal(operationId == "generateProjectWorkflow", idempotency.ValueKind != JsonValueKind.Undefined);
            if (operationId == "generateProjectWorkflow")
            {
                Assert.True(projectAuthorization.GetProperty("required").GetBoolean());
                Assert.True(idempotency.GetProperty("required").GetBoolean());
                Assert.Equal(64 * 1024, route.MaximumRequestBodyBytes);
                Assert.Equal(64 * 1024L,
                    gateway.GetRequestSizeLimit("/api/v1/projects/{projectId}/workflows/generate")?.MaxRequestBodySize);
            }
            Assert.True(operation.GetProperty("responses")
                .TryGetProperty(successStatus.ToString(), out _));
        }

        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var schemaName in new[]
        {
            "GenerateProjectWorkflowRequest", "WorkflowGenerationJobEnvelope",
            "WorkflowGenerationArtifactSummary", "WorkflowGenerationJobResult",
        })
            Assert.False(schemas.GetProperty(schemaName).GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            ["runId", "sessionId", "expectedDecisionStateVersion", "prompt"],
            schemas.GetProperty("GenerateProjectWorkflowRequest").GetProperty("required")
                .EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Fact]
    public async Task SubmissionUsesBothCurrentAuthoritiesAndProjectsPublicJobUrls()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var runBearer = CreateBearer(signingKey, bound: true);
        var projectBearer = CreateBearer(signingKey, bound: false);
        var jobId = Guid.NewGuid();
        var calls = new List<ForwardedCall>();
        var artifactId = Guid.NewGuid();
        await using var gateway = await StartGatewayAsync(
            signingKey, runBearer, projectBearer, calls, jobIdOverride: jobId, artifactIdOverride: artifactId);

        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/projects/{ProjectId}/workflows/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                runId = RunId,
                sessionId = SessionId,
                expectedDecisionStateVersion = 1,
                prompt = "Generate one workflow.",
            }), Encoding.UTF8, "application/json"),
        };
        AddHeaders(request, runBearer, projectBearer);
        request.Headers.Add("Idempotency-Key", "workflow-generation-1");

        using var accepted = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.True(accepted.Headers.CacheControl?.NoStore);
        var ownerStatusUrl = $"/api/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}";
        var publicStatusUrl = $"/api/v1/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}";
        Assert.Equal(publicStatusUrl, accepted.Headers.Location?.OriginalString);
        using var acceptedBody = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        Assert.Equal(publicStatusUrl, acceptedBody.RootElement.GetProperty("statusUrl").GetString());
        Assert.Equal(publicStatusUrl + "/result", acceptedBody.RootElement.GetProperty("resultUrl").GetString());

        using var statusRequest = new HttpRequestMessage(HttpMethod.Get, publicStatusUrl);
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
        using var status = await gateway.Client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        using var statusBody = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
        Assert.Equal(publicStatusUrl, statusBody.RootElement.GetProperty("statusUrl").GetString());
        Assert.Equal(publicStatusUrl + "/result", statusBody.RootElement.GetProperty("resultUrl").GetString());

        using var resultRequest = new HttpRequestMessage(HttpMethod.Get, publicStatusUrl + "/result");
        resultRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
        using var result = await gateway.Client.SendAsync(resultRequest);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        using var resultBody = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal(jobId, resultBody.RootElement.GetProperty("jobId").GetGuid());
        Assert.True(result.Headers.CacheControl?.NoStore);

        var post = Assert.Single(calls, call => call.Method == "POST");
        Assert.Equal("/api/projects/project-1/workflows/generate", post.Path);
        Assert.Equal("Bearer " + runBearer, post.Bearer);
        Assert.Equal("Bearer " + projectBearer, post.ProjectAuthorization);
        Assert.Equal("workflow-generation-1", post.IdempotencyKey);
        Assert.Equal(TenantId, post.Tenant);
        Assert.Single(calls, call => call.Method == "GET" && call.Path == ownerStatusUrl);
        Assert.All(calls.Where(call => call.Method == "GET"),
            call =>
            {
                Assert.Null(call.ProjectAuthorization);
                Assert.Equal("Bearer " + runBearer, call.Bearer);
            });
    }

    [Fact]
    public async Task RejectsWriterMismatchAndMalformedOwnerJobIdentity()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var runBearer = CreateBearer(signingKey, bound: true);
        var projectBearer = CreateBearer(signingKey, bound: false);

        var mismatchedCalls = new List<ForwardedCall>();
        await using (var mismatched = await StartGatewayAsync(
            signingKey, runBearer, projectBearer, mismatchedCalls, writerActorId: "other-actor"))
        using (var request = CreateSubmission(runBearer, projectBearer))
        using (var response = await mismatched.Client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Empty(mismatchedCalls);
        }

        var malformedCalls = new List<ForwardedCall>();
        await using (var malformed = await StartGatewayAsync(
            signingKey, runBearer, projectBearer, malformedCalls, malformedJobResponse: true))
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"/api/v1/projects/{ProjectId}/workflows/generation-jobs/{Guid.NewGuid():D}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
            using var response = await malformed.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            Assert.Single(malformedCalls);
        }
    }

    [Fact]
    public async Task RejectsMalformedSubmissionsBeforeForwardingAndMalformedOwnerValuesAsBadGateway()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var runBearer = CreateBearer(signingKey, bound: true);
        var projectBearer = CreateBearer(signingKey, bound: false);
        var calls = new List<ForwardedCall>();
        await using var gateway = await StartGatewayAsync(signingKey, runBearer, projectBearer, calls);

        using (var wrongCase = CreateSubmission(runBearer, projectBearer,
                   $$"""{"RunId":"{{RunId}}","sessionId":"{{SessionId}}","expectedDecisionStateVersion":1,"prompt":"Generate one workflow."}"""))
        using (var response = await gateway.Client.SendAsync(wrongCase))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using (var wrongType = CreateSubmission(runBearer, projectBearer,
                   JsonSerializer.Serialize(new
                   {
                       runId = RunId,
                       sessionId = SessionId,
                       expectedDecisionStateVersion = "1",
                       prompt = "Generate one workflow.",
                   })))
        using (var response = await gateway.Client.SendAsync(wrongType))
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Empty(calls);

        var jobId = Guid.NewGuid();
        var malformedCalls = new List<ForwardedCall>();
        await using var malformed = await StartGatewayAsync(
            signingKey, runBearer, projectBearer, malformedCalls, jobIdOverride: jobId,
            wrongOwnerJobIdType: true);
        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}");
        statusRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
        using var malformedResponse = await malformed.Client.SendAsync(statusRequest);
        Assert.Equal(HttpStatusCode.BadGateway, malformedResponse.StatusCode);

        var revisionCalls = new List<ForwardedCall>();
        await using var malformedResult = await StartGatewayAsync(
            signingKey, runBearer, projectBearer, revisionCalls, jobIdOverride: jobId,
            wrongOwnerRevisionType: true);
        using var resultRequest = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}/result");
        resultRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
        using var resultResponse = await malformedResult.Client.SendAsync(resultRequest);
        Assert.Equal(HttpStatusCode.BadGateway, resultResponse.StatusCode);
    }

    [Fact]
    public async Task RejectsOwnerStatusEnvelopeForAnotherRun()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var runBearer = CreateBearer(signingKey, bound: true);
        var projectBearer = CreateBearer(signingKey, bound: false);
        var calls = new List<ForwardedCall>();
        var jobId = Guid.NewGuid();
        await using var gateway = await StartGatewayAsync(
            signingKey, runBearer, projectBearer, calls,
            jobIdOverride: jobId, ownerRunId: "another-run");

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"/api/v1/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Single(calls);
    }

    private static async Task<GatewayResourceServer> StartGatewayAsync(
        SecurityKey signingKey,
        string runBearer,
        string projectBearer,
        List<ForwardedCall> calls,
        string? writerActorId = null,
        bool malformedJobResponse = false,
        bool wrongOwnerJobIdType = false,
        bool wrongOwnerRevisionType = false,
        string? ownerRunId = null,
        Guid? jobIdOverride = null,
        Guid? artifactIdOverride = null)
    {
        var jobId = jobIdOverride ?? Guid.NewGuid();
        var artifactId = artifactIdOverride ?? Guid.NewGuid();
        HttpResponseMessage ProjectsResponse(HttpRequestMessage request)
        {
            if (request.RequestUri?.PathAndQuery != "/api/authorization/context")
                throw new InvalidOperationException($"Unexpected Projects request {request.RequestUri}.");
            var bearer = request.Headers.Authorization?.Parameter;
            var writer = bearer == projectBearer;
            return JsonResponse(HttpStatusCode.OK, new
            {
                contractVersion = 1,
                issuer = Issuer,
                actorId = writer ? writerActorId ?? ActorId : ActorId,
                tenantId = TenantId,
                membershipRevision = 1,
                boundProjectId = writer ? null : ProjectId,
                boundRunId = writer ? null : RunId,
                effectiveAuthority = new[]
                {
                    new
                    {
                        resourceType = "project",
                        resourceId = ProjectId,
                        permissions = new[]
                        {
                            new
                            {
                                permission = writer ? "writeProjects" : "acceptRunSelection",
                                roleRevision = 1,
                            },
                        },
                    },
                },
            });
        }

        return await GatewayResourceServer.StartAsync(
            signingKey,
            () => new CallbackHandler((request, _) => Task.FromResult(ProjectsResponse(request))),
            async (owner, request, cancellationToken) =>
            {
                if (owner != GatewayOwner.Orchestrator)
                    throw new InvalidOperationException($"Unexpected owner {owner}.");
                var body = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                var call = new ForwardedCall(
                    request.Method.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.TryGetValues("X-Agentweaver-Project-Authorization", out var projectHeaders)
                        ? projectHeaders.Single()
                        : null,
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants)
                        ? tenants.Single()
                        : null,
                    request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.Single() : null,
                    body);
                calls.Add(call);
                if (request.Method == HttpMethod.Post)
                {
                    Assert.Equal("Bearer " + runBearer, call.Bearer);
                    Assert.Equal("Bearer " + projectBearer, call.ProjectAuthorization);
                    using var submitted = JsonDocument.Parse(body!);
                    Assert.Equal(RunId, submitted.RootElement.GetProperty("runId").GetString());
                    Assert.Equal(SessionId, submitted.RootElement.GetProperty("sessionId").GetString());
                    var ownerUrl = $"/api/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}";
                    var response = JsonResponse(HttpStatusCode.Accepted, Envelope(jobId, "queued", null));
                    response.Headers.Location = new Uri(ownerUrl, UriKind.Relative);
                    return response;
                }

                if (request.RequestUri.AbsolutePath.EndsWith("/result", StringComparison.Ordinal))
                {
                    object configurationRevision = wrongOwnerRevisionType ? "1" : 1;
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        jobId,
                        artifactId,
                        workflowDefinition = new { id = "generated-workflow" },
                        definitionSha256 = new string('a', 64),
                        nativeCompletion = new { contractVersion = 1 },
                        configurationRevision,
                        acceptedSelectionHash = new string('b', 64),
                    });
                }

                if (wrongOwnerJobIdType)
                {
                    var ownerUrl = $"/api/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}";
                    return JsonResponse(HttpStatusCode.OK, new
                    {
                        jobId = 1,
                        status = "running",
                        projectId = ProjectId,
                        runId = ownerRunId ?? RunId,
                        sessionId = SessionId,
                        createdAt = "2026-11-17T00:00:00Z",
                        updatedAt = "2026-11-17T00:00:01Z",
                        statusUrl = ownerUrl,
                        resultUrl = ownerUrl + "/result",
                        artifact = (object?)null,
                        failure = (object?)null,
                    });
                }

                if (malformedJobResponse)
                    return JsonResponse(HttpStatusCode.OK, Envelope(Guid.NewGuid(), "running", null));
                return JsonResponse(HttpStatusCode.OK,
                    Envelope(jobId, "completed", new
                    {
                        artifactId,
                        workflowId = "generated-workflow",
                        definitionRevision = "1",
                    }, ownerRunId));
            });
    }

    private static object Envelope(Guid jobId, string status, object? artifact, string? runId = null)
    {
        var ownerUrl = $"/api/projects/{ProjectId}/workflows/generation-jobs/{jobId:D}";
        return new
        {
            jobId,
            status,
            projectId = ProjectId,
            runId = runId ?? RunId,
            sessionId = SessionId,
            createdAt = "2026-11-17T00:00:00Z",
            updatedAt = "2026-11-17T00:00:01Z",
            statusUrl = ownerUrl,
            resultUrl = ownerUrl + "/result",
            artifact,
            failure = (object?)null,
        };
    }

    private static HttpRequestMessage CreateSubmission(
        string runBearer,
        string projectBearer,
        string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/v1/projects/{ProjectId}/workflows/generate")
        {
            Content = new StringContent(body ?? JsonSerializer.Serialize(new
                {
                    runId = RunId,
                    sessionId = SessionId,
                    expectedDecisionStateVersion = 1,
                    prompt = "Generate one workflow.",
                }), Encoding.UTF8, "application/json"),
        };
        AddHeaders(request, runBearer, projectBearer);
        request.Headers.Add("Idempotency-Key", "workflow-generation-1");
        return request;
    }

    private static void AddHeaders(HttpRequestMessage request, string runBearer, string projectBearer)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runBearer);
        request.Headers.Add("X-Agentweaver-Project-Authorization", "Bearer " + projectBearer);
        request.Headers.Add("X-Agentweaver-Tenant", TenantId);
    }

    private static string CreateBearer(SecurityKey signingKey, bool bound)
    {
        var claims = new List<Claim>
        {
            new("sub", ActorId),
            new("scope", bound ? "api.read projects.orchestrator" : "api.read projects.admin"),
        };
        if (bound)
        {
            claims.Add(new Claim("project_id", ProjectId));
            claims.Add(new Claim("run_id", RunId));
        }
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, object body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed record ForwardedCall(
        string Method,
        string Path,
        string? Bearer,
        string? ProjectAuthorization,
        string? Tenant,
        string? IdempotencyKey,
        string? Body);

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request, cancellationToken);
    }
}
