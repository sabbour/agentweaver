using System.Collections.Immutable;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ProjectPolicyBaselineApprovalPinAndRevokeEndToEnd()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "baseline-tenant-" + suffix;

        // Seed PlatformAdmin to write Platform v3 head.
        var platformAdmin = await SeedCallerAsync(
            db, authorityStore, "baseline-plat-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var platformArtifact = CreateV3PlatformArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        await WritePlatformPolicyAsync(platformAdmin, platformArtifact, "plat-" + suffix, "plat-corr-" + suffix);

        // Seed Project Owner and Project.
        var owner = await SeedCallerAsync(
            db, authorityStore, "baseline-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var project = await service.CreateProjectAsync(owner, "Baseline Project " + suffix, CancellationToken.None);

        // Seed least-privilege operator with active membership ONLY (no Owner/TenantAdmin role).
        var operatorSubject = "baseline-op-" + suffix;
        var opMembership = await authorityStore.GrantMembershipAsync(
            TestIssuer, operatorSubject, tenantId, "test-seed", CancellationToken.None);
        var operatorContext = new ProjectAuthorizationContext(
            TestIssuer,
            operatorSubject,
            tenantId,
            opMembership.MembershipId,
            opMembership.Revision,
            [],
            ImmutableHashSet.Create("api.read"),
            boundProjectId: null,
            boundRunId: null,
            purpose: null,
            isVerifiedHuman: true);

        // Matching Project v3 artifact (same 4 non-Skills data files, stricter Skills threshold).
        var projectArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":700,"requireCompletedEvidence":true}""");

        var opaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH")
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_TEST_OPA_PATH");
        using var host = await StartBaselineEndpointHostAsync(fixture.DataSource, opaPath);
        using var client = host.GetTestClient();

        // Missing aw_actor_class returns 403.
        using (var nonHuman = new HttpRequestMessage(HttpMethod.Post, $"/api/policies/projects/{project.ProjectId}/baseline-approvals"))
        {
            nonHuman.Headers.Add("X-Test-Sub", owner.ActorId);
            nonHuman.Headers.Add("X-Agentweaver-Tenant", tenantId);
            nonHuman.Content = JsonContent.Create(new
            {
                artifact = projectArtifact,
                idempotencyKey = "app-nonhuman-" + suffix,
                reason = "approve",
                correlationId = "corr-1",
            });
            using var res = await client.SendAsync(nonHuman);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        Guid baselineId;
        if (!string.IsNullOrWhiteSpace(opaPath) && File.Exists(opaPath))
        {
            // Valid human approval with OPA validator returns 200 Approved.
            using (var approveReq = new HttpRequestMessage(HttpMethod.Post, $"/api/policies/projects/{project.ProjectId}/baseline-approvals"))
            {
                approveReq.Headers.Add("X-Test-Sub", owner.ActorId);
                approveReq.Headers.Add("X-Test-Actor-Class", "human");
                approveReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
                approveReq.Content = JsonContent.Create(new
                {
                    artifact = projectArtifact,
                    idempotencyKey = "app-key-" + suffix,
                    reason = "Initial project policy approval",
                    correlationId = "corr-app-" + suffix,
                });
                using var res = await client.SendAsync(approveReq);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var receipt = await res.Content.ReadFromJsonAsync<NativeAcsProjectPolicyBaselineReceipt>();
                Assert.NotNull(receipt);
                Assert.Equal(NativeAcsProjectPolicyBaselineState.Approved, receipt.State);
                Assert.Equal(projectArtifact.ContentSha256, receipt.ContentSha256);
                Assert.False(receipt.Replayed);
                baselineId = receipt.BaselineId;
            }

            // Exact replay returns Replayed = true.
            using (var replayReq = new HttpRequestMessage(HttpMethod.Post, $"/api/policies/projects/{project.ProjectId}/baseline-approvals"))
            {
                replayReq.Headers.Add("X-Test-Sub", owner.ActorId);
                replayReq.Headers.Add("X-Test-Actor-Class", "human");
                replayReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
                replayReq.Content = JsonContent.Create(new
                {
                    artifact = projectArtifact,
                    idempotencyKey = "app-key-" + suffix,
                    reason = "Initial project policy approval",
                    correlationId = "corr-app-" + suffix,
                });
                using var res = await client.SendAsync(replayReq);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                var receipt = await res.Content.ReadFromJsonAsync<NativeAcsProjectPolicyBaselineReceipt>();
                Assert.NotNull(receipt);
                Assert.True(receipt.Replayed);
                Assert.Equal(baselineId, receipt.BaselineId);
            }
        }
        else
        {
            // Without an OPA validator, HTTP approval fails closed with 503.
            using (var unavailableReq = new HttpRequestMessage(HttpMethod.Post, $"/api/policies/projects/{project.ProjectId}/baseline-approvals"))
            {
                unavailableReq.Headers.Add("X-Test-Sub", owner.ActorId);
                unavailableReq.Headers.Add("X-Test-Actor-Class", "human");
                unavailableReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
                unavailableReq.Content = JsonContent.Create(new
                {
                    artifact = projectArtifact,
                    idempotencyKey = "app-unavail-" + suffix,
                    reason = "Initial project policy approval",
                    correlationId = "corr-unavail-" + suffix,
                });
                using var res = await client.SendAsync(unavailableReq);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            }

            var storeForApproval = CreateNativeAcsStore();
            var approved = await storeForApproval.CreateBaselineApprovalAsync(
                new ProjectAuthorizationContext(
                    owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                    owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
                new NativeAcsProjectPolicyBaselineApprovalRequest(
                    project.ProjectId, projectArtifact, "app-key-" + suffix,
                    "Initial project policy approval", "corr-app-" + suffix),
                CancellationToken.None);
            baselineId = approved.BaselineId;
        }

        // Least-privilege operator pins the approved baseline via ProjectsConfigAuthority store.
        var authorityPolicyStore = new NativeAcsPolicyStore(
            CreateMigrationDbContextOptionsFactory(), TimeProvider.System, CreateTestDataProtectionProvider());
        var pinned = await authorityPolicyStore.PinBaselineByOperatorAsync(
            operatorContext,
            project.ProjectId,
            baselineId,
            projectArtifact.ContentSha256,
            "pin-key-" + suffix,
            "Record starting baseline",
            "corr-pin-" + suffix,
            CancellationToken.None);
        Assert.Equal(NativeAcsProjectPolicyBaselineState.Pinned, pinned.State);
        Assert.Equal(operatorSubject, pinned.OperatorActorId);
        Assert.False(pinned.Replayed);

        // Pin replay returns Replayed = true.
        var pinReplay = await authorityPolicyStore.PinBaselineByOperatorAsync(
            operatorContext,
            project.ProjectId,
            baselineId,
            projectArtifact.ContentSha256,
            "pin-key-" + suffix,
            "Record starting baseline",
            "corr-pin-" + suffix,
            CancellationToken.None);
        Assert.True(pinReplay.Replayed);

        // Project stays headless (no row in native_acs_policy_heads).
        var store = CreateNativeAcsStore();
        var head = await store.ReadAsync(NativeAcsPolicyScopeType.Project, project.ProjectId, CancellationToken.None);
        Assert.Null(head);

        // Revoke pinned baseline via operator CLI store method.
        var revoked = await authorityPolicyStore.RevokeBaselineByOperatorAsync(
            operatorContext,
            project.ProjectId,
            baselineId,
            projectArtifact.ContentSha256,
            "Superseded before first write",
            "corr-rev-" + suffix,
            CancellationToken.None);
        Assert.Equal(NativeAcsProjectPolicyBaselineState.Revoked, revoked.State);
        Assert.Equal(operatorSubject, revoked.RevokedByActorId);
    }

    [Fact]
    public async Task ProjectPolicyBaselineRejectsLegacyV2AndArrayReorderAndDuplicateKeys()
    {
        var actionsV3 = """{"allowed_action_ids":["a","b"]}""";
        var modelUse = """{"rules":[]}""";
        var guardrails = """{"required_checks":[],"blocked_findings":[]}""";
        string? toolPerms = null;
        var skills = """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""";

        var platformV3 = CreateV3PlatformArtifact(actionsV3, modelUse, guardrails, toolPerms, skills);
        var projectReorderedArray = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["b","a"]}""",
            modelUse, guardrails, toolPerms, skills);

        Assert.False(NativeAcsUnifiedPolicyProfile.IsProjectBaselineCompatibleWithPlatform(
            platformV3, projectReorderedArray, out var code1, out _));
        Assert.Equal("native_acs_policy_incompatible", code1);

        // Object key order normalized is allowed when arrays and values match.
        var projectReorderedKeys = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            actionsV3,
            modelUse,
            """{"blocked_findings":[],"required_checks":[]}""",
            toolPerms,
            skills);
        Assert.True(NativeAcsUnifiedPolicyProfile.IsProjectBaselineCompatibleWithPlatform(
            platformV3, projectReorderedKeys, out _, out _));

        // Legacy v2 Platform artifact (using LegacyToolPermissionsModuleBytes) must be rejected.
        var legacyToolPermsModule = typeof(NativeAcsUnifiedPolicyProfile)
            .GetField("LegacyToolPermissionsModuleBytes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null) as byte[];
        Assert.NotNull(legacyToolPermsModule);

        var platformV2 = NativeAcsUnifiedPolicyProfile.CreateArtifactV2(
            actionsV3,
            ComposedNetworkData,
            modelUse,
            guardrails);
        var legacyFiles = platformV2.Files
            .Select(f => f.RelativePath == "policy/tool-permissions.rego"
                ? new NativeAcsArtifactFile("policy/tool-permissions.rego", legacyToolPermsModule.ToImmutableArray())
                : f.RelativePath == "policy/tool_permissions/data.json"
                    ? new NativeAcsArtifactFile("policy/tool_permissions/data.json", Encoding.UTF8.GetBytes("{\"rules\":[]}").ToImmutableArray())
                    : f)
            .ToImmutableArray();
        var legacyPlatform = platformV2 with { Files = legacyFiles, ContentSha256 = string.Empty };
        legacyPlatform = legacyPlatform with { ContentSha256 = NativeAcsArtifactValidator.ComputeContentSha256(legacyPlatform) };

        Assert.False(NativeAcsUnifiedPolicyProfile.IsProjectBaselineCompatibleWithPlatform(
            legacyPlatform, projectReorderedKeys, out var code2, out _));
        Assert.Equal("native_acs_policy_incompatible", code2);

        // Non-legacy v2.1 Platform artifact is accepted when non-Skills data matches.
        Assert.True(NativeAcsUnifiedPolicyProfile.IsProjectBaselineCompatibleWithPlatform(
            platformV2, projectReorderedKeys, out _, out _));
    }

    [Fact]
    public async Task ProjectPolicyBaselineRejectsRevokedOperatorMembershipOnReplay()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "revoked-op-tenant-" + suffix;

        var platformAdmin = await SeedCallerAsync(
            db, authorityStore, "rev-plat-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var platformArtifact = CreateV3PlatformArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        await WritePlatformPolicyAsync(platformAdmin, platformArtifact, "rev-plat-" + suffix, "corr-p-" + suffix);

        var owner = await SeedCallerAsync(
            db, authorityStore, "rev-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Revoke Op Project " + suffix, CancellationToken.None);

        var opSubject = "rev-op-" + suffix;
        var opMembership = await authorityStore.GrantMembershipAsync(
            TestIssuer, opSubject, tenantId, "test-seed", CancellationToken.None);
        var opCaller = new ProjectAuthorizationContext(
            TestIssuer,
            opSubject,
            tenantId,
            opMembership.MembershipId,
            opMembership.Revision,
            [],
            ImmutableHashSet.Create("api.read"),
            null, null, null, isVerifiedHuman: true);

        var projectArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":600,"requireCompletedEvidence":true}""");

        var store = CreateNativeAcsStore();
        var approved = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                project.ProjectId, projectArtifact, "app-" + suffix, "Approve", "corr-a-" + suffix),
            CancellationToken.None);

        var authorityPolicyStore = new NativeAcsPolicyStore(
            CreateMigrationDbContextOptionsFactory(), TimeProvider.System, CreateTestDataProtectionProvider());
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, project.ProjectId, approved.BaselineId, projectArtifact.ContentSha256,
            "pin-" + suffix, "Pin", "corr-pin-" + suffix, CancellationToken.None);

        // Revoke operator's tenant membership directly.
        await authorityStore.RevokeMembershipAsync(
            opMembership.MembershipId, opMembership.Revision, "test-revoke", CancellationToken.None);

        // Replay must fail closed with 403 Forbidden.
        var ex = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            authorityPolicyStore.PinBaselineByOperatorAsync(
                opCaller, project.ProjectId, approved.BaselineId, projectArtifact.ContentSha256,
                "pin-" + suffix, "Pin", "corr-pin-" + suffix, CancellationToken.None));
        Assert.Equal(403, ex.StatusCode);
    }

    private IDbContextFactory<ProjectsConfigDbContext> CreateMigrationDbContextOptionsFactory()
    {
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(
                fixture.MigrationDataSource,
                npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        return new StaticDbContextFactory(options);
    }

    private const string ComposedNetworkData = """
        {
          "agentweaver": {
            "platform_egress": {
              "profile": "finite-network-v1",
              "mode": "Allowlist",
              "approved": [
                { "host": "approved.example.com", "port": 443 }
              ],
              "system": {
                "catalog": "hosted-copilot-v1",
                "destinations": [
                  { "host": "api.business.githubcopilot.com", "port": 443 },
                  { "host": "api.enterprise.githubcopilot.com", "port": 443 },
                  { "host": "api.github.com", "port": 443 },
                  { "host": "api.githubcopilot.com", "port": 443 },
                  { "host": "api.individual.githubcopilot.com", "port": 443 }
                ]
              }
            }
          }
        }
        """;

    private static NativeAcsArtifact CreateV3PlatformArtifact(
        string actions, string modelUse, string guardrails, string? toolPerms, string skills) =>
        NativeAcsUnifiedPolicyProfile.CreateArtifact(
            actions,
            ComposedNetworkData,
            modelUse,
            guardrails,
            toolPerms,
            skills);

    [Fact]
    public async Task ProjectPolicyBaselineRuntimeRoleEnforcesInsertAndUpdateGuards()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "runtime-guard-tenant-" + suffix;

        var owner = await SeedCallerAsync(
            db, authorityStore, "runtime-guard-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Runtime Guard Project " + suffix, CancellationToken.None);

        var projectArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":600,"requireCompletedEvidence":true}""");
        var artifactJson = JsonSerializer.Serialize(projectArtifact, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var runtimeConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
        await using var runtimeConn = await runtimeDataSource.OpenConnectionAsync();
        await using var instCmd = new NpgsqlCommand(
            "SELECT installation_id FROM projects_config.installation_identity WHERE singleton = true",
            runtimeConn);
        var installationId = (Guid)(await instCmd.ExecuteScalarAsync())!;

        // 1. Runtime role CANNOT insert state = 'Pinned' (blocked by BEFORE INSERT guard trigger).
        var insertPinnedEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO projects_config.native_acs_project_policy_baselines (
                    baseline_id, installation_id, tenant_id, project_id, policy_id, state,
                    content_sha256, artifact, profile_id,
                    approver_issuer, approver_actor_id, approver_membership_id, approver_membership_revision,
                    approver_assignment_id, approver_assignment_revision,
                    approval_idempotency_key, approval_request_digest, approval_scope_digest,
                    approval_reason, approval_correlation_id, approved_at)
                VALUES (
                    @id, @inst, @tid, @pid, 'canonical', 'Pinned',
                    @sha, @art::jsonb, 'composable-domains-v3',
                    @iss, @sub, @mid, 1, @aid, 1,
                    'k1', @sha, @sha, 'reason', 'corr', now())
                """, runtimeConn);
            cmd.Parameters.AddWithValue("id", Guid.NewGuid());
            cmd.Parameters.AddWithValue("inst", installationId);
            cmd.Parameters.AddWithValue("tid", tenantId);
            cmd.Parameters.AddWithValue("pid", project.ProjectId);
            cmd.Parameters.AddWithValue("sha", projectArtifact.ContentSha256);
            cmd.Parameters.AddWithValue("art", artifactJson);
            cmd.Parameters.AddWithValue("iss", owner.Issuer);
            cmd.Parameters.AddWithValue("sub", owner.ActorId);
            cmd.Parameters.AddWithValue("mid", owner.MembershipId);
            cmd.Parameters.AddWithValue("aid", owner.Grants[0].AssignmentId);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("55000", insertPinnedEx.SqlState);

        // 2. Runtime role CAN insert Approved, and CANNOT update pin columns (42501).
        var baselineId = Guid.NewGuid();
        await using (var insertApproved = new NpgsqlCommand("""
            INSERT INTO projects_config.native_acs_project_policy_baselines (
                baseline_id, installation_id, tenant_id, project_id, policy_id, state,
                content_sha256, artifact, profile_id,
                approver_issuer, approver_actor_id, approver_membership_id, approver_membership_revision,
                approver_assignment_id, approver_assignment_revision,
                approval_idempotency_key, approval_request_digest, approval_scope_digest,
                approval_reason, approval_correlation_id, approved_at)
            VALUES (
                @id, @inst, @tid, @pid, 'canonical', 'Approved',
                @sha, @art::jsonb, 'composable-domains-v3',
                @iss, @sub, @mid, 1, @aid, 1,
                'k2', @sha, @sha, 'reason', 'corr', now())
            """, runtimeConn))
        {
            insertApproved.Parameters.AddWithValue("id", baselineId);
            insertApproved.Parameters.AddWithValue("inst", installationId);
            insertApproved.Parameters.AddWithValue("tid", tenantId);
            insertApproved.Parameters.AddWithValue("pid", project.ProjectId);
            insertApproved.Parameters.AddWithValue("sha", projectArtifact.ContentSha256);
            insertApproved.Parameters.AddWithValue("art", artifactJson);
            insertApproved.Parameters.AddWithValue("iss", owner.Issuer);
            insertApproved.Parameters.AddWithValue("sub", owner.ActorId);
            insertApproved.Parameters.AddWithValue("mid", owner.MembershipId);
            insertApproved.Parameters.AddWithValue("aid", owner.Grants[0].AssignmentId);
            await insertApproved.ExecuteNonQueryAsync();
        }

        var updatePinEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE projects_config.native_acs_project_policy_baselines
                SET state = 'Pinned', operator_issuer = 'https://evil'
                WHERE baseline_id = @id
                """, runtimeConn);
            cmd.Parameters.AddWithValue("id", baselineId);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", updatePinEx.SqlState);
    }

    [Fact]
    public async Task ProjectPolicyBaselineHumanRevokeRouteAndReplayWorkEndToEnd()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "human-revoke-tenant-" + suffix;

        var owner = await SeedCallerAsync(
            db, authorityStore, "human-revoke-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Human Revoke Project " + suffix, CancellationToken.None);

        var projectArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":600,"requireCompletedEvidence":true}""");
        var approved = await CreateNativeAcsStore().CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                project.ProjectId, projectArtifact, "app-" + suffix, "Approve", "corr-a-" + suffix),
            CancellationToken.None);

        using var host = await StartBaselineEndpointHostAsync(fixture.DataSource);
        using var client = host.GetTestClient();
        var revokeUrl = $"/api/policies/projects/{project.ProjectId}/baseline-approvals/{approved.BaselineId}/revoke";

        using (var nonHuman = new HttpRequestMessage(HttpMethod.Post, revokeUrl))
        {
            nonHuman.Headers.Add("X-Test-Sub", owner.ActorId);
            nonHuman.Headers.Add("X-Agentweaver-Tenant", tenantId);
            nonHuman.Content = JsonContent.Create(new { reason = "Revoke", correlationId = "corr-r-" + suffix });
            using var res = await client.SendAsync(nonHuman);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        using (var revokeReq = new HttpRequestMessage(HttpMethod.Post, revokeUrl))
        {
            revokeReq.Headers.Add("X-Test-Sub", owner.ActorId);
            revokeReq.Headers.Add("X-Test-Actor-Class", "human");
            revokeReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            revokeReq.Content = JsonContent.Create(new { reason = "Revoke", correlationId = "corr-r-" + suffix });
            using var res = await client.SendAsync(revokeReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(res.Headers.CacheControl?.NoStore);
            var receipt = await res.Content.ReadFromJsonAsync<NativeAcsProjectPolicyBaselineReceipt>();
            Assert.NotNull(receipt);
            Assert.Equal(NativeAcsProjectPolicyBaselineState.Revoked, receipt.State);
            Assert.False(receipt.Replayed);
        }

        using (var replayReq = new HttpRequestMessage(HttpMethod.Post, revokeUrl))
        {
            replayReq.Headers.Add("X-Test-Sub", owner.ActorId);
            replayReq.Headers.Add("X-Test-Actor-Class", "human");
            replayReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            replayReq.Content = JsonContent.Create(new { reason = "Revoke", correlationId = "corr-r-" + suffix });
            using var res = await client.SendAsync(replayReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var receipt = await res.Content.ReadFromJsonAsync<NativeAcsProjectPolicyBaselineReceipt>();
            Assert.NotNull(receipt);
            Assert.True(receipt.Replayed);
        }
    }

    [Fact]
    public async Task ProjectPolicyBaselineCommandPinsEndToEndWithSignedJwtAndRejectsRuntimeConnection()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "cmd-e2e-tenant-" + suffix;

        var platformAdmin = await SeedCallerAsync(
            db, authorityStore, "cmd-plat-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var platformArtifact = CreateV3PlatformArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        await WritePlatformPolicyAsync(platformAdmin, platformArtifact, "cmd-plat-" + suffix, "corr-cmd-p-" + suffix);

        var owner = await SeedCallerAsync(
            db, authorityStore, "cmd-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Command E2E Project " + suffix, CancellationToken.None);

        var opSubject = "cmd-op-" + suffix;
        _ = await authorityStore.GrantMembershipAsync(
            TestIssuer, opSubject, tenantId, "test-seed", CancellationToken.None);

        var projectArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":650,"requireCompletedEvidence":true}""");
        var approved = await CreateNativeAcsStore().CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                project.ProjectId, projectArtifact, "cmd-app-" + suffix, "Approve", "corr-cmd-a-" + suffix),
            CancellationToken.None);

        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "cmd-test-key" };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256);
        var now = DateTime.UtcNow;
        var tokenHandler = new JwtSecurityTokenHandler();
        var jwt = tokenHandler.WriteToken(new JwtSecurityToken(
            issuer: TestIssuer,
            audience: "projects-config",
            claims:
            [
                new Claim("sub", opSubject),
                new Claim("aw_actor_class", "human"),
                new Claim("scp", "api.read"),
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddMinutes(5),
            signingCredentials: credentials));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:Authentication:Authority"] = TestIssuer,
                ["ProjectsConfig:Authentication:Audience"] = "projects-config",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedIssuer"] = TestIssuer,
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedSubject"] = opSubject,
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedTenantId"] = tenantId,
            })
            .Build();
        var provisioning = ProjectPolicyProvisioningApproval.Read(config);

        var stdinJson = JsonSerializer.Serialize(new
        {
            accessToken = jwt,
            operation = "pin",
            tenantId,
            projectId = project.ProjectId,
            baselineId = approved.BaselineId,
            expectedContentSha256 = projectArtifact.ContentSha256,
            idempotencyKey = "cmd-pin-" + suffix,
            reason = "Operator E2E pin",
            correlationId = "corr-cmd-pin-" + suffix,
        });

        void ConfigureLocalJwt(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions options)
        {
            options.Authority = null;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = TestIssuer,
                ValidateAudience = true,
                ValidAudience = "projects-config",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = signingKey,
                ValidateLifetime = true,
            };
        }

        // 1. Restricted runtime connection fails (cannot update pin columns).
        var runtimeConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
        await using (var runtimeStdin = new MemoryStream(Encoding.UTF8.GetBytes(stdinJson)))
        {
            await Assert.ThrowsAnyAsync<Exception>(() =>
                ProjectPolicyBaselineCommand.RunCoreAsync(
                    config, provisioning, runtimeStdin, runtimeDataSource, ConfigureLocalJwt, CancellationToken.None));
        }

        // 2. Pin-capable authority connection succeeds and persists Pinned row.
        await using var authorityStdin = new MemoryStream(Encoding.UTF8.GetBytes(stdinJson));
        var receipt = await ProjectPolicyBaselineCommand.RunCoreAsync(
            config, provisioning, authorityStdin, fixture.MigrationDataSource, ConfigureLocalJwt, CancellationToken.None);
        Assert.Equal(NativeAcsProjectPolicyBaselineState.Pinned, receipt.State);
        Assert.Equal(opSubject, receipt.OperatorActorId);
        Assert.Equal(approved.BaselineId, receipt.BaselineId);
        Assert.False(receipt.Replayed);

        await using var verifyDb = CreateDbContext();
        var row = await verifyDb.NativeAcsProjectPolicyBaselines.AsNoTracking()
            .SingleAsync(item => item.BaselineId == approved.BaselineId);
        Assert.Equal(NativeAcsProjectPolicyBaselineState.Pinned, row.State);
        Assert.Equal(TestIssuer, row.OperatorIssuer);
        Assert.Equal(opSubject, row.OperatorActorId);
        Assert.Equal(platformArtifact.ContentSha256, row.PlatformContentSha256);

        // 3. Direct RunAsync with only ConnectionStrings:ProjectsConfig (and no ProjectsConfigAuthority) fails closed.
        var runtimeOnlyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:Authentication:Authority"] = TestIssuer,
                ["ProjectsConfig:Authentication:Audience"] = "projects-config",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedIssuer"] = TestIssuer,
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedSubject"] = opSubject,
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedTenantId"] = tenantId,
                ["ConnectionStrings:ProjectsConfig"] = fixture.ConnectionString,
            })
            .Build();
        var missingAuthorityEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectPolicyBaselineCommand.RunAsync(runtimeOnlyConfig, CancellationToken.None));
        Assert.Contains("ConnectionStrings:ProjectsConfigAuthority", missingAuthorityEx.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectPolicyBaselineRejectsV2AndDuplicateKeysAndValidatesOperatorConfig()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "v2-reject-tenant-" + suffix;

        var owner = await SeedCallerAsync(
            db, authorityStore, "v2-reject-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "V2 Reject Project " + suffix, CancellationToken.None);

        var projectV2 = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""");
        var store = CreateNativeAcsStore();
        var ex = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.CreateBaselineApprovalAsync(
                new ProjectAuthorizationContext(
                    owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                    owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
                new NativeAcsProjectPolicyBaselineApprovalRequest(
                    project.ProjectId, projectV2, "v2-" + suffix, "Approve v2", "corr-v2-" + suffix),
                CancellationToken.None));
        Assert.Equal("native_acs_policy_invalid", ex.Code);

        // Duplicate keys (including case-insensitive) in non-Skills data must be rejected.
        var platformV3 = CreateV3PlatformArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        var projectDup = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":[]}""",
            """{"rules":[],"Rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        Assert.False(NativeAcsUnifiedPolicyProfile.IsProjectBaselineCompatibleWithPlatform(
            platformV3, projectDup, out var dupCode, out _));
        Assert.Equal("native_acs_policy_invalid", dupCode);

        // Operator config validation.
        var validConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:Authentication:Authority"] = "https://id.example.test/",
                ["ProjectsConfig:Authentication:Audience"] = "projects-config",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedIssuer"] = "https://id.example.test/",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedSubject"] = "op-1",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedTenantId"] = "tenant-1",
            })
            .Build();
        var approval = ProjectPolicyProvisioningApproval.Read(validConfig);
        Assert.Equal("op-1", approval.ApprovedSubject);
        Assert.Equal("tenant-1", approval.ApprovedTenantId);

        var mismatchedIssuer = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ProjectsConfig:Authentication:Authority"] = "https://id.example.test/",
                ["ProjectsConfig:Authentication:Audience"] = "projects-config",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedIssuer"] = "https://other.example.test/",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedSubject"] = "op-1",
                ["ProjectsConfig:ProjectPolicyProvisioning:ApprovedTenantId"] = "tenant-1",
            })
            .Build();
        Assert.Throws<InvalidOperationException>(() => ProjectPolicyProvisioningApproval.Read(mismatchedIssuer));

        // Stdin parser rejects duplicate/ambiguous keys and unmapped fields.
        var dupJson = """
            {
              "accessToken": "tok",
              "operation": "pin",
              "tenantId": "t1",
              "projectId": "p1",
              "baselineId": "11111111-1111-1111-1111-111111111111",
              "expectedContentSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "idempotencyKey": "k1",
              "reason": "r1",
              "Reason": "r2",
              "correlationId": "c1"
            }
            """;
        await using var dupStream = new MemoryStream(Encoding.UTF8.GetBytes(dupJson));
        await Assert.ThrowsAsync<ProjectConfigException>(() =>
            ProjectPolicyBaselineCommand.ReadInputAsync(dupStream, CancellationToken.None));

        var validJson = """
            {
              "accessToken": "tok",
              "operation": "pin",
              "tenantId": "t1",
              "projectId": "p1",
              "baselineId": "11111111-1111-1111-1111-111111111111",
              "expectedContentSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "idempotencyKey": "k1",
              "reason": "r1",
              "correlationId": "c1"
            }
            """;
        await using var validStream = new MemoryStream(Encoding.UTF8.GetBytes(validJson));
        var parsed = await ProjectPolicyBaselineCommand.ReadInputAsync(validStream, CancellationToken.None);
        Assert.Equal(ProjectPolicyBaselineOperation.Pin, parsed.Operation);
        Assert.Equal("t1", parsed.TenantId);
    }

    private static async Task<IHost> StartBaselineEndpointHostAsync(NpgsqlDataSource dataSource, string? opaPath = null) =>
        await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddMemoryCache();
                    services.AddAuthentication("BaselineTestAuth")
                        .AddScheme<AuthenticationSchemeOptions, BaselineTestAuthHandler>("BaselineTestAuth", _ => { });
                    services.AddAuthorization();
                    services.AddSingleton<IDataProtectionProvider>(CreateTestDataProtectionProvider());
                    services.AddDbContextFactory<ProjectsConfigDbContext>((_, options) =>
                    {
                        options.UseNpgsql(
                            dataSource,
                            npgsql => npgsql.MigrationsHistoryTable(
                                "__ef_migrations_history", ProjectsConfigDbContext.Schema));
                    });
                    services.AddScoped<ProjectsConfigService>();
                    services.AddSingleton(CreateProviderCatalog());
                    services.AddScoped<ProjectAuthorizationOwner>();
                    services.AddSingleton(new ProjectsConfigIdentityOptions(TestIssuer));
                    if (!string.IsNullOrWhiteSpace(opaPath) && File.Exists(opaPath))
                        services.AddSingleton(new NativeAcsArtifactValidator(opaPath));
                    services.AddSingleton<NativeAcsPolicyRequestGate>();
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<NativeAcsPolicyStore>();
                    services.AddSingleton<ProjectGovernanceDecisionStore>();
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

    private sealed class StaticDbContextFactory(DbContextOptions<ProjectsConfigDbContext> options)
        : IDbContextFactory<ProjectsConfigDbContext>
    {
        public ProjectsConfigDbContext CreateDbContext() => new(options);
    }

    private sealed class BaselineTestAuthHandler(
        Microsoft.Extensions.Options.IOptionsMonitor<AuthenticationSchemeOptions> options,
        Microsoft.Extensions.Logging.ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-Test-Sub", out var sub))
                return Task.FromResult(AuthenticateResult.Fail("Missing X-Test-Sub"));
            var claims = new List<Claim>
            {
                new("iss", TestIssuer),
                new("sub", sub.ToString()),
                new("scp", "api.read"),
                new("scp", "projects.admin"),
            };
            if (Request.Headers.TryGetValue("X-Test-Actor-Class", out var actorClass))
                claims.Add(new Claim("aw_actor_class", actorClass.ToString()));
            var identity = new ClaimsIdentity(claims, "BaselineTestAuth");
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), "BaselineTestAuth")));
        }
    }
}
