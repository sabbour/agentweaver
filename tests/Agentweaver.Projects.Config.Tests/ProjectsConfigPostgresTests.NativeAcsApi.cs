using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Json;
using System.Data.Common;
using Agentweaver.Abstractions;
using Agentweaver.Policy.Acs;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
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
        var storedHead = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        Assert.NotNull(storedHead);

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
        Assert.Equal("platform", root.GetProperty("scopeType").GetString());
        Assert.Equal(ProjectAuthorizationOwner.PlatformResourceId, root.GetProperty("scopeId").GetString());
        Assert.Equal(written.PolicyId, root.GetProperty("policyId").GetString());
        Assert.Equal(written.Revision, root.GetProperty("revision").GetInt64());
        Assert.Equal(storedHead.UpdatedAt, root.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(artifact.ContentSha256, root.GetProperty("contentSha256").GetString());
        Assert.Equal(
            artifact.ContentSha256,
            root.GetProperty("artifact").GetProperty("contentSha256").GetString());
        var manifest = root.GetProperty("artifact").GetProperty("manifestBytes")
            .EnumerateArray().Select(value => value.GetByte()).ToArray();
        Assert.Equal(artifact.ManifestBytes.ToArray(), manifest);
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyReadUsesOneSnapshotWhenHeadAdvances()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "head-snapshot-" + suffix;
        var tenantId = "head-snapshot-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var oldArtifact = HistoryArtifact("old " + suffix);
        var old = await WritePlatformPolicyAsync(caller, oldArtifact, "old-" + suffix, "old-correlation-" + suffix);
        var newArtifact = HistoryArtifact("new " + suffix);
        NativeAcsPolicyWriteReceipt? next = null;
        var advanced = 0;
        var settingsObserved = 0;
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(async _ =>
            {
                if (Interlocked.Exchange(ref advanced, 1) == 0)
                    next = await WritePlatformPolicyAsync(caller, newArtifact, "new-" + suffix, "new-correlation-" + suffix);
            }, "native_acs_policy_revisions", observeCommand: async command =>
            {
                Assert.NotNull(command.Transaction);
                await using var settings = command.Connection!.CreateCommand();
                settings.Transaction = command.Transaction;
                settings.CommandText =
                    "SELECT current_setting('transaction_isolation'), current_setting('transaction_read_only')";
                await using var reader = await settings.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("repeatable read", reader.GetString(0));
                Assert.Equal("on", reader.GetString(1));
                Interlocked.Increment(ref settingsObserved);
            }));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);

        using var firstResponse = await client.GetAsync("/api/policies/platform/");
        Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        Assert.Equal(old.PolicyId, first.RootElement.GetProperty("policyId").GetString());
        Assert.Equal(old.Revision, first.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(oldArtifact.ContentSha256, first.RootElement.GetProperty("contentSha256").GetString());
        Assert.Equal(oldArtifact.ManifestBytes.ToArray(), first.RootElement.GetProperty("artifact")
            .GetProperty("manifestBytes").EnumerateArray().Select(value => value.GetByte()).ToArray());
        Assert.NotNull(next);
        var state = await CapturePolicyTableStateAsync();
        using var secondResponse = await client.GetAsync("/api/policies/platform/");
        Assert.Equal(System.Net.HttpStatusCode.OK, secondResponse.StatusCode);
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.Equal(next.Revision, second.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(newArtifact.ContentSha256, second.RootElement.GetProperty("contentSha256").GetString());
        Assert.Equal(newArtifact.ManifestBytes.ToArray(), second.RootElement.GetProperty("artifact")
            .GetProperty("manifestBytes").EnumerateArray().Select(value => value.GetByte()).ToArray());
        Assert.Equal(2, settingsObserved);
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("policy", "provider")]
    [InlineData("initial-authority", "provider")]
    [InlineData("recheck-authority", "provider")]
    [InlineData("policy", "timeout")]
    [InlineData("policy", "wrapped-timeout")]
    [InlineData("policy", "provider-timeout")]
    [InlineData("policy", "wrapped-provider-timeout")]
    [InlineData("initial-authority", "timeout")]
    [InlineData("initial-authority", "provider-timeout")]
    [InlineData("recheck-authority", "wrapped-timeout")]
    public async Task NativeAcsPlatformPolicyReadSanitizesDatabaseFailuresInResponsesAndLogs(
        string phase, string failure)
    {
        const string sentinel = "synthetic-sensitive-provider-detail-2655";
        var (subject, tenant) = await SeedPlatformPolicyReadCallerAsync();
        var logs = new PolicyReadLogProvider();
        var interceptor = new PlatformReadFailureInterceptor(phase, failure, sentinel);
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, commandInterceptor: interceptor, loggerProvider: logs);
        using var client = CreatePlatformPolicyReader(host, subject, tenant);

        using var response = await client.GetAsync("/api/policies/platform/");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("native_acs_policy_source_unavailable", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain(sentinel, body, StringComparison.Ordinal);
        Assert.Equal(phase != "initial-authority", interceptor.Rechecked);
        Assert.Contains(logs.Entries, entry =>
            entry.EventName == "Microsoft.EntityFrameworkCore.Query.QueryIterationFailed" &&
            entry.Message == "Platform Policy read database event.");
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(sentinel, StringComparison.Ordinal));

        interceptor.Reset();
        logs.Clear();
        using var historyResponse = await client.GetAsync("/api/policies/platform/revisions");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, historyResponse.StatusCode);
        Assert.Contains(logs.Entries, entry =>
            entry.EventName == "Microsoft.EntityFrameworkCore.Query.QueryIterationFailed" &&
            entry.Message.Contains(sentinel, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAcsPlatformPolicyReadPropagatesCancellationWhenDatabaseFailureRaces(
        bool wrappedProviderFailure)
    {
        var (subject, tenant) = await SeedPlatformPolicyReadCallerAsync();
        using var cancellation = new CancellationTokenSource();
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(_ =>
            {
                cancellation.Cancel();
                return Task.FromException(wrappedProviderFailure
                    ? new InvalidOperationException("synthetic", new NpgsqlException("synthetic"))
                    : new TimeoutException("synthetic"));
            }),
            requestToken: cancellation.Token);
        using var client = CreatePlatformPolicyReader(host, subject, tenant);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("/api/policies/platform/"));
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyReadPreservesAllArtifactProfilesAndHistoricalSizes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "head-profiles-" + suffix;
        var tenant = "head-profiles-tenant-" + suffix;
        var caller = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var finite = FiniteNetworkArtifact("approved.example.com");
        var network = Encoding.UTF8.GetString(finite.Files.Single(file => file.RelativePath == "policy/data.json").Bytes.AsSpan());
        var actions = """{"allowed_action_ids":["merge","model.turn"]}""";
        var composed = NativeAcsComposableDomainsProfile.CreateArtifact(actions, network);
        var unified = NativeAcsUnifiedPolicyProfile.CreateArtifact(actions, network,
            """{"rules":[{"effect":"allow","model_selection_reference":"byok-model","model_id":"deployment-1","source_mode":"byok","provider_type":"azure","provider_id":"byok-model","configuration_revision":"7","configuration_hash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}""",
            """{"required_checks":[{"provider_id":"prompt-shields","adapter_version":"1.0.0","options_schema_version":1,"options_revision":"options-v1"}],"blocked_findings":[{"check_id":"prompt-shields","code":"prompt-injection","severity":"high"}]}""");
        Assert.True(NativeAcsFiniteNetworkProfile.Validate(finite).IsValid);
        Assert.True(NativeAcsComposableDomainsProfile.Inspect(composed).IsValid);
        Assert.True(NativeAcsUnifiedPolicyProfile.Inspect(unified).IsValid);
        var opaque = HistoryArtifact("opaque " + suffix);
        var large = opaque with
        {
            Files = opaque.Files.Add(new NativeAcsArtifactFile(
                "data/large.json", System.Collections.Immutable.ImmutableArray.CreateRange(new byte[262_145])))
        };
        large = large with { ContentSha256 = NativeAcsArtifactHash.Compute(large) };
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenant);
        var index = 0;
        foreach (var artifact in new[] { finite, composed, unified, opaque, large })
        {
            var receipt = await WritePlatformPolicyAsync(caller, artifact, $"profile-{suffix}-{index}",
                $"profile-correlation-{suffix}-{index}");
            var state = await CapturePolicyTableStateAsync();
            using var response = await client.GetAsync("/api/policies/platform/");
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(receipt.PolicyId, body.RootElement.GetProperty("policyId").GetString());
            Assert.Equal(receipt.Revision, body.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(artifact.ContentSha256, body.RootElement.GetProperty("contentSha256").GetString());
            var returned = JsonSerializer.Deserialize<NativeAcsArtifact>(
                body.RootElement.GetProperty("artifact"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(returned);
            Assert.Equal(artifact.ManifestBytes.ToArray(), returned.ManifestBytes.ToArray());
            Assert.Equal(artifact.Files.Length, returned.Files.Length);
            foreach (var file in artifact.Files)
                Assert.Equal(file.Bytes.ToArray(), returned.Files.Single(item => item.RelativePath == file.RelativePath).Bytes.ToArray());
            Assert.Equal(artifact.ContentSha256, NativeAcsArtifactHash.Compute(returned));
            Assert.Equal(state, await CapturePolicyTableStateAsync());
            index++;
        }
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyReadDeniesRevocationAfterCorruptResult()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "head-corrupt-revoked-" + suffix;
        var tenantId = "head-corrupt-revoked-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId, ["api.read"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var grant = caller.Grants.Single(item => item.Role == ProjectAuthorityRole.PlatformAdmin);
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(async cancellationToken =>
            {
                await authorityStore.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, "fixture", cancellationToken);
                throw new ProjectConfigException("native_acs_policy_corrupt", "Stored source failed integrity validation.", 500);
            }));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        var state = await CapturePolicyTableStateAsync();

        using var response = await client.GetAsync("/api/policies/platform/");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("native_acs_policy_corrupt", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyReadRechecksAuthorityWhenHeadIsMissing()
    {
        var emptyFixture = new ProjectsConfigPostgresFixture();
        try
        {
            await emptyFixture.InitializeAsync();
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(emptyFixture.DataSource).Options;
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            await using var db = new ProjectsConfigDbContext(options);
            var suffix = Guid.NewGuid().ToString("N");
            var subject = "missing-head-" + suffix;
            var tenant = "missing-head-tenant-" + suffix;
            var caller = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read"],
                ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin);
            var state = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using (var host = await StartNativeAcsPolicyEndpointHostAsync(emptyFixture.DataSource, opaPath: null))
            using (var client = CreatePlatformPolicyReader(host, subject, tenant))
            using (var response = await client.GetAsync("/api/policies/platform/"))
            {
                Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal("native_acs_policy_not_found", body.RootElement.GetProperty("code").GetString());
            }
            var grant = caller.Grants.Single(item => item.Role == ProjectAuthorityRole.PlatformAdmin);
            using (var host = await StartNativeAcsPolicyEndpointHostAsync(
                emptyFixture.DataSource, opaPath: null,
                commandInterceptor: new PolicyReadInterceptor(cancellationToken =>
                    authorityStore.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, "fixture", cancellationToken))))
            using (var client = CreatePlatformPolicyReader(host, subject, tenant))
            using (var response = await client.GetAsync("/api/policies/platform/"))
            {
                Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
                var body = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("native_acs_policy_not_found", body, StringComparison.Ordinal);
                Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
            }
            Assert.Equal(state, await CapturePolicyTableStateAsync(emptyFixture.DataSource));
        }
        finally
        {
            await emptyFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyReadReturnsUnavailableWhenAuthorityRecheckFails()
    {
        var (subject, tenant) = await SeedPlatformPolicyReadCallerAsync();
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, commandInterceptor: new AuthorityRecheckFailureInterceptor());
        using var client = CreatePlatformPolicyReader(host, subject, tenant);
        var state = await CapturePolicyTableStateAsync();
        using var response = await client.GetAsync("/api/policies/platform/");
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("native_acs_policy_source_unavailable", document.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private connection detail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyReadRejectsCorruptStoredHash()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "head-hash-" + suffix;
        var tenant = "head-hash-tenant-" + suffix;
        var caller = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var artifact = HistoryArtifact("hash corruption " + suffix);
        var receipt = await WritePlatformPolicyAsync(caller, artifact, "hash-" + suffix, "hash-correlation-" + suffix);
        var before = await CapturePolicyTableStateAsync();
        async Task SetHashAsync(string hash)
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                """
                UPDATE projects_config.native_acs_policy_revisions SET content_sha256 = @hash
                WHERE scope_type = 'Platform' AND scope_id = 'default'
                  AND policy_id = @id AND revision = @revision
                """, connection);
            command.Parameters.AddWithValue("hash", hash);
            command.Parameters.AddWithValue("id", receipt.PolicyId);
            command.Parameters.AddWithValue("revision", receipt.Revision);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await SetImmutableTriggerEnabledAsync(
            fixture.DataSource, "native_acs_policy_revisions", "native_acs_policy_revisions_immutable", false);
        try
        {
            await SetHashAsync(new string(artifact.ContentSha256[0] == '0' ? '1' : '0', 64));
            using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
            using var client = CreatePlatformPolicyReader(host, subject, tenant);
            using var response = await client.GetAsync("/api/policies/platform/");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("native_acs_policy_corrupt", document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(receipt.ContentSha256, body, StringComparison.Ordinal);
            Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        }
        finally
        {
            try { await SetHashAsync(artifact.ContentSha256); }
            finally
            {
                await SetImmutableTriggerEnabledAsync(
                    fixture.DataSource, "native_acs_policy_revisions", "native_acs_policy_revisions_immutable", true);
            }
        }
        Assert.Equal(before, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsProjectValidationRequiresCurrentProjectAuthorAndHasNoPolicyEffects()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "project-validation-tenant-" + suffix;
        var tenantSubject = "project-validation-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, tenantSubject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var project = await service.CreateProjectAsync(tenantAdmin, "Validation project " + suffix, CancellationToken.None);
        var other = await service.CreateProjectAsync(tenantAdmin, "Other project " + suffix, CancellationToken.None);
        var ownerSubject = "project-validation-owner-" + suffix;
        var viewerSubject = "project-validation-viewer-" + suffix;
        var contributorSubject = "project-validation-contributor-" + suffix;
        var platformSubject = "project-validation-platform-" + suffix;
        var ownerCaller = await SeedCallerAsync(
            db, authorityStore, ownerSubject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        await authorityStore.AssignRoleAsync(ownerCaller.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        foreach (var (subject, role) in new[]
        {
            (viewerSubject, ProjectAuthorityRole.Viewer),
            (contributorSubject, ProjectAuthorityRole.Contributor)
        })
            _ = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read", "projects.admin"],
                ProjectAuthorityResourceType.Project, project.ProjectId, role);
        _ = await SeedCallerAsync(db, authorityStore, platformSubject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, Path.Combine(AppContext.BaseDirectory, "missing-opa"));
        using var owner = CreateProjectPolicyReader(host, ownerSubject, tenant);
        using var admin = CreateProjectPolicyReader(host, tenantSubject, tenant);
        using var viewer = CreateProjectPolicyReader(host, viewerSubject, tenant);
        using var contributor = CreateProjectPolicyReader(host, contributorSubject, tenant);
        using var platform = CreateProjectPolicyReader(host, platformSubject, tenant);
        using var bound = CreateProjectPolicyReader(host, ownerSubject, tenant, project.ProjectId);
        using var runBound = CreateProjectPolicyReader(host, ownerSubject, tenant, project.ProjectId, "run-" + suffix);
        using var purpose = CreateProjectPolicyReader(host, ownerSubject, tenant, purpose: "policy.validate");
        using var wrongTenant = CreateProjectPolicyReader(host, ownerSubject, "wrong-tenant-" + suffix);
        using var noAdminScope = CreateProjectPolicyReader(host, ownerSubject, tenant);
        foreach (var client in new[] { owner, admin, viewer, contributor, platform, bound, runBound, purpose, wrongTenant })
        {
            client.DefaultRequestHeaders.Remove("X-Test-Scope");
            client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
        }

        var artifact = NativeAcsComposableDomainsProfile.CreateProjectArtifact("""{"allowed_action_ids":[]}""");
        var network = artifact with
        {
            Files = artifact.Files.Add(new NativeAcsArtifactFile(
                "policy/data.json", Encoding.UTF8.GetBytes("{}").ToImmutableArray()))
        };
        network = network with { ContentSha256 = NativeAcsArtifactHash.Compute(network) };
        var path = $"/api/policies/projects/{project.ProjectId}/validate";
        var state = await CapturePolicyTableStateAsync();
        async Task<HttpResponseMessage> Validate(HttpClient client, NativeAcsArtifact input, string? route = null) =>
            await client.PostAsJsonAsync(route ?? path, new NativeAcsPolicyValidationRequest { Artifact = input });

        using var ownerResponse = await Validate(owner, artifact);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, ownerResponse.StatusCode);
        Assert.True(ownerResponse.Headers.CacheControl?.NoStore);
        using var adminResponse = await Validate(admin, artifact);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, adminResponse.StatusCode);
        using var networkResponse = await Validate(owner, network);
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, networkResponse.StatusCode);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var requestJson = JsonSerializer.Serialize(new NativeAcsPolicyValidationRequest { Artifact = artifact }, jsonOptions);
        var duplicateHash = requestJson.Replace(
            "\"contentSha256\":", "\"contentSha256\":\"invalid\",\"contentSha256\":", StringComparison.Ordinal);
        using var duplicateResponse = await owner.PostAsync(path,
            new StringContent(duplicateHash, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, duplicateResponse.StatusCode);
        var duplicateArtifact = requestJson.Replace(
            "\"artifact\":", "\"artifact\":null,\"artifact\":", StringComparison.Ordinal);
        using var duplicateRootResponse = await owner.PostAsync(path,
            new StringContent(duplicateArtifact, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, duplicateRootResponse.StatusCode);
        var caseDuplicate = requestJson.Replace(
            "\"contentSha256\":", "\"ContentSha256\":\"invalid\",\"contentSha256\":", StringComparison.Ordinal);
        using var caseDuplicateResponse = await admin.PostAsync(path,
            new StringContent(caseDuplicate, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, caseDuplicateResponse.StatusCode);
        var pascalCaseRequest = JsonSerializer.Serialize(new NativeAcsPolicyValidationRequest { Artifact = artifact });
        using var pascalCaseResponse = await admin.PostAsync(path,
            new StringContent(pascalCaseRequest, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, pascalCaseResponse.StatusCode);
        var largePathArtifact = artifact with
        {
            Files = [new NativeAcsArtifactFile(new string('\u2028', 900_000), ImmutableArray<byte>.Empty)],
        };
        var largeRequest = JsonSerializer.Serialize(
            new NativeAcsPolicyValidationRequest { Artifact = largePathArtifact }, jsonOptions)
            .Replace("\\u2028", "\u2028", StringComparison.OrdinalIgnoreCase);
        Assert.True(Encoding.UTF8.GetByteCount(largeRequest) < 5 * 1024 * 1024);
        using var largeResponse = await owner.PostAsync(path,
            new StringContent(largeRequest, Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, largeResponse.StatusCode);
        var responseBytes = await largeResponse.Content.ReadAsByteArrayAsync();
        Assert.True(responseBytes.Length < 5 * 1024 * 1024, $"Response size: {responseBytes.Length} bytes.");
        using var largeDocument = JsonDocument.Parse(responseBytes);
        var fileSize = largeDocument.RootElement.GetProperty("diagnostics").EnumerateArray()
            .Single(item => item.GetProperty("code").GetString() == "file_size");
        Assert.Equal(JsonValueKind.Null, fileSize.GetProperty("relativePath").ValueKind);
        using var wrongProject = await Validate(owner, artifact, $"/api/policies/projects/{other.ProjectId}/validate");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, wrongProject.StatusCode);
        foreach (var deniedClient in new[] { viewer, contributor, platform, bound, runBound, purpose, wrongTenant, noAdminScope })
        {
            using var denied = await Validate(deniedClient, artifact);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.StatusCode);
        }
        using var query = await Validate(owner, artifact, path + "?scope=platform");
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, query.StatusCode);
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsProjectValidationDoesNotReadOrDisclosePlatformSource()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "project-validation-private-" + suffix;
        var tenantSubject = "project-validation-private-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(db, authorityStore, tenantSubject, tenant,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var platformAdmin = await SeedCallerAsync(db, authorityStore, "project-validation-private-platform-" + suffix,
            tenant, ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Private baseline validation " + suffix, CancellationToken.None);
        var projectArtifact = NativeAcsComposableDomainsProfile.CreateProjectArtifact("""{"allowed_action_ids":[]}""");
        await CreateNativeAcsStore().WriteAsync(tenantAdmin,
            Request(project.ProjectId, projectArtifact, null, "project-private-" + suffix), CancellationToken.None);
        await WritePlatformPolicyAsync(platformAdmin, Artifact("private-platform-first-" + suffix),
            "platform-first-" + suffix, "platform-correlation-first-" + suffix);

        var configuredOpaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("CI") == "true")
            Assert.True(File.Exists(configuredOpaPath), "Set ACS_OPA_PATH to the pinned OPA executable.");
        var opaPath = OperatingSystem.IsLinux() && File.Exists(configuredOpaPath)
            ? configuredOpaPath!
            : Path.Combine(AppContext.BaseDirectory, "missing-opa");
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath);
        using var client = CreateProjectPolicyReader(host, tenantSubject, tenant);
        client.DefaultRequestHeaders.Remove("X-Test-Scope");
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
        var path = $"/api/policies/projects/{project.ProjectId}/validate";
        using var first = await client.PostAsJsonAsync(path, new NativeAcsPolicyValidationRequest { Artifact = projectArtifact });
        var firstBody = await first.Content.ReadAsStringAsync();
        Assert.Equal(OperatingSystem.IsLinux() && File.Exists(opaPath)
                ? System.Net.HttpStatusCode.OK
                : System.Net.HttpStatusCode.ServiceUnavailable,
            first.StatusCode);

        await WritePlatformPolicyAsync(platformAdmin, Artifact("private-platform-second-" + suffix),
            "platform-second-" + suffix, "platform-correlation-second-" + suffix);
        var state = await CapturePolicyTableStateAsync();
        using var second = await client.PostAsJsonAsync(path, new NativeAcsPolicyValidationRequest { Artifact = projectArtifact });
        Assert.Equal(first.StatusCode, second.StatusCode);
        Assert.Equal(firstBody, await second.Content.ReadAsStringAsync());
        Assert.DoesNotContain("private-platform-", firstBody, StringComparison.Ordinal);
        using var read = await client.GetAsync($"/api/policies/projects/{project.ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, read.StatusCode);
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var returned = JsonSerializer.Deserialize<NativeAcsArtifact>(
            document.RootElement.GetProperty("artifact"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(returned);
        Assert.Equal(projectArtifact.ContentSha256, NativeAcsArtifactHash.Compute(returned));
        Assert.DoesNotContain(returned.Files, file => file.RelativePath == "policy/data.json");
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task NativeAcsProjectValidationRechecksRevokedRoleForInvalidAndDependency(
        bool tenantRole, bool invalidArtifact) =>
        CheckProjectValidationRoleRevocation(tenantRole, invalidArtifact ? "invalid" : "dependency");

    [ProjectNativeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NativeAcsProjectValidationRechecksRevokedRoleForSuccess(bool tenantRole) =>
        CheckProjectValidationRoleRevocation(tenantRole, "success");

    private async Task CheckProjectValidationRoleRevocation(bool tenantRole, string outcome)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "project-validation-revoke-" + suffix;
        var tenantAdmin = await SeedCallerAsync(db, authorityStore, "project-validation-revoke-admin-" + suffix,
            tenant, ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenant,
            ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Validation revocation " + suffix, CancellationToken.None);
        var subject = "project-validation-revoke-owner-" + suffix;
        var caller = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read", "projects.admin"],
            tenantRole ? ProjectAuthorityResourceType.Tenant : ProjectAuthorityResourceType.Project,
            tenantRole ? tenant : project.ProjectId,
            tenantRole ? ProjectAuthorityRole.TenantAdmin : ProjectAuthorityRole.Owner);
        var grant = await db.RoleAssignments.AsNoTracking().SingleAsync(item =>
            item.MembershipId == caller.MembershipId &&
            item.Role == (tenantRole ? ProjectAuthorityRole.TenantAdmin : ProjectAuthorityRole.Owner));
        var artifact = NativeAcsComposableDomainsProfile.CreateProjectArtifact("""{"allowed_action_ids":[]}""");
        if (outcome == "invalid")
        {
            artifact = artifact with
            {
                Files = artifact.Files.Add(new NativeAcsArtifactFile(
                    "policy/data.json", Encoding.UTF8.GetBytes("{}").ToImmutableArray()))
            };
            artifact = artifact with { ContentSha256 = NativeAcsArtifactHash.Compute(artifact) };
        }
        var opaPath = outcome == "success"
            ? Environment.GetEnvironmentVariable("ACS_OPA_PATH")
            : Path.Combine(AppContext.BaseDirectory, "missing-opa");
        Assert.False(string.IsNullOrEmpty(opaPath));
        if (outcome == "success")
            Assert.True(File.Exists(opaPath), "Set ACS_OPA_PATH to the pinned OPA executable.");
        var directResult = new NativeAcsArtifactValidator(opaPath!).ValidateComposedProject(artifact);
        Assert.Equal(outcome == "success", directResult.IsValid);
        Assert.Equal(outcome == "dependency", directResult.DependencyFailure is not null);
        Assert.Equal(outcome == "invalid", !directResult.Diagnostics.IsEmpty);
        var checks = 0;
        var revoked = 0;
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            if (Interlocked.Increment(ref checks) == 5)
            {
                await authorityStore.RevokeRoleAssignmentAsync(
                    grant.AssignmentId, grant.Revision, "fixture", cancellationToken);
                Interlocked.Increment(ref revoked);
            }
        }, "project_role_assignments");
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath, commandInterceptor: interceptor);
        using var client = CreateProjectPolicyReader(host, subject, tenant);
        client.DefaultRequestHeaders.Remove("X-Test-Scope");
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
        var state = await CapturePolicyTableStateAsync();
        using var response = await client.PostAsJsonAsync(
            $"/api/policies/projects/{project.ProjectId}/validate",
            new NativeAcsPolicyValidationRequest { Artifact = artifact });
        Assert.Equal(1, revoked);
        Assert.Contains(response.StatusCode, new[] { System.Net.HttpStatusCode.NotFound, System.Net.HttpStatusCode.Forbidden });
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("diagnostics", body, StringComparison.Ordinal);
        Assert.DoesNotContain("native_acs_dependency_unavailable", body, StringComparison.Ordinal);
        Assert.DoesNotContain("isValid", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(artifact.ContentSha256, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("policy/data.json", body, StringComparison.Ordinal);
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsProjectValidationPropagatesCancellationDuringAuthorityRecheck()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "project-validation-cancel-" + suffix;
        var subject = "project-validation-cancel-admin-" + suffix;
        var admin = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(admin, "Validation cancellation " + suffix, CancellationToken.None);
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var interceptor = new PolicyReadInterceptor(_ =>
        {
            if (Interlocked.Increment(ref calls) == 5)
                cancellation.Cancel();
            return Task.CompletedTask;
        }, "project_role_assignments");
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, Path.Combine(AppContext.BaseDirectory, "missing-opa"),
            commandInterceptor: interceptor, requestToken: cancellation.Token);
        using var client = CreateProjectPolicyReader(host, subject, tenant);
        client.DefaultRequestHeaders.Remove("X-Test-Scope");
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
        var artifact = NativeAcsComposableDomainsProfile.CreateProjectArtifact("""{"allowed_action_ids":[]}""");
        var state = await CapturePolicyTableStateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PostAsJsonAsync(
            $"/api/policies/projects/{project.ProjectId}/validate",
            new NativeAcsPolicyValidationRequest { Artifact = artifact }));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(state, await CapturePolicyTableStateAsync());
    }

    [ProjectNativeFact]
    public async Task NativeAcsProjectValidationKeepsSlotUntilCancelledNativeCallExits()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException();
        var opaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH");
        Assert.True(File.Exists(opaPath), "Set ACS_OPA_PATH to the pinned OPA executable.");
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "project-validation-slot-" + suffix;
        var subject = "project-validation-slot-admin-" + suffix;
        var admin = await SeedCallerAsync(db, authorityStore, subject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(admin, "Validation slot " + suffix, CancellationToken.None);
        var marker = Path.Combine(AppContext.BaseDirectory, "opa-" + suffix + ".started");
        var release = Path.Combine(AppContext.BaseDirectory, "opa-" + suffix + ".release");
        var wrapper = Path.Combine(AppContext.BaseDirectory, "opa-" + suffix + ".sh");
        await File.WriteAllTextAsync(wrapper,
            $"#!/bin/sh\nif [ \"$1\" = \"parse\" ] && [ ! -f \"{release}\" ]; then : > \"{marker}\"; " +
            $"i=0; while [ ! -f \"{release}\" ] && [ \"$i\" -lt 400 ]; do sleep 0.01; i=$((i+1)); done; fi\n" +
            $"exec \"{opaPath}\" \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cancellation = new CancellationTokenSource();
        Task<HttpResponseMessage>? pending = null;
        try
        {
            using var host = await StartNativeAcsPolicyEndpointHostAsync(
                fixture.DataSource, wrapper, requestToken: cancellation.Token);
            using var first = CreateProjectPolicyReader(host, subject, tenant);
            using var second = CreateProjectPolicyReader(host, subject, tenant);
            foreach (var client in new[] { first, second })
            {
                client.DefaultRequestHeaders.Remove("X-Test-Scope");
                client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read projects.admin");
            }
            second.DefaultRequestHeaders.Add("X-Test-No-Cancel", "true");
            var artifact = NativeAcsComposableDomainsProfile.CreateProjectArtifact("""{"allowed_action_ids":[]}""");
            var request = new NativeAcsPolicyValidationRequest { Artifact = artifact };
            var path = $"/api/policies/projects/{project.ProjectId}/validate";
            var state = await CapturePolicyTableStateAsync();
            pending = first.PostAsJsonAsync(path, request);
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(marker) && !pending.IsCompleted && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.True(File.Exists(marker), "The native OPA parser did not start.");
            cancellation.Cancel();
            using var busy = await second.PostAsJsonAsync(path, request);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            using var busyJson = JsonDocument.Parse(await busy.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_validation_busy", busyJson.RootElement.GetProperty("code").GetString());
            await File.WriteAllTextAsync(release, "");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await pending.WaitAsync(TimeSpan.FromSeconds(10)));
            using var after = await second.PostAsJsonAsync(path, request);
            Assert.Equal(System.Net.HttpStatusCode.OK, after.StatusCode);
            Assert.Equal(state, await CapturePolicyTableStateAsync());
        }
        finally
        {
            await File.WriteAllTextAsync(release, "");
            if (pending is not null)
            {
                try { using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (OperationCanceledException) { }
            }
            File.Delete(wrapper);
            File.Delete(marker);
            File.Delete(release);
        }
    }

    [Fact]
    public async Task NativeAcsProjectPolicyReadsEnforceScopeAndReturnOnlyStoredSources()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-project-reader-tenant-" + suffix;
        var tenantAdminSubject = "acs-project-tenant-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, tenantAdminSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var projectsService = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var projects = new List<ProjectSummary>();
        for (var index = 0; index < 5; index++)
            projects.Add(await projectsService.CreateProjectAsync(
                tenantAdmin, $"Project policy read {suffix} {index}", CancellationToken.None));

        var roles = new[]
        {
            ProjectAuthorityRole.Owner,
            ProjectAuthorityRole.Contributor,
            ProjectAuthorityRole.Viewer,
            ProjectAuthorityRole.Owner,
        };
        var readerSubjects = new List<string>();
        for (var index = 0; index < roles.Length; index++)
        {
            var subject = $"acs-project-reader-{index}-{suffix}";
            readerSubjects.Add(subject);
            _ = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read"], ProjectAuthorityResourceType.Project,
                projects[index].ProjectId, roles[index]);
        }
        var platformAdminSubject = "acs-project-platform-admin-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, platformAdminSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        var store = CreateNativeAcsStore();
        var artifacts = new List<NativeAcsArtifact>();
        var receipts = new List<NativeAcsPolicyWriteReceipt>();
        string? firstProjectHistoryCursor = null;
        for (var index = 0; index < 4; index++)
        {
            var artifact = HistoryArtifact($"project policy {suffix} {index} first");
            artifacts.Add(artifact);
            receipts.Add(await store.WriteAsync(
                tenantAdmin,
                Request(
                    projects[index].ProjectId,
                    artifact,
                    expectedRevision: null,
                    $"acs-project-policy-{suffix}-{index}"),
                CancellationToken.None));
        }
        var secondArtifact = HistoryArtifact($"project policy {suffix} first second");
        artifacts.Add(secondArtifact);
        receipts.Add(await store.WriteAsync(
            tenantAdmin,
            Request(
                projects[0].ProjectId,
                secondArtifact,
                expectedRevision: 1,
                $"acs-project-policy-{suffix}-second"),
            CancellationToken.None));

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        using var tenantAdminClient = CreateProjectPolicyReader(host, tenantAdminSubject, tenantId);
        using var firstList = await tenantAdminClient.GetAsync("/api/policies/projects?pageSize=2");
        Assert.Equal(System.Net.HttpStatusCode.OK, firstList.StatusCode);
        Assert.True(firstList.Headers.CacheControl?.NoStore);
        using var firstListBody = JsonDocument.Parse(await firstList.Content.ReadAsStringAsync());
        var firstListItems = firstListBody.RootElement.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(2, firstListItems.Length);
        Assert.All(firstListItems, item =>
        {
            Assert.Equal("project", item.GetProperty("scopeType").GetString());
            Assert.False(item.TryGetProperty("artifact", out _));
        });
        var listCursor = firstListBody.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(listCursor);
        using var secondList = await tenantAdminClient.GetAsync(
            "/api/policies/projects?pageSize=2&cursor=" + Uri.EscapeDataString(listCursor!));
        Assert.Equal(System.Net.HttpStatusCode.OK, secondList.StatusCode);
        using var secondListBody = JsonDocument.Parse(await secondList.Content.ReadAsStringAsync());
        Assert.Equal(2, secondListBody.RootElement.GetProperty("items").GetArrayLength());
        Assert.Null(secondListBody.RootElement.GetProperty("nextCursor").GetString());
        var listedIds = firstListItems
            .Concat(secondListBody.RootElement.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("scopeId").GetString())
            .ToArray();
        Assert.Equal(projects.Take(4).Select(project => project.ProjectId).Order(StringComparer.Ordinal), listedIds);
        using var tenantAdminHead = await tenantAdminClient.GetAsync(
            $"/api/policies/projects/{projects[0].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, tenantAdminHead.StatusCode);

        for (var index = 0; index < 4; index++)
        {
            using var reader = CreateProjectPolicyReader(host, readerSubjects[index], tenantId);
            using var currentResponse = await reader.GetAsync($"/api/policies/projects/{projects[index].ProjectId}");
            Assert.Equal(System.Net.HttpStatusCode.OK, currentResponse.StatusCode);
            Assert.True(currentResponse.Headers.CacheControl?.NoStore);
            using var current = JsonDocument.Parse(await currentResponse.Content.ReadAsStringAsync());
            var selectedArtifact = index == 0 ? secondArtifact : artifacts[index];
            var selectedReceipt = index == 0 ? receipts[^1] : receipts[index];
            Assert.Equal(selectedReceipt.PolicyId, current.RootElement.GetProperty("policyId").GetString());
            Assert.Equal(selectedReceipt.Revision, current.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(selectedArtifact.ContentSha256, current.RootElement.GetProperty("contentSha256").GetString());
            Assert.Equal(
                selectedArtifact.ManifestBytes.ToArray(),
                current.RootElement.GetProperty("artifact").GetProperty("manifestBytes")
                    .EnumerateArray().Select(value => value.GetByte()).ToArray());

            using var historyResponse = await reader.GetAsync(
                $"/api/policies/projects/{projects[index].ProjectId}/revisions?pageSize=1");
            Assert.Equal(System.Net.HttpStatusCode.OK, historyResponse.StatusCode);
            using var history = JsonDocument.Parse(await historyResponse.Content.ReadAsStringAsync());
            Assert.Equal(selectedReceipt.PolicyId, history.RootElement.GetProperty("policyId").GetString());
            Assert.Single(history.RootElement.GetProperty("items").EnumerateArray());
            if (index == 0)
            {
                var cursor = history.RootElement.GetProperty("nextCursor").GetString();
                Assert.NotNull(cursor);
                firstProjectHistoryCursor = cursor;
                using var continuation = await reader.GetAsync(
                    $"/api/policies/projects/{projects[index].ProjectId}/revisions?pageSize=1&cursor=" +
                    Uri.EscapeDataString(cursor!));
                Assert.Equal(System.Net.HttpStatusCode.OK, continuation.StatusCode);
                using var secondHistory = JsonDocument.Parse(await continuation.Content.ReadAsStringAsync());
                Assert.Equal(1, secondHistory.RootElement.GetProperty("items")[0]
                    .GetProperty("revision").GetInt64());
                using var exact = await reader.GetAsync(
                    $"/api/policies/projects/{projects[index].ProjectId}/revisions/1");
                Assert.Equal(System.Net.HttpStatusCode.OK, exact.StatusCode);
                using var exactBody = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
                Assert.Equal(artifacts[0].Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal).First()
                        .Bytes.ToArray(),
                    exactBody.RootElement.GetProperty("artifact").GetProperty("files")[0]
                        .GetProperty("bytes").EnumerateArray().Select(value => value.GetByte()).ToArray());
            }
        }

        using var crossProjectCursor = await tenantAdminClient.GetAsync(
            $"/api/policies/projects/{projects[1].ProjectId}/revisions?pageSize=1&cursor=" +
            Uri.EscapeDataString(firstProjectHistoryCursor!));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, crossProjectCursor.StatusCode);

        using var owner = CreateProjectPolicyReader(host, readerSubjects[0], tenantId, projects[0].ProjectId);
        using var boundRead = await owner.GetAsync($"/api/policies/projects/{projects[0].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.OK, boundRead.StatusCode);
        using var boundList = await owner.GetAsync("/api/policies/projects");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, boundList.StatusCode);
        using var unboundOwner = CreateProjectPolicyReader(host, readerSubjects[0], tenantId);
        using var ownerList = await unboundOwner.GetAsync("/api/policies/projects");
        Assert.Equal(System.Net.HttpStatusCode.OK, ownerList.StatusCode);
        using var ownerListBody = JsonDocument.Parse(await ownerList.Content.ReadAsStringAsync());
        var ownerListItem = Assert.Single(ownerListBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(projects[0].ProjectId, ownerListItem.GetProperty("scopeId").GetString());
        using var crossProjectRead = await owner.GetAsync($"/api/policies/projects/{projects[1].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, crossProjectRead.StatusCode);
        using var runBound = CreateProjectPolicyReader(
            host, readerSubjects[0], tenantId, projects[0].ProjectId, runId: "bound-run-" + suffix);
        using var runBoundRead = await runBound.GetAsync($"/api/policies/projects/{projects[0].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, runBoundRead.StatusCode);
        using var purposeBound = CreateProjectPolicyReader(
            host, readerSubjects[0], tenantId, projects[0].ProjectId, purpose: "policy.read");
        using var purposeBoundRead = await purposeBound.GetAsync($"/api/policies/projects/{projects[0].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, purposeBoundRead.StatusCode);

        using var platformAdmin = CreateProjectPolicyReader(host, platformAdminSubject, tenantId);
        using var platformAdminRead = await platformAdmin.GetAsync($"/api/policies/projects/{projects[0].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, platformAdminRead.StatusCode);

        using var noHeadHistory = await tenantAdminClient.GetAsync(
            $"/api/policies/projects/{projects[4].ProjectId}/revisions");
        Assert.Equal(System.Net.HttpStatusCode.OK, noHeadHistory.StatusCode);
        using var emptyHistory = JsonDocument.Parse(await noHeadHistory.Content.ReadAsStringAsync());
        Assert.Null(emptyHistory.RootElement.GetProperty("policyId").GetString());
        Assert.Empty(emptyHistory.RootElement.GetProperty("items").EnumerateArray());
        using var noHead = await tenantAdminClient.GetAsync($"/api/policies/projects/{projects[4].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, noHead.StatusCode);
        using var missingRevision = await tenantAdminClient.GetAsync(
            $"/api/policies/projects/{projects[0].ProjectId}/revisions/99");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missingRevision.StatusCode);

        using var changedTenant = CreateProjectPolicyReader(host, tenantAdminSubject, "another-tenant-" + suffix);
        using var tenantDenied = await changedTenant.GetAsync($"/api/policies/projects/{projects[0].ProjectId}");
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, tenantDenied.StatusCode);
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsProjectPolicyListProtectsRevokedCursorAndUsesOneBoundedAuthorizationQuery()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-project-list-tenant-" + suffix;
        var tenantAdminSubject = "acs-project-list-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, tenantAdminSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var projectsService = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var projects = new List<ProjectSummary>();
        for (var index = 0; index < 4; index++)
            projects.Add(await projectsService.CreateProjectAsync(
                tenantAdmin, $"Project policy list {suffix} {index}", CancellationToken.None));

        var readerSubject = "acs-project-list-reader-" + suffix;
        _ = await SeedCallerAsync(db, authorityStore, readerSubject, tenantId, ["api.read"]);
        var roleService = new ProjectRoleAssignmentService(
            new TestDbContextFactory(CreateDbContextOptions()), TimeProvider.System);
        var assignments = new Dictionary<string, ProjectRoleAssignmentView>(StringComparer.Ordinal);
        var store = CreateNativeAcsStore();
        foreach (var project in projects)
        {
            assignments.Add(project.ProjectId, await roleService.AssignAsync(
                tenantAdmin,
                project.ProjectId,
                new AssignProjectRoleRequest
                {
                    PrincipalId = readerSubject,
                    Role = ProjectAuthorityRole.Viewer,
                },
                CancellationToken.None));
            await store.WriteAsync(
                tenantAdmin,
                Request(
                    project.ProjectId,
                    HistoryArtifact($"project list policy {suffix} {project.ProjectId}"),
                    expectedRevision: null,
                    $"acs-project-list-policy-{suffix}-{project.ProjectId}"),
                CancellationToken.None);
        }

        var queryInterceptor = new PolicyScopeListQueryInterceptor();
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, commandInterceptor: queryInterceptor);
        using var reader = CreateProjectPolicyReader(host, readerSubject, tenantId);
        using var firstPage = await reader.GetAsync("/api/policies/projects?pageSize=1");
        Assert.Equal(System.Net.HttpStatusCode.OK, firstPage.StatusCode);
        using var firstPageBody = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        var firstItem = Assert.Single(firstPageBody.RootElement.GetProperty("items").EnumerateArray());
        var revokedProjectId = firstItem.GetProperty("scopeId").GetString()!;
        var cursor = firstPageBody.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        Assert.False(cursor!.Contains(revokedProjectId, StringComparison.Ordinal));

        var listQuery = Assert.Single(queryInterceptor.ScopeListQueries);
        Assert.Contains("LIMIT", listQuery, StringComparison.OrdinalIgnoreCase);

        var assignment = assignments[revokedProjectId];
        await roleService.RevokeAsync(
            tenantAdmin,
            revokedProjectId,
            assignment.AssignmentId,
            assignment.Revision,
            CancellationToken.None);

        using var stalePage = await reader.GetAsync(
            "/api/policies/projects?pageSize=1&cursor=" + Uri.EscapeDataString(cursor));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, stalePage.StatusCode);
        var staleBody = await stalePage.Content.ReadAsStringAsync();
        Assert.DoesNotContain(revokedProjectId, staleBody, StringComparison.Ordinal);

        using var refreshedPage = await reader.GetAsync("/api/policies/projects?pageSize=10");
        Assert.Equal(System.Net.HttpStatusCode.OK, refreshedPage.StatusCode);
        using var refreshedBody = JsonDocument.Parse(await refreshedPage.Content.ReadAsStringAsync());
        var visibleIds = refreshedBody.RootElement.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("scopeId").GetString()!)
            .ToArray();
        Assert.Equal(
            projects.Select(project => project.ProjectId)
                .Where(projectId => projectId != revokedProjectId)
                .Order(StringComparer.Ordinal),
            visibleIds);
    }

    [Theory]
    [InlineData("/api/policies/projects?unknown=x", "native_acs_policy_scope_list_request_invalid")]
    [InlineData("/api/policies/projects?pageSize=1&pageSize=2", "native_acs_policy_scope_list_request_invalid")]
    [InlineData("/api/policies/projects?pageSize=101", "native_acs_policy_scope_list_request_invalid")]
    [InlineData("/api/policies/projects/00000000000000000000000000000001?cursor=x",
        "native_acs_policy_query_not_supported")]
    [InlineData("/api/policies/projects/00000000000000000000000000000001/revisions?unknown=x",
        "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/projects/00000000000000000000000000000001/revisions/1?cursor=x",
        "native_acs_policy_revision_request_invalid")]
    public async Task NativeAcsProjectPolicyReadsRejectUndeclaredAndDuplicateQueryValues(
        string path,
        string expectedCode)
    {
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreateProjectPolicyReader(host, "invalid-project-query", "invalid-project-tenant");

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NativeAcsProjectPolicyReadRejectsCorruptArtifactBytes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-project-corrupt-tenant-" + suffix;
        var tenantAdminSubject = "acs-project-corrupt-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, tenantAdminSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Project Policy corrupt read", CancellationToken.None);
        var artifact = Artifact("project source integrity " + suffix);
        var receipt = await CreateNativeAcsStore().WriteAsync(
            tenantAdmin,
            Request(project.ProjectId, artifact, null, "acs-project-corrupt-" + suffix),
            CancellationToken.None);
        await using var readDb = CreateDbContext();
        var file = await readDb.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .SingleAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Project &&
                item.ScopeId == project.ProjectId &&
                item.PolicyId == receipt.PolicyId &&
                item.Revision == receipt.Revision);
        var originalBytes = file.Bytes.ToArray();
        var relativePath = file.RelativePath;
        var stateBeforeCorruption = await CapturePolicyTableStateAsync();
        try
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetProjectArtifactFileBytesAsync(
                    project.ProjectId, receipt.PolicyId, receipt.Revision, relativePath, [.. originalBytes, 0x7f]));
            using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
            using var client = CreateProjectPolicyReader(host, tenantAdminSubject, tenantId);
            var stateBeforeRead = await CapturePolicyTableStateAsync();

            using var response = await client.GetAsync($"/api/policies/projects/{project.ProjectId}");

            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            using var result = JsonDocument.Parse(body);
            Assert.Equal("native_acs_policy_corrupt", result.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
            Assert.Equal(stateBeforeRead, await CapturePolicyTableStateAsync());
        }
        finally
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetProjectArtifactFileBytesAsync(
                    project.ProjectId, receipt.PolicyId, receipt.Revision, relativePath, originalBytes));
        }
        Assert.Equal(stateBeforeCorruption, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsProjectPolicyReadRechecksMembershipAfterRetrieval()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-project-revoked-tenant-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, "acs-project-revoked-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Project Policy revocation read", CancellationToken.None);
        var ownerSubject = "acs-project-revoked-owner-" + suffix;
        var owner = await SeedCallerAsync(
            db, authorityStore, ownerSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);
        var receipt = await CreateNativeAcsStore().WriteAsync(
            tenantAdmin,
            Request(project.ProjectId, Artifact("revoke during project source read " + suffix),
                null, "acs-project-revoke-read-" + suffix),
            CancellationToken.None);
        var revoked = 0;
        var interceptor = new PolicyReadInterceptor(async cancellationToken =>
        {
            if (Interlocked.Exchange(ref revoked, 1) == 0)
                await authorityStore.RevokeMembershipAsync(
                    owner.MembershipId, owner.MembershipRevision, "fixture", cancellationToken);
        });
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, commandInterceptor: interceptor);
        using var client = CreateProjectPolicyReader(host, ownerSubject, tenantId);
        var stateBeforeRead = await CapturePolicyTableStateAsync();

        using var response = await client.GetAsync($"/api/policies/projects/{project.ProjectId}");

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.PolicyId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.Equal(1, revoked);
        Assert.Equal(stateBeforeRead, await CapturePolicyTableStateAsync());
    }

    [Fact]
    public async Task NativeAcsPlatformPolicyHistoryPagesAtOneSnapshotAndReadsExactArtifactBytes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-history-reader-" + suffix;
        var tenantId = "acs-history-reader-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifacts = new[]
        {
            HistoryArtifact("history revision one " + suffix),
            HistoryArtifact("history revision two " + suffix),
            HistoryArtifact("history revision three " + suffix),
        };
        var receipts = new List<NativeAcsPolicyWriteReceipt>();
        for (var index = 0; index < artifacts.Length; index++)
            receipts.Add(await WritePlatformPolicyAsync(
                caller,
                artifacts[index],
                $"acs-history-{suffix}-{index}",
                $"acs-history-correlation-{suffix}-{index}"));

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        var stateBeforeFirstPage = await CapturePolicyTableStateAsync();
        using var firstResponse = await client.GetAsync("/api/policies/platform/revisions?pageSize=2");
        Assert.Equal(System.Net.HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.True(firstResponse.Headers.CacheControl?.NoStore);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        var firstRoot = first.RootElement;
        var snapshot = firstRoot.GetProperty("snapshotRevision").GetInt64();
        var policyId = firstRoot.GetProperty("policyId").GetString();
        var items = firstRoot.GetProperty("items").EnumerateArray().ToList();
        var cursor = firstRoot.GetProperty("nextCursor").GetString();
        Assert.Equal(receipts[^1].Revision, snapshot);
        Assert.Equal(receipts[0].PolicyId, policyId);
        Assert.Equal(2, items.Count);
        Assert.Equal(stateBeforeFirstPage, await CapturePolicyTableStateAsync());
        var expectedRevisions = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item => item.PolicyId == policyId && item.Revision <= snapshot)
            .OrderByDescending(item => item.Revision)
            .Select(item => item.Revision)
            .ToArrayAsync();

        var appended = await WritePlatformPolicyAsync(
            caller, HistoryArtifact("history appended after page one " + suffix),
            "acs-history-appended-" + suffix, "acs-history-appended-correlation-" + suffix);
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        using var continuationHost = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var continuationClient = CreatePlatformPolicyReader(continuationHost, subject, tenantId);
        if (cursor is not null)
        {
            var retryPath = "/api/policies/platform/revisions?pageSize=2&cursor=" +
                Uri.EscapeDataString(cursor);
            using var retryOne = await continuationClient.GetAsync(retryPath);
            using var retryTwo = await continuationClient.GetAsync(retryPath);
            Assert.Equal(await retryOne.Content.ReadAsStringAsync(), await retryTwo.Content.ReadAsStringAsync());
        }

        var all = items.Select(item => item.GetProperty("revision").GetInt64()).ToList();
        var exhaustedPageSize = 0;
        while (cursor is not null)
        {
            using var pageResponse = await continuationClient.GetAsync(
                "/api/policies/platform/revisions?pageSize=2&cursor=" + Uri.EscapeDataString(cursor));
            Assert.Equal(System.Net.HttpStatusCode.OK, pageResponse.StatusCode);
            Assert.True(pageResponse.Headers.CacheControl?.NoStore);
            using var page = JsonDocument.Parse(await pageResponse.Content.ReadAsStringAsync());
            Assert.Equal(snapshot, page.RootElement.GetProperty("snapshotRevision").GetInt64());
            Assert.Equal(policyId, page.RootElement.GetProperty("policyId").GetString());
            var pageItems = page.RootElement.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("revision").GetInt64()).ToArray();
            exhaustedPageSize = pageItems.Length;
            all.AddRange(pageItems);
            cursor = page.RootElement.GetProperty("nextCursor").GetString();
        }
        Assert.InRange(exhaustedPageSize, 1, 2);
        Assert.DoesNotContain(appended.Revision, all);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(all.OrderByDescending(value => value), all);
        Assert.Equal(expectedRevisions, all);
        foreach (var receipt in receipts)
            Assert.Contains(receipt.Revision, all);

        var expected = receipts[0];
        using var exactResponse = await continuationClient.GetAsync(
            $"/api/policies/platform/revisions/{expected.Revision}");
        Assert.Equal(System.Net.HttpStatusCode.OK, exactResponse.StatusCode);
        Assert.True(exactResponse.Headers.CacheControl?.NoStore);
        using var exact = JsonDocument.Parse(await exactResponse.Content.ReadAsStringAsync());
        Assert.Equal(policyId, exact.RootElement.GetProperty("policyId").GetString());
        Assert.Equal(expected.Revision, exact.RootElement.GetProperty("revision").GetInt64());
        Assert.Equal(expected.ContentSha256, exact.RootElement.GetProperty("contentSha256").GetString());
        Assert.Equal(expected.ContentSha256,
            exact.RootElement.GetProperty("artifact").GetProperty("contentSha256").GetString());
        Assert.Equal(artifacts[0].ManifestBytes.ToArray(),
            exact.RootElement.GetProperty("artifact").GetProperty("manifestBytes")
                .EnumerateArray().Select(value => value.GetByte()).ToArray());
        var exactFiles = exact.RootElement.GetProperty("artifact").GetProperty("files")
            .EnumerateArray().ToArray();
        var expectedFiles = artifacts[0].Files
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedFiles.Length, exactFiles.Length);
        for (var index = 0; index < exactFiles.Length; index++)
        {
            Assert.Equal(expectedFiles[index].RelativePath,
                exactFiles[index].GetProperty("relativePath").GetString());
            Assert.Equal(expectedFiles[index].Bytes.ToArray(),
                exactFiles[index].GetProperty("bytes").EnumerateArray()
                    .Select(value => value.GetByte()).ToArray());
        }

        await using var revisionDb = CreateDbContext();
        var storedCreatedAt = await revisionDb.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item => item.PolicyId == policyId && item.Revision == expected.Revision)
            .Select(item => item.CreatedAt)
            .SingleAsync();
        Assert.Equal(storedCreatedAt, exact.RootElement.GetProperty("createdAt").GetDateTimeOffset());
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    private static NativeAcsArtifact HistoryArtifact(string text)
    {
        var artifact = Artifact(text);
        artifact = artifact with
        {
            Files = artifact.Files
                .Add(new NativeAcsArtifactFile(
                    "data/rules.json",
                    System.Collections.Immutable.ImmutableArray.CreateRange(Encoding.UTF8.GetBytes($"{{\"name\":\"{text}\"}}"))))
                .Add(new NativeAcsArtifactFile(
                    "policies/helpers.rego",
                    System.Collections.Immutable.ImmutableArray.CreateRange(Encoding.UTF8.GetBytes($"package helper\n# {text}\n")))),
        };
        return artifact with { ContentSha256 = NativeAcsArtifactHash.Compute(artifact) };
    }

    [Fact]
    public async Task NativeAcsPolicyHistoryHandlesEmptyAndNonCanonicalLineage()
    {
        var emptyFixture = new ProjectsConfigPostgresFixture();
        try
        {
            await emptyFixture.InitializeAsync();
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(
                    emptyFixture.DataSource,
                    npgsql => npgsql.MigrationsHistoryTable(
                        "__ef_migrations_history", ProjectsConfigDbContext.Schema))
                .Options;
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N");
            var subject = "acs-empty-history-" + suffix;
            var tenantId = "acs-empty-history-tenant-" + suffix;
            await using var db = new ProjectsConfigDbContext(options);
            var caller = await SeedCallerAsync(
                db, authorityStore, subject, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            using var host = await StartNativeAcsPolicyEndpointHostAsync(emptyFixture.DataSource, opaPath: null);
            using var client = CreatePlatformPolicyReader(host, subject, tenantId);

            var stateBeforeEmptyReads = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using var emptyHistory = await client.GetAsync("/api/policies/platform/revisions");
            Assert.Equal(System.Net.HttpStatusCode.OK, emptyHistory.StatusCode);
            using (var empty = JsonDocument.Parse(await emptyHistory.Content.ReadAsStringAsync()))
            {
                Assert.Null(empty.RootElement.GetProperty("policyId").GetString());
                Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("snapshotRevision").ValueKind);
                Assert.Empty(empty.RootElement.GetProperty("items").EnumerateArray());
                Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("nextCursor").ValueKind);
            }
            using var missing = await client.GetAsync("/api/policies/platform/revisions/1");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(stateBeforeEmptyReads, await CapturePolicyTableStateAsync(emptyFixture.DataSource));

            var store = new NativeAcsPolicyStore(
                new TestDbContextFactory(options), TimeProvider.System, CreateTestDataProtectionProvider());
            var policyId = "stable-lineage-" + suffix;
            var receipts = new List<NativeAcsPolicyWriteReceipt>();
            for (var index = 0; index < 3; index++)
            {
                var current = await store.ReadAsync(
                    NativeAcsPolicyScopeType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId,
                    CancellationToken.None);
                receipts.Add(await store.WriteAsync(
                    caller,
                    new NativeAcsPolicyWriteRequest(
                        NativeAcsPolicyScopeType.Platform,
                        ProjectAuthorizationOwner.PlatformResourceId,
                        policyId,
                        current?.Revision,
                        HistoryArtifact($"noncanonical lineage {suffix} {index}"),
                        $"acs-noncanonical-{suffix}-{index}",
                        "Test history",
                        $"acs-noncanonical-correlation-{suffix}-{index}"),
                    CancellationToken.None));
            }

            var stateBeforeReads = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using var firstPage = await client.GetAsync("/api/policies/platform/revisions?pageSize=2");
            Assert.Equal(System.Net.HttpStatusCode.OK, firstPage.StatusCode);
            using var first = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
            Assert.Equal(policyId, first.RootElement.GetProperty("policyId").GetString());
            Assert.Equal(receipts[^1].Revision, first.RootElement.GetProperty("snapshotRevision").GetInt64());
            var cursor = first.RootElement.GetProperty("nextCursor").GetString();
            Assert.NotNull(cursor);
            using var lastPage = await client.GetAsync(
                "/api/policies/platform/revisions?pageSize=2&cursor=" + Uri.EscapeDataString(cursor!));
            using var last = JsonDocument.Parse(await lastPage.Content.ReadAsStringAsync());
            Assert.Equal(policyId, last.RootElement.GetProperty("policyId").GetString());
            Assert.Null(last.RootElement.GetProperty("nextCursor").GetString());

            using var exact = await client.GetAsync("/api/policies/platform/revisions/1");
            Assert.Equal(System.Net.HttpStatusCode.OK, exact.StatusCode);
            using var exactBody = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
            Assert.Equal(policyId, exactBody.RootElement.GetProperty("policyId").GetString());
            Assert.Equal(3, exactBody.RootElement.GetProperty("artifact").GetProperty("files").GetArrayLength());
            using var future = await client.GetAsync("/api/policies/platform/revisions/4");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, future.StatusCode);
            Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync(emptyFixture.DataSource));

            var fileTriggerEnabled = false;
            var revisionTriggerEnabled = false;
            try
            {
                await SetImmutableTriggerEnabledAsync(
                    emptyFixture.DataSource,
                    "native_acs_policy_artifact_files",
                    "native_acs_policy_artifact_files_immutable",
                    false);
                fileTriggerEnabled = true;
                await SetImmutableTriggerEnabledAsync(
                    emptyFixture.DataSource,
                    "native_acs_policy_revisions",
                    "native_acs_policy_revisions_immutable",
                    false);
                revisionTriggerEnabled = true;
                await using var connection = await emptyFixture.DataSource.OpenConnectionAsync();
                await using (var deleteFiles = new NpgsqlCommand(
                    """
                    DELETE FROM projects_config.native_acs_policy_artifact_files
                    WHERE scope_type = 'Platform' AND scope_id = 'default'
                      AND policy_id = @policy_id AND revision IN (2, 3)
                    """,
                    connection))
                {
                    deleteFiles.Parameters.AddWithValue("policy_id", policyId);
                    await deleteFiles.ExecuteNonQueryAsync();
                }
                await using var deleteRevision = new NpgsqlCommand(
                    """
                    DELETE FROM projects_config.native_acs_policy_revisions
                    WHERE scope_type = 'Platform' AND scope_id = 'default'
                      AND policy_id = @policy_id AND revision IN (2, 3)
                    """,
                    connection);
                deleteRevision.Parameters.AddWithValue("policy_id", policyId);
                await deleteRevision.ExecuteNonQueryAsync();
            }
            finally
            {
                try
                {
                    if (revisionTriggerEnabled)
                        await SetImmutableTriggerEnabledAsync(
                            emptyFixture.DataSource,
                            "native_acs_policy_revisions",
                            "native_acs_policy_revisions_immutable",
                            true);
                }
                finally
                {
                    if (fileTriggerEnabled)
                        await SetImmutableTriggerEnabledAsync(
                            emptyFixture.DataSource,
                            "native_acs_policy_artifact_files",
                            "native_acs_policy_artifact_files_immutable",
                            true);
                }
            }

            var stateBeforeMissingRead = await CapturePolicyTableStateAsync(emptyFixture.DataSource);
            using var missingCommitted = await client.GetAsync("/api/policies/platform/revisions/2");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, missingCommitted.StatusCode);
            using var missingCommittedBody = JsonDocument.Parse(await missingCommitted.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_corrupt", missingCommittedBody.RootElement.GetProperty("code").GetString());
            using var missingHeadRevision = await client.GetAsync("/api/policies/platform/");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, missingHeadRevision.StatusCode);
            using var missingHeadBody = JsonDocument.Parse(await missingHeadRevision.Content.ReadAsStringAsync());
            Assert.Equal("native_acs_policy_corrupt", missingHeadBody.RootElement.GetProperty("code").GetString());
            Assert.Equal(stateBeforeMissingRead, await CapturePolicyTableStateAsync(emptyFixture.DataSource));
        }
        finally
        {
            await emptyFixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task NativeAcsPolicyExactRevisionRejectsCorruptStoredBytes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-corrupt-reader-" + suffix;
        var tenantId = "acs-corrupt-reader-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var artifact = Artifact("corrupt exact read " + suffix);
        var receipt = await WritePlatformPolicyAsync(
            caller, artifact, "acs-corrupt-exact-" + suffix, "acs-corrupt-correlation-" + suffix);
        var stateBeforeCorruption = await CapturePolicyTableStateAsync();
        byte[] originalBytes;
        string relativePath;
        await using (var read = CreateDbContext())
        {
            var file = await read.NativeAcsPolicyArtifactFiles.AsNoTracking()
                .Where(item => item.PolicyId == receipt.PolicyId && item.Revision == receipt.Revision)
                .OrderBy(item => item.RelativePath)
                .FirstAsync();
            relativePath = file.RelativePath;
            originalBytes = file.Bytes.ToArray();
        }

        try
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetArtifactFileBytesAsync(
                    receipt.PolicyId, receipt.Revision, relativePath, [.. originalBytes, 0x7f]));

            using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
            using var client = CreatePlatformPolicyReader(host, subject, tenantId);
            using var response = await client.GetAsync(
                $"/api/policies/platform/revisions/{receipt.Revision}");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("native_acs_policy_corrupt", document.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(Convert.ToBase64String(originalBytes), body, StringComparison.Ordinal);
            using var headResponse = await client.GetAsync("/api/policies/platform/");
            Assert.Equal(System.Net.HttpStatusCode.InternalServerError, headResponse.StatusCode);
            var headBody = await headResponse.Content.ReadAsStringAsync();
            using var headDocument = JsonDocument.Parse(headBody);
            Assert.Equal("native_acs_policy_corrupt", headDocument.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain(relativePath, headBody, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToBase64String(originalBytes), headBody, StringComparison.Ordinal);
        }
        finally
        {
            await WithArtifactFilesImmutableTriggerDisabledAsync(
                () => SetArtifactFileBytesAsync(receipt.PolicyId, receipt.Revision, relativePath, originalBytes));
        }
        Assert.Equal(stateBeforeCorruption, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/?unknown=x", "native_acs_policy_query_not_supported")]
    [InlineData("/api/policies/platform/?x=1&x=2", "native_acs_policy_query_not_supported")]
    [InlineData("/api/policies/platform/revisions?unknown=x", "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/platform/revisions?pageSize=1&pageSize=2", "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/platform/revisions/1?cursor=x", "native_acs_policy_revision_request_invalid")]
    public async Task NativeAcsPolicyHistoryRoutesRejectUndeclaredAndDuplicateQueryValues(
        string path,
        string expectedCode)
    {
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, "malformed-query-subject", "malformed-query-tenant");

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/policies/platform/", "native_acs_policy_read_request_invalid")]
    [InlineData("/api/policies/platform/revisions", "native_acs_policy_history_request_invalid")]
    [InlineData("/api/policies/platform/revisions/1", "native_acs_policy_revision_request_invalid")]
    public async Task NativeAcsPolicyHistoryRoutesRejectRequestBodies(string path, string expectedCode)
    {
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, "malformed-body-subject", "malformed-body-tenant");
        using var request = new HttpRequestMessage(HttpMethod.Get, path)
        {
            Content = new ByteArrayContent([1]),
        };

        using var response = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, body.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeAcsPlatformPolicyReadRejectsContentTypeAndTransferEncoding(bool chunked)
    {
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, "invalid-body-subject", "invalid-body-tenant");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/policies/platform/");
        if (chunked)
        {
            request.Headers.TransferEncodingChunked = true;
            request.Content = new ByteArrayContent([1]);
        }
        else
            request.Content = new StringContent("");
        using var response = await client.SendAsync(request);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("native_acs_policy_read_request_invalid", body.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/policies/platform/")]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryRoutesRequireCurrentUnboundPlatformAuthority(string path)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-authority-tenant-" + suffix;
        var tenantAdminSubject = "acs-tenant-admin-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, tenantAdminSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        Assert.False(tenantAdmin.IsPlatformAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "Policy history authority test", CancellationToken.None);
        var projectOwnerSubject = "acs-project-owner-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, projectOwnerSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Owner);

        var staleSubject = "acs-stale-admin-" + suffix;
        var stale = await SeedCallerAsync(
            db, authorityStore, staleSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var staleGrant = stale.Grants.Single(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Platform &&
            grant.Role == ProjectAuthorityRole.PlatformAdmin);
        await authorityStore.RevokeRoleAssignmentAsync(
            staleGrant.AssignmentId, staleGrant.Revision, "fixture", CancellationToken.None);

        var validSubject = "acs-bound-admin-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, validSubject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var tenantAdminClient = CreatePlatformPolicyReader(host, tenantAdminSubject, tenantId);
        using var projectOwnerClient = CreatePlatformPolicyReader(host, projectOwnerSubject, tenantId);
        using var staleClient = CreatePlatformPolicyReader(host, staleSubject, tenantId);
        using var wrongTenantClient = CreatePlatformPolicyReader(host, validSubject, "another-tenant-" + suffix);
        using var projectBoundClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        projectBoundClient.DefaultRequestHeaders.Add("X-Test-Project", "bound-project-" + suffix);
        using var runBoundClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        runBoundClient.DefaultRequestHeaders.Add("X-Test-Project", "bound-project-" + suffix);
        runBoundClient.DefaultRequestHeaders.Add("X-Test-Run", "bound-run-" + suffix);
        using var purposeBoundClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        purposeBoundClient.DefaultRequestHeaders.Add("X-Test-Purpose", "policy.read");
        using var missingScopeClient = CreatePlatformPolicyReader(host, validSubject, tenantId);
        missingScopeClient.DefaultRequestHeaders.Remove("X-Test-Scope");
        missingScopeClient.DefaultRequestHeaders.Add("X-Test-Scope", "projects.admin");
        var stateBeforeDeniedReads = await CapturePolicyTableStateAsync();

        foreach (var client in new[]
        {
            tenantAdminClient,
            projectOwnerClient,
            staleClient,
            wrongTenantClient,
            projectBoundClient,
            runBoundClient,
            purposeBoundClient,
            missingScopeClient,
        })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
        Assert.Equal(stateBeforeDeniedReads, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/")]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryRoutesRejectRevokedMembership(string path)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-revoked-membership-" + suffix;
        var tenantId = "acs-revoked-membership-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var receipt = await WritePlatformPolicyAsync(
            caller, HistoryArtifact("revoked membership route " + suffix),
            "acs-revoked-membership-" + suffix, "acs-revoked-membership-correlation-" + suffix);
        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        await authorityStore.RevokeMembershipAsync(
            caller.MembershipId, 1, "fixture", CancellationToken.None);

        using var response = await client.GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.PolicyId, body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"policyId\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeAcsPolicyHistoryContinuationRejectsRevokedMembership()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-revoked-history-" + suffix;
        var tenantId = "acs-revoked-history-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        for (var index = 0; index < 2; index++)
            await WritePlatformPolicyAsync(
                caller, HistoryArtifact($"revoked continuation {suffix} {index}"),
                $"acs-revoked-history-{suffix}-{index}", $"acs-revoked-history-correlation-{suffix}-{index}");

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        using var first = await client.GetAsync("/api/policies/platform/revisions?pageSize=1");
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var cursor = firstBody.RootElement.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var stateBeforeContinuation = await CapturePolicyTableStateAsync();
        await authorityStore.RevokeMembershipAsync(
            caller.MembershipId, 1, "fixture", CancellationToken.None);
        using var continuation = await client.GetAsync(
            "/api/policies/platform/revisions?pageSize=1&cursor=" + Uri.EscapeDataString(cursor!));

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, continuation.StatusCode);
        Assert.True(continuation.Headers.CacheControl?.NoStore);
        var body = await continuation.Content.ReadAsStringAsync();
        Assert.DoesNotContain("\"policyId\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"artifact\"", body, StringComparison.Ordinal);
        Assert.Equal(stateBeforeContinuation, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/")]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryRoutesRecheckAuthorityAfterRetrieval(string path)
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-read-revoked-during-query-" + suffix;
        var tenantId = "acs-read-revoked-during-query-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var receipt = await WritePlatformPolicyAsync(
            caller, HistoryArtifact("revoked while reading " + suffix),
            "acs-read-revoked-query-" + suffix, "acs-read-revoked-query-correlation-" + suffix);
        var stateBeforeRead = await CapturePolicyTableStateAsync();
        var grant = caller.Grants.Single(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.Role == ProjectAuthorityRole.PlatformAdmin);
        var revoked = 0;
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(async cancellationToken =>
            {
                if (Interlocked.Exchange(ref revoked, 1) == 0)
                    await authorityStore.RevokeRoleAssignmentAsync(
                        grant.AssignmentId, grant.Revision, "fixture", cancellationToken);
            }, "native_acs_policy_revisions"));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        var requestPath = path.EndsWith("/revisions", StringComparison.Ordinal) || path.EndsWith("/platform/", StringComparison.Ordinal)
            ? path
            : $"/api/policies/platform/revisions/{receipt.Revision}";

        using var response = await client.GetAsync(requestPath);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(receipt.PolicyId, body, StringComparison.Ordinal);
        Assert.Equal(stateBeforeRead, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/", "unavailable", 503, "native_acs_policy_source_unavailable")]
    [InlineData("/api/policies/platform/", "corrupt", 500, "native_acs_policy_corrupt")]
    [InlineData("/api/policies/platform/revisions", "unavailable", 503, "native_acs_policy_source_unavailable")]
    [InlineData("/api/policies/platform/revisions/1", "unavailable", 503, "native_acs_policy_source_unavailable")]
    [InlineData("/api/policies/platform/revisions", "corrupt", 500, "native_acs_policy_corrupt")]
    [InlineData("/api/policies/platform/revisions/1", "corrupt", 500, "native_acs_policy_corrupt")]
    public async Task NativeAcsPolicyHistoryReadsClassifyControlledDatabaseFailures(
        string path,
        string failure,
        int status,
        string code)
    {
        var (subject, tenantId) = await SeedPlatformPolicyReadCallerAsync();
        var stateBeforeReads = await CapturePolicyTableStateAsync();
        Exception injected = failure switch
        {
            "unavailable" => new InvalidOperationException(
                "EF wrapped a provider connection failure",
                new NpgsqlException(
                    "provider connection failure detail",
                    new IOException("provider socket detail"))),
            _ => new ProjectConfigException(
                "native_acs_policy_corrupt",
                "Stored source failed integrity validation.",
                500),
        };
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource, opaPath: null, commandInterceptor: new PolicyReadInterceptor(_ =>
                Task.FromException(injected)));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);

        using var response = await client.GetAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        if (failure == "unavailable")
        {
            Assert.DoesNotContain("provider connection failure detail", body, StringComparison.Ordinal);
            Assert.DoesNotContain("provider socket detail", body, StringComparison.Ordinal);
        }
        Assert.Equal(stateBeforeReads, await CapturePolicyTableStateAsync());
    }

    [Theory]
    [InlineData("/api/policies/platform/")]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryReadsPropagateCancellation(string path)
    {
        var (subject, tenantId) = await SeedPlatformPolicyReadCallerAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);
        using var cancellation = new CancellationTokenSource();
        var request = client.GetAsync(path, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Theory]
    [InlineData("/api/policies/platform/")]
    [InlineData("/api/policies/platform/revisions")]
    [InlineData("/api/policies/platform/revisions/1")]
    public async Task NativeAcsPolicyHistoryReadsDoNotClassifyUnrelatedExceptionsAsOutages(string path)
    {
        var (subject, tenantId) = await SeedPlatformPolicyReadCallerAsync();
        using var host = await StartNativeAcsPolicyEndpointHostAsync(
            fixture.DataSource,
            opaPath: null,
            commandInterceptor: new PolicyReadInterceptor(_ =>
                Task.FromException(new InvalidOperationException("controlled unrelated failure"))));
        using var client = CreatePlatformPolicyReader(host, subject, tenantId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync(path));
    }

    private async Task<(string Subject, string TenantId)> SeedPlatformPolicyReadCallerAsync()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-read-failure-" + suffix;
        var tenantId = "acs-read-failure-tenant-" + suffix;
        _ = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        return (subject, tenantId);
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

    private static HttpClient CreatePlatformPolicyReader(IHost host, string subject, string tenantId)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        return client;
    }

    private static HttpClient CreateProjectPolicyReader(
        IHost host,
        string subject,
        string tenantId,
        string? projectId = null,
        string? runId = null,
        string? purpose = null)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", subject);
        client.DefaultRequestHeaders.Add("X-Test-Scope", "api.read");
        client.DefaultRequestHeaders.Add(ProjectAuthorizationOwner.TenantSelectorHeader, tenantId);
        if (projectId is not null)
            client.DefaultRequestHeaders.Add("X-Test-Project", projectId);
        if (runId is not null)
            client.DefaultRequestHeaders.Add("X-Test-Run", runId);
        if (purpose is not null)
            client.DefaultRequestHeaders.Add("X-Test-Purpose", purpose);
        return client;
    }

    private ProjectsConfigDbContext CreateDbContext(NpgsqlDataSource dataSource) =>
        new(new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options);

    private async Task<(int Heads, int Revisions, int Files, int Preparations, int Idempotency, int Audit, int Outbox,
        long? HeadRevision, string RetainedBytes)>
        CapturePolicyTableStateAsync(NpgsqlDataSource? dataSource = null)
    {
        await using var db = dataSource is null ? CreateDbContext() : CreateDbContext(dataSource);
        var revisions = await db.NativeAcsPolicyRevisions.AsNoTracking()
            .OrderBy(item => item.ScopeType).ThenBy(item => item.ScopeId)
            .ThenBy(item => item.PolicyId).ThenBy(item => item.Revision)
            .Select(item => new
            {
                item.ScopeType,
                item.ScopeId,
                item.PolicyId,
                item.Revision,
                item.ManifestBytes,
            }).ToArrayAsync();
        var files = await db.NativeAcsPolicyArtifactFiles.AsNoTracking()
            .OrderBy(item => item.ScopeType).ThenBy(item => item.ScopeId)
            .ThenBy(item => item.PolicyId).ThenBy(item => item.Revision).ThenBy(item => item.RelativePath)
            .Select(item => new
            {
                item.ScopeType,
                item.ScopeId,
                item.PolicyId,
                item.Revision,
                item.RelativePath,
                item.Bytes,
            }).ToArrayAsync();
        return (
            await db.NativeAcsPolicyHeads.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyRevisions.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyArtifactFiles.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyPreparations.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyIdempotency.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyChangeAudit.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyOutbox.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyHeads.AsNoTracking().Where(item =>
                    item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                    item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId)
                .Select(item => (long?)item.CurrentRevision)
                .SingleOrDefaultAsync(),
            JsonSerializer.Serialize(new { revisions, files }));
    }

    private async Task SetArtifactFilesImmutableTriggerEnabledAsync(bool enabled)
        => await SetImmutableTriggerEnabledAsync(
            fixture.DataSource,
            "native_acs_policy_artifact_files",
            "native_acs_policy_artifact_files_immutable",
            enabled);

    private static async Task SetImmutableTriggerEnabledAsync(
        NpgsqlDataSource dataSource,
        string tableName,
        string triggerName,
        bool enabled)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"ALTER TABLE projects_config.{tableName} {(enabled ? "ENABLE" : "DISABLE")} TRIGGER {triggerName}",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task WithArtifactFilesImmutableTriggerDisabledAsync(Func<Task> action)
    {
        try
        {
            await SetArtifactFilesImmutableTriggerEnabledAsync(false);
            await action();
        }
        finally
        {
            await SetArtifactFilesImmutableTriggerEnabledAsync(true);
        }
    }

    private async Task SetArtifactFileBytesAsync(
        string policyId,
        long revision,
        string relativePath,
        byte[] bytes)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.native_acs_policy_artifact_files
            SET bytes = @bytes
            WHERE scope_type = 'Platform' AND scope_id = 'default'
              AND policy_id = @policy_id AND revision = @revision
              AND relative_path = @relative_path
            """,
            connection);
        command.Parameters.AddWithValue("bytes", bytes);
        command.Parameters.AddWithValue("policy_id", policyId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("relative_path", relativePath);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task SetProjectArtifactFileBytesAsync(
        string projectId,
        string policyId,
        long revision,
        string relativePath,
        byte[] bytes)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            UPDATE projects_config.native_acs_policy_artifact_files
            SET bytes = @bytes
            WHERE scope_type = 'Project' AND scope_id = @scope_id
              AND policy_id = @policy_id AND revision = @revision
              AND relative_path = @relative_path
            """,
            connection);
        command.Parameters.AddWithValue("bytes", bytes);
        command.Parameters.AddWithValue("scope_id", projectId);
        command.Parameters.AddWithValue("policy_id", policyId);
        command.Parameters.AddWithValue("revision", revision);
        command.Parameters.AddWithValue("relative_path", relativePath);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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
        bool headWritesEnabled = false,
        DbCommandInterceptor? commandInterceptor = null,
        ILoggerProvider? loggerProvider = null,
        CancellationToken? requestToken = null) =>
        await new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    [NativeAcsPolicyEndpoints.HeadWritesEnabledSetting] = headWritesEnabled ? "true" : "false",
                }))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                if (loggerProvider is not null)
                    web.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthentication(ProjectSummaryTestAuthenticationHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, ProjectSummaryTestAuthenticationHandler>(
                            ProjectSummaryTestAuthenticationHandler.SchemeName, _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<IDataProtectionProvider>(CreateTestDataProtectionProvider());
                    services.AddSingleton<PlatformPolicyReadLoggerFactory>();
                    services.AddDbContextFactory<ProjectsConfigDbContext>((provider, options) =>
                    {
                        options.UseLoggerFactory(provider.GetRequiredService<PlatformPolicyReadLoggerFactory>());
                        options.UseNpgsql(
                            dataSource,
                            npgsql => npgsql.MigrationsHistoryTable(
                                "__ef_migrations_history", ProjectsConfigDbContext.Schema));
                        if (commandInterceptor is not null)
                            options.AddInterceptors(commandInterceptor);
                    });
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
                    if (requestToken is not null)
                        app.Use(async (context, next) =>
                        {
                            if (!context.Request.Headers.ContainsKey("X-Test-No-Cancel"))
                                context.RequestAborted = requestToken.Value;
                            await next(context);
                        });
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapNativeAcsPolicyEndpoints());
                });
            })
            .StartAsync();

    private sealed class PolicyScopeListQueryInterceptor : DbCommandInterceptor
    {
        private readonly ConcurrentQueue<string> _scopeListQueries = new();

        public IReadOnlyCollection<string> ScopeListQueries => _scopeListQueries.ToArray();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("native_acs_policy_heads", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("project_role_assignments", StringComparison.OrdinalIgnoreCase))
                _scopeListQueries.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }

    }

    public sealed class ProjectNativeFactAttribute : FactAttribute
    {
        public ProjectNativeFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "The pinned ACS package has a Linux-x64 native library.";
        }
    }

    public sealed class ProjectNativeTheoryAttribute : TheoryAttribute
    {
        public ProjectNativeTheoryAttribute()
        {
            if (!OperatingSystem.IsLinux())
                Skip = "The pinned ACS package has a Linux-x64 native library.";
        }
    }

    private sealed class PolicyReadInterceptor(
        Func<CancellationToken, Task> intercept,
        string targetTable = "native_acs_policy_heads",
        Action<DbDataReader>? observeResult = null,
        Func<DbCommand, Task>? observeCommand = null) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(targetTable, StringComparison.OrdinalIgnoreCase))
            {
                if (observeCommand is not null)
                    await observeCommand(command).ConfigureAwait(false);
                await intercept(cancellationToken).ConfigureAwait(false);
            }
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains(targetTable, StringComparison.OrdinalIgnoreCase))
                observeResult?.Invoke(result);
            return ValueTask.FromResult(result);
        }
    }

        private sealed class PlatformReadFailureInterceptor(string phase, string failure, string sentinel)
            : DbCommandInterceptor
        {
            private bool _headRead;
            private bool _control;
            public bool Rechecked { get; private set; }

            public void Reset()
            {
                _headRead = false;
                _control = true;
                Rechecked = false;
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
                DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                var head = command.CommandText.Contains("native_acs_policy_heads", StringComparison.OrdinalIgnoreCase);
                var authority = command.CommandText.Contains("tenant_memberships", StringComparison.OrdinalIgnoreCase);
                if (head)
                    _headRead = true;
                if (authority && _headRead)
                    Rechecked = true;
                if (phase == "policy" && head ||
                    phase == "initial-authority" && authority && !_headRead ||
                    phase == "recheck-authority" && authority && _headRead)
                {
                    throw (_control ? "provider" : failure) switch
                    {
                        "timeout" => new TimeoutException(sentinel),
                        "wrapped-timeout" => new InvalidOperationException(sentinel, new TimeoutException(sentinel)),
                        "provider-timeout" => new NpgsqlException(sentinel, new TimeoutException(sentinel)),
                        "wrapped-provider-timeout" => new InvalidOperationException(sentinel,
                            new NpgsqlException(sentinel, new TimeoutException(sentinel))),
                        _ => new InvalidOperationException(sentinel,
                            new NpgsqlException(sentinel, new IOException(sentinel))),
                    };
                }
                return ValueTask.FromResult(result);
            }
        }

        private sealed class PolicyReadLogProvider : ILoggerProvider
        {
            private readonly ConcurrentQueue<(string? EventName, string Message)> _entries = new();
            public (string? EventName, string Message)[] Entries => _entries.ToArray();
            public void Clear() => _entries.Clear();
            public ILogger CreateLogger(string categoryName) => new CaptureLogger(_entries);
            public void Dispose() { }

            private sealed class CaptureLogger(ConcurrentQueue<(string? EventName, string Message)> entries) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
                public bool IsEnabled(LogLevel logLevel) => true;
                public void Log<TState>(
                    LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                    Func<TState, Exception?, string> formatter) =>
                    entries.Enqueue((eventId.Name, formatter(state, exception) + exception));
            }
        }

    private sealed class AuthorityRecheckFailureInterceptor : DbCommandInterceptor
    {
        private bool _headRead;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("native_acs_policy_heads", StringComparison.OrdinalIgnoreCase))
                _headRead = true;
            if (_headRead && command.CommandText.Contains("project_role_assignments", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("private connection detail",
                    new NpgsqlException("private connection detail", new IOException("private connection detail")));
            return ValueTask.FromResult(result);
        }
    }
}
