extern alias GatewayHost;

using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.SourceControl;
using GatewayHost::Agentweaver.Gateway;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Agentweaver.Identity.Broker.Tests;

public sealed class GatewayWorkspaceReadRegressionTests
{
    private const string SelectionCode = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void ExecutionPlanRouteForwardsNoStoreAndUsesTheOwnerResponseLimit()
    {
        var route = Assert.Single(GatewayRouteCatalog.Routes, item =>
            item.OperationId == "readExecutionPlan");

        Assert.Equal("GET", route.Method);
        Assert.Equal(
            "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/execution-plan",
            route.PublicPath);
        Assert.Equal(
            "/api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/execution-plan",
            route.OwnerPath);
        Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
        Assert.Contains("Cache-Control", route.ExposedResponseHeaders);
        Assert.Equal(8 * 1024 * 1024, route.MaximumResponseBytes);
    }

    [Fact]
    public void RecoveryEventsRouteForwardsNoStoreAndUsesTheOwnerResponseLimit()
    {
        var route = Assert.Single(GatewayRouteCatalog.Routes, item =>
            item.OperationId == "readCheckpointRecoveryEvents");
        Assert.Equal("GET", route.Method);
        Assert.Equal(
            "/api/v1/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/checkpoint-recoveries/{operationId}/events",
            route.PublicPath);
        Assert.Equal(
            "/api/projects/{projectId}/runs/{runId}/coordination/sessions/{sessionId}/checkpoint-recoveries/{operationId}/events",
            route.OwnerPath);
        Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
        Assert.Equal(new[] { "cursor", "limit" }, route.QueryParameters);
        Assert.Contains("Cache-Control", route.ExposedResponseHeaders);
        Assert.Equal(8 * 1024 * 1024, route.MaximumResponseBytes);
    }

