using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Agentweaver.Orchestrator.Core.Tests;

[Collection("Coordination PostgreSQL")]
public sealed class RunEgressEndpointTests(CoordinationPostgresFixture fixture) : IAsyncLifetime
{
    private readonly string _schema = "runegress_" + Guid.NewGuid().ToString("N");
    private readonly CoordinationActor _actor = new("https://issuer.example/", Guid.NewGuid().ToString("D"));
    private ProjectRunReadStore _store = null!;

    public async Task InitializeAsync()
    {
        await CoordinationOwnerMigrator.MigrateAsync(fixture.DataSource, _schema);
        _store = new ProjectRunReadStore(fixture.DataSource, _schema);
    }

    public async Task DisposeAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task ResolveVisibleRunProjectCountsVisibleMatchesRegardlessOfHiddenDuplicates()
    {
        const string tenant = "tenant-a";
        const string runId = "shared-run";
        await InsertAcceptedRunAsync(tenant, "proj-hidden", runId);
        await InsertAcceptedRunAsync(tenant, "proj-visible-1", runId);
        await InsertAcceptedRunAsync("tenant-other", "proj-visible-1", "other-tenant-run");

        // Zero visible matches -> null.
        Assert.Null(await _store.ResolveVisibleRunProjectAsync(
            tenant, runId, null, allProjectsVisible: false, ["proj-unrelated"], CancellationToken.None));

        // One visible match + one hidden duplicate -> resolves the visible project.
        Assert.Equal("proj-visible-1", await _store.ResolveVisibleRunProjectAsync(
            tenant, runId, null, allProjectsVisible: false, ["proj-visible-1"], CancellationToken.None));

        // Two visible matches -> null (ambiguous -> 404 run_not_found).
        Assert.Null(await _store.ResolveVisibleRunProjectAsync(
            tenant, runId, null, allProjectsVisible: false, ["proj-hidden", "proj-visible-1"],
            CancellationToken.None));

        // Tenant-wide / platform-admin visibility sees both -> null.
        Assert.Null(await _store.ResolveVisibleRunProjectAsync(
            tenant, runId, null, allProjectsVisible: true, [], CancellationToken.None));

        // Foreign tenant run is not visible.
        Assert.Null(await _store.ResolveVisibleRunProjectAsync(
            tenant, "other-tenant-run", null, allProjectsVisible: true, [], CancellationToken.None));
    }

    [Fact]
    public async Task ResolveVisibleRunProjectUsesTenantRunProjectIndexForExactMiss()
    {
        await CoordinationOwnerMigrator.VerifyAsync(fixture.DataSource, _schema);
        for (var i = 0; i < 40; i++)
            await InsertAcceptedRunAsync($"tenant-{i % 4}", $"proj-{i % 5}", $"run-{i}");

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var analyze = new NpgsqlCommand($"ANALYZE \"{_schema}\".accepted_runs", connection))
            await analyze.ExecuteNonQueryAsync();

        await using var tx = await connection.BeginTransactionAsync();
        await using (var disableSeqScan = new NpgsqlCommand("SET LOCAL enable_seqscan = off", connection, tx))
            await disableSeqScan.ExecuteNonQueryAsync();

        await using var explain = new NpgsqlCommand(
            $"""
            EXPLAIN (ANALYZE, FORMAT JSON)
            SELECT project_id
            FROM "{_schema}".accepted_runs
            WHERE tenant_id = @tenant
              AND run_id = @run
              AND (@bound_project::varchar IS NULL OR project_id = @bound_project)
              AND (@all_projects OR project_id = ANY(@visible_projects))
            LIMIT 2
            """,
            connection,
            tx);
        explain.Parameters.AddWithValue("tenant", NpgsqlTypes.NpgsqlDbType.Varchar, "tenant-1");
        explain.Parameters.AddWithValue("run", NpgsqlTypes.NpgsqlDbType.Varchar, "missing-run-exact");
        explain.Parameters.AddWithValue("bound_project", NpgsqlTypes.NpgsqlDbType.Varchar, DBNull.Value);
        explain.Parameters.AddWithValue("all_projects", NpgsqlTypes.NpgsqlDbType.Boolean, false);
        explain.Parameters.AddWithValue(
            "visible_projects",
            NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Varchar,
            new[] { "proj-1", "proj-2" });
        var planJson = (string)(await explain.ExecuteScalarAsync())!;
        await tx.CommitAsync();

