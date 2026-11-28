using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
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
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task FirstProjectPolicyWriteSwitchOffTransportStrictJsonAuthAndEndToEnd()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "first-write-tenant-" + suffix;

        var platformAdmin = await SeedCallerAsync(
            db, authorityStore, "fw-plat-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var platformArtifact = CreateV3PlatformArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        await WritePlatformPolicyAsync(platformAdmin, platformArtifact, "fw-plat-" + suffix, "corr-fw-p-" + suffix);

        var owner = await SeedCallerAsync(
            db, authorityStore, "fw-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var project = await service.CreateProjectAsync(owner, "First Write Project " + suffix, CancellationToken.None);
        var projectOnlyOwner = await SeedCallerAsync(
            db, authorityStore, "fw-proj-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Project,
            project.ProjectId, ProjectAuthorityRole.Owner);
        var foreignProject = await service.CreateProjectAsync(owner, "Foreign Project " + suffix, CancellationToken.None);
        var otherTenantId = "other-tenant-" + suffix;
        var otherOwner = await SeedCallerAsync(
            db, authorityStore, "fw-other-owner-" + suffix, otherTenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            otherTenantId, ProjectAuthorityRole.TenantAdmin);
        var crossTenantProject = await service.CreateProjectAsync(otherOwner, "Cross Tenant " + suffix, CancellationToken.None);
        var archivedProject = await service.CreateProjectAsync(owner, "Archived Project " + suffix, CancellationToken.None);
        var archivedRow = await db.Projects.SingleAsync(p => p.ProjectId == archivedProject.ProjectId);
        archivedRow.State = ProjectLifecycleState.Archived;
        await db.SaveChangesAsync();

        var opSubject = "fw-op-" + suffix;
        var opMembership = await authorityStore.GrantMembershipAsync(
            TestIssuer, opSubject, tenantId, "test-seed", CancellationToken.None);
        var opCaller = new ProjectAuthorizationContext(
            TestIssuer, opSubject, tenantId, opMembership.MembershipId, opMembership.Revision,
            [], ImmutableHashSet.Create("api.read"), null, null, null, isVerifiedHuman: true);

        var baselineArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":600,"requireCompletedEvidence":true}""");
        var approved = await CreateNativeAcsStore().CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                project.ProjectId, baselineArtifact, "fw-app-" + suffix, "Approve baseline", "corr-fw-a-" + suffix),
            CancellationToken.None);

        var authorityPolicyStore = new NativeAcsPolicyStore(
            CreateMigrationDbContextOptionsFactory(), TimeProvider.System, CreateTestDataProtectionProvider());
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, project.ProjectId, approved.BaselineId, baselineArtifact.ContentSha256,
            "fw-pin-" + suffix, "Pin baseline", "corr-fw-pin-" + suffix, CancellationToken.None);

        var candidateSkillsOnly = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":800,"requireCompletedEvidence":true}""");

        var opaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH")
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_TEST_OPA_PATH");

        // 1. Default switch OFF -> 503 native_acs_policy_writes_unavailable.
        using (var disabledHost = await StartFirstWriteEndpointHostAsync(fixture.DataSource, projectHeadWritesEnabled: false, opaPath: opaPath))
        using (var disabledClient = disabledHost.GetTestClient())
        {
            using var req = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
            {
                expectedRevision = (long?)null,
                artifact = candidateSkillsOnly,
                idempotencyKey = "fw-off-" + suffix,
                reason = "Attempt while disabled",
                correlationId = "corr-off-" + suffix,
            });
            using var res = await disabledClient.SendAsync(req);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.True(res.Headers.CacheControl?.NoStore);
            var body = await res.Content.ReadAsStringAsync();
            Assert.Contains("native_acs_policy_writes_unavailable", body);
        }

        // 2. Switch ON -> test transport, strict JSON, auth, delta, commit, replay, and second write.
        using var enabledHost = await StartFirstWriteEndpointHostAsync(fixture.DataSource, projectHeadWritesEnabled: true, opaPath: opaPath);
        using var client = enabledHost.GetTestClient();

        // Query string rejected with 400.
        using (var qReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}?scope=platform", owner.ActorId, tenantId, new
        {
            expectedRevision = (long?)null,
            artifact = candidateSkillsOnly,
            idempotencyKey = "fw-q-" + suffix,
            reason = "Query",
            correlationId = "corr-q-" + suffix,
        }))
        {
            using var res = await client.SendAsync(qReq);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // Non-JSON content type -> 415.
        using (var ctReq = new HttpRequestMessage(HttpMethod.Put, $"/api/policies/projects/{project.ProjectId}"))
        {
            ctReq.Headers.Add("X-Test-Sub", owner.ActorId);
            ctReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            ctReq.Content = new StringContent("{}", Encoding.UTF8, "text/plain");
            using var res = await client.SendAsync(ctReq);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, res.StatusCode);
        }

        // Missing expectedRevision -> 400.
        using (var missingExpReq = new HttpRequestMessage(HttpMethod.Put, $"/api/policies/projects/{project.ProjectId}"))
        {
            missingExpReq.Headers.Add("X-Test-Sub", owner.ActorId);
            missingExpReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            missingExpReq.Content = new StringContent(JsonSerializer.Serialize(new
            {
                artifact = candidateSkillsOnly,
                idempotencyKey = "fw-noexp-" + suffix,
                reason = "Missing exp",
                correlationId = "corr-noexp-" + suffix,
            }), Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(missingExpReq);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // Unknown / duplicate case-colliding property -> 400.
        using (var dupReq = new HttpRequestMessage(HttpMethod.Put, $"/api/policies/projects/{project.ProjectId}"))
        {
            dupReq.Headers.Add("X-Test-Sub", owner.ActorId);
            dupReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            dupReq.Content = new StringContent(
                $$"""{"expectedRevision":null,"artifact":{{JsonSerializer.Serialize(candidateSkillsOnly)}},"idempotencyKey":"k","reason":"r","Reason":"r2","correlationId":"c"}""",
                Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(dupReq);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // Contributor -> 403; non-TenantAdmin Project Owner on foreign/unknown/archived project -> 404 (not 403).
        var contributor = await SeedCallerAsync(
            db, authorityStore, "fw-contrib-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Project,
            project.ProjectId, ProjectAuthorityRole.Contributor);
        using (var contribReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", contributor.ActorId, tenantId, new
        {
            expectedRevision = (long?)null,
            artifact = candidateSkillsOnly,
            idempotencyKey = "fw-contrib-" + suffix,
            reason = "Contrib",
            correlationId = "corr-contrib-" + suffix,
        }))
        {
            using var res = await client.SendAsync(contribReq);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        }

        foreach (var targetProjectId in new[] { Guid.NewGuid().ToString("N"), foreignProject.ProjectId, crossTenantProject.ProjectId })
        {
            using var notFoundReq = new HttpRequestMessage(HttpMethod.Put, $"/api/policies/projects/{targetProjectId}");
            notFoundReq.Headers.Add("X-Test-Sub", projectOnlyOwner.ActorId);
            notFoundReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            notFoundReq.Content = new StringContent("not-json-body", Encoding.UTF8, "text/plain");
            using var res = await client.SendAsync(notFoundReq);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        // Malformed application/json as authorized TenantAdmin: active Project -> 400 (parsed), archived Project -> 404 (before parse).
        using (var activeMalformedReq = new HttpRequestMessage(HttpMethod.Put, $"/api/policies/projects/{project.ProjectId}"))
        {
            activeMalformedReq.Headers.Add("X-Test-Sub", owner.ActorId);
            activeMalformedReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            activeMalformedReq.Content = new StringContent("{invalid-json", Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(activeMalformedReq);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        // Archived target using same authorized TenantAdmin with malformed application/json -> 404 before body parsing.
        using (var archivedReq = new HttpRequestMessage(HttpMethod.Put, $"/api/policies/projects/{archivedProject.ProjectId}"))
        {
            archivedReq.Headers.Add("X-Test-Sub", owner.ActorId);
            archivedReq.Headers.Add("X-Agentweaver-Tenant", tenantId);
            archivedReq.Content = new StringContent("{invalid-json", Encoding.UTF8, "application/json");
            using var res = await client.SendAsync(archivedReq);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        // Bad ContentSha256 -> 422.
        var badHashArtifact = candidateSkillsOnly with { ContentSha256 = new string('0', 64) };
        using (var badHashReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
        {
            expectedRevision = (long?)null,
            artifact = badHashArtifact,
            idempotencyKey = "fw-badhash-" + suffix,
            reason = "Bad hash",
            correlationId = "corr-bh-" + suffix,
        }))
        {
            using var res = await client.SendAsync(badHashReq);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(opaPath) || !File.Exists(opaPath))
            return;

        // Non-Skills delta (changed actions data) -> 422 without leaking baseline/platform bytes.
        var nonSkillsDelta = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.write"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":800,"requireCompletedEvidence":true}""");
        using (var deltaReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
        {
            expectedRevision = (long?)null,
            artifact = nonSkillsDelta,
            idempotencyKey = "fw-delta-" + suffix,
            reason = "Non-skills delta",
            correlationId = "corr-delta-" + suffix,
        }))
        {
            using var res = await client.SendAsync(deltaReq);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
            var deltaBody = await res.Content.ReadAsStringAsync();
            Assert.Contains("native_acs_policy_invalid", deltaBody);
            Assert.DoesNotContain("tool.read", deltaBody);
        }

        // Record fence state before commit.
        var fenceBefore = await db.SkillAdmissionFences.AsNoTracking().SingleAsync(f => f.Id == 1);

        // Valid first write (without aw_actor_class — PUT does not require human actor).
        var writeKey = "fw-ok-" + suffix;
        NativeAcsPolicyWriteReceipt firstReceipt;
        using (var okReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
        {
            expectedRevision = (long?)null,
            artifact = candidateSkillsOnly,
            idempotencyKey = writeKey,
            reason = "Initial Project Skills policy",
            correlationId = "corr-ok-" + suffix,
        }))
        {
            using var res = await client.SendAsync(okReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(res.Headers.CacheControl?.NoStore);
            firstReceipt = (await res.Content.ReadFromJsonAsync<NativeAcsPolicyWriteReceipt>())!;
            Assert.Equal(NativeAcsPolicyScopeType.Project, firstReceipt.ScopeType);
            Assert.Equal(project.ProjectId, firstReceipt.ScopeId);
            Assert.Equal("canonical", firstReceipt.PolicyId);
            Assert.Equal(1, firstReceipt.Revision);
            Assert.Equal(candidateSkillsOnly.ContentSha256, firstReceipt.ContentSha256);
            Assert.False(firstReceipt.Replayed);
        }

        // Verify head=1, revision=1, baseline=Consumed(consumed_revision=1), and fence Generation incremented once.
        await using (var verifyDb = CreateDbContext())
        {
            var head = await verifyDb.NativeAcsPolicyHeads.AsNoTracking()
                .SingleAsync(h => h.ScopeType == NativeAcsPolicyScopeType.Project && h.ScopeId == project.ProjectId);
            Assert.Equal(1, head.CurrentRevision);
            Assert.Equal("canonical", head.PolicyId);

            var baselineRow = await verifyDb.NativeAcsProjectPolicyBaselines.AsNoTracking()
                .SingleAsync(b => b.BaselineId == approved.BaselineId);
            Assert.Equal(NativeAcsProjectPolicyBaselineState.Consumed, baselineRow.State);
            Assert.Equal(1, baselineRow.ConsumedRevision);
            Assert.NotNull(baselineRow.ConsumedAt);

            var fenceAfter = await verifyDb.SkillAdmissionFences.AsNoTracking().SingleAsync(f => f.Id == 1);
            Assert.Equal(fenceBefore.Generation + 1, fenceAfter.Generation);
            Assert.Equal(fenceBefore.CutoverGeneration, fenceAfter.CutoverGeneration);
            Assert.Equal(fenceBefore.State, fenceAfter.State);
            Assert.Equal(fenceBefore.PlatformSkillsPolicySha256, fenceAfter.PlatformSkillsPolicySha256);
        }

        // Exact replay succeeds EVEN when switch is OFF and OPA validator is unavailable!
        using (var replayHost = await StartFirstWriteEndpointHostAsync(fixture.DataSource, projectHeadWritesEnabled: false, opaPath: null))
        using (var replayClient = replayHost.GetTestClient())
        {
            using var replayReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
            {
                expectedRevision = (long?)null,
                artifact = candidateSkillsOnly,
                idempotencyKey = writeKey,
                reason = "Initial Project Skills policy",
                correlationId = "corr-ok-" + suffix,
            });
            using var res = await replayClient.SendAsync(replayReq);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var replayReceipt = (await res.Content.ReadFromJsonAsync<NativeAcsPolicyWriteReceipt>())!;
            Assert.True(replayReceipt.Replayed);
            Assert.Equal(firstReceipt.AuditId, replayReceipt.AuditId);
            Assert.Equal(firstReceipt.OutboxId, replayReceipt.OutboxId);
        }

        // Changed valid input on same key -> 409 idempotency_conflict.
        using (var conflictReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
        {
            expectedRevision = (long?)null,
            artifact = candidateSkillsOnly,
            idempotencyKey = writeKey,
            reason = "Changed reason",
            correlationId = "corr-ok-" + suffix,
        }))
        {
            using var res = await client.SendAsync(conflictReq);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }

        // Second write (fresh key, expectedRevision: 1 or null) -> 409 Conflict (first-write-only).
        using (var secondReq = CreatePutRequest($"/api/policies/projects/{project.ProjectId}", owner.ActorId, tenantId, new
        {
            expectedRevision = 1L,
            artifact = candidateSkillsOnly,
            idempotencyKey = "fw-second-" + suffix,
            reason = "Second write",
            correlationId = "corr-second-" + suffix,
        }))
        {
            using var res = await client.SendAsync(secondReq);
            Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        }
    }

    [Fact]
    public async Task FirstProjectPolicyWriteConcurrencyDriftRevokeAndRuntimeConsumeGuard()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "fw-conc-tenant-" + suffix;

        var platformAdmin = await SeedCallerAsync(
            db, authorityStore, "fwc-plat-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var platformArtifact = CreateV3PlatformArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");
        await WritePlatformPolicyAsync(platformAdmin, platformArtifact, "fwc-plat-" + suffix, "corr-fwc-p-" + suffix);

        var owner = await SeedCallerAsync(
            db, authorityStore, "fwc-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var projectA = await service.CreateProjectAsync(owner, "Conc A " + suffix, CancellationToken.None);
        var projectB = await service.CreateProjectAsync(owner, "Conc B " + suffix, CancellationToken.None);

        var opSubject = "fwc-op-" + suffix;
        var opMembership = await authorityStore.GrantMembershipAsync(
            TestIssuer, opSubject, tenantId, "test-seed", CancellationToken.None);
        var opCaller = new ProjectAuthorizationContext(
            TestIssuer, opSubject, tenantId, opMembership.MembershipId, opMembership.Revision,
            [], ImmutableHashSet.Create("api.read"), null, null, null, isVerifiedHuman: true);

        var baselineArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":600,"requireCompletedEvidence":true}""");
        var candidateArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":750,"requireCompletedEvidence":true}""");

        var store = CreateNativeAcsStore();
        var authorityPolicyStore = new NativeAcsPolicyStore(
            CreateMigrationDbContextOptionsFactory(), TimeProvider.System, CreateTestDataProtectionProvider());

        // Approve & pin Project A and Project B.
        var appA = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectA.ProjectId, baselineArtifact, "fwc-appA-" + suffix, "Approve A", "corr-a-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectA.ProjectId, appA.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinA-" + suffix, "Pin A", "corr-pa-" + suffix, CancellationToken.None);

        var appB = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectB.ProjectId, baselineArtifact, "fwc-appB-" + suffix, "Approve B", "corr-b-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectB.ProjectId, appB.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinB-" + suffix, "Pin B", "corr-pb-" + suffix, CancellationToken.None);

        // 1. Runtime role CANNOT consume a Pinned baseline without a matching head=1 & revision=1 (trigger 55000),
        // and CANNOT set consumed_revision = NULL even when a matching head=1 & revision=1 exists.
        var runtimeConnectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var runtimeDataSource = NpgsqlDataSource.Create(runtimeConnectionString);
        await using (var runtimeConn = await runtimeDataSource.OpenConnectionAsync())
        {
            var directConsumeEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = new NpgsqlCommand("""
                    UPDATE projects_config.native_acs_project_policy_baselines
                    SET state = 'Consumed', consumed_revision = 1, consumed_at = now()
                    WHERE baseline_id = @id
                    """, runtimeConn);
                cmd.Parameters.AddWithValue("id", appA.BaselineId);
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Equal("55000", directConsumeEx.SqlState);
        }

        // 2. Competing first writes with explicit database fence barrier: 1 wins (200) and 1 loses (409).
        var store1 = CreateNativeAcsStore();
        var store2 = CreateNativeAcsStore();
        var req1 = new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Project, projectA.ProjectId, "canonical", null,
            candidateArtifact, "conc-k1-" + suffix, "Write 1", "corr-1-" + suffix);
        var req2 = new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Project, projectA.ProjectId, "canonical", null,
            candidateArtifact, "conc-k2-" + suffix, "Write 2", "corr-2-" + suffix);

        (NativeAcsPolicyWriteReceipt? Receipt, ProjectConfigException? Error)[] results;
        await using (var barrierDb = CreateDbContext())
        await using (var barrierTx = await barrierDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            _ = await new SkillAdmissionService(barrierDb, TimeProvider.System).LockFenceAsync(CancellationToken.None);
            var blockerPid = await ReadBackendPidAsync(barrierDb);
            var t1 = Task.Run(async () =>
            {
                try { return (Receipt: (NativeAcsPolicyWriteReceipt?)await store1.WriteFirstProjectPolicyAsync(owner, req1, () => true, CancellationToken.None), Error: (ProjectConfigException?)null); }
                catch (ProjectConfigException ex) { return (Receipt: (NativeAcsPolicyWriteReceipt?)null, Error: (ProjectConfigException?)ex); }
            });
            var t2 = Task.Run(async () =>
            {
                try { return (Receipt: (NativeAcsPolicyWriteReceipt?)await store2.WriteFirstProjectPolicyAsync(owner, req2, () => true, CancellationToken.None), Error: (ProjectConfigException?)null); }
                catch (ProjectConfigException ex) { return (Receipt: (NativeAcsPolicyWriteReceipt?)null, Error: (ProjectConfigException?)ex); }
            });
            await WaitForBlockedFenceWaitersAsync(fixture.DataSource, blockerPid, 2);
            Assert.False(t1.IsCompleted);
            Assert.False(t2.IsCompleted);
            await barrierTx.CommitAsync();
            results = await Task.WhenAll(t1, t2);
        }
        Assert.Single(results, r => r.Receipt is not null && r.Receipt.Revision == 1);
        var loser = Assert.Single(results, r => r.Error is not null);
        Assert.Equal(409, loser.Error!.StatusCode);

        // Consumed baseline on Project A cannot be revoked.
        var revokeConsumedEx = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            authorityPolicyStore.RevokeBaselineByOperatorAsync(
                opCaller, projectA.ProjectId, appA.BaselineId, baselineArtifact.ContentSha256,
                "Late revoke", "corr-lr-" + suffix, CancellationToken.None));
        Assert.Equal(409, revokeConsumedEx.StatusCode);

        // 2a. Write-vs-Revoke race with explicit PostgreSQL lock-queue observation in BOTH winner orders.
        var projectRaceWriteFirst = await service.CreateProjectAsync(owner, "Race Write First " + suffix, CancellationToken.None);
        var appRaceWriteFirst = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectRaceWriteFirst.ProjectId, baselineArtifact, "fwc-appR1-" + suffix, "Approve R1", "corr-r1-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectRaceWriteFirst.ProjectId, appRaceWriteFirst.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinR1-" + suffix, "Pin R1", "corr-pr1-" + suffix, CancellationToken.None);
        await using (var raceBarrierDb = CreateDbContext())
        await using (var raceBarrierTx = await raceBarrierDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            _ = await new SkillAdmissionService(raceBarrierDb, TimeProvider.System).LockFenceAsync(CancellationToken.None);
            var blockerPid = await ReadBackendPidAsync(raceBarrierDb);
            var tWrite = CreateNativeAcsStore().WriteFirstProjectPolicyAsync(
                owner,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project, projectRaceWriteFirst.ProjectId, "canonical", null,
                    candidateArtifact, "fwc-race-w1-" + suffix, "Race write 1", "corr-rw1-" + suffix),
                () => true, CancellationToken.None);
            await WaitForBlockedFenceWaitersAsync(fixture.DataSource, blockerPid, 1);
            var tRevoke = CreateNativeAcsStore().RevokeBaselineByAuthorAsync(
                new ProjectAuthorizationContext(
                    owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                    owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
                new NativeAcsProjectPolicyBaselineRevokeRequest(
                    projectRaceWriteFirst.ProjectId, appRaceWriteFirst.BaselineId, "Race revoke 1", "corr-rr1-" + suffix),
                CancellationToken.None);
            await WaitForBlockedFenceWaitersAsync(fixture.DataSource, blockerPid, 2);
            await raceBarrierTx.CommitAsync();
            var receipt = await tWrite;
            Assert.Equal(1, receipt.Revision);
            var revEx = await Assert.ThrowsAsync<ProjectConfigException>(() => tRevoke);
            Assert.Equal(409, revEx.StatusCode);
        }

        var projectRaceRevokeFirst = await service.CreateProjectAsync(owner, "Race Revoke First " + suffix, CancellationToken.None);
        var appRaceRevokeFirst = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectRaceRevokeFirst.ProjectId, baselineArtifact, "fwc-appR2-" + suffix, "Approve R2", "corr-r2-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectRaceRevokeFirst.ProjectId, appRaceRevokeFirst.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinR2-" + suffix, "Pin R2", "corr-pr2-" + suffix, CancellationToken.None);
        await using (var raceBarrierDb2 = CreateDbContext())
        await using (var raceBarrierTx2 = await raceBarrierDb2.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            _ = await new SkillAdmissionService(raceBarrierDb2, TimeProvider.System).LockFenceAsync(CancellationToken.None);
            var blockerPid = await ReadBackendPidAsync(raceBarrierDb2);
            var tRevoke = CreateNativeAcsStore().RevokeBaselineByAuthorAsync(
                new ProjectAuthorizationContext(
                    owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                    owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
                new NativeAcsProjectPolicyBaselineRevokeRequest(
                    projectRaceRevokeFirst.ProjectId, appRaceRevokeFirst.BaselineId, "Race revoke 2", "corr-rr2-" + suffix),
                CancellationToken.None);
            await WaitForBlockedFenceWaitersAsync(fixture.DataSource, blockerPid, 1);
            var tWrite = CreateNativeAcsStore().WriteFirstProjectPolicyAsync(
                owner,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project, projectRaceRevokeFirst.ProjectId, "canonical", null,
                    candidateArtifact, "fwc-race-w2-" + suffix, "Race write 2", "corr-rw2-" + suffix),
                () => true, CancellationToken.None);
            await WaitForBlockedFenceWaitersAsync(fixture.DataSource, blockerPid, 2);
            await raceBarrierTx2.CommitAsync();
            var revokeReceipt = await tRevoke;
            Assert.Equal(NativeAcsProjectPolicyBaselineState.Revoked, revokeReceipt.State);
            var writeEx = await Assert.ThrowsAsync<ProjectConfigException>(() => tWrite);
            Assert.Equal(503, writeEx.StatusCode);
        }

        // 2b. Runtime role CAN publish Project C under deployed runtime grants,
        // and CANNOT update a Pinned baseline to Consumed with consumed_revision = NULL even when a matching head=1 exists.
        var projectC = await service.CreateProjectAsync(owner, "Runtime Publish " + suffix, CancellationToken.None);
        var appC = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectC.ProjectId, baselineArtifact, "fwc-appC-" + suffix, "Approve C", "corr-c-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectC.ProjectId, appC.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinC-" + suffix, "Pin C", "corr-pc-" + suffix, CancellationToken.None);

        var runtimeOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(runtimeDataSource, npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var runtimeStore = new NativeAcsPolicyStore(
            new StaticDbContextFactory(runtimeOptions), TimeProvider.System, CreateTestDataProtectionProvider());
        var runtimeReceipt = await runtimeStore.WriteFirstProjectPolicyAsync(
            owner,
            new NativeAcsPolicyWriteRequest(
                NativeAcsPolicyScopeType.Project, projectC.ProjectId, "canonical", null,
                candidateArtifact, "fwc-rt-" + suffix, "Runtime write C", "corr-rt-" + suffix),
            () => true,
            CancellationToken.None);
        Assert.Equal(1, runtimeReceipt.Revision);

        var projectNullGuard = await service.CreateProjectAsync(owner, "Null Guard " + suffix, CancellationToken.None);
        var appNullGuard = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectNullGuard.ProjectId, baselineArtifact, "fwc-appNG-" + suffix, "Approve NG", "corr-ng-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectNullGuard.ProjectId, appNullGuard.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinNG-" + suffix, "Pin NG", "corr-png-" + suffix, CancellationToken.None);
        await using (var ownerConn = await fixture.MigrationDataSource.OpenConnectionAsync())
        {
            await using var seedHead = new NpgsqlCommand("""
                INSERT INTO projects_config.native_acs_policy_revisions
                    (scope_type, scope_id, policy_id, revision, content_sha256, manifest_bytes,
                     created_by_issuer, created_by_actor_id, membership_id, membership_revision,
                     role_assignment_id, role_assignment_revision, reason, correlation_id, created_at)
                VALUES ('Project', @pid, 'canonical', 1, @sha, '\x7b7d', @iss, @sub, @mid, 1, @aid, 1, 'r', 'c', now());
                INSERT INTO projects_config.native_acs_policy_heads
                    (scope_type, scope_id, policy_id, current_revision, created_at, updated_at)
                VALUES ('Project', @pid, 'canonical', 1, now(), now());
                """, ownerConn);
            seedHead.Parameters.AddWithValue("pid", projectNullGuard.ProjectId);
            seedHead.Parameters.AddWithValue("sha", candidateArtifact.ContentSha256);
            seedHead.Parameters.AddWithValue("iss", owner.Issuer);
            seedHead.Parameters.AddWithValue("sub", owner.ActorId);
            seedHead.Parameters.AddWithValue("mid", owner.MembershipId);
            seedHead.Parameters.AddWithValue("aid", owner.Grants[0].AssignmentId);
            await seedHead.ExecuteNonQueryAsync();
        }
        await using (var runtimeConn = await runtimeDataSource.OpenConnectionAsync())
        {
            var nullConsumeEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = new NpgsqlCommand("""
                    UPDATE projects_config.native_acs_project_policy_baselines
                    SET state = 'Consumed', consumed_revision = NULL, consumed_at = now()
                    WHERE baseline_id = @id
                    """, runtimeConn);
                cmd.Parameters.AddWithValue("id", appNullGuard.BaselineId);
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Equal("55000", nullConsumeEx.SqlState);
        }

        // 2c. Post-wait same-key concurrent first writes under fence barrier: one commits (Replayed=false), waiter replays (Replayed=true).
        var projectD = await service.CreateProjectAsync(owner, "Same Key Conc " + suffix, CancellationToken.None);
        var appD = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectD.ProjectId, baselineArtifact, "fwc-appD-" + suffix, "Approve D", "corr-d-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectD.ProjectId, appD.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinD-" + suffix, "Pin D", "corr-pd-" + suffix, CancellationToken.None);
        var sameKeyReq = new NativeAcsPolicyWriteRequest(
            NativeAcsPolicyScopeType.Project, projectD.ProjectId, "canonical", null,
            candidateArtifact, "same-key-" + suffix, "Same key conc", "corr-sk-" + suffix);
        NativeAcsPolicyWriteReceipt[] sameKeyResults;
        await using (var skBarrierDb = CreateDbContext())
        await using (var skBarrierTx = await skBarrierDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted))
        {
            _ = await new SkillAdmissionService(skBarrierDb, TimeProvider.System).LockFenceAsync(CancellationToken.None);
            var blockerPid = await ReadBackendPidAsync(skBarrierDb);
            var sk1 = CreateNativeAcsStore().WriteFirstProjectPolicyAsync(owner, sameKeyReq, () => true, CancellationToken.None);
            var sk2 = CreateNativeAcsStore().WriteFirstProjectPolicyAsync(owner, sameKeyReq, () => true, CancellationToken.None);
            await WaitForBlockedFenceWaitersAsync(fixture.DataSource, blockerPid, 2);
            Assert.False(sk1.IsCompleted);
            Assert.False(sk2.IsCompleted);
            await skBarrierTx.CommitAsync();
            sameKeyResults = await Task.WhenAll(sk1, sk2);
        }
        Assert.Equal(sameKeyResults[0].AuditId, sameKeyResults[1].AuditId);
        Assert.Equal(sameKeyResults[0].OutboxId, sameKeyResults[1].OutboxId);
        Assert.Single(sameKeyResults, r => !r.Replayed);
        Assert.Single(sameKeyResults, r => r.Replayed);

        var projectE = await service.CreateProjectAsync(owner, "Rollback " + suffix, CancellationToken.None);
        var appE = await store.CreateBaselineApprovalAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineApprovalRequest(
                projectE.ProjectId, baselineArtifact, "fwc-appE-" + suffix, "Approve E", "corr-e-" + suffix),
            CancellationToken.None);
        await authorityPolicyStore.PinBaselineByOperatorAsync(
            opCaller, projectE.ProjectId, appE.BaselineId, baselineArtifact.ContentSha256,
            "fwc-pinE-" + suffix, "Pin E", "corr-pe-" + suffix, CancellationToken.None);

        async Task AssertNoPartialStateAsync(string pid, Guid bid, long expectedGen)
        {
            await using var rbDb = CreateDbContext();
            Assert.Null(await rbDb.NativeAcsPolicyHeads.AsNoTracking()
                .SingleOrDefaultAsync(h => h.ScopeType == NativeAcsPolicyScopeType.Project && h.ScopeId == pid));
            Assert.Empty(await rbDb.NativeAcsPolicyRevisions.AsNoTracking()
                .Where(r => r.ScopeType == NativeAcsPolicyScopeType.Project && r.ScopeId == pid).ToListAsync());
            Assert.Empty(await rbDb.NativeAcsPolicyArtifactFiles.AsNoTracking()
                .Where(f => f.ScopeType == NativeAcsPolicyScopeType.Project && f.ScopeId == pid).ToListAsync());
            Assert.Empty(await rbDb.NativeAcsPolicyIdempotency.AsNoTracking()
                .Where(i => i.ScopeType == NativeAcsPolicyScopeType.Project && i.ScopeId == pid).ToListAsync());
            Assert.Empty(await rbDb.NativeAcsPolicyChangeAudit.AsNoTracking()
                .Where(a => a.ScopeType == NativeAcsPolicyScopeType.Project && a.ScopeId == pid).ToListAsync());
            var outboxRows = await rbDb.NativeAcsPolicyOutbox.AsNoTracking().ToListAsync();
            Assert.DoesNotContain(outboxRows, o => o.PayloadJson.Contains(pid, StringComparison.Ordinal));
            var pinRow = await rbDb.NativeAcsProjectPolicyBaselines.AsNoTracking().SingleAsync(b => b.BaselineId == bid);
            Assert.Equal(NativeAcsProjectPolicyBaselineState.Pinned, pinRow.State);
            Assert.Null(pinRow.ConsumedRevision);
            var fenceNow = await rbDb.SkillAdmissionFences.AsNoTracking().SingleAsync(f => f.Id == 1);
            Assert.Equal(expectedGen, fenceNow.Generation);
        }

        long genBeforeFailures;
        await using (var genDb = CreateDbContext())
            genBeforeFailures = (await genDb.SkillAdmissionFences.AsNoTracking().SingleAsync(f => f.Id == 1)).Generation;

        await using (var ownerConn = await fixture.MigrationDataSource.OpenConnectionAsync())
        {
            // Outbox insert failure
            await using (var addTrig = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION projects_config.test_fail_outbox_{suffix}()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.payload::text LIKE '%{projectE.ProjectId}%' THEN
                        RAISE EXCEPTION 'injected outbox failure';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER tr_test_fail_outbox_{suffix}
                    BEFORE INSERT ON projects_config.native_acs_policy_outbox
                    FOR EACH ROW EXECUTE FUNCTION projects_config.test_fail_outbox_{suffix}();
                """, ownerConn))
                await addTrig.ExecuteNonQueryAsync();
            try
            {
                var outboxEx = await Assert.ThrowsAsync<DbUpdateException>(() =>
                    store.WriteFirstProjectPolicyAsync(
                        owner,
                        new NativeAcsPolicyWriteRequest(
                            NativeAcsPolicyScopeType.Project, projectE.ProjectId, "canonical", null,
                            candidateArtifact, "fwc-rb-outbox-" + suffix, "Injected outbox", "corr-rb1-" + suffix),
                        () => true, CancellationToken.None));
                Assert.Equal("injected outbox failure", Assert.IsType<PostgresException>(outboxEx.InnerException).MessageText);
            }
            finally
            {
                await using var dropTrig = new NpgsqlCommand($"""
                    DROP TRIGGER IF EXISTS tr_test_fail_outbox_{suffix} ON projects_config.native_acs_policy_outbox;
                    DROP FUNCTION IF EXISTS projects_config.test_fail_outbox_{suffix}();
                    """, ownerConn);
                await dropTrig.ExecuteNonQueryAsync();
            }
            await AssertNoPartialStateAsync(projectE.ProjectId, appE.BaselineId, genBeforeFailures);

            // Baseline consume failure
            await using (var addConsumeTrig = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION projects_config.test_fail_consume_{suffix}()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD.baseline_id = '{appE.BaselineId}' AND NEW.state = 'Consumed' THEN
                        RAISE EXCEPTION 'injected consume failure';
                    END IF;
                    RETURN NEW;
                END; $$;
                CREATE TRIGGER tr_test_fail_consume_{suffix}
                    BEFORE UPDATE ON projects_config.native_acs_project_policy_baselines
                    FOR EACH ROW EXECUTE FUNCTION projects_config.test_fail_consume_{suffix}();
                """, ownerConn))
                await addConsumeTrig.ExecuteNonQueryAsync();
            try
            {
                var consumeEx = await Assert.ThrowsAsync<PostgresException>(() =>
                    store.WriteFirstProjectPolicyAsync(
                        owner,
                        new NativeAcsPolicyWriteRequest(
                            NativeAcsPolicyScopeType.Project, projectE.ProjectId, "canonical", null,
                            candidateArtifact, "fwc-rb-consume-" + suffix, "Injected consume", "corr-rb2-" + suffix),
                        () => true, CancellationToken.None));
                Assert.Equal("injected consume failure", consumeEx.MessageText);
            }
            finally
            {
                await using var dropTrig = new NpgsqlCommand($"""
                    DROP TRIGGER IF EXISTS tr_test_fail_consume_{suffix} ON projects_config.native_acs_project_policy_baselines;
                    DROP FUNCTION IF EXISTS projects_config.test_fail_consume_{suffix}();
                    """, ownerConn);
                await dropTrig.ExecuteNonQueryAsync();
            }
            await AssertNoPartialStateAsync(projectE.ProjectId, appE.BaselineId, genBeforeFailures);

            // Deferred commit failure
            await using (var addCommitTrig = new NpgsqlCommand($"""
                CREATE OR REPLACE FUNCTION projects_config.test_fail_commit_{suffix}()
                RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NEW.baseline_id = '{appE.BaselineId}' AND NEW.state = 'Consumed' THEN
                        RAISE EXCEPTION 'injected commit failure';
                    END IF;
                    RETURN NULL;
                END; $$;
                CREATE CONSTRAINT TRIGGER tr_test_fail_commit_{suffix}
                    AFTER UPDATE ON projects_config.native_acs_project_policy_baselines
                    DEFERRABLE INITIALLY DEFERRED
                    FOR EACH ROW EXECUTE FUNCTION projects_config.test_fail_commit_{suffix}();
                """, ownerConn))
                await addCommitTrig.ExecuteNonQueryAsync();
            try
            {
                var commitEx = await Assert.ThrowsAsync<PostgresException>(() =>
                    store.WriteFirstProjectPolicyAsync(
                        owner,
                        new NativeAcsPolicyWriteRequest(
                            NativeAcsPolicyScopeType.Project, projectE.ProjectId, "canonical", null,
                            candidateArtifact, "fwc-rb-commit-" + suffix, "Injected commit", "corr-rb3-" + suffix),
                        () => true, CancellationToken.None));
                Assert.Equal("injected commit failure", commitEx.MessageText);
            }
            finally
            {
                await using var dropTrig = new NpgsqlCommand($"""
                    DROP TRIGGER IF EXISTS tr_test_fail_commit_{suffix} ON projects_config.native_acs_project_policy_baselines;
                    DROP FUNCTION IF EXISTS projects_config.test_fail_commit_{suffix}();
                    """, ownerConn);
                await dropTrig.ExecuteNonQueryAsync();
            }
            await AssertNoPartialStateAsync(projectE.ProjectId, appE.BaselineId, genBeforeFailures);
        }

        // 2d. Draining fence AND missing fence both return 503 for new writes while exact replay still succeeds.
        await using (var drainDb = CreateDbContext())
        {
            var fence = await drainDb.SkillAdmissionFences.SingleAsync(f => f.Id == 1);
            var origState = fence.State;
            fence.State = ProjectSkillAdmissionFenceState.Draining;
            await drainDb.SaveChangesAsync();
            try
            {
                var drainEx = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                    store.WriteFirstProjectPolicyAsync(
                        owner,
                        new NativeAcsPolicyWriteRequest(
                            NativeAcsPolicyScopeType.Project, projectE.ProjectId, "canonical", null,
                            candidateArtifact, "fwc-drain-" + suffix, "During drain", "corr-dr-" + suffix),
                        () => true,
                        CancellationToken.None));
                Assert.Equal(503, drainEx.StatusCode);

                var replayWinning = await store.FindFirstProjectWriteReplayAsync(
                    owner, sameKeyReq, CancellationToken.None);
                Assert.NotNull(replayWinning);
                Assert.True(replayWinning!.Replayed);
            }
            finally
            {
                fence.State = origState;
                await drainDb.SaveChangesAsync();
            }
        }

        await using (var ownerConn = await fixture.MigrationDataSource.OpenConnectionAsync())
        {
            ProjectSkillAdmissionFenceRecord savedFence;
            await using (var fDb = CreateDbContext())
                savedFence = await fDb.SkillAdmissionFences.AsNoTracking().SingleAsync(f => f.Id == 1);
            await using (var delFence = new NpgsqlCommand("DELETE FROM projects_config.project_skill_admission_fence WHERE id = 1", ownerConn))
                await delFence.ExecuteNonQueryAsync();
            try
            {
                var missingFenceEx = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                    store.WriteFirstProjectPolicyAsync(
                        owner,
                        new NativeAcsPolicyWriteRequest(
                            NativeAcsPolicyScopeType.Project, projectE.ProjectId, "canonical", null,
                            candidateArtifact, "fwc-missing-fence-" + suffix, "Missing fence", "corr-mf-" + suffix),
                        () => true,
                        CancellationToken.None));
                Assert.Equal(503, missingFenceEx.StatusCode);
                Assert.Equal("native_acs_policy_source_unavailable", missingFenceEx.Code);

                var replayMissingFence = await store.FindFirstProjectWriteReplayAsync(
                    owner, sameKeyReq, CancellationToken.None);
                Assert.NotNull(replayMissingFence);
                Assert.True(replayMissingFence!.Replayed);
            }
            finally
            {
                await using var restoreFence = new NpgsqlCommand("""
                    INSERT INTO projects_config.project_skill_admission_fence
                        (id, state, generation, cutover_generation, release_inventory_revision, platform_skills_policy_sha256, updated_at)
                    VALUES (1, @state, @gen, @cgen, @rev, @sha, @upd)
                    """, ownerConn);
                restoreFence.Parameters.AddWithValue("state", savedFence.State.ToString());
                restoreFence.Parameters.AddWithValue("gen", savedFence.Generation);
                restoreFence.Parameters.AddWithValue("cgen", savedFence.CutoverGeneration);
                restoreFence.Parameters.AddWithValue("rev", (object?)savedFence.ReleaseInventoryRevision ?? DBNull.Value);
                restoreFence.Parameters.AddWithValue("sha", (object?)savedFence.PlatformSkillsPolicySha256 ?? DBNull.Value);
                restoreFence.Parameters.AddWithValue("upd", savedFence.UpdatedAt);
                await restoreFence.ExecuteNonQueryAsync();
            }
        }

        // 3. Healthy Platform drift after pin blocks Project B first write with 409 Conflict.
        var driftedPlatform = CreateV3PlatformArtifact(
            """{"allowed_action_ids":["tool.read"]}""",
            """{"rules":[]}""",
            """{"required_checks":[],"blocked_findings":[]}""",
            null,
            """{"schemaVersion":1,"minimumScore":550,"requireCompletedEvidence":true}""");
        var currentPlatHead = await store.ReadPlatformHeadAsync(CancellationToken.None);
        var platHeadRev = currentPlatHead!.Revision;
        await WritePlatformPolicyAtRevisionAsync(platformAdmin, driftedPlatform, platHeadRev, "fwc-plat-drift-" + suffix, "corr-drift-" + suffix);

        var driftEx = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.WriteFirstProjectPolicyAsync(
                owner,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project, projectB.ProjectId, "canonical", null,
                    candidateArtifact, "fwc-drift-" + suffix, "Write after drift", "corr-wd-" + suffix),
                () => true,
                CancellationToken.None));
        Assert.Equal(409, driftEx.StatusCode);

        // Revoking Project B pin succeeds, after which write fails with 503 (no usable pin).
        await store.RevokeBaselineByAuthorAsync(
            new ProjectAuthorizationContext(
                owner.Issuer, owner.ActorId, owner.TenantId, owner.MembershipId, owner.MembershipRevision,
                owner.Grants, owner.Scopes, null, null, null, isVerifiedHuman: true),
            new NativeAcsProjectPolicyBaselineRevokeRequest(
                projectB.ProjectId, appB.BaselineId, "Revoke after drift", "corr-rad-" + suffix),
            CancellationToken.None);
        var noPinEx = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.WriteFirstProjectPolicyAsync(
                owner,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project, projectB.ProjectId, "canonical", null,
                    candidateArtifact, "fwc-nopin-" + suffix, "Write after revoke", "corr-war-" + suffix),
                () => true,
                CancellationToken.None));
        Assert.Equal(503, noPinEx.StatusCode);
    }

    [Fact]
    public async Task FirstProjectPolicyWriteStalesNullPinAndRestoresDeliveryAfterRefresh()
    {
        var opaPath = Environment.GetEnvironmentVariable("ACS_OPA_PATH")
            ?? Environment.GetEnvironmentVariable("AGENTWEAVER_TEST_OPA_PATH");
        if (string.IsNullOrWhiteSpace(opaPath) || !File.Exists(opaPath))
            return;

        await using var db = CreateDbContext();
        await ResetSkillAdmissionCutoverAsync(db);
        try
        {
            var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(
                CreateDbContextOptions(), TimeProvider.System);
            var project = await CreateSkillInventoryProjectAsync(db, authorityStore);
            var projectB = await CreateSkillInventoryProjectAsync(db, authorityStore);
            var objects = new SkillTestObjectStore();
            var objectStore = new SkillContentObjectStore(objects);
            var configurations = new ProjectsConfigService(
                db, CreateProviderCatalog(), TimeProvider.System, skillContentObjects: objectStore);
            var configured = await configurations.UpdateProjectConfigurationAsync(
                project.Owner,
                project.Project.ProjectId,
                project.Project.ConfigurationRevision,
                ProjectSettings() with
                {
                    AgentCharters =
                    [
                        new ProjectAgentCharter(
                            "agent-one", "First Write Refresh Agent", "assistant", "Refresh test agent."),
                    ],
                    Casting = [new ProjectAgentCast("agent-one", "assistant", 0)],
                },
                CancellationToken.None);
            var skills = new SkillContentService(
                db, objectStore, configurations, TimeProvider.System);
            skills.EvaluateSkillsPolicyOverride = (_, _, _, _, _, _) => Task.CompletedTask;

            // Pre-cutover LegacyThirdParty Skill remains grandfathered.
            var legacyCandidate = SkillCandidate(
                "A sufficiently long pre-cutover legacy Skill instruction for first write.",
                "# Legacy First Write\n");
            var legacyImported = await skills.ImportAsync(
                project.Owner,
                project.Project.ProjectId,
                new SkillContentImportRequest(
                    "fw-legacy-" + project.Suffix,
                    skills.Preview(legacyCandidate).ContentDigest,
                    "fw-legacy-skill",
                    null,
                    null,
                    legacyCandidate),
                CancellationToken.None);

            // Seed Platform v3 Policy with minimumScore: 400 and activate admission fence.
            var platformArtifact = CreateV3PlatformArtifact(
                """{"allowed_action_ids":["tool.read"]}""",
                """{"rules":[]}""",
                """{"required_checks":[],"blocked_findings":[]}""",
                null,
                """{"schemaVersion":1,"minimumScore":400,"requireCompletedEvidence":true}""");
            var platformAdmin = await SeedCallerAsync(
                db, authorityStore, "fw-refresh-plat-" + project.Suffix, project.Owner.TenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            await WritePlatformPolicyAsync(platformAdmin, platformArtifact, "fw-ref-plat-" + project.Suffix, "corr-p-" + project.Suffix);

            var fence = await db.SkillAdmissionFences.SingleAsync(item => item.Id == 1);
            fence.State = ProjectSkillAdmissionFenceState.Active;
            fence.ReleaseInventoryRevision = "fw-refresh-inv-" + project.Suffix;
            fence.PlatformSkillsPolicySha256 = platformArtifact.ContentSha256;
            await db.SaveChangesAsync();

            var candidateA = SkillCandidate(
                "A sufficiently long approved instruction for first-write refresh A.",
                "# FW Refresh A\n");
            var importedA = await skills.ImportAsync(
                project.Owner,
                project.Project.ProjectId,
                new SkillContentImportRequest(
                    "fw-import-a-" + project.Suffix,
                    skills.Preview(candidateA).ContentDigest,
                    "fw-skill-a",
                    null,
                    new SkillContentSourceSelection(
                        "caller-source", "caller-revision", "main", new string('c', 40), "skills/a"),
                    candidateA),
                CancellationToken.None);

            var candidateB = SkillCandidate(
                "A sufficiently long approved instruction for first-write refresh B.",
                "# FW Refresh B\n");
            var importedB = await skills.ImportAsync(
                project.Owner,
                project.Project.ProjectId,
                new SkillContentImportRequest(
                    "fw-import-b-" + project.Suffix,
                    skills.Preview(candidateB).ContentDigest,
                    "fw-skill-b",
                    null,
                    new SkillContentSourceSelection(
                        "caller-source", "caller-revision", "main", new string('d', 40), "skills/b"),
                    candidateB),
                CancellationToken.None);

            // Create accepted run BEFORE Project Policy head exists (pinned to null Project Policy).
            var assignedConfiguration = configured.Configuration with
            {
                Skills =
                [
                    new SkillCatalogSetting(legacyImported.SkillId, true, 0)
                    {
                        Revision = legacyImported.Revision,
                        ContentDigest = legacyImported.ContentDigest,
                        AgentIds = ["agent-one"],
                    },
                    new SkillCatalogSetting(importedA.SkillId, true, 1)
                    {
                        Revision = importedA.Revision,
                        ContentDigest = importedA.ContentDigest,
                        AgentIds = ["agent-one"],
                    },
                    new SkillCatalogSetting(importedB.SkillId, true, 2)
                    {
                        Revision = importedB.Revision,
                        ContentDigest = importedB.ContentDigest,
                        AgentIds = ["agent-one"],
                    },
                ],
            };
            var assignedRevision = checked(configured.Revision + 1);
            db.ProjectConfigurationRevisions.Add(new ProjectConfigurationRevisionRecord
            {
                ProjectId = project.Project.ProjectId,
                Revision = assignedRevision,
                ConfigurationJson = ProjectsConfigService.SerializeProjectConfiguration(assignedConfiguration),
                UpdatedByActorId = project.Owner.ActorId,
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
            var projectRecord = await db.Projects.SingleAsync(item => item.ProjectId == project.Project.ProjectId);
            projectRecord.ConfigurationRevision = assignedRevision;
            var runId = "fw-refresh-run-" + project.Suffix;
            var selection = new EffectiveRunSelection(
                project.Project.ProjectId,
                runId,
                project.Project.Revision,
                assignedRevision,
                project.PlatformDefaultsRevision,
                "fw-refresh-context",
                new ModelSelectionSettings("fw-refresh-model"),
                [],
                [],
                new CopilotRunLimits(),
                assignedConfiguration,
                [],
                null,
                []);
            db.RunSelections.Add(new ProjectRunSelectionRecord
            {
                RunId = runId,
                ProjectId = project.Project.ProjectId,
                ProjectRevision = project.Project.Revision,
                ProjectConfigurationRevision = assignedRevision,
                PlatformRuntimeRevision = project.PlatformDefaultsRevision,
                ContextRevision = "fw-refresh-context",
                RequestFingerprint = new string('a', 64),
                SnapshotJson = ProjectsConfigService.SerializeRunSelection(selection),
                CreatedAt = TimeProvider.System.GetUtcNow(),
            });
            await db.SaveChangesAsync();

            // Capture pre-write ordered assignments and exact bytes before Project Policy write.
            var orchestratorActor = await SeedCallerAsync(
                db,
                authorityStore,
                "fw-orch-" + project.Suffix,
                project.TenantId,
                ["api.read", "projects.orchestrator"],
                ProjectAuthorityResourceType.Project,
                project.Project.ProjectId,
                ProjectAuthorityRole.Orchestrator);
            var orchestrator = await ResolveCallerAsync(
                db,
                orchestratorActor.ActorId,
                project.TenantId,
                ["api.read", "projects.orchestrator"],
                project.Project.ProjectId,
                runId);
            var preWriteDelivered = await skills.ReadAcceptedRunSkillsAsync(
                orchestrator,
                project.Project.ProjectId,
                runId,
                "agent-one",
                CancellationToken.None);
            Assert.Equal(3, preWriteDelivered.Length);

            // Approve, pin, and write first Project Policy (stricter Skills minimumScore: 500).
            var baselineArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
                """{"allowed_action_ids":["tool.read"]}""",
                """{"rules":[]}""",
                """{"required_checks":[],"blocked_findings":[]}""",
                null,
                """{"schemaVersion":1,"minimumScore":400,"requireCompletedEvidence":true}""");
            var candidateArtifact = NativeAcsUnifiedPolicyProfile.CreateProjectArtifact(
                """{"allowed_action_ids":["tool.read"]}""",
                """{"rules":[]}""",
                """{"required_checks":[],"blocked_findings":[]}""",
                null,
                """{"schemaVersion":1,"minimumScore":500,"requireCompletedEvidence":true}""");

            var store = CreateNativeAcsStore();
            var approved = await store.CreateBaselineApprovalAsync(
                new ProjectAuthorizationContext(
                    project.Owner.Issuer, project.Owner.ActorId, project.Owner.TenantId,
                    project.Owner.MembershipId, project.Owner.MembershipRevision,
                    project.Owner.Grants, project.Owner.Scopes, null, null, null, isVerifiedHuman: true),
                new NativeAcsProjectPolicyBaselineApprovalRequest(
                    project.Project.ProjectId, baselineArtifact, "fw-r-app-" + project.Suffix, "Approve", "corr-a-" + project.Suffix),
                CancellationToken.None);

            var opSubject = "fw-r-op-" + project.Suffix;
            var opMembership = await authorityStore.GrantMembershipAsync(
                TestIssuer, opSubject, project.Owner.TenantId, "test-seed", CancellationToken.None);
            var opCaller = new ProjectAuthorizationContext(
                TestIssuer, opSubject, project.Owner.TenantId, opMembership.MembershipId, opMembership.Revision,
                [], ImmutableHashSet.Create("api.read"), null, null, null, isVerifiedHuman: true);
            var authorityPolicyStore = new NativeAcsPolicyStore(
                CreateMigrationDbContextOptionsFactory(), TimeProvider.System, CreateTestDataProtectionProvider());
            await authorityPolicyStore.PinBaselineByOperatorAsync(
                opCaller, project.Project.ProjectId, approved.BaselineId, baselineArtifact.ContentSha256,
                "fw-r-pin-" + project.Suffix, "Pin", "corr-p-" + project.Suffix, CancellationToken.None);

            await store.WriteFirstProjectPolicyAsync(
                project.Owner,
                new NativeAcsPolicyWriteRequest(
                    NativeAcsPolicyScopeType.Project, project.Project.ProjectId, "canonical", null,
                    candidateArtifact, "fw-r-put-" + project.Suffix, "First write", "corr-w-" + project.Suffix),
                () => true,
                CancellationToken.None);

            // Unrelated Project B still has no Project head.
            Assert.Null(await store.ReadAsync(NativeAcsPolicyScopeType.Project, projectB.Project.ProjectId, CancellationToken.None));

            // Before refresh, Orchestrator delivery fails because the formerly-null Project Policy pin is stale.
            var staleEx = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
                skills.ReadAcceptedRunSkillsAsync(
                    orchestrator,
                    project.Project.ProjectId,
                    runId,
                    "agent-one",
                    CancellationToken.None));
            Assert.Equal(409, staleEx.StatusCode);

            // Refresh inputs and refresh both governed Skills through the refresh endpoints.
            await using var host = await StartRefreshEndpointHostAsync(project.Owner.Issuer, objects);
            using var client = host.GetTestClient();
            var inputsPath = $"/api/projects/{project.Project.ProjectId}/runs/{runId}/agents/agent-one/skills/refresh-inputs";
            var refreshPath = $"/api/projects/{project.Project.ProjectId}/runs/{runId}/agents/agent-one/skills/refresh";

            AcceptedRunSkillRefreshInputsResponse inputs;
            using (var inputsReq = new HttpRequestMessage(HttpMethod.Get, inputsPath))
            {
                inputsReq.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                inputsReq.Headers.Add("X-Test-Actor-Class", "human");
                inputsReq.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                using var res = await client.SendAsync(inputsReq);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
                inputs = (await res.Content.ReadFromJsonAsync<AcceptedRunSkillRefreshInputsResponse>())!;
                Assert.Equal(3, inputs.Assignments.Length);
                Assert.False(inputs.Assignments[0].RefreshEligible);
                Assert.True(inputs.Assignments[1].RefreshEligible);
                Assert.True(inputs.Assignments[2].RefreshEligible);
            }

            // Refresh Skill A only: partial refresh must keep delivery blocked.
            using (var refReqA = new HttpRequestMessage(HttpMethod.Post, refreshPath))
            {
                refReqA.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                refReqA.Headers.Add("X-Test-Actor-Class", "human");
                refReqA.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                refReqA.Content = JsonContent.Create(new
                {
                    skillId = importedA.SkillId,
                    revision = importedA.Revision,
                    contentDigest = importedA.ContentDigest,
                    expectedPlatformPolicy = inputs.PlatformPolicy,
                    expectedProjectPolicy = inputs.ProjectPolicy,
                    idempotencyKey = "fw-ref-a-" + project.Suffix,
                    reason = "Refresh A after first Project Policy write",
                    correlationId = "corr-ref-a-" + project.Suffix,
                });
                using var res = await client.SendAsync(refReqA);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            }
            var partialEx = await Assert.ThrowsAsync<SkillContentServiceException>(() =>
                skills.ReadAcceptedRunSkillsAsync(
                    orchestrator,
                    project.Project.ProjectId,
                    runId,
                    "agent-one",
                    CancellationToken.None));
            Assert.Equal(409, partialEx.StatusCode);

            // Refresh Skill B: full delivery and checkpoint recovery readback now succeed with exact ordered bytes.
            using (var refReqB = new HttpRequestMessage(HttpMethod.Post, refreshPath))
            {
                refReqB.Headers.Add("X-Test-Sub", project.Owner.ActorId);
                refReqB.Headers.Add("X-Test-Actor-Class", "human");
                refReqB.Headers.Add("X-Agentweaver-Tenant", project.Owner.TenantId);
                refReqB.Content = JsonContent.Create(new
                {
                    skillId = importedB.SkillId,
                    revision = importedB.Revision,
                    contentDigest = importedB.ContentDigest,
                    expectedPlatformPolicy = inputs.PlatformPolicy,
                    expectedProjectPolicy = inputs.ProjectPolicy,
                    idempotencyKey = "fw-ref-b-" + project.Suffix,
                    reason = "Refresh B after first Project Policy write",
                    correlationId = "corr-ref-b-" + project.Suffix,
                });
                using var res = await client.SendAsync(refReqB);
                Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            }

            var delivered = await skills.ReadAcceptedRunSkillsAsync(
                orchestrator,
                project.Project.ProjectId,
                runId,
                "agent-one",
                CancellationToken.None);

            // Checkpoint recovery readback on a fresh service/DbContext returns identical assignments and bytes.
            await using var recoveryDb = CreateDbContext();
            var recoverySkills = new SkillContentService(
                recoveryDb, objectStore,
                new ProjectsConfigService(recoveryDb, CreateProviderCatalog(), TimeProvider.System, skillContentObjects: objectStore),
                TimeProvider.System);
            var recovered = await recoverySkills.ReadAcceptedRunSkillsAsync(
                orchestrator,
                project.Project.ProjectId,
                runId,
                "agent-one",
                CancellationToken.None);

            foreach (var actual in new[] { delivered, recovered })
            {
                Assert.Equal(preWriteDelivered.Length, actual.Length);
                for (var i = 0; i < preWriteDelivered.Length; i++)
                {
                    Assert.Equal(preWriteDelivered[i].SkillId, actual[i].SkillId);
                    Assert.Equal(preWriteDelivered[i].Revision, actual[i].Revision);
                    Assert.Equal(preWriteDelivered[i].ContentDigest, actual[i].ContentDigest);
                    Assert.Equal(preWriteDelivered[i].Instructions, actual[i].Instructions);
                    Assert.Equal(preWriteDelivered[i].Resources.Length, actual[i].Resources.Length);
                    for (var r = 0; r < preWriteDelivered[i].Resources.Length; r++)
                    {
                        Assert.Equal(preWriteDelivered[i].Resources[r].RelativePath, actual[i].Resources[r].RelativePath);
                        Assert.True(preWriteDelivered[i].Resources[r].Content.AsSpan()
                            .SequenceEqual(actual[i].Resources[r].Content.AsSpan()));
                    }
                }
            }
        }
        finally
        {
            await ResetSkillAdmissionCutoverAsync(db);
        }
    }

    private static async Task<int> ReadBackendPidAsync(ProjectsConfigDbContext db)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var tx = (NpgsqlTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
        await using var cmd = new NpgsqlCommand("SELECT pg_catalog.pg_backend_pid()", conn, tx);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task WaitForBlockedFenceWaitersAsync(
        NpgsqlDataSource dataSource,
        int blockerPid,
        int expectedWaiters)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        await using var conn = await dataSource.OpenConnectionAsync();
        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT COUNT(DISTINCT a.pid)::int
                FROM pg_catalog.pg_stat_activity AS a
                WHERE a.pid <> @blocker_pid
                  AND a.wait_event_type = 'Lock'
                  AND a.query LIKE '%project_skill_admission_fence%'
                  AND (
                    @blocker_pid = ANY(pg_catalog.pg_blocking_pids(a.pid))
                    OR EXISTS (
                        SELECT 1
                        FROM pg_catalog.pg_stat_activity AS mid
                        WHERE mid.pid = ANY(pg_catalog.pg_blocking_pids(a.pid))
                          AND mid.query LIKE '%project_skill_admission_fence%'
                          AND @blocker_pid = ANY(pg_catalog.pg_blocking_pids(mid.pid))
                    )
                  )
                """, conn);
            cmd.Parameters.AddWithValue("blocker_pid", blockerPid);
            var count = (int)(await cmd.ExecuteScalarAsync())!;
            if (count >= expectedWaiters)
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"Timed out waiting for {expectedWaiters} fence waiters blocked by pid {blockerPid}.");
    }

    private Task WritePlatformPolicyAtRevisionAsync(
        ProjectAuthorizationContext platformAdmin,
        NativeAcsArtifact artifact,
        long? expectedRevision,
        string idempotencyKey,
        string correlationId)
    {
        var store = CreateNativeAcsStore();
        return store.WriteAsync(
            platformAdmin,
            new NativeAcsPolicyWriteRequest(
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                "canonical",
                expectedRevision,
                artifact,
                idempotencyKey,
                "Platform test policy",
                correlationId),
            CancellationToken.None);
    }

    private static HttpRequestMessage CreatePutRequest(string url, string sub, string tenantId, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, url);
        request.Headers.Add("X-Test-Sub", sub);
        request.Headers.Add("X-Agentweaver-Tenant", tenantId);
        request.Content = JsonContent.Create(body);
        return request;
    }

    private static async Task<IHost> StartFirstWriteEndpointHostAsync(
        NpgsqlDataSource dataSource,
        bool projectHeadWritesEnabled,
        string? opaPath = null) =>
        await new HostBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["NativeAcsPolicy:ProjectHeadWritesEnabled"] = projectHeadWritesEnabled ? "true" : "false",
                });
            })
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
}
