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

/// <summary>Protects the public casting catalog, owner forwarding, and OpenAPI contracts.</summary>
public sealed class GatewayCastingRegressionTests
{
    // Independent expectations catch missing routes as well as accidental owner contract changes.
    private static readonly (string Method, string Suffix, string Operation, string? Request, string Response, string Status)[] Routes =
    [
        ("GET", "/projects/{projectId}/casting/universes", "listProjectCastingUniverses", null, "ProjectCastingUniverseList", "200"),
        ("POST", "/projects/{projectId}/casting/proposals", "createProjectCastingProposal", "CreateProjectCastingProposalRequest", "ProjectCastingProposal", "201"),
        ("POST", "/projects/{projectId}/casting/proposals/scenario", "createScenarioProjectCastingProposal", "CreateScenarioProjectCastingProposalRequest", "ProjectCastingProposal", "201"),
        ("POST", "/projects/{projectId}/casting/proposals/manual", "createManualProjectCastingProposal", "CreateManualProjectCastingProposalRequest", "ProjectCastingProposal", "201"),
        ("GET", "/projects/{projectId}/casting/proposals", "listProjectCastingProposals", null, "ProjectCastingProposalList", "200"),
        ("GET", "/projects/{projectId}/casting/proposals/{proposalId:guid}", "getProjectCastingProposal", null, "ProjectCastingProposal", "200"),
        ("PATCH", "/projects/{projectId}/casting/proposals/{proposalId:guid}", "updateProjectCastingProposal", "UpdateProjectCastingProposalRequest", "ProjectCastingProposal", "200"),
        ("POST", "/projects/{projectId}/casting/proposals/{proposalId:guid}/confirm", "confirmProjectCastingProposal", "TransitionProjectCastingProposalRequest", "ProjectCastingProposal", "200"),
        ("POST", "/projects/{projectId}/casting/proposals/{proposalId:guid}/reject", "rejectProjectCastingProposal", "TransitionProjectCastingProposalRequest", "ProjectCastingProposal", "200"),
        ("GET", "/projects/{projectId}/casting/export", "exportProjectCasting", null, "ProjectCastingTransfer", "200"),
        ("POST", "/projects/{projectId}/casting/import", "importProjectCasting", "ImportProjectCastingTransferRequest", "ProjectCastingProposal", "201"),
        ("GET", "/casting/templates", "listProjectCastingTemplates", null, "ProjectCastingTemplateList", "200"),
        ("GET", "/catalog/roles", "listProjectCastingRoles", null, "ProjectCastingRoleList", "200"),
    ];