        Assert.Contains("ix_accepted_runs_tenant_run_project", planJson, StringComparison.Ordinal);
        using var doc = JsonDocument.Parse(planJson);
        var plan = doc.RootElement[0].GetProperty("Plan");
        Assert.Equal(0, plan.GetProperty("Actual Rows").GetInt64());
    }

    [Fact]
    public async Task EndpointAuthorizesResolvesVisibleRunAndForwardsCallerToEnvironment()
    {
        const string tenant = "tenant-1";
        const string runId = "run-1";
        await InsertAcceptedRunAsync(tenant, "proj-hidden", runId);
        await InsertAcceptedRunAsync(tenant, "proj-visible", runId);
        await InsertAcceptedRunAsync(tenant, "proj-visible", "unique-run");

        var projectsHandler = new StubOwnerHandler();
        var envHandler = new StubOwnerHandler();
        await using var app = await StartOrchestratorAsync(projectsHandler, envHandler);
        var client = app.GetTestClient();

        // 1. Project read resolves 1 visible match despite hidden duplicate and forwards bearer + tenant.
        projectsHandler.Responder = _ => Json(HttpStatusCode.OK, AuthPayload(_actor.Subject, tenant, [
            new { resourceType = "project", resourceId = "proj-visible",
                permissions = new[] { new { permission = "readProjects", roleRevision = 1L } } }
        ]));
        envHandler.Responder = req =>
        {
            Assert.Equal("/api/projects/proj-visible/runs/run-1/egress?limit=50", req.RequestUri!.PathAndQuery);
            Assert.Equal("caller-jwt", req.Headers.Authorization?.Parameter);
            Assert.Equal(tenant, req.Headers.GetValues("X-Agentweaver-Tenant").Single());
            return Json(HttpStatusCode.OK,
                """{"runId":"run-1","coverage":"Unavailable","delayedUntil":null,"items":[],"nextCursor":null}""");
        };

        using (var req = AuthedGet($"/api/runs/{runId}/egress", _actor.Subject, "api.read", tenant))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 2. Platform-admin path resolves unique run across tenant.
        projectsHandler.Responder = _ => Json(HttpStatusCode.OK, AuthPayload(_actor.Subject, tenant, [
            new { resourceType = "platform", resourceId = "default",
                permissions = new[] { new { permission = "readPlatformRuntimeDefaults", roleRevision = 3L } } }
        ]));
        envHandler.Responder = req =>
        {
            Assert.Equal("/api/projects/proj-visible/runs/unique-run/egress?limit=10", req.RequestUri!.PathAndQuery);
            return Json(HttpStatusCode.OK,
                """{"runId":"unique-run","coverage":"Unavailable","delayedUntil":null,"items":[],"nextCursor":null}""");
        };
        using (var req = AuthedGet("/api/runs/unique-run/egress?limit=10", _actor.Subject, "api.read projects.admin", tenant))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }

        // Ambiguous run for platform-admin (2 visible matches for run-1) -> 404 run_not_found.
        using (var req = AuthedGet($"/api/runs/{runId}/egress", _actor.Subject, "api.read projects.admin", tenant))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
            Assert.Contains("run_not_found", await resp.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        // 3. Invalid query -> 400.
        using (var req = AuthedGet("/api/runs/unique-run/egress?limit=101", _actor.Subject, "api.read", tenant))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 4. Purpose-bound token -> 403.
        using (var req = AuthedGet("/api/runs/unique-run/egress", _actor.Subject, "api.read", tenant,
            [new Claim("purpose", "bootstrap")]))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }

        // 5. Environment missing no-store -> 503.
        envHandler.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"runId":"unique-run","coverage":"Unavailable","delayedUntil":null,"items":[],"nextCursor":null}""",
                Encoding.UTF8, "application/json")
        };
        using (var req = AuthedGet("/api/runs/unique-run/egress", _actor.Subject, "api.read projects.admin", tenant))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 6. Projects unavailable -> 503.
        projectsHandler.Responder = _ => Json(HttpStatusCode.ServiceUnavailable, """{"code":"down"}""");
        using (var req = AuthedGet("/api/runs/unique-run/egress", _actor.Subject, "api.read projects.admin", tenant))
        using (var resp = await client.SendAsync(req))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 7. Tenant-bound token (tenant_id, tid, Microsoft tenant claim) cannot use platform-admin fallback -> 403;
        // matching tenant/project grant succeeds -> 200; conflicting/duplicate bindings -> 403.
        foreach (var tenantClaimType in new[]
        {
            "tenant_id",
            "tid",
            "http://schemas.microsoft.com/identity/claims/tenantid",
        })
        {
            projectsHandler.Responder = _ => Json(HttpStatusCode.OK, AuthPayload(_actor.Subject, tenant, [
                new { resourceType = "platform", resourceId = "default",
                    permissions = new[] { new { permission = "readPlatformRuntimeDefaults", roleRevision = 3L } } }
            ]));
            using (var req = AuthedGet(
                "/api/runs/unique-run/egress", _actor.Subject, "api.read projects.admin", tenant,
                [new Claim(tenantClaimType, tenant)]))
            using (var resp = await client.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }

            projectsHandler.Responder = _ => Json(HttpStatusCode.OK, AuthPayload(_actor.Subject, tenant, [
                new { resourceType = "tenant", resourceId = tenant,
                    permissions = new[] { new { permission = "readProjects", roleRevision = 1L } } }
            ]));
            envHandler.Responder = _ => Json(HttpStatusCode.OK,
                """{"runId":"unique-run","coverage":"Unavailable","delayedUntil":null,"items":[],"nextCursor":null}""");
            using (var req = AuthedGet(
                "/api/runs/unique-run/egress", _actor.Subject, "api.read", tenant,
                [new Claim(tenantClaimType, tenant)]))
            using (var resp = await client.SendAsync(req))
            {
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.True(resp.Headers.CacheControl?.NoStore);
            }
        }

        // Duplicate or conflicting tenant claim aliases and duplicate project/run bindings -> 403.
        foreach (var conflictingClaims in new[]
        {
            new[] { new Claim("tenant_id", tenant), new Claim("tid", tenant) },
            new[] { new Claim("tenant_id", tenant), new Claim("http://schemas.microsoft.com/identity/claims/tenantid", "other-tenant") },
            new[] { new Claim("project_id", "proj-visible"), new Claim("project_id", "proj-visible") },
            new[] { new Claim("project_id", "proj-visible"), new Claim("run_id", "unique-run"), new Claim("run_id", "unique-run") },
        })
        {
            using var req = AuthedGet(
                "/api/runs/unique-run/egress", _actor.Subject, "api.read", tenant,
                conflictingClaims);
            using var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }

        // 8. Unauthenticated request receives Cache-Control: no-store before auth rejects with 401.
        using (var resp = await client.GetAsync("/api/runs/unique-run/egress"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.True(resp.Headers.CacheControl?.NoStore);
        }
    }

    private async Task InsertAcceptedRunAsync(string tenantId, string projectId, string runId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_schema}".accepted_runs
                (tenant_id, project_id, run_id, accepted_selection, accepted_selection_hash,
                 accepted_by_issuer, accepted_by_subject)
            VALUES
                (@tenant, @project, @run, @selection::jsonb, @hash, @issuer, @subject)
            """,
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("project", projectId);
        command.Parameters.AddWithValue("run", runId);
        command.Parameters.AddWithValue("selection", "{}");
        command.Parameters.AddWithValue("hash", new string('a', 64));
        command.Parameters.AddWithValue("issuer", _actor.Issuer);
        command.Parameters.AddWithValue("subject", _actor.Subject);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<WebApplication> StartOrchestratorAsync(
        StubOwnerHandler projectsHandler,
        StubOwnerHandler envHandler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var options = new OrchestratorOptions(
            _actor.Issuer, "https://api.test", "https://projects.test/", "https://api.test",
            "https://events.test/", "https://api.test", _schema);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton(new CoordinationOwnerStore(fixture.DataSource, _schema));
        builder.Services.AddSingleton<IReviewedRemoteToolSnapshotResolver>(new EmptySnapshotResolver());
        builder.Services.AddHttpClient<ProjectsRunSelectionClient>()
            .ConfigurePrimaryHttpMessageHandler(() => projectsHandler);
        builder.Services.AddSingleton(new RuntimeRegistrationOwnerOptions(new Uri("https://environment.test/")));
        builder.Services.AddHttpClient("run-egress-env-test")
            .ConfigurePrimaryHttpMessageHandler(() => envHandler);
        builder.Services.AddScoped(sp => new RunEgressEnvironmentClient(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("run-egress-env-test"),
            sp.GetRequiredService<RuntimeRegistrationOwnerOptions>().EnvironmentOwnerAddress));
        builder.Services.AddAuthentication("Test")
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        var app = builder.Build();
        app.UseRunEgressNoStore();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapProjectRunEndpoints();
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage AuthedGet(
        string path, string sub, string scope, string tenant, Claim[]? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            new("sub", sub),
            new("iss", "https://issuer.example/"),
            new("aud", "https://api.test"),
            new("scope", scope),
        };
        if (extraClaims is not null)
            claims.AddRange(extraClaims);
        var encoded = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            claims.Select(c => new ClaimDto(c.Type, c.Value)).ToArray()));
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "caller-jwt");
        req.Headers.Add("X-Test-Claims", encoded);
        req.Headers.Add("X-Agentweaver-Tenant", tenant);
        return req;
    }

    private static string AuthPayload(string actorId, string tenantId, object[] effectiveAuthority) =>
        JsonSerializer.Serialize(new
        {
            contractVersion = 1,
            issuer = "https://issuer.example/",
            actorId,
            tenantId,
            membershipRevision = 1L,
            boundProjectId = (string?)null,
            boundRunId = (string?)null,
            effectiveAuthority
        });

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        var resp = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        resp.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return resp;
    }

    private sealed record ClaimDto(string Type, string Value);

    private sealed class EmptySnapshotResolver : IReviewedRemoteToolSnapshotResolver
    {
        public Task<ReviewedRemoteToolSnapshot?> ResolveAsync(
            ReviewedRemoteToolSnapshotReference reference, CancellationToken cancellationToken) =>
            Task.FromResult<ReviewedRemoteToolSnapshot?>(null);
    }

    private sealed class StubOwnerHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Responder { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = Responder!(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
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
            var props = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(5) };
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), props, "Test")));
        }
    }
}
