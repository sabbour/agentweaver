extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

/// <summary>Protects the Gateway contract and owner forwarding for explicit project team sync.</summary>
public sealed class GatewayTeamSyncRegressionTests
{
    private const string SelectionCode = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task TeamSyncRoutesExposeStrictOwnerContractsAndRequiredSelectionHeader()
    {
        using var rsa = RSA.Create(2048);
        await using var gateway = await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI must not call an owner."));
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        var expected = new[]
        {
            ("GET", "readProjectTeamSync", "ProjectTeamSyncStatusResponse", (string?)null),
            ("POST", "commitProjectTeamSync", "ProjectTeamSyncCommitResponse", "ProjectTeamSyncCommitCommand"),
        };

        // Check catalog, OpenAPI, and request bounds independently so contract drift cannot be self-consistent.
        foreach (var (method, operationId, responseSchema, requestSchema) in expected)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == operationId);
            Assert.Equal(method, route.Method);
            Assert.Equal("/api/v1/projects/{projectId}/team/sync", route.PublicPath);
            Assert.Equal("/api/projects/{projectId}/team/sync", route.OwnerPath);
            Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.Equal(
                new[] { ProjectTeamSyncContract.SelectionCodeHeader },
                route.ForwardRequestHeaders.ToArray());

            var operation = paths.GetProperty(route.PublicPath).GetProperty(method.ToLowerInvariant());
            Assert.Equal(operationId, operation.GetProperty("operationId").GetString());
            Assert.Equal("Orchestrator", operation.GetProperty("x-agentweaver-owner").GetString());
            Assert.Equal(route.OwnerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            var selectionHeader = Assert.Single(parameters,
                item => item.GetProperty("name").GetString() == ProjectTeamSyncContract.SelectionCodeHeader);
            Assert.Equal("header", selectionHeader.GetProperty("in").GetString());
            Assert.True(selectionHeader.GetProperty("required").GetBoolean());
            Assert.Equal(64, selectionHeader.GetProperty("schema").GetProperty("minLength").GetInt32());
            Assert.Equal("#/components/schemas/" + responseSchema,
                SchemaReference(operation.GetProperty("responses").GetProperty("200")));
            Assert.Equal("#/components/schemas/Problem",
                operation.GetProperty("responses").GetProperty("403").GetProperty("content")
                    .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.False(schemas.GetProperty(responseSchema)
                .GetProperty("additionalProperties").GetBoolean());
            if (requestSchema is not null)
            {
                Assert.True(route.HasJsonBody);
                Assert.Equal(4096, route.MaximumRequestBodyBytes);
                Assert.Equal("#/components/schemas/" + requestSchema,
                    SchemaReference(operation.GetProperty("requestBody")));
                Assert.False(schemas.GetProperty(requestSchema)
                    .GetProperty("additionalProperties").GetBoolean());
                Assert.Equal(200, schemas.GetProperty(requestSchema).GetProperty("properties")
                    .GetProperty("message").GetProperty("anyOf")[0].GetProperty("maxLength").GetInt32());
            }
            else
            {
                Assert.False(route.HasJsonBody);
            }
        }
        var postEndpoint = Assert.Single(
            gateway.Endpoints.OfType<RouteEndpoint>(),
            endpoint => endpoint.RoutePattern.RawText == "/api/v1/projects/{projectId}/team/sync" &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("POST") == true);
        Assert.Equal(4096L, postEndpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize);

        using var preflight = new HttpRequestMessage(
            HttpMethod.Options, "/api/v1/projects/project-1/team/sync");
        preflight.Headers.Add("Origin", "https://web.test");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers",
            "authorization,content-type,x-agentweaver-tenant," + ProjectTeamSyncContract.SelectionCodeHeader);
        using var cors = await gateway.Client.SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, cors.StatusCode);
        var allowedHeaders = string.Join(',', cors.Headers.GetValues("Access-Control-Allow-Headers"))
            .Split(',', StringSplitOptions.TrimEntries);
        Assert.Contains(allowedHeaders,
            header => header.Equals(ProjectTeamSyncContract.SelectionCodeHeader, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RequestsForwardBearerTenantSelectionAndBodyOnlyToOrchestrator()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "team-sync-actor"), new Claim("scope", "api.read projects.admin")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var bearer = new JwtSecurityTokenHandler().WriteToken(token);
        var calls = new List<(GatewayOwner Owner, string Method, string Path, string? Bearer, string? Tenant, string? Selection, string? Body)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            async (owner, request, cancellationToken) =>
            {
                request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
                request.Headers.TryGetValues(ProjectTeamSyncContract.SelectionCodeHeader, out var selectionValues);
                calls.Add((
                    owner,
                    request.Method.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.ToString(),
                    tenantValues?.SingleOrDefault(),
                    selectionValues?.SingleOrDefault(),
                    request.Content is null
                        ? null
                        : await request.Content.ReadAsStringAsync(cancellationToken)));
                return new HttpResponseMessage(HttpStatusCode.Conflict)
                {
                    Content = new StringContent(
                        """{"code":"team_sync_change_set_changed"}""",
                        Encoding.UTF8,
                        "application/problem+json"),
                };
            });

        var commitBody = """{"expectedChangeSetHash":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","expectedConfigurationRevision":7}""";
        var cases = new[]
        {
            (HttpMethod.Get, (string?)null),
            (HttpMethod.Post, commitBody),
        };
        foreach (var (method, body) in cases)
        {
            using var request = new HttpRequestMessage(
                method, "/api/v1/projects/project-1/team/sync");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
            request.Headers.Add(ProjectTeamSyncContract.SelectionCodeHeader, SelectionCode);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("""{"code":"team_sync_change_set_changed"}""",
                await response.Content.ReadAsStringAsync());
            var call = calls[^1];
            Assert.Equal(GatewayOwner.Orchestrator, call.Owner);
            Assert.Equal(method.Method, call.Method);
            Assert.Equal("/api/projects/project-1/team/sync", call.Path);
            Assert.Equal("Bearer " + bearer, call.Bearer);
            Assert.Equal("tenant-1", call.Tenant);
            Assert.Equal(SelectionCode, call.Selection);
            Assert.Equal(body, call.Body);
        }

        Assert.Equal(2, calls.Count);
        using var anonymous = await gateway.Client.GetAsync("/api/v1/projects/project-1/team/sync");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(2, calls.Count);
    }

    private static string? SchemaReference(JsonElement value) =>
        value.GetProperty("content").GetProperty("application/json")
            .GetProperty("schema").GetProperty("$ref").GetString();
}
