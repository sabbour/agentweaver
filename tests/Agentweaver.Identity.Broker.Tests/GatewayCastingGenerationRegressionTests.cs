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

/// <summary>Protects the accepted-run generation routes and their proposal lifecycle.</summary>
public sealed class GatewayCastingGenerationRegressionTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid RequestId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ProposalId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task FormulateUsesTheRunBoundBearerAndStoresStrictOutputOnTheProposal()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = CreateToken(signingKey);
        var calls = new List<(string Owner, string Path, string? Bearer, string? Tenant, string Body)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new DelegateHandler(async (request, cancellationToken) =>
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                calls.Add(("Projects", request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.GetValues("X-Agentweaver-Tenant").SingleOrDefault(),
                    body));
                using var document = JsonDocument.Parse(body);
                if (request.RequestUri!.AbsolutePath.EndsWith("/generation-requests", StringComparison.Ordinal))
                {
                    Assert.Equal("formulate", document.RootElement.GetProperty("mode").GetString());
                    Assert.Equal("The Matrix", document.RootElement.GetProperty("castingUniverse").GetString());
                    return JsonResponse(GenerationRequest("pending"));
                }

                Assert.EndsWith($"/generation-requests/{RequestId:D}/complete", request.RequestUri.AbsolutePath);
                Assert.Equal("run-1", document.RootElement.GetProperty("runId").GetString());
                Assert.Equal("The Matrix", document.RootElement.GetProperty("castingUniverse").GetString());
                Assert.Equal(2, document.RootElement.GetProperty("agentCharters").GetArrayLength());
                Assert.Equal(2, document.RootElement.GetProperty("casting").GetArrayLength());
                Assert.DoesNotContain("prompt", document.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
                return JsonResponse(GenerationRequest("completed"));
            }),
            async (owner, request, cancellationToken) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                calls.Add(("Orchestrator", request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    request.Headers.GetValues("X-Agentweaver-Tenant").SingleOrDefault(),
                    body));
                Assert.Equal("/api/projects/project-1/runs/run-1/coordination/sessions/session-1/casting-generation",
                    request.RequestUri!.AbsolutePath);
                return JsonResponse(new
                {
                    requestId = RequestId,
                    runId = "run-1",
                    output = JsonSerializer.Serialize(new
                    {
                        agentCharters = new[]
                        {
                            new { agentId = "builder", name = "Builder", role = "builder", charter = "Build the product." },
                            new { agentId = "tester", name = "Tester", role = "tester", charter = "Test the product." },
                        },
                        casting = new[]
                        {
                            new { agentId = "builder", role = "builder", order = 0 },
                            new { agentId = "tester", role = "tester", order = 1 },
                        },
                    }, JsonOptions),
                });
            });

        using var request = CreateRequest(token, new
        {
            expectedConfigurationRevision = 4,
            runId = "run-1",
            sessionId = "session-1",
            idempotencyKey = "key-1",
            prompt = "Return a project team.",
            castingUniverse = "The Matrix",
        });
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(RequestId.ToString(), accepted.RootElement.GetProperty("requestId").GetString());
        Assert.Equal(ProposalId.ToString(), accepted.RootElement.GetProperty("proposalId").GetString());
        Assert.Equal("run-1", accepted.RootElement.GetProperty("runId").GetString());
        Assert.Equal("formulate", accepted.RootElement.GetProperty("mode").GetString());
        Assert.Equal("completed", accepted.RootElement.GetProperty("state").GetString());
        Assert.Equal(["Projects", "Orchestrator", "Projects"], calls.Select(call => call.Owner).ToArray());
        Assert.All(calls, call =>
        {
            Assert.Equal("Bearer " + token, call.Bearer);
            Assert.Equal("tenant-1", call.Tenant);
        });
        using var startBody = JsonDocument.Parse(calls[0].Body);
        Assert.Equal(64, startBody.RootElement.GetProperty("requestDigest").GetString()!.Length);
        Assert.DoesNotContain("prompt", startBody.RootElement.ToString(), StringComparison.OrdinalIgnoreCase);
        using var executionBody = JsonDocument.Parse(calls[1].Body);
        Assert.Equal("Return a project team.", executionBody.RootElement.GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task InvalidOutputFailsTheProposalWithoutExposingModelText()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = CreateToken(signingKey);
        var projectCalls = new List<string>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new DelegateHandler(async (request, cancellationToken) =>
            {
                projectCalls.Add(request.RequestUri!.AbsolutePath);
                _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(GenerationRequest(
                    request.RequestUri.AbsolutePath.EndsWith("/fail", StringComparison.Ordinal)
                        ? "failed"
                        : "pending",
                    request.RequestUri.AbsolutePath.EndsWith("/fail", StringComparison.Ordinal)
                        ? "agenthost_output_invalid"
                        : null));
            }),
            async (owner, request, cancellationToken) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                _ = await request.Content!.ReadAsStringAsync(cancellationToken);
                return JsonResponse(new { requestId = RequestId, runId = "run-1", output = "not json" });
            });

        using var request = CreateRequest(token, new
        {
            expectedConfigurationRevision = 4,
            runId = "run-1",
            sessionId = "session-1",
            idempotencyKey = "key-2",
            prompt = "Return a project team.",
            castingUniverse = (string?)null,
        });
        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var accepted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("failed", accepted.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            ["/api/projects/project-1/casting/generation-requests",
                $"/api/projects/project-1/casting/generation-requests/{RequestId:D}/fail"],
            projectCalls);
        Assert.DoesNotContain("not json", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrictRequestRejectsUnknownFieldsAndQueriesBeforeCallingOwners()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = CreateToken(signingKey);
        var calls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () =>
            {
                calls++;
                return new DelegateHandler((_, _) => throw new InvalidOperationException("Invalid input reached Projects."));
            },
            (_, _, _) => throw new InvalidOperationException("Invalid input reached another owner."));

        using var extraField = CreateRequest(token, new
        {
            expectedConfigurationRevision = 4,
            runId = "run-1",
            sessionId = "session-1",
            idempotencyKey = "key-3",
            prompt = "Return a project team.",
            castingUniverse = (string?)null,
            unexpected = true,
        });
        using var extraFieldResponse = await gateway.Client.SendAsync(extraField);
        Assert.Equal(HttpStatusCode.BadRequest, extraFieldResponse.StatusCode);

        using var query = CreateRequest(token, new
        {
            expectedConfigurationRevision = 4,
            runId = "run-1",
            sessionId = "session-1",
            idempotencyKey = "key-4",
            prompt = "Return a project team.",
            castingUniverse = (string?)null,
        }, "?unexpected=true");
        using var queryResponse = await gateway.Client.SendAsync(query);
        Assert.Equal(HttpStatusCode.BadRequest, queryResponse.StatusCode);
        Assert.Equal(0, calls);
    }

    private static HttpRequestMessage CreateRequest(string token, object body, string query = "")
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/project-1/casting/proposals/formulate" + query)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        return request;
    }

    private static string CreateToken(SecurityKey signingKey)
    {
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [
                new Claim("sub", "casting-generation-actor"),
                new Claim("scope", "api.read projects.admin"),
                new Claim("project_id", "project-1"),
                new Claim("run_id", "run-1"),
            ],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static object GenerationRequest(string state, string? failureCode = null) => new
    {
        requestId = RequestId,
        proposalId = ProposalId,
        projectId = "project-1",
        baseConfigurationRevision = 4,
        runId = "run-1",
        mode = "formulate",
        state,
        failureCode,
        createdAt = "2026-10-25T00:00:00Z",
        updatedAt = "2026-10-25T00:00:01Z",
    };

    private static HttpResponseMessage JsonResponse<T>(T value)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json"),
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return response;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
