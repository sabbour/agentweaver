using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task NativeAcsPlatformPolicyReadAllowsReadOnlyPlatformAdmin()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-platform-reader-" + suffix;
        var tenantId = "acs-platform-reader-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifact = Artifact("platform policy read " + suffix);
        var written = await WritePlatformPolicyAsync(
            caller, artifact, "acs-platform-read-" + suffix, "acs-platform-read-correlation-" + suffix);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);

        using var response = await client.GetAsync("/api/policies/platform/");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal(written.PolicyId, root.GetProperty("policyId").GetString());
        Assert.Equal(written.Revision, root.GetProperty("revision").GetInt64());
        Assert.Equal(artifact.ContentSha256, root.GetProperty("contentSha256").GetString());
        Assert.Equal(
            artifact.ContentSha256,
            root.GetProperty("artifact").GetProperty("contentSha256").GetString());
        var manifest = root.GetProperty("artifact").GetProperty("manifestBytes")
            .EnumerateArray().Select(value => value.GetByte()).ToArray();
        Assert.Equal(artifact.ManifestBytes.ToArray(), manifest);
    }

    [Fact]
    public async Task NativeAcsPolicyValidationSeparatesDependencyFailureAndRateLimitsCaller()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-validator-" + suffix;
        var tenantId = "acs-policy-validator-tenant-" + suffix;
        await using (var db = CreateDbContext())
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        }

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            Path.Combine(AppContext.BaseDirectory, "missing-opa-" + suffix));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        var request = new NativeAcsPolicyValidationRequest { Artifact = Artifact("validation dependency") };

        for (var index = 0; index < 6; index++)
        {
            using var response = await client.PostAsJsonAsync("/api/policies/platform/validate", request);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(
                "native_acs_dependency_unavailable",
                document.RootElement.GetProperty("code").GetString());
            Assert.Equal("opa", document.RootElement.GetProperty("dependency").GetString());
        }

        using var limited = await client.PostAsJsonAsync("/api/policies/platform/validate", request);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), limited.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task NativeAcsPolicyValidationReportsProvenInvalidArtifactAsUnprocessable()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-invalid-" + suffix;
        var tenantId = "acs-policy-invalid-tenant-" + suffix;
        await using (var db = CreateDbContext())
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        }

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            Path.Combine(AppContext.BaseDirectory, "missing-opa-" + suffix));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        var request = new NativeAcsPolicyValidationRequest
        {
            Artifact = Artifact("hash mismatch") with { ContentSha256 = new string('0', 64) },
        };

        using var response = await client.PostAsJsonAsync("/api/policies/platform/validate", request);

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("native_acs_policy_invalid", document.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(true, "native_acs_dependency_unavailable")]
    [InlineData(false, "native_acs_policy_writes_unavailable")]
    public async Task NativeAcsPolicyWriteDoesNotPersistWhenWritesOrValidationAreUnavailable(
        bool headWritesEnabled,
        string expectedCode)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-writer-" + suffix;
        var tenantId = "acs-policy-writer-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var headBefore = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, headWritesEnabled);
        using var client = CreatePlatformPolicyWriter(host, subject, tenantId);
        var request = new NativeAcsPolicyWriteBody
        {
            ExpectedRevision = headBefore?.Revision,
            Artifact = Artifact("write unavailable " + suffix),
            IdempotencyKey = "acs-policy-write-" + suffix,
            Reason = "Test write",
            CorrelationId = "acs-policy-write-correlation-" + suffix,
        };

        using var response = await client.PutAsJsonAsync("/api/policies/platform/", request);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("code").GetString());
        var headAfter = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        Assert.Equal(headBefore?.Revision, headAfter?.Revision);
        Assert.Equal(headBefore?.ContentSha256, headAfter?.ContentSha256);
        await using var verify = CreateDbContext();
        Assert.False(await verify.NativeAcsPolicyIdempotency.AsNoTracking()
            .AnyAsync(item => item.IdempotencyKey == request.IdempotencyKey));
        Assert.False(await verify.NativeAcsPolicyRevisions.AsNoTracking()
            .AnyAsync(item => item.CreatedByActorId == caller.ActorId));
        Assert.False(await verify.NativeAcsPolicyChangeAudit.AsNoTracking()
            .AnyAsync(item => item.ActorId == caller.ActorId));
    }

    [Fact]
    public async Task NativeAcsPolicyWriteReplaysReceiptBeforeWriteGateAndValidation()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-replay-" + suffix;
        var tenantId = "acs-policy-replay-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var expectedRevision = (await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None))
            ?.Revision;
        var artifact = Artifact("lost response " + suffix);
        var key = "acs-policy-replay-key-" + suffix;
        var correlation = "acs-policy-replay-correlation-" + suffix;
        var committed = await WritePlatformPolicyAsync(caller, artifact, key, correlation, "Test write");

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyWriter(host, subject, tenantId);
        using var response = await client.PutAsJsonAsync("/api/policies/platform/", new NativeAcsPolicyWriteBody
        {
            ExpectedRevision = expectedRevision,
            Artifact = artifact,
            IdempotencyKey = key,
            Reason = "Test write",
            CorrelationId = correlation,
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(committed.PolicyId, document.RootElement.GetProperty("policyId").GetString());
        Assert.Equal(committed.Revision, document.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(committed.AuditId, document.RootElement.GetProperty("auditId").GetGuid());
    }

    private async Task<NativeAcsPolicyWriteReceipt> WritePlatformPolicyAsync(
        ProjectAuthorizationContext caller,
        NativeAcsArtifact artifact,
        string idempotencyKey,
        string correlationId,
        string reason = "Create test policy")
    {
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        return await store.WriteAsync(
            caller,
            new NativeAcsPolicyWriteRequest(
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                current?.PolicyId ?? "canonical",
                current?.Revision,
                artifact,
                idempotencyKey,
                reason,
                correlationId),
            CancellationToken.None);
    }

    private static HttpClient CreatePlatformPolicyWriter(IHost host, string subject, string tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        return client;
    }

    [Fact]
    public async Task NativeAcsPolicyRequestRejectsUnknownNestedArtifactFields()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-policy-strict-" + suffix;
        var tenantId = "acs-policy-strict-tenant-" + suffix;
        await using (var db = CreateDbContext())
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        }

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            Path.Combine(AppContext.BaseDirectory, "missing-opa-" + suffix));
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        using var content = new StringContent(
            """{"artifact":{"manifestBytes":[],"files":[],"contentSha256":"","unexpected":true}}""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/policies/platform/validate", content);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<IHost> StartNativeAcsPolicyEndpointHostAsync(
        Npgsql.NpgsqlDataSource dataSource,
        string? opaPath,
        bool headWritesEnabled = false) =>
        await new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [NativeAcsPolicyEndpoints.HeadWritesEnabledSetting] = headWritesEnabled ? "true" : "false",
                }))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(ProjectSummaryTestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, ProjectSummaryTestAuthenticationHandler>(
                            ProjectSummaryTestAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                    services.AddDbContextFactory<ProjectsConfigDbContext>(options => options.UseNpgsql(
                        dataSource,
                        npgsql => npgsql.MigrationsHistoryTable(
                            "__ef_migrations_history", ProjectsConfigDbContext.Schema)));
                    services.AddSingleton(CreateProviderCatalog());
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
                    if (opaPath is not null)
                        services.AddSingleton(new NativeAcsArtifactValidator(opaPath));
                    services.AddMemoryCache();
                    services.AddSingleton<NativeAcsPolicyRequestGate>();
                    services.AddScoped<ProjectAuthorizationOwner>();
                    services.AddScoped<ProjectsConfigService>();
                    services.AddScoped<NativeAcsPolicyStore>();
                    services.ConfigureHttpJsonOptions(options =>
                        options.SerializerOptions.Converters.Add(
                            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapNativeAcsPolicyEndpoints());
                });
            })
            .StartAsync();
}