    /// <summary>Every casting operation has an owner-aligned schema, selector, and request bound.</summary>
    [Fact]
    public async Task CastingRoutesAndOpenApiMatchProjectsContracts()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa), () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        foreach (var expected in Routes)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == expected.Operation);
            Assert.Equal(expected.Method, route.Method);
            Assert.Equal("/api/v1" + expected.Suffix, route.PublicPath);
            Assert.Equal("/api" + expected.Suffix, route.OwnerPath);
            Assert.Equal(GatewayOwner.Projects, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.False(route.RequiresTenantSelector);
            Assert.Equal(expected.Request is not null, route.HasJsonBody);
            Assert.False(route.AcceptsOnly);
            var expectedQuery = expected.Operation switch
            {
                "listProjectCastingProposals" => new[] { "limit" },
                "exportProjectCasting" => ["revision"],
                _ => [],
            };
            Assert.Equal(expectedQuery, route.QueryParameters.ToArray());
            var operation = paths.GetProperty(route.PublicPath.Replace(":guid", "", StringComparison.Ordinal))
                .GetProperty(expected.Method.ToLowerInvariant());
            Assert.Equal(expected.Operation, operation.GetProperty("operationId").GetString());
            Assert.Equal("Projects", operation.GetProperty("x-agentweaver-owner").GetString());
            Assert.Equal(route.OwnerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.False(Assert.Single(parameters, item => item.GetProperty("name").GetString() == "X-Agentweaver-Tenant")
                .GetProperty("required").GetBoolean());
            Assert.DoesNotContain(parameters, item => item.GetProperty("name").GetString() == "Idempotency-Key");
            if (expected.Request is not null)
            {
                var body = operation.GetProperty("requestBody");
                Assert.True(body.GetProperty("required").GetBoolean());
                Assert.Equal("#/components/schemas/" + expected.Request, SchemaReference(body));
            }
            Assert.Equal("#/components/schemas/" + expected.Response,
                SchemaReference(operation.GetProperty("responses").GetProperty(expected.Status)));
            // Every casting write documents the owner's authorization denial.
            if (expected.Request is not null)
                Assert.Equal("#/components/schemas/Problem",
                    operation.GetProperty("responses").GetProperty("403").GetProperty("content")
                        .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString());
            if (expected.Suffix.Contains("{proposalId", StringComparison.Ordinal))
                Assert.Equal("uuid", Assert.Single(parameters, item => item.GetProperty("name").GetString() == "proposalId")
                    .GetProperty("schema").GetProperty("format").GetString());
        }

        var import = paths.GetProperty("/api/v1/projects/{projectId}/casting/import").GetProperty("post");
        Assert.Equal(1_048_576, import.GetProperty("x-agentweaver-max-request-body-bytes").GetInt32());
        Assert.Equal(1_048_576L, gateway.GetRequestSizeLimit("/api/v1/projects/{projectId}/casting/import")?.MaxRequestBodySize);
        Assert.True(import.GetProperty("responses").TryGetProperty("413", out _));
        Assert.True(import.GetProperty("responses").TryGetProperty("422", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[]
        {
            "CreateProjectCastingProposalRequest", "CreateScenarioProjectCastingProposalRequest",
            "CreateManualProjectCastingProposalRequest", "UpdateProjectCastingProposalRequest",
            "TransitionProjectCastingProposalRequest", "ImportProjectCastingTransferRequest",
            "ProjectCastingProposal", "ProjectCastingTransfer", "ProjectCastingTransferProvenance",
            "ProjectCastingUniverseList", "ProjectCastingTemplateDefinition", "ProjectCastingRoleDefinition",
        })
            Assert.False(schemas.GetProperty(name).GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(["pending", "confirmed", "rejected"], schemas.GetProperty("ProjectCastingProposal")
            .GetProperty("properties").GetProperty("state").GetProperty("enum").EnumerateArray()
            .Select(item => item.GetString()!).ToArray());
        Assert.Equal(1, schemas.GetProperty("ProjectCastingTransfer").GetProperty("properties")
            .GetProperty("formatVersion").GetProperty("const").GetInt32());
        Assert.Equal("array", schemas.GetProperty("ProjectCastingUniverseList").GetProperty("properties")
            .GetProperty("universes").GetProperty("type").GetString());
        var scenarioRequest = schemas.GetProperty("CreateScenarioProjectCastingProposalRequest");
        Assert.True(scenarioRequest.GetProperty("properties").TryGetProperty("universe", out _));
        Assert.DoesNotContain("universe", scenarioRequest.GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()!));
        Assert.Equal(100, paths.GetProperty("/api/v1/projects/{projectId}/casting/proposals").GetProperty("get")
            .GetProperty("parameters").EnumerateArray().Single(item => item.GetProperty("name").GetString() == "limit")
            .GetProperty("schema").GetProperty("maximum").GetInt32());
    }

    /// <summary>The proxy preserves every casting method, body, query, bearer, tenant, and owner conflict.</summary>
    [Fact]
    public async Task CastingRequestsForwardOnlyToProjectsAndPreserveOwnerResponses()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = new JwtSecurityToken(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test", [new Claim("sub", "casting-actor"), new Claim("scope", "api.read projects.admin")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var bearer = new JwtSecurityTokenHandler().WriteToken(token);
        var calls = new List<(string Method, string Path, string? Bearer, string? Tenant, string? Body)>();
        await using var gateway = await GatewayResourceServer.StartAsync(signingKey,
            () => new RecordingProjectsHandler(async (request, cancellationToken) =>
            {
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenants);
                calls.Add((request.Method.Method, request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(), tenants?.SingleOrDefault(),
                    request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent("""{"code":"casting_configuration_conflict"}""", Encoding.UTF8, "application/problem+json"),
                };
            }),
            (_, _, _) => throw new InvalidOperationException("Casting must call Projects only."));

        foreach (var route in Routes)
        {
            var suffix = route.Suffix.Replace("{projectId}", "project-1", StringComparison.Ordinal)
                .Replace("{proposalId:guid}", "11111111-1111-1111-1111-111111111111", StringComparison.Ordinal);
            var query = route.Operation switch
            {
                "listProjectCastingProposals" => "?limit=7",
                "exportProjectCasting" => "?revision=3",
                _ => "",
            };
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), "/api/v1" + suffix + query);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            if (route.Request is not null)
                request.Content = new StringContent("""{"expectedDraftRevision":3}""", Encoding.UTF8, "application/json");
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("""{"code":"casting_configuration_conflict"}""", await response.Content.ReadAsStringAsync());
            var call = calls[^1];
            Assert.Equal(route.Method, call.Method);
            Assert.Equal("/api" + suffix + query, call.Path);
            Assert.Equal("Bearer " + bearer, call.Bearer);
            Assert.Equal("tenant-1", call.Tenant);
            Assert.Equal(route.Request is null ? null : """{"expectedDraftRevision":3}""", call.Body);
        }
        Assert.Equal(Routes.Length, calls.Count);
        using var anonymous = await gateway.Client.GetAsync("/api/v1/casting/templates");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(Routes.Length, calls.Count);
    }

    /// <summary>Enforces casting and skill-content bounds before Projects, with or without a length header.</summary>
    [Theory]
    [InlineData(-1, false, false)]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(-1, true, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(-1, false, true)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(-1, true, true)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    public async Task CastingAndSkillImportsEnforceBodyLimitsBeforeForwarding(
        int sizeOffset, bool unknownLength, bool skillContent)
    {
        var maximumBytes = skillContent ? 3 * 1_048_576 : 1_048_576;
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = new JwtSecurityToken(new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test", [new Claim("sub", "casting-import-actor"), new Claim("scope", "api.read projects.admin")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var calls = 0;
        var body = Encoding.UTF8.GetBytes("{}" + new string(' ', maximumBytes + sizeOffset - 2));
        await using var gateway = await GatewayResourceServer.StartAsync(signingKey,
            () => new RecordingProjectsHandler(async (request, cancellationToken) =>
            {
                calls++;
                Assert.Equal(body, await request.Content!.ReadAsByteArrayAsync(cancellationToken));
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json"),
                };
            }),
            (_, _, _) => throw new InvalidOperationException("Casting must call Projects only."));
        var path = skillContent ? "/api/v1/projects/project-1/skills/import" : "/api/v1/projects/project-1/casting/import";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new ImportBodyContent(body, unknownLength),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(sizeOffset > 0 ? HttpStatusCode.RequestEntityTooLarge : HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(sizeOffset > 0 ? 0 : 1, calls);
        if (sizeOffset > 0)
        {
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("request_body_too_large", problem.RootElement.GetProperty("code").GetString());
        }
    }

    /// <summary>Streams the same JSON bytes with or without a computable Content-Length.</summary>
    private sealed class ImportBodyContent(byte[] body, bool unknownLength) : HttpContent
    {
        /// <summary>Writes the exact boundary payload to the outgoing request.</summary>
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        /// <summary>Exercises both declared-length and streaming request limits.</summary>
        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return !unknownLength;
        }
    }

    /// <summary>Reads the shared JSON response or request schema reference.</summary>
    private static string? SchemaReference(JsonElement value) =>
        value.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();

    /// <summary>Records Projects calls without introducing another owner or external service.</summary>
    private sealed class RecordingProjectsHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        /// <summary>Delivers a proxied request to the in-process assertion handler.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