    [Fact]
    public void PlatformPolicyRoutesAreBoundedAndExcludedFromMcp()
    {
        var routes = new[]
        {
            ("GET", "/api/v1/policies/platform", "readPlatformNativeAcsPolicy"),
            ("GET", "/api/v1/policies/platform/revisions", "listPlatformNativeAcsPolicyRevisions"),
            ("GET", "/api/v1/policies/platform/revisions/{revision}", "readPlatformNativeAcsPolicyRevision"),
            ("POST", "/api/v1/policies/platform/validate", "validatePlatformNativeAcsPolicy"),
            ("PUT", "/api/v1/policies/platform", "writePlatformNativeAcsPolicy"),
        };

        foreach (var (method, path, operationId) in routes)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == operationId);
            Assert.Equal(method, route.Method);
            Assert.Equal(path, route.PublicPath);
            Assert.Equal(GatewayOwner.Projects, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.True(route.ExcludeFromMcp);
            Assert.Contains("Cache-Control", route.ExposedResponseHeaders);
            Assert.Equal(5 * 1024 * 1024, route.MaximumResponseBytes);
            Assert.Equal(method != "GET", route.HasJsonBody);
            Assert.True(route.ForwardRequestHeaders.IsDefaultOrEmpty);
            Assert.Equal(
                method == "GET" ? null : 5 * 1024 * 1024,
                route.MaximumRequestBodyBytes);
        }
        Assert.Equal(
            new[] { "pageSize", "cursor" },
            Assert.Single(GatewayRouteCatalog.Routes, item =>
                item.OperationId == "listPlatformNativeAcsPolicyRevisions").QueryParameters);
        Assert.Empty(Assert.Single(GatewayRouteCatalog.Routes, item =>
            item.OperationId == "readPlatformNativeAcsPolicyRevision").QueryParameters);
    }

    [Fact]
    public async Task PlatformPolicyRevisionRoutesEnforceQueryAndHeaderForwarding()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        var forwarded = new List<(
            string Path,
            string? Tenant,
            bool Bearer,
            bool Accept,
            bool Idempotency,
            bool IfMatch,
            bool CallerHeader,
            bool ProjectAuthorization,
            bool HasBody)>();
        Task<HttpResponseMessage> RespondAsync(
            GatewayOwner owner,
            HttpRequestMessage request,
            CancellationToken _)
        {
            Assert.Equal(GatewayOwner.Projects, owner);
            request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
            forwarded.Add((
                request.RequestUri!.PathAndQuery,
                tenantValues?.SingleOrDefault(),
                request.Headers.Authorization?.Scheme == "Bearer",
                request.Headers.Accept.Any(value => value.MediaType == "application/json"),
                request.Headers.Contains("Idempotency-Key"),
                request.Headers.Contains("If-Match"),
                request.Headers.Contains("X-Caller-Header"),
                request.Headers.Contains("X-Agentweaver-Project-Authorization"),
                request.Content is not null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new CallbackHttpMessageHandler((request, cancellationToken) =>
                RespondAsync(GatewayOwner.Projects, request, cancellationToken)),
            RespondAsync);

        using (var history = CreateRequest(
            bearer, "/api/v1/policies/platform/revisions?pageSize=7&cursor=opaque"))
        {
            history.Headers.Add("Idempotency-Key", "not-forwarded");
            history.Headers.Add("If-Match", "\"not-forwarded\"");
            history.Headers.Add("X-Caller-Header", "not-forwarded");
            history.Headers.Remove("X-Agentweaver-Tenant");
            history.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
            history.Content = new StringContent("ignored");
            using var response = await gateway.Client.SendAsync(history);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        using (var exact = CreateRequest(bearer, "/api/v1/policies/platform/revisions/3"))
        {
            exact.Headers.Add("Idempotency-Key", "not-forwarded");
            exact.Headers.Add("If-Match", "\"not-forwarded\"");
            exact.Headers.Add("X-Caller-Header", "not-forwarded");
            exact.Headers.Remove("X-Agentweaver-Tenant");
            exact.Headers.Add("X-Agentweaver-Tenant", "tenant-b");
            using var response = await gateway.Client.SendAsync(exact);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var invalidHistory = CreateRequest(
            bearer, "/api/v1/policies/platform/revisions?pageSize=7&unknown=value"))
        using (var rejected = await gateway.Client.SendAsync(invalidHistory))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.True(rejected.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(
                "native_acs_policy_history_request_invalid",
                body.RootElement.GetProperty("code").GetString());
        }

        using (var invalidExact = CreateRequest(
            bearer, "/api/v1/policies/platform/revisions/3?pageSize=7"))
        using (var rejected = await gateway.Client.SendAsync(invalidExact))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal(
                "native_acs_policy_revision_request_invalid",
                body.RootElement.GetProperty("code").GetString());
        }

        using (var existing = CreateRequest(bearer, "/api/v1/policies/platform"))
        {
            existing.Headers.Remove("X-Agentweaver-Tenant");
            existing.Headers.Add("X-Agentweaver-Tenant", "tenant-head");
            existing.Headers.Add("Idempotency-Key", "existing-key");
            existing.Headers.Add("If-Match", "\"4\"");
            existing.Headers.Add("X-Agentweaver-Project-Authorization", "not-forwarded");
            using var response = await gateway.Client.SendAsync(existing);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        foreach (var path in new[] { "/api/v1/policies/platform?unknown=x", "/api/v1/policies/platform?a=1&a=2" })
        {
            using var invalid = CreateRequest(bearer, path);
            using var response = await gateway.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_query_not_supported", body.RootElement.GetProperty("code").GetString());
        }
        using (var invalid = CreateRequest(bearer, "/api/v1/policies/platform"))
        {
            invalid.Content = new StringContent("body");
            using var response = await gateway.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_read_request_invalid", body.RootElement.GetProperty("code").GetString());
        }
        using (var invalid = CreateRequest(bearer, "/api/v1/policies/platform"))
        {
            invalid.Content = new StringContent("");
            using var response = await gateway.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_read_request_invalid", body.RootElement.GetProperty("code").GetString());
        }
        using (var invalid = CreateRequest(bearer, "/api/v1/policies/platform"))
        {
            invalid.Headers.TransferEncodingChunked = true;
            invalid.Content = new ByteArrayContent([1]);
            using var response = await gateway.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_read_request_invalid", body.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(3, forwarded.Count);
        Assert.Equal(
            "/api/policies/platform/revisions?pageSize=7&cursor=opaque",
            forwarded[0].Path);
        Assert.Equal("/api/policies/platform/revisions/3", forwarded[1].Path);
        Assert.Equal("tenant-a", forwarded[0].Tenant);
        Assert.Equal("tenant-b", forwarded[1].Tenant);
        Assert.All(forwarded.Take(2), request =>
        {
            Assert.True(request.Bearer);
            Assert.True(request.Accept);
            Assert.False(request.Idempotency);
            Assert.False(request.IfMatch);
            Assert.False(request.CallerHeader);
            Assert.False(request.ProjectAuthorization);
            Assert.False(request.HasBody);
        });
        Assert.False(forwarded[2].Idempotency);
        Assert.False(forwarded[2].IfMatch);
        Assert.True(forwarded[2].Bearer);
        Assert.Equal("tenant-head", forwarded[2].Tenant);
        Assert.False(forwarded[2].ProjectAuthorization);
        Assert.False(forwarded[2].HasBody);
    }

    [Theory]
    [InlineData(5 * 1024 * 1024, HttpStatusCode.OK)]
    [InlineData(5 * 1024 * 1024 + 1, HttpStatusCode.BadGateway)]
    public async Task PlatformPolicyHeadEnforcesGatewayResponseLimit(int length, HttpStatusCode expected)
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        Task<HttpResponseMessage> RespondAsync(GatewayOwner _, HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[length]),
            });
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new CallbackHttpMessageHandler((request, cancellationToken) =>
                RespondAsync(GatewayOwner.Projects, request, cancellationToken)),
            RespondAsync);
        using var request = CreateRequest(bearer, "/api/v1/policies/platform");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
            Assert.Equal(length, (await response.Content.ReadAsByteArrayAsync()).Length);
        else
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("owner_response_too_large", body.RootElement.GetProperty("code").GetString());
        }
    }

    [Fact]
    public void ProjectPolicyRoutesAreBoundedAndExcludedFromMcp()
    {
        var routes = new[]
        {
            ("GET", "/api/v1/policies/projects", "listProjectNativeAcsPolicies"),
            ("GET", "/api/v1/policies/projects/{projectId}", "readProjectNativeAcsPolicy"),
            ("GET", "/api/v1/policies/projects/{projectId}/revisions", "listProjectNativeAcsPolicyRevisions"),
            ("GET", "/api/v1/policies/projects/{projectId}/revisions/{revision}",
                "readProjectNativeAcsPolicyRevision"),
            ("POST", "/api/v1/policies/projects/{projectId}/validate", "validateProjectNativeAcsPolicy"),
        };

        foreach (var (method, path, operationId) in routes)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == operationId);
            Assert.Equal(method, route.Method);
            Assert.Equal(path, route.PublicPath);
            Assert.Equal(GatewayOwner.Projects, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.True(route.ExcludeFromMcp);
            Assert.Contains("Cache-Control", route.ExposedResponseHeaders);
            Assert.Equal(5 * 1024 * 1024, route.MaximumResponseBytes);
            Assert.Equal(method != "GET", route.HasJsonBody);
            Assert.True(route.ForwardRequestHeaders.IsDefaultOrEmpty);
            Assert.Equal(
                method == "GET" ? null : 5 * 1024 * 1024,
                route.MaximumRequestBodyBytes);
        }
        Assert.Equal(
            new[] { "pageSize", "cursor" },
            Assert.Single(GatewayRouteCatalog.Routes, item =>
                item.OperationId == "listProjectNativeAcsPolicies").QueryParameters);
        Assert.Equal(
            new[] { "pageSize", "cursor" },
            Assert.Single(GatewayRouteCatalog.Routes, item =>
                item.OperationId == "listProjectNativeAcsPolicyRevisions").QueryParameters);
        Assert.Empty(Assert.Single(GatewayRouteCatalog.Routes, item =>
            item.OperationId == "readProjectNativeAcsPolicy").QueryParameters);
        Assert.Empty(Assert.Single(GatewayRouteCatalog.Routes, item =>
            item.OperationId == "readProjectNativeAcsPolicyRevision").QueryParameters);
        Assert.Empty(Assert.Single(GatewayRouteCatalog.Routes, item =>
            item.OperationId == "validateProjectNativeAcsPolicy").QueryParameters);
    }

    [Fact]
    public async Task ProjectPolicyRoutesForwardTenantAndRejectUnknownQueryValues()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        var forwarded = new List<string>();
        Task<HttpResponseMessage> RespondAsync(
            GatewayOwner owner,
            HttpRequestMessage request,
            CancellationToken _)
        {
            Assert.Equal(GatewayOwner.Projects, owner);
            request.Headers.TryGetValues("X-Agentweaver-Tenant", out var tenantValues);
            forwarded.Add($"{request.RequestUri!.PathAndQuery}|{tenantValues?.SingleOrDefault()}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new CallbackHttpMessageHandler((request, cancellationToken) =>
                RespondAsync(GatewayOwner.Projects, request, cancellationToken)),
            RespondAsync);

        var requests = new[]
        {
            ("/api/v1/policies/projects?pageSize=7&cursor=next",
                "/api/policies/projects/?pageSize=7&cursor=next"),
            ("/api/v1/policies/projects/project-one", "/api/policies/projects/project-one"),
            ("/api/v1/policies/projects/project-one/revisions?pageSize=4",
                "/api/policies/projects/project-one/revisions?pageSize=4"),
            ("/api/v1/policies/projects/project-one/revisions/3",
                "/api/policies/projects/project-one/revisions/3"),
        };
        for (var index = 0; index < requests.Length; index++)
        {
            using var request = CreateRequest(bearer, requests[index].Item1);
            request.Headers.Remove("X-Agentweaver-Tenant");
            request.Headers.Add("X-Agentweaver-Tenant", "tenant-" + index);
            request.Headers.Add("Idempotency-Key", "not-forwarded");
            request.Headers.Add("If-Match", "\"not-forwarded\"");
            using var response = await gateway.Client.SendAsync(request);
            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"{requests[index].Item1}: {await response.Content.ReadAsStringAsync()}");
            Assert.True(response.Headers.CacheControl?.NoStore);
        }

        foreach (var (path, code) in new[]
        {
            ("/api/v1/policies/projects?unknown=value", "native_acs_policy_scope_list_request_invalid"),
            ("/api/v1/policies/projects/project-one?cursor=x", "native_acs_policy_query_not_supported"),
            ("/api/v1/policies/projects/project-one/revisions?unknown=value",
                "native_acs_policy_history_request_invalid"),
            ("/api/v1/policies/projects/project-one/revisions/3?pageSize=4",
                "native_acs_policy_revision_request_invalid"),
        })
        {
            using var request = CreateRequest(bearer, path);
            using var response = await gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(requests.Select((request, index) => request.Item2 + "|tenant-" + index), forwarded);
    }

    [Fact]
    public async Task ProjectPolicyValidationForwardsBoundedBodyAndRejectsQueries()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        var calls = 0;
        Task<HttpResponseMessage> RespondAsync(GatewayOwner owner, HttpRequestMessage request, CancellationToken _)
        {
            Assert.Equal(GatewayOwner.Projects, owner);
            Assert.Equal("/api/policies/projects/project-one/validate", request.RequestUri!.PathAndQuery);
            Assert.Equal("tenant-a", request.Headers.GetValues("X-Agentweaver-Tenant").Single());
            Assert.False(request.Headers.Contains("Idempotency-Key"));
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"isValid\":true,\"diagnostics\":[]}", Encoding.UTF8, "application/json"),
            });
        }

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new CallbackHttpMessageHandler((request, cancellationToken) =>
                RespondAsync(GatewayOwner.Projects, request, cancellationToken)),
            RespondAsync);
        var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == "validateProjectNativeAcsPolicy");
        Assert.Equal(5 * 1024 * 1024, route.MaximumRequestBodyBytes);
        Assert.Equal(5 * 1024 * 1024, route.MaximumResponseBytes);
        Assert.True(route.ExcludeFromMcp);
        Assert.True(route.HasJsonBody);

        using var request = CreateRequest(bearer, "/api/v1/policies/projects/project-one/validate");
        request.Method = HttpMethod.Post;
        request.Headers.Remove("X-Agentweaver-Tenant");
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-a");
        request.Headers.Add("Idempotency-Key", "not-forwarded");
        request.Content = new StringContent("{\"artifact\":{}}", Encoding.UTF8, "application/json");
        using var response = await gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);

        using var query = CreateRequest(bearer, "/api/v1/policies/projects/project-one/validate?scope=platform");
        query.Method = HttpMethod.Post;
        query.Content = new StringContent("{\"artifact\":{}}", Encoding.UTF8, "application/json");
        using var denied = await gateway.Client.SendAsync(query);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ProjectPolicyReadsPublishTypedOpenApiContracts()
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
        var list = paths.GetProperty("/api/v1/policies/projects").GetProperty("get");
        Assert.Equal("listProjectNativeAcsPolicies", list.GetProperty("operationId").GetString());
        Assert.Equal(
            "#/components/schemas/NativeAcsPolicyScopePage",
            list.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var listParameters = list.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(listParameters, parameter =>
            parameter.GetProperty("name").GetString() == "pageSize" &&
            parameter.GetProperty("schema").GetProperty("default").GetInt32() == 20);
        Assert.Contains(listParameters, parameter => parameter.GetProperty("name").GetString() == "cursor");

        var current = paths.GetProperty("/api/v1/policies/projects/{projectId}").GetProperty("get");
        Assert.Equal("readProjectNativeAcsPolicy", current.GetProperty("operationId").GetString());
        Assert.Equal(
            "#/components/schemas/NativeAcsPolicyReadResult",
            current.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var history = paths.GetProperty("/api/v1/policies/projects/{projectId}/revisions").GetProperty("get");
        Assert.Equal("listProjectNativeAcsPolicyRevisions", history.GetProperty("operationId").GetString());
        var exact = paths.GetProperty("/api/v1/policies/projects/{projectId}/revisions/{revision}")
            .GetProperty("get");
        Assert.Equal("readProjectNativeAcsPolicyRevision", exact.GetProperty("operationId").GetString());
        var validate = paths.GetProperty("/api/v1/policies/projects/{projectId}/validate").GetProperty("post");
        Assert.Equal("validateProjectNativeAcsPolicy", validate.GetProperty("operationId").GetString());
        Assert.Equal("#/components/schemas/NativeAcsPolicyValidationRequest",
            validate.GetProperty("requestBody").GetProperty("content").GetProperty("application/json")
                .GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Equal("#/components/schemas/NativeAcsPolicyValidationResponse",
            validate.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Contains(
            exact.GetProperty("responses").GetProperty("200").GetProperty("headers").EnumerateObject(),
            header => header.Name == "Cache-Control");
    }

    [Fact]
    public async Task WorkspaceRoutesPublishTypedOwnerBoundReadContracts()
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
        var expected = new[]
        {
            ("listWorkspaceRepositoryRefs", "SourceControlWorkspaceRefsPage"),
            ("readWorkspaceRepositoryTree", "SourceControlWorkspaceTree"),
            ("readWorkspaceRepositoryFile", "SourceControlWorkspaceFile"),
        };

        foreach (var (operationId, responseSchema) in expected)
        {
            var route = Assert.Single(GatewayRouteCatalog.Routes, item => item.OperationId == operationId);
            Assert.Equal("GET", route.Method);
            Assert.Equal(GatewayOwner.Orchestrator, route.Owner);
            Assert.True(route.ForwardTenantSelector);
            Assert.True(route.ExcludeFromMcp);
            Assert.Equal(
                new[] { ProjectTeamSyncContract.SelectionCodeHeader },
                route.ForwardRequestHeaders.ToArray());
            Assert.False(route.HasJsonBody);

            var operation = paths.EnumerateObject()
                .Where(path => path.Value.TryGetProperty("get", out _))
                .Select(path => path.Value.GetProperty("get"))
                .Single(item => item.GetProperty("operationId").GetString() == operationId);
            Assert.Equal(route.OwnerPath, operation.GetProperty("x-agentweaver-owner-path").GetString());
            Assert.Equal("#/components/schemas/" + responseSchema,
                operation.GetProperty("responses").GetProperty("200").GetProperty("content")
                    .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            Assert.Contains(
                operation.GetProperty("parameters").EnumerateArray(),
                parameter => parameter.GetProperty("name").GetString() == ProjectTeamSyncContract.SelectionCodeHeader &&
                    parameter.GetProperty("in").GetString() == "header" &&
                    parameter.GetProperty("required").GetBoolean());
        }
    }

    [Fact]
    public async Task PlatformPolicyHistoryPublishesTypedOpenApiContracts()
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
        var history = paths.GetProperty("/api/v1/policies/platform/revisions").GetProperty("get");
        Assert.Equal(
            "listPlatformNativeAcsPolicyRevisions",
            history.GetProperty("operationId").GetString());
        Assert.Equal(
            "#/components/schemas/NativeAcsPolicyRevisionPage",
            history.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        var historyParameters = history.GetProperty("parameters").EnumerateArray().ToArray();
        Assert.Contains(historyParameters, parameter =>
            parameter.GetProperty("name").GetString() == "pageSize" &&
            parameter.GetProperty("schema").GetProperty("minimum").GetInt32() == 1 &&
            parameter.GetProperty("schema").GetProperty("maximum").GetInt32() == 100);
        Assert.Contains(historyParameters, parameter =>
            parameter.GetProperty("name").GetString() == "cursor" &&
            parameter.GetProperty("schema").GetProperty("maxLength").GetInt32() == 2048);

        var exact = paths.GetProperty("/api/v1/policies/platform/revisions/{revision}").GetProperty("get");
        Assert.Equal(
            "readPlatformNativeAcsPolicyRevision",
            exact.GetProperty("operationId").GetString());
        Assert.Equal(
            "#/components/schemas/NativeAcsPolicyRevisionReadResult",
            exact.GetProperty("responses").GetProperty("200").GetProperty("content")
                .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.Contains(exact.GetProperty("parameters").EnumerateArray(), parameter =>
            parameter.GetProperty("name").GetString() == "revision" &&
            parameter.GetProperty("schema").GetProperty("pattern").GetString() == "^[1-9][0-9]{0,18}$");
        Assert.DoesNotContain(history.GetProperty("responses").EnumerateObject(),
            item => item.Name == "404");
        Assert.True(GatewayRouteCatalog.Routes
            .Where(route => route.OperationId is
                "listPlatformNativeAcsPolicyRevisions" or "readPlatformNativeAcsPolicyRevision")
            .All(route => route.ExcludeFromMcp));
    }

    [Fact]
    public async Task FileReadEscapesNestedSegmentsAndRejectsEncodedTraversalBeforeForwarding()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        var forwarded = new List<(string Path, string? Selection)>();
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (owner, request, _) =>
            {
                Assert.Equal(GatewayOwner.Orchestrator, owner);
                request.Headers.TryGetValues(ProjectTeamSyncContract.SelectionCodeHeader, out var selectionValues);
                forwarded.Add((request.RequestUri!.PathAndQuery, selectionValues?.SingleOrDefault()));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"repository":"octo/repo","path":"src/nested/a file.cs","commitSha":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","content":"ok","isBinary":false,"language":"csharp","size":2}""",
                        Encoding.UTF8,
                        "application/json"),
                });
            });

        using var valid = CreateRequest(
            bearer,
            "/api/v1/projects/project-1/source-control/files/src/nested/a%20file.cs?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var response = await gateway.Client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(
            "/api/projects/project-1/source-control/files/src/nested/a%20file.cs?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Assert.Single(forwarded).Path);
        Assert.Equal(SelectionCode, Assert.Single(forwarded).Selection);

        foreach (var invalidPath in new[] { "src%252Fprivate.txt", "%252e%252e/private.txt", "src%25252Fprivate.txt" })
        {
            using var invalid = CreateRequest(
                bearer,
                $"/api/v1/projects/project-1/source-control/files/{invalidPath}?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using var rejected = await gateway.Client.SendAsync(invalid);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        Assert.Single(forwarded);
    }

    [Fact]
    public async Task FileReadForwardsMaximumMultibyteTextWithinRouteResponseLimit()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        const int maximumFileBytes = 1024 * 1024;
        var content = new string('é', maximumFileBytes / 2);
        var ownerBody = JsonSerializer.SerializeToUtf8Bytes(
            new SourceControlWorkspaceFile(
                "octo/repo",
                "file.txt",
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                content,
                false,
                "plaintext",
                maximumFileBytes),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var route = Assert.Single(
            GatewayRouteCatalog.Routes,
            item => item.OperationId == "readWorkspaceRepositoryFile");
        Assert.Equal(maximumFileBytes, Encoding.UTF8.GetByteCount(content));
        Assert.True(ownerBody.Length > 2 * 1024 * 1024);
        Assert.True(ownerBody.Length <= route.MaximumResponseBytes);

        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ownerBody)
                {
                    Headers = { ContentType = new("application/json") },
                },
            }));

        using var request = CreateRequest(
            bearer,
            "/api/v1/projects/project-1/source-control/files/file.txt?ref=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        using var response = await gateway.Client.SendAsync(request);
        var forwardedBody = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ownerBody, forwardedBody);
    }

    [Fact]
    public async Task TreeReadForwardsActualAdapterOutputAfterOwnerJsonExpansion()
    {
        // Keep provider JSON under 8 MiB; owner JSON escaping expands it past 9 MiB.
        const int nodeCount = 13_001;
        const int pathLength = 470;
        const int escapedCharactersPerPath = 53;
        const string commitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string treeSha = "cccccccccccccccccccccccccccccccccccccccc";
        const string blobSha = "dddddddddddddddddddddddddddddddddddddddd";
        var repository = new SourceControlRepositoryIdentity("octo", "repo");
        var providerTree = CreateProviderTree(treeSha, blobSha, nodeCount, pathLength, escapedCharactersPerPath);
        var providerHandler = new WorkspaceProviderHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path == $"/repos/octo/repo/commits/{commitSha}")
                return JsonResponse(JsonSerializer.SerializeToUtf8Bytes(new
                {
                    sha = commitSha,
                    commit = new { tree = new { sha = treeSha } }
                }));
            if (path == $"/repos/octo/repo/git/trees/{treeSha}?recursive=1")
                return JsonResponse(providerTree);
            throw new InvalidOperationException("Unexpected GitHub request.");
        });
        using var providerClient = new HttpClient(providerHandler)
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var adapter = new GitHubSourceControlAdapter(providerClient);
        var tree = await adapter.ReadWorkspaceTreeAsync(
            repository,
            commitSha,
            new SecretCredential("test-token", DateTimeOffset.UtcNow.AddMinutes(5)),
            CancellationToken.None);
        var ownerBody = JsonSerializer.SerializeToUtf8Bytes(
            tree,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal(nodeCount, tree.Items.Length);
        Assert.Equal(7_410_647, providerTree.Length);
        Assert.True(providerTree.Length < 8 * 1024 * 1024);
        Assert.True(ownerBody.Length > 9 * 1024 * 1024);
        var route = Assert.Single(
            GatewayRouteCatalog.Routes,
            item => item.OperationId == "readWorkspaceRepositoryTree");
        Assert.True(ownerBody.Length <= route.MaximumResponseBytes);

        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa);
        var bearer = CreateBearer(signingKey);
        await using var gateway = await GatewayResourceServer.StartAsync(
            signingKey,
            () => new HttpClientHandler(),
            (_, request, _) =>
            {
                Assert.Equal(
                    "/api/projects/project-1/source-control/tree?ref=" + commitSha,
                    request.RequestUri!.PathAndQuery);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ownerBody)
                    {
                        Headers = { ContentType = new("application/json") },
                    },
                });
            });

        using var request = CreateRequest(
            bearer,
            "/api/v1/projects/project-1/source-control/tree?ref=" + commitSha);
        using var response = await gateway.Client.SendAsync(request);
        var forwardedBody = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ownerBody, forwardedBody);
    }

    private static byte[] CreateProviderTree(
        string treeSha,
        string blobSha,
        int nodeCount,
        int pathLength,
        int escapedCharactersPerPath)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("sha", treeSha);
            writer.WriteBoolean("truncated", false);
            writer.WriteStartArray("tree");
            for (var index = 0; index < nodeCount; index++)
            {
                var prefix = "entry-" + index.ToString("D5", CultureInfo.InvariantCulture);
                var path = prefix +
                    new string('a', pathLength - prefix.Length - escapedCharactersPerPath) +
                    new string('<', escapedCharactersPerPath);
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteString("mode", "100644");
                writer.WriteString("type", "blob");
                writer.WriteString("sha", blobSha);
                writer.WriteNumber("size", 1);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static HttpResponseMessage JsonResponse(byte[] body) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
            {
                Headers = { ContentType = new("application/json") },
            },
        };

    private static HttpRequestMessage CreateRequest(string bearer, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        request.Headers.Add("X-Agentweaver-Tenant", "tenant-1");
        request.Headers.Add(ProjectTeamSyncContract.SelectionCodeHeader, SelectionCode);
        return request;
    }

    private static string CreateBearer(SecurityKey signingKey)
    {
        var token = new JwtSecurityToken(
            new Uri(IdentityBrokerWebApplicationFactory.Issuer).AbsoluteUri,
            "https://api.test",
            [new Claim("sub", "workspace-read-actor"), new Claim("scope", "api.read")],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class CallbackHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    private sealed class WorkspaceProviderHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
