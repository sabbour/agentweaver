extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatewayOwner = GatewayHost::Agentweaver.Gateway.GatewayOwner;
using GatewayRouteCatalog = GatewayHost::Agentweaver.Gateway.GatewayRouteCatalog;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayEnvironmentRecoveryTests
{
    [Fact]
    public async Task EnvironmentRoutesForwardTheirExactOwnerRequestsAndCallerContext()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var token = CreateToken(key);
        var forwarded = new List<(GatewayOwner Owner, string Method, string Uri, string? Bearer, string? Tenant, string? Body)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            async (owner, request, cancellationToken) =>
            {
                forwarded.Add((
                    owner,
                    request.Method.Method,
                    request.RequestUri!.PathAndQuery,
                    request.Headers.Authorization?.Parameter,
                    request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenant) ? tenant.Single() : null,
                    request.Content is null
                        ? null
                        : await request.Content.ReadAsStringAsync(cancellationToken)));
                return Json(HttpStatusCode.OK, """{"result":"forwarded"}""");
            },
            environmentOwner: new Uri("https://environment.test/"));

        var environmentRoutes = GatewayRouteCatalog.Routes
            .Where(route => route.Owner == GatewayOwner.Environment &&
                route.OperationId != "listRemoteMcpConnections")
            .ToArray();
        foreach (var route in environmentRoutes)
        {
            var path = ReplaceRouteValues(route.PublicPath);
            var query = route.QueryParameters.Length == 0
                ? string.Empty
                : "?" + string.Join("&", route.QueryParameters.Select(name => Uri.EscapeDataString(name) + "=" +
                    Uri.EscapeDataString(name == "networkPolicyGeneration" ? "1" :
                        name == "limit" ? "5" : "cursor")));
            var body = route.HasJsonBody ? """{"attempt":"unchanged"}""" : null;
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), path + query);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(environmentRoutes.Length, forwarded.Count);
        foreach (var route in environmentRoutes)
        {
            var expectedQuery = route.QueryParameters.Length == 0
                ? string.Empty
                : "?" + string.Join("&", route.QueryParameters.Select(name => Uri.EscapeDataString(name) + "=" +
                    Uri.EscapeDataString(name == "networkPolicyGeneration" ? "1" :
                        name == "limit" ? "5" : "cursor")));
            var expectedOwnerUri = ReplaceRouteValues(route.OwnerPath) + expectedQuery;
            var forwardedRequest = Assert.Single(forwarded, call =>
                call.Uri == expectedOwnerUri);
            Assert.Equal(GatewayOwner.Environment, forwardedRequest.Owner);
            Assert.Equal(route.Method, forwardedRequest.Method);
            Assert.Equal(token, forwardedRequest.Bearer);
            Assert.Equal("tenant-a", forwardedRequest.Tenant);
            Assert.Equal(route.HasJsonBody ? """{"attempt":"unchanged"}""" : null, forwardedRequest.Body);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task EnvironmentOwnerStatusesAndBodiesPassThrough(HttpStatusCode ownerStatus)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        const string body = """{"code":"owner_result"}""";
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) => Task.FromResult(Json(ownerStatus, body)),
            environmentOwner: new Uri("https://environment.test/"));
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/projects/project-a/runs/run-a/environments?limit=5");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(ownerStatus, response.StatusCode);
        Assert.Equal(body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task InvalidAuthenticationStopsBeforeEnvironmentOwnerCall()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                Interlocked.Increment(ref ownerCalls);
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
            },
            environmentOwner: new Uri("https://environment.test/"));

        using var response = await gateway.Client.GetAsync(
            "/api/v1/projects/project-a/runs/run-a/environments");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, ownerCalls);
    }

    [Fact]
    public async Task MissingEnvironmentOwnerReturns503WithoutCallingAnOwner()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                Interlocked.Increment(ref ownerCalls);
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
            });
        using var request = new HttpRequestMessage(
            HttpMethod.Get, "/api/v1/projects/project-a/runs/run-a/environments");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("owner_not_configured", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, ownerCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvironmentRecoveryWritesRejectOverLimitBodiesBeforeForwarding(bool chunked)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var ownerCalls = 0;
        await using var gateway = await GatewayResourceServer.StartAsync(
            key,
            () => new HttpClientHandler(),
            (_, _, _) =>
            {
                Interlocked.Increment(ref ownerCalls);
                return Task.FromResult(Json(HttpStatusCode.OK, "{}"));
            },
            environmentOwner: new Uri("https://environment.test/"));
        var body = Encoding.UTF8.GetBytes("{}" + new string(' ', 16 * 1024 - 1));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/projects/project-a/runs/run-a/environments/environment-a/workspace-volumes/volume-a/bind")
        {
            Content = new EnvironmentRecoveryBodyContent(body, chunked),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(key));

        using var response = await gateway.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request_body_too_large", problem.RootElement.GetProperty("code").GetString());
        Assert.Equal(0, ownerCalls);
    }

    [Fact]
    public async Task ServedOpenApiDescribesEnvironmentDiscoveryRecoveryAndErrors()
    {
        await using var gateway = await StartAsync();
        using var response = await gateway.Client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var environmentRoutes = GatewayRouteCatalog.Routes
            .Where(route => route.Owner == GatewayOwner.Environment &&
                route.OperationId != "listRemoteMcpConnections")
            .ToArray();
        Assert.Equal(13, environmentRoutes.Length);

        foreach (var route in environmentRoutes)
        {
            var path = route.PublicPath.Replace(":guid", "", StringComparison.Ordinal);
            var operation = root.GetProperty("paths").GetProperty(path).GetProperty(route.Method.ToLowerInvariant());
            Assert.Equal(route.OperationId, operation.GetProperty("operationId").GetString());
            Assert.Equal("Environment", operation.GetProperty("x-agentweaver-owner").GetString());
            Assert.Equal(route.OwnerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            Assert.True(operation.GetProperty("x-agentweaver-mcp-excluded").GetBoolean());
            Assert.Contains(operation.GetProperty("parameters").EnumerateArray(), parameter =>
                parameter.GetProperty("name").GetString() == "X-Agentweaver-Tenant" &&
                !parameter.GetProperty("required").GetBoolean());
            Assert.True(operation.GetProperty("responses").TryGetProperty("403", out _));
            Assert.True(operation.GetProperty("responses").TryGetProperty("503", out _));
            Assert.Contains("Cache-Control", operation.GetProperty("responses").GetProperty("200")
                .GetProperty("headers").EnumerateObject().Select(header => header.Name));
        }

        foreach (var schemaName in new[]
        {
            "EnvironmentApiError", "EnvironmentRunSummary", "EnvironmentLifecycleSnapshot",
            "EnvironmentWorkspaceVolumeStatus", "EnvironmentWorkspaceVolumeRetryDescriptor",
            "EnvironmentWorkspaceVolumeTransitionStatus", "EnvironmentWorkspaceVolumeCleanupStatus",
            "EnvironmentSandboxResult", "EnvironmentSandboxStartupPhaseObservation",
            "EnvironmentSandboxObservationSummary", "EnvironmentSandboxTerminalEvidence",
            "EnvironmentSandboxStartupBudgetFailure", "SandboxProviderAttemptReceipt",
            "EnvironmentSandboxReleaseReceipt",
        })
            AssertSchemaEnumValuesUseCamelCase(schemas.GetProperty(schemaName));

        var effectKinds = schemas.GetProperty("SandboxProviderAttemptReceipt").GetProperty("properties")
            .GetProperty("effectKind").GetProperty("enum").EnumerateArray()
            .Select(value => value.GetString()!).ToArray();
        Assert.Equal(
            ["provision", "release", "partialRelease", "lateResourceRelease", "legacyUnknown"],
            effectKinds);

        var pagedList = root.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs/{runId}/environments").GetProperty("get");
        var limit = Assert.Single(pagedList.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "limit");
        Assert.Equal(1, limit.GetProperty("schema").GetProperty("minimum").GetInt32());
        Assert.Equal(100, limit.GetProperty("schema").GetProperty("maximum").GetInt32());
        Assert.Equal(25, limit.GetProperty("schema").GetProperty("default").GetInt32());
        var runListDescription = pagedList.GetProperty("description").GetString();
        Assert.Contains("is bound to the tenant, project, and run", runListDescription, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycle generation", runListDescription, StringComparison.Ordinal);

        var volumeListDescription = root.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs/{runId}/environments/{environmentId}/workspace-volumes")
            .GetProperty("get").GetProperty("description").GetString();
        Assert.Contains("current lifecycle generation", volumeListDescription, StringComparison.Ordinal);

        var sandboxRead = root.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox")
            .GetProperty("get");
        var requiredPolicyGeneration = Assert.Single(sandboxRead.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "networkPolicyGeneration");
        Assert.True(requiredPolicyGeneration.GetProperty("required").GetBoolean());
        Assert.Equal(1, requiredPolicyGeneration.GetProperty("schema").GetProperty("minimum").GetInt64());

        var reconcile = root.GetProperty("paths")
            .GetProperty("/api/v1/projects/{projectId}/runs/{runId}/environments/{environmentId}/sandbox/reconcile")
            .GetProperty("post");
        Assert.False(Assert.Single(reconcile.GetProperty("parameters").EnumerateArray(),
            parameter => parameter.GetProperty("name").GetString() == "networkPolicyGeneration")
            .GetProperty("required").GetBoolean());
        Assert.Equal(JsonValueKind.Null, reconcile.GetProperty("requestBody").ValueKind);

        var transition = schemas.GetProperty("EnvironmentWorkspaceVolumeTransitionRequest");
        Assert.False(transition.GetProperty("additionalProperties").GetBoolean());
        Assert.False(transition.GetProperty("properties").TryGetProperty("nextDataGeneration", out _));
        Assert.False(transition.GetProperty("properties").TryGetProperty("p1Binding", out _));
        Assert.Equal(0, transition.GetProperty("properties").GetProperty("expectedDataGeneration")
            .GetProperty("minimum").GetInt64());
        var idempotencyPattern = transition.GetProperty("properties").GetProperty("idempotencyKey")
            .GetProperty("pattern").GetString()!;
        var idempotencyRegex = new System.Text.RegularExpressions.Regex(
            idempotencyPattern,
            System.Text.RegularExpressions.RegexOptions.ECMAScript);
        Assert.Matches(idempotencyRegex, "volume operation 1");
        Assert.DoesNotMatch(idempotencyRegex, " ");
        Assert.DoesNotMatch(idempotencyRegex, "\u0085");
        var sandboxProvision = schemas.GetProperty("EnvironmentSandboxProvisionRequest").GetProperty("properties");
        Assert.Equal(0, sandboxProvision.GetProperty("dataGeneration").GetProperty("minimum").GetInt64());
        Assert.Equal(1, sandboxProvision.GetProperty("networkPolicyGeneration").GetProperty("minimum").GetInt64());

        foreach (var route in environmentRoutes.Where(route => route.HasJsonBody))
        {
            var operation = root.GetProperty("paths")
                .GetProperty(route.PublicPath.Replace(":guid", "", StringComparison.Ordinal))
                .GetProperty(route.Method.ToLowerInvariant());
            Assert.Equal(16 * 1024, operation.GetProperty("x-agentweaver-max-request-body-bytes").GetInt64());
            Assert.True(operation.GetProperty("responses").TryGetProperty("413", out _));
        }

        foreach (var operationId in new[]
        {
            "readEnvironmentWorkspaceVolume",
            "listEnvironmentWorkspaceVolumes",
            "readEnvironmentSandboxProviderAttempts",
            "readEnvironmentSandboxLateResourceCleanups",
        })
        {
            var operation = root.GetProperty("paths").EnumerateObject()
                .SelectMany(path => path.Value.EnumerateObject())
                .Select(candidate => candidate.Value)
                .Single(candidate => candidate.ValueKind == JsonValueKind.Object &&
                    candidate.TryGetProperty("operationId", out var id) && id.GetString() == operationId);
            Assert.True(operation.GetProperty("responses").TryGetProperty("409", out _));
        }
        var attemptsRead = root.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject())
            .Select(candidate => candidate.Value)
            .Single(candidate => candidate.ValueKind == JsonValueKind.Object &&
                candidate.TryGetProperty("operationId", out var id) &&
                id.GetString() == "readEnvironmentSandboxProviderAttempts");
        Assert.True(attemptsRead.GetProperty("responses").TryGetProperty("400", out _));

        var errorSchema = schemas.GetProperty("EnvironmentApiErrorOrProblem");
        Assert.Equal(JsonValueKind.Array, errorSchema.GetProperty("oneOf").ValueKind);
    }

    private static void AssertSchemaEnumValuesUseCamelCase(JsonElement schema)
    {
        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("enum", out var values))
            {
                foreach (var value in values.EnumerateArray())
                {
                    var text = value.GetString()!;
                    Assert.Equal(JsonNamingPolicy.CamelCase.ConvertName(text), text);
                }
            }

            foreach (var property in schema.EnumerateObject().Where(property => property.Name != "enum"))
                AssertSchemaEnumValuesUseCamelCase(property.Value);
        }
        else if (schema.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in schema.EnumerateArray())
                AssertSchemaEnumValuesUseCamelCase(item);
        }
    }

    private static async Task<GatewayResourceServer> StartAsync()
    {
        using var rsa = RSA.Create(2048);
        return await GatewayResourceServer.StartAsync(
            new RsaSecurityKey(rsa.ExportParameters(true)),
            () => new HttpClientHandler(),
            (_, _, _) => throw new InvalidOperationException("OpenAPI checks must not call an owner."));
    }

    private static string ReplaceRouteValues(string path) =>
        System.Text.RegularExpressions.Regex.Replace(
            path,
            @"\{(?<name>[A-Za-z][A-Za-z0-9]*)(?::[^}]+)?\}",
            match => match.Groups["name"].Value switch
            {
                "projectId" => "project-a",
                "runId" => "run-a",
                "environmentId" => "environment-a",
                "volumeId" => "volume-a",
                "operationId" => "ea222cae-692a-49e1-93ec-2b9d36b75be6",
                _ => throw new InvalidOperationException("Unexpected Environment route parameter."),
            });

    private static string CreateToken(SecurityKey key)
    {
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "environment-recovery-test")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        token.Header["typ"] = "at+jwt";
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class EnvironmentRecoveryBodyContent(byte[] body, bool chunked) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(body).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return !chunked;
        }
    }
}
