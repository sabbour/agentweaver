using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task HostRequestIsCommittedAtomicallyWithRevisionAndPreservesHistoricalReplay()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "hr-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "hr-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, "hr-tenant-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var project = await projects.CreateProjectAsync(tenantAdmin, "HR Project", CancellationToken.None);
        var ownerSubject = "hr-owner-" + suffix;
        await SeedCallerAsync(
            db, authorityStore, ownerSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Project,
            project.ProjectId, ProjectAuthorityRole.Owner);
        var owner = await ResolveCallerAsync(
            db, ownerSubject, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);

        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var platformOp = Guid.NewGuid();
        var platformId = "byok-" + platformOp.ToString("N");
        var platformCreated = await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, platformId,
            new SaveModelProviderDraftRequest(
                platformOp, 0, "Platform OpenAI", "openai", "https://API.OpenAI.COM/v1", "gpt-5"),
            CancellationToken.None);

        Assert.Equal(ModelProviderDraftState.Draft, platformCreated.State);
        Assert.NotNull(platformCreated.Egress);
        Assert.Equal("api.openai.com", platformCreated.Egress!.Host);
        Assert.Equal(443, platformCreated.Egress.Port);
        Assert.Null(platformCreated.Egress.DestinationState);
        Assert.False(platformCreated.Egress.CanApprove);
        Assert.Equal("pending", platformCreated.Egress.DeliveryStatus);

        var platformReq = await db.ModelProviderHostRequests.AsNoTracking()
            .SingleAsync(r => r.ProviderId == platformId && r.ProviderRevision == 1);
        Assert.Equal(tenantId, platformReq.TenantId);
        Assert.Equal(ModelProviderBindingScope.Platform, platformReq.Scope);
        Assert.Null(platformReq.ProjectId);
        Assert.Equal(platformCreated.Binding.ConfigurationHash, platformReq.ConfigurationHash);
        Assert.Equal(platformOp, platformReq.OperationId);
        Assert.Equal(admin.ActorId, platformReq.ActorId);
        Assert.Equal("api.openai.com", platformReq.Host);
        Assert.Equal(443, platformReq.Port);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Pending, platformReq.DeliveryState);

        var projectOp = Guid.NewGuid();
        var projectProviderId = "byok-" + projectOp.ToString("N");
        var projectCreated = await service.SaveAsync(
            owner, ModelProviderBindingScope.Project, project.ProjectId, projectProviderId,
            new SaveModelProviderDraftRequest(
                projectOp, 0, "Project Anthropic", "anthropic", "https://api.anthropic.com/v1", "claude"),
            CancellationToken.None);
        Assert.Equal("api.anthropic.com", projectCreated.Egress!.Host);
        Assert.Equal("pending", projectCreated.Egress.DeliveryStatus);

        var projectUpdateOp = Guid.NewGuid();
        var projectUpdated = await service.SaveAsync(
            owner, ModelProviderBindingScope.Project, project.ProjectId, projectProviderId,
            new SaveModelProviderDraftRequest(
                projectUpdateOp, 1, "Project Azure", "azure", "https://My-Resource.openai.azure.com/", "dep"),
            CancellationToken.None);
        Assert.Equal(2, projectUpdated.Binding.ProviderRevision);
        Assert.Equal("my-resource.openai.azure.com", projectUpdated.Egress!.Host);
        Assert.Equal("pending", projectUpdated.Egress.DeliveryStatus);
        Assert.Equal(2, await db.ModelProviderHostRequests.CountAsync(r => r.ProviderId == projectProviderId));

        // Exact historical replay creates no new revision or request and omits current egress status.
        var replay = await service.SaveAsync(
            owner, ModelProviderBindingScope.Project, project.ProjectId, projectProviderId,
            new SaveModelProviderDraftRequest(
                projectOp, 0, "Project Anthropic", "anthropic", "https://api.anthropic.com/v1", "claude"),
            CancellationToken.None);
        Assert.Equal(1, replay.Binding.ProviderRevision);
        Assert.Null(replay.Egress);
        Assert.Equal(2, await db.ModelProviderHostRequests.CountAsync(r => r.ProviderId == projectProviderId));
    }

    [Fact]
    public async Task HostRequestSaveDeniesRevocationBeforeLockAndKeepsHistoricalRequestAfterCommit()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "hr-rev-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "hr-rev-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, "hr-rev-tadmin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var project = await projects.CreateProjectAsync(tenantAdmin, "HR Rev Project", CancellationToken.None);
        var ownerSubject = "hr-rev-owner-" + suffix;
        await SeedCallerAsync(
            db, authorityStore, ownerSubject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Project,
            project.ProjectId, ProjectAuthorityRole.Owner);
        var owner = await ResolveCallerAsync(
            db, ownerSubject, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);

        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var op1 = Guid.NewGuid();
        var providerId = "byok-" + op1.ToString("N");
        await service.SaveAsync(
            owner, ModelProviderBindingScope.Project, project.ProjectId, providerId,
            new SaveModelProviderDraftRequest(op1, 0, "P1", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);

        // Revoke owner's role assignment before update.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE projects_config.project_role_assignments SET state = 'Revoked', revision = revision + 1 WHERE assignment_id = {owner.Grants[0].AssignmentId}");

        await Assert.ThrowsAsync<ProjectConfigException>(() => service.SaveAsync(
            owner, ModelProviderBindingScope.Project, project.ProjectId, providerId,
            new SaveModelProviderDraftRequest(Guid.NewGuid(), 1, "P2", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None));

        // Historical request from before revocation remains intact.
        Assert.Equal(1, await db.ModelProviderHostRequests.CountAsync(r => r.ProviderId == providerId));
    }

    [Fact]
    public async Task HostRequestRelayDeliversRetriesRecoversExpiredLeaseAndHandlesPermanentMismatch()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "hr-relay-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "hr-relay-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        var fakeEnv = new FakeEnvironmentHostRequestClient();
        var service = new ModelProviderDraftService(db, TimeProvider.System, null, false, fakeEnv);

        var op1 = Guid.NewGuid();
        var id1 = "byok-" + op1.ToString("N");
        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, id1,
            new SaveModelProviderDraftRequest(op1, 0, "Relay1", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        var req1 = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == id1);

        // 1. Transient failure leaves Pending with backoff; ReadAsync reports pending.
        fakeEnv.Mode = FakeEnvironmentMode.Transient;
        await using var scopeProvider = BuildRelayServiceProvider(fakeEnv);
        var relay1 = new ModelProviderHostRequestRelay(
            fixture.DataSource,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ModelProviderHostRequestRelay>.Instance);
        while (await relay1.RunOnceAsync(CancellationToken.None) == 20)
        {
        }

        var afterTransient = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == req1.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Pending, afterTransient.DeliveryState);
        Assert.Equal(1, afterTransient.AttemptCount);
        Assert.Null(afterTransient.LeaseToken);
        var readPending = await service.ReadAsync(admin, ModelProviderBindingScope.Platform, null, id1, CancellationToken.None);
        Assert.Equal("pending", readPending.Egress!.DeliveryStatus);
        Assert.Null(readPending.Egress.DestinationState);

        // 2. Simulate crashed worker with expired lease and ready next_attempt_at; fresh relay reclaims and delivers.
        var past = TimeProvider.System.GetUtcNow().AddMinutes(-5);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE projects_config.model_provider_host_requests
            SET lease_owner = 'crashed-worker',
                lease_token = {Guid.NewGuid()},
                lease_expires_at = {past},
                next_attempt_at = {past}
            WHERE request_id = {req1.RequestId}
            """);

        fakeEnv.Mode = FakeEnvironmentMode.Deliver;
        var relay2 = new ModelProviderHostRequestRelay(
            fixture.DataSource,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ModelProviderHostRequestRelay>.Instance);
        while (await relay2.RunOnceAsync(CancellationToken.None) == 20)
        {
        }

        var delivered = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == req1.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Delivered, delivered.DeliveryState);
        Assert.NotNull(delivered.EnvironmentDestinationId);

        var readDelivered = await service.ReadAsync(admin, ModelProviderBindingScope.Platform, null, id1, CancellationToken.None);
        Assert.Equal("delivered", readDelivered.Egress!.DeliveryStatus);
        Assert.Equal("requested", readDelivered.Egress.DestinationState);

        // 3. Environment outage on ReadAsync returns saved metadata with unavailable deliveryStatus.
        fakeEnv.Mode = FakeEnvironmentMode.StatusOutage;
        var readOutage = await service.ReadAsync(admin, ModelProviderBindingScope.Platform, null, id1, CancellationToken.None);
        Assert.Equal("Relay1", readOutage.Binding.DisplayName);
        Assert.Equal("unavailable", readOutage.Egress!.DeliveryStatus);
        Assert.Null(readOutage.Egress.DestinationState);

        // 4. Mismatched receipt records terminal Failed state.
        var op2 = Guid.NewGuid();
        var id2 = "byok-" + op2.ToString("N");
        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, id2,
            new SaveModelProviderDraftRequest(op2, 0, "Relay2", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        fakeEnv.Mode = FakeEnvironmentMode.MismatchedReceipt;
        while (await relay2.RunOnceAsync(CancellationToken.None) == 20)
        {
        }
        var failed = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == id2);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Failed, failed.DeliveryState);
        var readFailed = await service.ReadAsync(admin, ModelProviderBindingScope.Platform, null, id2, CancellationToken.None);
        Assert.Equal("failed", readFailed.Egress!.DeliveryStatus);
        Assert.Null(readFailed.Egress.DestinationState);
    }

    [Fact]
    public async Task HostRequestTableEnforcesImmutabilityAndNarrowRuntimeGrants()
    {
        await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(fixture.DataSource, CreateDbContextOptions());

        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var admin = await SeedCallerAsync(
            db, authorityStore, "hr-grant-admin-" + suffix, "hr-grant-tenant-" + suffix,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var op = Guid.NewGuid();
        var id = "byok-" + op.ToString("N");
        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, id,
            new SaveModelProviderDraftRequest(op, 0, "GrantTest", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        var req = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == id);

        // Immutable identity columns cannot be updated.
        var ex1 = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE projects_config.model_provider_host_requests SET host = 'evil.example' WHERE request_id = {req.RequestId}"));
        Assert.Equal("55000", ex1.SqlState);

        // Delete is rejected.
        var ex2 = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM projects_config.model_provider_host_requests WHERE request_id = {req.RequestId}"));
        Assert.Equal("55000", ex2.SqlState);

        // Terminal Delivered state cannot be mutated.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE projects_config.model_provider_host_requests SET delivery_state = 'Delivered', delivered_at = now(), environment_destination_id = {Guid.NewGuid()} WHERE request_id = {req.RequestId}");
        var ex3 = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE projects_config.model_provider_host_requests SET delivery_state = 'Pending' WHERE request_id = {req.RequestId}"));
        Assert.Equal("55000", ex3.SqlState);
    }

    [Fact]
    public async Task HostRequestAuthorityPreservesOwnerFirstCrossTenantTenantAdminDenialAndBothGrantsRevocation()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = "hr-auth-a-" + suffix;
        var tenantB = "hr-auth-b-" + suffix;

        var projectCreator = await SeedCallerAsync(
            db, authorityStore, "hr-ta-creator-" + suffix, tenantA,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantA, ProjectAuthorityRole.TenantAdmin);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var projectA = await projects.CreateProjectAsync(projectCreator, "HR Auth A", CancellationToken.None);

        var tenantAdminA = await SeedCallerAsync(
            db, authorityStore, "hr-ta-a-" + suffix, tenantA,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantA, ProjectAuthorityRole.TenantAdmin);

        var service = new ModelProviderDraftService(db, TimeProvider.System);

        // TenantAdmin-only is denied on both Platform and Project scopes.
        var deniedPlatform = await Assert.ThrowsAsync<ProjectConfigException>(() => service.SaveAsync(
            tenantAdminA, ModelProviderBindingScope.Platform, null, "byok-" + Guid.NewGuid().ToString("N"),
            new SaveModelProviderDraftRequest(Guid.NewGuid(), 0, "TA", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, deniedPlatform.StatusCode);

        var tenantAdminBoundA = await ResolveCallerAsync(
            db, tenantAdminA.ActorId, tenantA, ["api.read", "projects.admin"], boundProjectId: projectA.ProjectId);
        var deniedProject = await Assert.ThrowsAsync<ProjectConfigException>(() => service.SaveAsync(
            tenantAdminBoundA, ModelProviderBindingScope.Project, projectA.ProjectId, "byok-" + Guid.NewGuid().ToString("N"),
            new SaveModelProviderDraftRequest(Guid.NewGuid(), 0, "TA", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, deniedProject.StatusCode);

        // Cross-tenant PlatformAdmin succeeds on projectA and records projectA's tenantId.
        var crossPlatformAdmin = await SeedCallerAsync(
            db, authorityStore, "hr-cross-pa-" + suffix, tenantB,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var crossBound = await ResolveCallerAsync(
            db, crossPlatformAdmin.ActorId, tenantB, ["api.read", "projects.admin"], boundProjectId: projectA.ProjectId);
        var crossOp = Guid.NewGuid();
        var crossProviderId = "byok-" + crossOp.ToString("N");
        await service.SaveAsync(
            crossBound, ModelProviderBindingScope.Project, projectA.ProjectId, crossProviderId,
            new SaveModelProviderDraftRequest(crossOp, 0, "CrossPA", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        var crossReq = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == crossProviderId);
        Assert.Equal(tenantA, crossReq.TenantId);

        // Caller with BOTH Project Owner and PlatformAdmin:
        var dualSubject = "hr-dual-" + suffix;
        var dualMembership = await authorityStore.GrantMembershipAsync(
            TestIssuer, dualSubject, tenantA, "fixture");
        var ownerGrant = await authorityStore.AssignRoleAsync(
            dualMembership.MembershipId, ProjectAuthorityResourceType.Project, projectA.ProjectId,
            ProjectAuthorityRole.Owner, "fixture");
        var paGrant = await authorityStore.AssignRoleAsync(
            dualMembership.MembershipId, ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin, "fixture");
        var dualCaller = await ResolveCallerAsync(
            db, dualSubject, tenantA, ["api.read", "projects.admin"], boundProjectId: projectA.ProjectId);

        // Revoking PlatformAdmin leaves Owner active (owner-first choice succeeds).
        await authorityStore.RevokeRoleAssignmentAsync(paGrant.AssignmentId, paGrant.Revision, "fixture");
        var dualOp1 = Guid.NewGuid();
        var dualProviderId = "byok-" + dualOp1.ToString("N");
        await service.SaveAsync(
            dualCaller, ModelProviderBindingScope.Project, projectA.ProjectId, dualProviderId,
            new SaveModelProviderDraftRequest(dualOp1, 0, "Dual1", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);

        // Re-grant PlatformAdmin, revoke Owner while caller still holds both grants -> selects Owner first,
        // fails lock, and never switches to PlatformAdmin.
        var paGrant2 = await authorityStore.AssignRoleAsync(
            dualMembership.MembershipId, ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin, "fixture");
        var dualCaller2 = await ResolveCallerAsync(
            db, dualSubject, tenantA, ["api.read", "projects.admin"], boundProjectId: projectA.ProjectId);
        await authorityStore.RevokeRoleAssignmentAsync(ownerGrant.AssignmentId, ownerGrant.Revision, "fixture");
        var deniedOwnerRevokedBeforeLock = await Assert.ThrowsAsync<ProjectConfigException>(() => service.SaveAsync(
            dualCaller2, ModelProviderBindingScope.Project, projectA.ProjectId, dualProviderId,
            new SaveModelProviderDraftRequest(Guid.NewGuid(), 1, "Dual2", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, deniedOwnerRevokedBeforeLock.StatusCode);

        // Freshly resolved caller after Owner revocation holds only PlatformAdmin and succeeds.
        var dualCallerPlatformOnly = await ResolveCallerAsync(
            db, dualSubject, tenantA, ["api.read", "projects.admin"], boundProjectId: projectA.ProjectId);
        await service.SaveAsync(
            dualCallerPlatformOnly, ModelProviderBindingScope.Project, projectA.ProjectId, dualProviderId,
            new SaveModelProviderDraftRequest(Guid.NewGuid(), 1, "Dual2", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);

        // Revoking PlatformAdmin too -> both revoked -> 403 Forbidden.
        await authorityStore.RevokeRoleAssignmentAsync(paGrant2.AssignmentId, paGrant2.Revision, "fixture");
        var deniedBoth = await Assert.ThrowsAsync<ProjectConfigException>(() => service.SaveAsync(
            dualCallerPlatformOnly, ModelProviderBindingScope.Project, projectA.ProjectId, dualProviderId,
            new SaveModelProviderDraftRequest(Guid.NewGuid(), 2, "Dual3", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, deniedBoth.StatusCode);
    }

    [Fact]
    public async Task HostRequestRelayRenewsLiveLeaseDuringSlowSendAndHandlesLostLease()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var admin = await SeedCallerAsync(
            db, authorityStore, "hr-lease-admin-" + suffix, "hr-lease-tenant-" + suffix,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

        var fakeEnv = new FakeEnvironmentHostRequestClient();
        var service = new ModelProviderDraftService(db, TimeProvider.System, null, false, fakeEnv);

        // Drain any Pending host requests left by earlier tests in the shared fixture database.
        await using (var drainScopeProvider = BuildRelayServiceProvider(fakeEnv))
        {
            var drainRelay = new ModelProviderHostRequestRelay(
                fixture.DataSource,
                drainScopeProvider.GetRequiredService<IServiceScopeFactory>(),
                TimeProvider.System,
                NullLogger<ModelProviderHostRequestRelay>.Instance);
            while (await drainRelay.RunOnceAsync(CancellationToken.None) > 0)
            {
            }
        }

        var opSlow = Guid.NewGuid();
        var idSlow = "byok-" + opSlow.ToString("N");
        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, idSlow,
            new SaveModelProviderDraftRequest(opSlow, 0, "SlowSend", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        var reqSlow = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == idSlow);

        // 1. Bounded slow send and slow completion (1200ms > 1000ms leaseDuration) succeed because renewal stays active through completion.
        fakeEnv.SendDelay = TimeSpan.FromMilliseconds(400);
        await using var scopeProvider = BuildRelayServiceProvider(fakeEnv);
        var relay = new ModelProviderHostRequestRelay(
            fixture.DataSource,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<ModelProviderHostRequestRelay>.Instance,
            leaseDuration: TimeSpan.FromSeconds(1),
            attemptDeadline: TimeSpan.FromSeconds(5),
            completionDeadline: TimeSpan.FromSeconds(5))
        {
            OnBeforeCompleteAsync = async (claimed, ct) =>
            {
                if (claimed.RequestId == reqSlow.RequestId)
                    await Task.Delay(TimeSpan.FromMilliseconds(800), ct);
            },
        };

        await relay.RunOnceAsync(CancellationToken.None);
        var delivered = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == reqSlow.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Delivered, delivered.DeliveryState);

        // 2. Lost lease during send cancels the send and does NOT mark Delivered.
        var opLost = Guid.NewGuid();
        var idLost = "byok-" + opLost.ToString("N");
        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, idLost,
            new SaveModelProviderDraftRequest(opLost, 0, "LostLease", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        var reqLost = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == idLost);

        fakeEnv.SendDelay = TimeSpan.FromMilliseconds(500);
        fakeEnv.OnSendAsync = async (dispatch, _) =>
        {
            if (dispatch.RequestId == reqLost.RequestId)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE projects_config.model_provider_host_requests
                    SET lease_token = NULL, lease_owner = NULL, lease_expires_at = NULL, next_attempt_at = now()
                    WHERE request_id = {reqLost.RequestId}
                    """);
            }
        };

        await relay.RunOnceAsync(CancellationToken.None);
        var afterLost = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == reqLost.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Pending, afterLost.DeliveryState);

        // Next relay pass reclaims and delivers cleanly.
        fakeEnv.SendDelay = TimeSpan.Zero;
        fakeEnv.OnSendAsync = null;
        await relay.RunOnceAsync(CancellationToken.None);
        var afterRecover = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == reqLost.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Delivered, afterRecover.DeliveryState);

        // 3. Expired lease in Postgres without another worker rejects stale acknowledgements and allows reclaim.
        var opStale = Guid.NewGuid();
        var idStale = "byok-" + opStale.ToString("N");
        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, idStale,
            new SaveModelProviderDraftRequest(opStale, 0, "StaleAck", "openai", "https://api.openai.com/v1", "gpt-5"),
            CancellationToken.None);
        var reqStale = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.ProviderId == idStale);

        var claimedStale = await relay.ClaimNextAsync(CancellationToken.None);
        Assert.NotNull(claimedStale);
        Assert.Equal(reqStale.RequestId, claimedStale!.RequestId);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE projects_config.model_provider_host_requests SET lease_expires_at = clock_timestamp() - interval '1 second' WHERE request_id = {reqStale.RequestId}");

        Assert.False(await relay.MarkDeliveredAsync(claimedStale, Guid.NewGuid(), CancellationToken.None));
        Assert.False(await relay.MarkFailedAsync(claimedStale, "stale_failure", CancellationToken.None));
        Assert.False(await relay.ScheduleRetryAsync(claimedStale, "stale_retry", CancellationToken.None));

        var stillPending = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == reqStale.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Pending, stillPending.DeliveryState);
        Assert.Equal(claimedStale.LeaseToken, stillPending.LeaseToken);

        await relay.RunOnceAsync(CancellationToken.None);
        var deliveredAfterExpiry = await db.ModelProviderHostRequests.AsNoTracking().SingleAsync(r => r.RequestId == reqStale.RequestId);
        Assert.Equal(ModelProviderHostRequestDeliveryState.Delivered, deliveredAfterExpiry.DeliveryState);
    }

    [Fact]
    public async Task HostRequestChannelConfiguredStartupPassesScopeValidation()
    {
        using var ca = CreateTestCertificate("Test Backend CA", isCa: true);
        using var projectsCert = CreateTestCertificate("projects-config.agentweaver.svc.cluster.local", isCa: false, ca);
        using var envCert = CreateTestCertificate("environment.agentweaver.svc.cluster.local", isCa: false, ca);

        var tempDir = Directory.CreateTempSubdirectory("aw-hostreq-tls-");
        try
        {
            var projCertPath = Path.Combine(tempDir.FullName, "proj.crt");
            var projKeyPath = Path.Combine(tempDir.FullName, "proj.key");
            File.WriteAllText(projCertPath, projectsCert.ExportCertificatePem());
            File.WriteAllText(projKeyPath, projectsCert.GetRSAPrivateKey()!.ExportPkcs8PrivateKeyPem());

            var projPin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(projectsCert.RawData)).ToLowerInvariant();
            var envPin = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(envCert.RawData)).ToLowerInvariant();

            var builder = WebApplication.CreateBuilder();
            builder.Host.UseDefaultServiceProvider(options =>
            {
                options.ValidateScopes = true;
                options.ValidateOnBuild = true;
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kestrel:Endpoints:ProviderHostRequest:Url"] = "https://127.0.0.1:18445",
                [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalServiceName"] = "Projects",
                [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalServicePrincipalId"] = Guid.NewGuid().ToString("D"),
                [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalCertificatePemPath"] = projCertPath,
                [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalPrivateKeyPemPath"] = projKeyPath,
                [$"{HttpEnvironmentHostRequestClient.SectionName}:LocalCertificateSha256"] = projPin,
                [$"{HttpEnvironmentHostRequestClient.SectionName}:ListenerPort"] = "18445",
                [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerServiceName"] = "Environment",
                [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerServicePrincipalId"] = Guid.NewGuid().ToString("D"),
                [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerOwnerAddress"] = "https://127.0.0.1:18446/",
                [$"{HttpEnvironmentHostRequestClient.SectionName}:PeerCertificateSha256Pins:0"] = envPin,
            });
            builder.Services.AddSingleton(fixture.DataSource);
            builder.Services.AddSingleton(TimeProvider.System);
            Assert.True(IssueSyncServiceTransport.AddChannel(
                builder,
                HttpEnvironmentHostRequestClient.ChannelKey,
                HttpEnvironmentHostRequestClient.SectionName,
                "Projects",
                "Environment",
                listener: true));
            builder.Services.AddScoped<IEnvironmentHostRequestClient>(services => new HttpEnvironmentHostRequestClient(
                services.GetRequiredService<IHttpClientFactory>(),
                services.GetRequiredKeyedService<RuntimeServiceTlsIdentity>(
                    HttpEnvironmentHostRequestClient.ChannelKey)));
            builder.Services.AddHostedService<ModelProviderHostRequestRelay>();

            await using var app = builder.Build();
            Assert.NotNull(app.Services.GetRequiredService<IHostedService>());
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private static ServiceProvider BuildRelayServiceProvider(IEnvironmentHostRequestClient client)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => client);
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static System.Security.Cryptography.X509Certificates.X509Certificate2 CreateTestCertificate(
        string commonName,
        bool isCa,
        System.Security.Cryptography.X509Certificates.X509Certificate2? issuer = null)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            $"CN={commonName}",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension(isCa, false, 0, true));
        if (!isCa)
        {
            var eku = new System.Security.Cryptography.OidCollection
            {
                new("1.3.6.1.5.5.7.3.1"),
                new("1.3.6.1.5.5.7.3.2"),
            };
            req.CertificateExtensions.Add(
                new System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension(eku, false));
        }
        var now = DateTimeOffset.UtcNow;
        if (isCa || issuer is null)
            return req.CreateSelfSigned(now.AddMinutes(-5), now.AddDays(2));
        var serial = System.Security.Cryptography.RandomNumberGenerator.GetBytes(8);
        using var issued = req.Create(issuer, now.AddMinutes(-5), now.AddDays(1), serial);
        return System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(
            issued.ExportCertificatePem(),
            rsa.ExportPkcs8PrivateKeyPem());
    }

    private enum FakeEnvironmentMode
    {
        Deliver,
        Transient,
        MismatchedReceipt,
        StatusOutage,
    }

    private sealed class FakeEnvironmentHostRequestClient : IEnvironmentHostRequestClient
    {
        public FakeEnvironmentMode Mode { get; set; } = FakeEnvironmentMode.Deliver;
        public TimeSpan SendDelay { get; set; } = TimeSpan.Zero;
        public Func<ProviderHostRequestDispatch, CancellationToken, Task>? OnSendAsync { get; set; }
        private readonly Dictionary<Guid, ProviderHostRequestStatusItem> statuses = new();

        public async Task<EnvironmentHostRequestSendResult> SendHostRequestAsync(
            ProviderHostRequestDispatch dispatch,
            CancellationToken cancellationToken)
        {
            if (OnSendAsync is not null)
                await OnSendAsync(dispatch, cancellationToken).ConfigureAwait(false);
            if (SendDelay > TimeSpan.Zero)
                await Task.Delay(SendDelay, cancellationToken).ConfigureAwait(false);

            if (Mode == FakeEnvironmentMode.Transient)
                return new EnvironmentHostRequestSendResult(
                    EnvironmentHostRequestOutcome.TransientFailure, FailureCode: "environment_unavailable");

            if (Mode == FakeEnvironmentMode.MismatchedReceipt)
            {
                var mismatched = new ProviderHostRequestReceipt(
                    dispatch.RequestId, Guid.NewGuid(), dispatch.TenantId, dispatch.Scope, dispatch.ProjectId,
                    dispatch.ProviderId, dispatch.ProviderRevision, dispatch.ConfigurationHash, dispatch.OperationId,
                    dispatch.ActorId, "other.example", dispatch.Port, PlatformEgressDestinationState.Requested);
                return new EnvironmentHostRequestSendResult(
                    EnvironmentHostRequestOutcome.Delivered, Receipt: mismatched);
            }

            var destinationId = Guid.NewGuid();
            statuses[dispatch.RequestId] = new ProviderHostRequestStatusItem(
                dispatch.RequestId, destinationId, dispatch.Host, dispatch.Port, PlatformEgressDestinationState.Requested);
            var receipt = new ProviderHostRequestReceipt(
                dispatch.RequestId, destinationId, dispatch.TenantId, dispatch.Scope, dispatch.ProjectId,
                dispatch.ProviderId, dispatch.ProviderRevision, dispatch.ConfigurationHash, dispatch.OperationId,
                dispatch.ActorId, dispatch.Host, dispatch.Port, PlatformEgressDestinationState.Requested);
            return new EnvironmentHostRequestSendResult(
                EnvironmentHostRequestOutcome.Delivered, Receipt: receipt);
        }

        public Task<IReadOnlyDictionary<Guid, ProviderHostRequestStatusItem>?> GetStatusesAsync(
            IReadOnlyCollection<Guid> requestIds,
            CancellationToken cancellationToken)
        {
            if (Mode == FakeEnvironmentMode.StatusOutage)
                return Task.FromResult<IReadOnlyDictionary<Guid, ProviderHostRequestStatusItem>?>(null);
            IReadOnlyDictionary<Guid, ProviderHostRequestStatusItem> result = requestIds
                .Where(statuses.ContainsKey)
                .ToDictionary(id => id, id => statuses[id]);
            return Task.FromResult<IReadOnlyDictionary<Guid, ProviderHostRequestStatusItem>?>(result);
        }
    }
}
