using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentRunEgressTests(EnvironmentPostgresFixture database)
    : IClassFixture<EnvironmentPostgresFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public async Task StorePagesAttributedFlowsByFirstSeenDescFlowIdDescWithoutRawLabels()
    {
        var id = Guid.NewGuid().ToString("N");
        var tenantId = "tenant-" + id;
        var projectId = "project-" + id;
        var runId = "run-" + id;
        var owner1 = new EnvironmentOwnerIdentity(tenantId, projectId, runId, "env-1-" + id);
        var owner2 = new EnvironmentOwnerIdentity(tenantId, projectId, runId, "env-2-" + id);
        var otherRun = new EnvironmentOwnerIdentity(tenantId, projectId, "other-" + runId, "env-3-" + id);
        var audit = new EnvironmentEgressAuditStore(database.DataSource);
        var fp1 = await audit.RegisterOwnerAsync(owner1, CancellationToken.None);
        var fp2 = await audit.RegisterOwnerAsync(owner2, CancellationToken.None);
        var fpOther = await audit.RegisterOwnerAsync(otherRun, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-11-20T10:00:00Z");
        var t2 = DateTimeOffset.Parse("2026-11-20T10:01:00Z");
        // Two flows with identical first_seen to verify deterministic flow_id DESC tie-breaking.
        var r1 = await audit.AppendFlowAsync(
            new("a.example", null, 443, EgressFlowVerdict.Allowed, t2, t2, 2, fp1), CancellationToken.None);
        var r2 = await audit.AppendFlowAsync(
            new(null, "1.2.3.4", 8443, EgressFlowVerdict.Denied, t2, t2.AddSeconds(5), 1, fp2), CancellationToken.None);
        var r3 = await audit.AppendFlowAsync(
            new("c.example", "5.6.7.8", 443, EgressFlowVerdict.Allowed, t1, t1, 4, fp1), CancellationToken.None);
        await audit.AppendFlowAsync(
            new("other.example", null, 443, EgressFlowVerdict.Allowed, t2, t2, 1, fpOther), CancellationToken.None);
        await audit.AppendFlowAsync(
            new("unattributed.example", null, 443, EgressFlowVerdict.Denied, t2, t2, 1, "raw-label-secret"),
            CancellationToken.None);

        var page1 = await audit.ReadRunEgressAsync(tenantId, projectId, runId, 2, null, CancellationToken.None);
        Assert.Equal(runId, page1.RunId);
        Assert.Equal(RunEgressContract.CoverageUnavailable, page1.Coverage);
        Assert.Null(page1.DelayedUntil);
        Assert.Equal(2, page1.Items.Length);
        Assert.NotNull(page1.NextCursor);

        // Verify equal-timestamp ordering matches flow_id DESC.
        var expectedT2Order = new[]
        {
            (r1.FlowId, Host: (string?)"a.example", Address: (string?)null),
            (r2.FlowId, Host: (string?)null, Address: (string?)"1.2.3.4")
        }.OrderByDescending(x => x.FlowId, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedT2Order[0].Host, page1.Items[0].Host);
        Assert.Equal(expectedT2Order[0].Address, page1.Items[0].Address);
        Assert.Equal(expectedT2Order[1].Host, page1.Items[1].Host);
        Assert.Equal(expectedT2Order[1].Address, page1.Items[1].Address);

        // Verify no raw labels, fingerprints, or flow IDs appear in serialized JSON.
        var json = JsonSerializer.Serialize(page1, JsonOptions);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("Unavailable", doc.RootElement.GetProperty("coverage").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("delayedUntil").ValueKind);
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            var propNames = item.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(["address", "count", "firstSeen", "host", "lastSeen", "port", "verdict"], propNames);
        }
        Assert.DoesNotContain(fp1, json, StringComparison.Ordinal);
        Assert.DoesNotContain(r1.FlowId, json, StringComparison.Ordinal);

        var page2 = await audit.ReadRunEgressAsync(tenantId, projectId, runId, 2, page1.NextCursor, CancellationToken.None);
        Assert.Single(page2.Items);
        Assert.Equal("c.example", page2.Items[0].Host);
        Assert.Equal("5.6.7.8", page2.Items[0].Address);
        Assert.Equal(4, page2.Items[0].Count);
        Assert.Null(page2.NextCursor);

        // Cursor scope and page-size binding checks.
        await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync("other-tenant", projectId, runId, 2, page1.NextCursor, CancellationToken.None));
        await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, "other-project", runId, 2, page1.NextCursor, CancellationToken.None));
        await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, "other-run", 2, page1.NextCursor, CancellationToken.None));
        await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, runId, 3, page1.NextCursor, CancellationToken.None));
        await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, runId, 2, "not-a-valid-cursor", CancellationToken.None));
        await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, runId, 101, null, CancellationToken.None));

        // Empty run returns Unavailable and null delayedUntil.
        var empty = await audit.ReadRunEgressAsync(tenantId, projectId, "missing-run", 50, null, CancellationToken.None);
        Assert.Empty(empty.Items);
        Assert.Equal("Unavailable", empty.Coverage);
        Assert.Null(empty.DelayedUntil);
        Assert.Null(empty.NextCursor);

        // Cursor anchor validation: forged flow_id, mismatched timestamp, or anchor from another run -> 400.
        var forgedFlowCursor = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                order = "firstSeen-desc,flowId-desc",
                tenantId,
                projectId,
                runId,
                pageSize = 2,
                firstSeen = t2,
                flowId = new string('f', 64)
            }));
        var forgedEx = await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, runId, 2, forgedFlowCursor, CancellationToken.None));
        Assert.Equal("run_egress_cursor_invalid", forgedEx.Code);

        var tamperedTimeCursor = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                order = "firstSeen-desc,flowId-desc",
                tenantId,
                projectId,
                runId,
                pageSize = 2,
                firstSeen = t2.AddSeconds(1),
                flowId = r1.FlowId
            }));
        var tamperedEx = await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, runId, 2, tamperedTimeCursor, CancellationToken.None));
        Assert.Equal("run_egress_cursor_invalid", tamperedEx.Code);

        var otherRunFlow = await audit.AppendFlowAsync(
            new("other2.example", null, 443, EgressFlowVerdict.Allowed, t2, t2, 1, fpOther), CancellationToken.None);
        var crossRunAnchorCursor = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                order = "firstSeen-desc,flowId-desc",
                tenantId,
                projectId,
                runId,
                pageSize = 2,
                firstSeen = t2,
                flowId = otherRunFlow.FlowId
            }));
        var crossRunEx = await Assert.ThrowsAsync<EnvironmentEgressQueryException>(() =>
            audit.ReadRunEgressAsync(tenantId, projectId, runId, 2, crossRunAnchorCursor, CancellationToken.None));
        Assert.Equal("run_egress_cursor_invalid", crossRunEx.Code);
    }

    [Fact]
    public async Task EndpointEnforcesDuplicateTenantHeaderRevalidationRevocationAndDependencyErrors()
    {
        var id = Guid.NewGuid().ToString("N");
        var tenantId = "tenant-" + id;
        var projectId = "project-" + id;
        var runId = "run-" + id;
        var actorId = Guid.NewGuid().ToString("D");
        var projects = new StubProjectsConfigClient();
        var audit = new EnvironmentEgressAuditStore(database.DataSource);
        var fp = await audit.RegisterOwnerAsync(
            new(tenantId, projectId, runId, "env-" + id), CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        await audit.AppendFlowAsync(
            new("api.example", null, 443, EgressFlowVerdict.Allowed, now, now, 1, fp), CancellationToken.None);

        using var host = await StartEndpointHostAsync(projects, audit);
        var client = host.GetTestClient();

        // 1. Project read succeeds with no-store.
        projects.NextResponse = Authority(
            actorId, tenantId,
            [new(ProjectAuthorityResourceType.Project, projectId,
                [new(ProjectAuthorizationPermission.ReadProjects, 1)])]);
        using (var req = AuthedGet($"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            var body = await resp.Content.ReadAsStringAsync();
            Assert.Contains("api.example", body, StringComparison.Ordinal);
        }

        // 2. Platform-admin path requires api.read + projects.admin + unbound + ReadPlatformRuntimeDefaults > 0.
        projects.NextResponse = Authority(
            actorId, tenantId,
            [new(ProjectAuthorityResourceType.Platform, "default",
                [new(ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults, 2)])]);
        using (var req = AuthedGet(
            $"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read projects.admin", tenantId))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // Platform admin without projects.admin scope -> 403.
        using (var req = AuthedGet(
            $"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // Platform admin with project binding -> 403.
        projects.NextResponse = Authority(
            actorId, tenantId,
            [new(ProjectAuthorityResourceType.Platform, "default",
                [new(ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults, 2)])],
            boundProjectId: projectId);
        using (var req = AuthedGet(
            $"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read projects.admin", tenantId,
            extraClaims: [new("project_id", projectId)]))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 3. Duplicate X-Agentweaver-Tenant header -> 403.
        using (var req = AuthedGet($"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId))
        {
            req.Headers.Add("X-Agentweaver-Tenant", "other-tenant");
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 4. Purpose-bound token -> 403.
        using (var req = AuthedGet($"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId,
            extraClaims: [new("purpose", "runtime-bootstrap")]))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 5. Revoked grant (RoleRevision == 0 or denied) -> 403.
        projects.NextException = new ProjectsConfigApiException("authorization_context_denied", "revoked");
        using (var req = AuthedGet($"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 6. Projects dependency failure -> 503.
        projects.NextException = new ProjectsConfigApiException("projects_config_request_failed", "down");
        using (var req = AuthedGet($"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 7. Tenant-bound token (tenant_id, tid, Microsoft tenant claim) cannot use platform-admin fallback -> 403;
        // matching project/tenant grant succeeds -> 200; conflicting/duplicate bindings -> 403.
        foreach (var tenantClaimType in new[]
        {
            "tenant_id",
            "tid",
            "http://schemas.microsoft.com/identity/claims/tenantid",
        })
        {
            projects.NextResponse = Authority(
                actorId, tenantId,
                [new(ProjectAuthorityResourceType.Platform, "default",
                    [new(ProjectAuthorizationPermission.ReadPlatformRuntimeDefaults, 2)])]);
            using (var req = AuthedGet(
                $"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read projects.admin", tenantId,
                extraClaims: [new(tenantClaimType, tenantId)]))
            using (var resp = await client.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }

            projects.NextResponse = Authority(
                actorId, tenantId,
                [new(ProjectAuthorityResourceType.Tenant, tenantId,
                    [new(ProjectAuthorizationPermission.ReadProjects, 1)])]);
            using (var req = AuthedGet(
                $"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId,
                extraClaims: [new(tenantClaimType, tenantId)]))
            using (var resp = await client.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }
        }

        foreach (var conflictingClaims in new[]
        {
            new[] { new Claim("tenant_id", tenantId), new Claim("tid", tenantId) },
            new[] { new Claim("tenant_id", tenantId), new Claim("http://schemas.microsoft.com/identity/claims/tenantid", "other-tenant") },
            new[] { new Claim("project_id", projectId), new Claim("project_id", projectId) },
            new[] { new Claim("project_id", projectId), new Claim("run_id", runId), new Claim("run_id", runId) },
        })
        {
            using var req = AuthedGet(
                $"/api/projects/{projectId}/runs/{runId}/egress", actorId, "api.read", tenantId,
                extraClaims: conflictingClaims);
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 8. Unauthenticated request receives Cache-Control: no-store before auth rejects with 401.
        using (var resp = await client.GetAsync($"/api/projects/{projectId}/runs/{runId}/egress"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }
    }

    private static HttpRequestMessage AuthedGet(
        string path,
        string sub,
        string scope,
        string tenantId,
        Claim[]? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("iss", "https://issuer.example/"),
            new("scope", scope),
        };
        if (extraClaims is not null)
            claims.AddRange(extraClaims);
        var encodedClaims = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            claims.Select(c => new ClaimDto(c.Type, c.Value)).ToArray()));
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-token");
        req.Headers.Add("X-Test-Claims", encodedClaims);
        req.Headers.Add("X-Agentweaver-Tenant", tenantId);
        return req;
    }

    private static ProjectAuthorizationContextResponse Authority(
        string actorId,
        string tenantId,
        EffectiveProjectAuthorization[] authority,
        string? boundProjectId = null,
        string? boundRunId = null) =>
        new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://issuer.example/",
            actorId,
            tenantId,
            1,
            boundProjectId,
            boundRunId,
            authority.ToImmutableArray());

    private static async Task<IHost> StartEndpointHostAsync(
        StubProjectsConfigClient projects,
        IEnvironmentEgressAuditStore audit)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IProjectsConfigClient>(projects);
        builder.Services.AddSingleton(audit);
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        var app = builder.Build();
        app.UseRunEgressNoStore();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRunEgressEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed record ClaimDto(string Type, string Value);

    private sealed class StubProjectsConfigClient : IProjectsConfigClient
    {
        public ProjectAuthorizationContextResponse? NextResponse { get; set; }
        public Exception? NextException { get; set; }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller, CancellationToken cancellationToken)
        {
            if (NextException is { } ex)
            {
                NextException = null;
                throw ex;
            }
            return Task.FromResult(NextResponse!);
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller, string projectId, string runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Claims", out var raw))
                return Task.FromResult(AuthenticateResult.NoResult());
            var dtos = JsonSerializer.Deserialize<ClaimDto[]>(Convert.FromBase64String(raw.ToString()))!;
            var identity = new ClaimsIdentity(dtos.Select(d => new Claim(d.Type, d.Value)), "Test");
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), "Test");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
