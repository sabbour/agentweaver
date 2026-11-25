using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task NativeAcsPolicyPreparationPersistsReplaysAndLeavesCanonicalStateUnchanged()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-preparation-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-preparation-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var request = PlatformPreparationRequest(
            current, Artifact("pending preparation " + suffix), "acs-preparation-" + suffix);
        var canonicalBefore = await CapturePlatformCanonicalStateAsync();

        var prepared = await store.PrepareAsync(caller, request, CancellationToken.None);
        var replay = await CreateNativeAcsStore().PrepareAsync(caller, request, CancellationToken.None);

        Assert.Equal("pending", prepared.State);
        Assert.Null(prepared.Checkpoint);
        Assert.Equal(current?.PolicyId, prepared.ExpectedHeadPolicyId);
        Assert.Equal(current?.Revision, prepared.ExpectedHeadRevision);
        Assert.Equal(current?.ContentSha256, prepared.ExpectedHeadContentSha256);
        Assert.Equal(request.Artifact.ContentSha256, prepared.ProposedContentSha256);
        Assert.False(prepared.Replayed);
        Assert.Equal(prepared with { Replayed = true }, replay);
        var conflict = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            CreateNativeAcsStore().PrepareAsync(
                caller, request with { Reason = "A different edit" }, CancellationToken.None));
        Assert.Equal("idempotency_conflict", conflict.Code);
        Assert.Equal(canonicalBefore, await CapturePlatformCanonicalStateAsync());

        await using var verify = CreateDbContext();
        var record = await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .SingleAsync(item => item.PreparationId == prepared.PreparationId);
        Assert.Equal(request.Artifact.ContentSha256, record.ProposedContentSha256);
        using var artifact = JsonDocument.Parse(record.ArtifactJson);
        Assert.Equal(
            request.Artifact.ContentSha256,
            artifact.RootElement.GetProperty("contentSha256").GetString());
        await AssertImmutableAsync(
            "UPDATE projects_config.native_acs_policy_preparations SET reason = 'changed' " +
            "WHERE preparation_id = @id",
            ("id", prepared.PreparationId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.native_acs_policy_preparations WHERE preparation_id = @id",
            ("id", prepared.PreparationId));
    }

    [Fact]
    public async Task NativeAcsPolicyPreparationReplayIsReturnedBeforeTheWriteGateAndValidator()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var subject = "acs-preparation-api-owner-" + suffix;
        var tenantId = "acs-preparation-api-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, subject, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var current = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var artifact = Artifact("API preparation replay " + suffix);
        var request = PlatformPreparationRequest(
            current, artifact, "acs-preparation-api-" + suffix);
        var preparation = await CreateNativeAcsStore().PrepareAsync(caller, request, CancellationToken.None);
        var canonicalBefore = await CapturePlatformCanonicalStateAsync();

        using var host = await StartNativeAcsPolicyEndpointHostAsync(fixture.DataSource, opaPath: null);
        using var client = CreatePlatformPolicyWriter(host, subject, tenantId);
        using var response = await client.PutAsJsonAsync("/api/policies/platform/", new NativeAcsPolicyWriteBody
        {
            ExpectedRevision = request.ExpectedRevision,
            Artifact = artifact,
            IdempotencyKey = request.IdempotencyKey,
            Reason = request.Reason,
            CorrelationId = request.CorrelationId,
        });

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(preparation.PreparationId, document.RootElement.GetProperty("preparationId").GetGuid());
        Assert.Equal("pending", document.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("checkpoint").ValueKind);
        Assert.True(document.RootElement.GetProperty("replayed").GetBoolean());
        Assert.Equal(canonicalBefore, await CapturePlatformCanonicalStateAsync());
    }

    [Fact]
    public async Task ConcurrentNativeAcsPolicyPreparationRequestsCreateOneRecord()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-preparation-race-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-preparation-race-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var current = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var request = PlatformPreparationRequest(
            current, Artifact("concurrent preparation " + suffix), "acs-preparation-race-" + suffix);

        async Task<NativeAcsPolicyPreparationReceipt?> TryPrepareAsync()
        {
            try
            {
                return await CreateNativeAcsStore().PrepareAsync(caller, request, CancellationToken.None);
            }
            catch (ProjectConfigException)
            {
                return null;
            }
        }

        var receipts = (await Task.WhenAll(TryPrepareAsync(), TryPrepareAsync()))
            .Where(item => item is not null)
            .Cast<NativeAcsPolicyPreparationReceipt>()
            .ToArray();
        Assert.NotEmpty(receipts);
        var replay = await CreateNativeAcsStore().PrepareAsync(caller, request, CancellationToken.None);
        Assert.All(receipts, receipt => Assert.Equal(replay.PreparationId, receipt.PreparationId));
        Assert.True(replay.Replayed);

        await using var verify = CreateDbContext();
        Assert.Single(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .Where(item => item.IdempotencyKey == request.IdempotencyKey)
            .ToArrayAsync());
    }

    [Fact]
    public async Task NativeAcsPolicyPreparationRejectsStaleHeadsAndRevokedAuthority()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-preparation-stale-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-preparation-stale-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var staleRequest = PlatformPreparationRequest(
            current, Artifact("stale preparation " + suffix), "acs-preparation-stale-" + suffix);
        var staged = await store.PrepareAsync(caller, staleRequest, CancellationToken.None);
        await store.WriteAsync(
            caller,
            new NativeAcsPolicyWriteRequest(
                NativeAcsPolicyScopeType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                current?.PolicyId ?? "canonical",
                current?.Revision,
                Artifact("advance canonical head " + suffix),
                "acs-preparation-advance-" + suffix,
                "Advance head",
                "acs-preparation-advance-correlation-" + suffix),
            CancellationToken.None);

        var stateBeforeStaleRetry = await CapturePlatformCanonicalStateAsync();
        var stale = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            CreateNativeAcsStore().PrepareAsync(caller, staleRequest, CancellationToken.None));
        Assert.Equal("revision_conflict", stale.Code);
        Assert.Equal(stateBeforeStaleRetry, await CapturePlatformCanonicalStateAsync());

        var revokedTenantId = "acs-preparation-revoked-tenant-" + suffix;
        var revokedCaller = await SeedCallerAsync(
            db, authorityStore, "acs-preparation-revoked-owner-" + suffix, revokedTenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var afterAdvance = await CreateNativeAcsStore().ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var revokedRequest = PlatformPreparationRequest(
            afterAdvance, Artifact("revoked preparation " + suffix), "acs-preparation-revoked-" + suffix);
        var revokedPreparation = await CreateNativeAcsStore().PrepareAsync(
            revokedCaller, revokedRequest, CancellationToken.None);
        var grant = revokedCaller.Grants.Single(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.Role == ProjectAuthorityRole.PlatformAdmin);
        await authorityStore.RevokeRoleAssignmentAsync(
            grant.AssignmentId, grant.Revision, "fixture", CancellationToken.None);

        var stateBeforeDeniedRetry = await CapturePlatformCanonicalStateAsync();
        var denied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            CreateNativeAcsStore().PrepareAsync(revokedCaller, revokedRequest, CancellationToken.None));
        Assert.Equal("forbidden", denied.Code);
        Assert.Equal(stateBeforeDeniedRetry, await CapturePlatformCanonicalStateAsync());
        await using var verify = CreateDbContext();
        Assert.True(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .AnyAsync(item => item.PreparationId == staged.PreparationId));
        Assert.True(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .AnyAsync(item => item.PreparationId == revokedPreparation.PreparationId));
    }

    [Fact]
    public async Task NativeAcsPolicyPreparationRejectsCorruptOversizedProjectAndFailedWrites()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-preparation-invalid-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-preparation-invalid-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var canonicalBefore = await CapturePlatformCanonicalStateAsync();

        var corruptRequest = PlatformPreparationRequest(
            current,
            Artifact("corrupt preparation " + suffix) with { ContentSha256 = new string('0', 64) },
            "acs-preparation-corrupt-" + suffix);
        var corrupt = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.PrepareAsync(caller, corruptRequest, CancellationToken.None));
        Assert.Equal("native_acs_policy_invalid", corrupt.Code);

        var largeBytes = ImmutableArray.CreateRange(new byte[262_145]);
        var largeDraft = new NativeAcsArtifact(
            ImmutableArray.CreateRange(new byte[] { 1 }),
            [new NativeAcsArtifactFile("policies/main.rego", largeBytes)],
            string.Empty);
        var oversized = largeDraft with { ContentSha256 = NativeAcsArtifactHash.Compute(largeDraft) };
        var oversizedFailure = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.PrepareAsync(
                caller,
                PlatformPreparationRequest(
                    current, oversized, "acs-preparation-oversized-" + suffix),
                CancellationToken.None));
        Assert.Equal("native_acs_policy_invalid", oversizedFailure.Code);

        var projectRequest = Request(
            "project-scope-" + suffix[..12],
            Artifact("project preparation " + suffix),
            null,
            "acs-preparation-project-" + suffix);
        var projectFailure = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.PrepareAsync(caller, projectRequest, CancellationToken.None));
        Assert.Equal("native_acs_policy_invalid", projectFailure.Code);

        const string testConstraint = "ck_native_acs_policy_preparations_test_reject";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var addConstraint = new NpgsqlCommand(
            $"""
            ALTER TABLE projects_config.native_acs_policy_preparations
            ADD CONSTRAINT {testConstraint} CHECK (reason <> 'reject transaction')
            """,
            connection))
            await addConstraint.ExecuteNonQueryAsync();
        try
        {
            var rollbackRequest = PlatformPreparationRequest(
                current, Artifact("rollback preparation " + suffix), "acs-preparation-rollback-" + suffix)
                with { Reason = "reject transaction" };
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                store.PrepareAsync(caller, rollbackRequest, CancellationToken.None));
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var dropConstraint = new NpgsqlCommand(
                $"""
                ALTER TABLE projects_config.native_acs_policy_preparations
                DROP CONSTRAINT {testConstraint}
                """,
                connection);
            await dropConstraint.ExecuteNonQueryAsync();
        }

        Assert.Equal(canonicalBefore, await CapturePlatformCanonicalStateAsync());
        await using var verify = CreateDbContext();
        Assert.False(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .AnyAsync(item => item.IdempotencyKey == corruptRequest.IdempotencyKey));
        Assert.False(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .AnyAsync(item => item.IdempotencyKey == "acs-preparation-oversized-" + suffix));
        Assert.False(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .AnyAsync(item => item.IdempotencyKey == "acs-preparation-project-" + suffix));
        Assert.False(await verify.NativeAcsPolicyPreparations.AsNoTracking()
            .AnyAsync(item => item.IdempotencyKey == "acs-preparation-rollback-" + suffix));
        var rollbackRequestForDigest = PlatformPreparationRequest(
            current, Artifact("rollback preparation " + suffix), "acs-preparation-rollback-" + suffix)
            with { Reason = "reject transaction" };
        Assert.False(await verify.GovernanceDecisions.AsNoTracking()
            .AnyAsync(item => item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.ResourceType == "platform_policy" &&
                item.ResourceId == (current != null ? current.PolicyId : "canonical") &&
                item.IdempotencyKey == NativeAcsPolicyStore.ComputeScopeDigest(caller, rollbackRequestForDigest)));
    }

    [Fact]
    public async Task NativeAcsPolicyPreparationAppendsGovernanceDecisionAtomicallyWithSecrecyAndReplay()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-prep-gov-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-prep-gov-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var policyId = current?.PolicyId ?? "canonical";
        var secretMarker = "secret-rego-marker-" + suffix;
        var secretReason = "sensitive-reason-" + suffix;
        var secretIdempotencyKey = "idem-key-" + suffix;
        var request = PlatformPreparationRequest(
            current, Artifact(secretMarker), secretIdempotencyKey) with
        {
            Reason = secretReason,
            CorrelationId = "corr-" + suffix,
        };

        var beforeCount = await db.GovernanceDecisions.AsNoTracking()
            .CountAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.ResourceType == "platform_policy" &&
                item.ResourceId == policyId);

        var prepared = await store.PrepareAsync(caller, request, CancellationToken.None);
        var replay = await CreateNativeAcsStore().PrepareAsync(caller, request, CancellationToken.None);

        Assert.NotNull(prepared.GovernanceDecisionId);
        Assert.NotNull(prepared.GovernanceDecisionHash);
        Assert.Equal(prepared with { Replayed = true }, replay);

        await using var verify = CreateDbContext();
        var decisions = await verify.GovernanceDecisions.AsNoTracking()
            .Where(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.ResourceType == "platform_policy" &&
                item.ResourceId == policyId)
            .OrderBy(item => item.Sequence)
            .ToArrayAsync();
        Assert.Equal(beforeCount + 1, decisions.Length);

        var decisionRow = Assert.Single(decisions, item => item.DecisionId == prepared.GovernanceDecisionId);
        Assert.Equal(prepared.GovernanceDecisionHash, decisionRow.RecordHash);
        var outboxRow = await verify.GovernanceDecisionOutbox.AsNoTracking()
            .SingleAsync(item => item.DecisionId == decisionRow.DecisionId);
        Assert.Equal(decisionRow.RecordHash, outboxRow.RecordHash);
        var streamHead = await verify.GovernanceDecisionStreamHeads.AsNoTracking()
            .SingleAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.ResourceType == "platform_policy" &&
                item.ResourceId == policyId);
        Assert.Equal(decisionRow.Sequence, streamHead.Sequence);
        Assert.Equal(decisionRow.RecordHash, streamHead.HeadHash);

        Assert.True(ProjectGovernanceDecisionCanonicalJson.TryDeserializeRecord(
            decisionRow.CanonicalRecord, out var canonical));
        Assert.NotNull(canonical);
        Assert.Equal("platform.policy.preparation.v1", canonical.Decision.DecisionType);
        Assert.Equal(ProjectGovernanceOutcome.Pending, canonical.Decision.Outcome);
        Assert.Equal(ProjectGovernanceApprovalState.Pending, canonical.Decision.ApprovalState);
        Assert.Equal(current?.Revision, canonical.Decision.PolicyRevision);
        Assert.Equal(request.Artifact.ContentSha256, canonical.Decision.PolicyArtifactSha256);
        Assert.Null(canonical.Decision.HumanEntraOid);
        Assert.Null(canonical.Decision.TenantId);
        Assert.Equal(caller.Issuer, canonical.Decision.BrokerIssuer);
        Assert.Equal(caller.ActorId, canonical.Decision.BrokerSubject);
        Assert.Equal(prepared.PreparationId.ToString("N"), canonical.Decision.OwnerReceiptId);
        Assert.Equal(request.CorrelationId, canonical.Decision.CorrelationId);
        var grant = caller.Grants.Single(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.Role == ProjectAuthorityRole.PlatformAdmin);
        var recordedRole = Assert.Single(canonical.Decision.EffectiveRoleRevisions);
        Assert.Equal(grant.AssignmentId, recordedRole.AssignmentId);
        Assert.Equal(grant.Revision, recordedRole.Revision);

        var canonicalText = System.Text.Encoding.UTF8.GetString(decisionRow.CanonicalRecord);
        Assert.DoesNotContain(secretMarker, canonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretReason, canonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(request.IdempotencyKey, canonicalText, StringComparison.Ordinal);
        Assert.DoesNotContain(secretMarker, outboxRow.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretReason, outboxRow.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain(request.IdempotencyKey, outboxRow.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeAcsPolicyPreparationRollsBackDecisionOnDeadlockOrRevokedAuthority()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-prep-rollback-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-prep-rollback-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var policyId = current?.PolicyId ?? "canonical";

        var beforePreparations = await db.NativeAcsPolicyPreparations.AsNoTracking().CountAsync();
        var beforeDecisions = await db.GovernanceDecisions.AsNoTracking().CountAsync();
        var beforeOutbox = await db.GovernanceDecisionOutbox.AsNoTracking().CountAsync();
        var beforeHead = await db.GovernanceDecisionStreamHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.ResourceType == "platform_policy" &&
                item.ResourceId == policyId);

        const string triggerName = "native_acs_policy_preparations_test_deadlock";
        const string functionName = "projects_config.reject_preparation_test_deadlock()";
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var createTrigger = new NpgsqlCommand(
            $"""
            CREATE OR REPLACE FUNCTION {functionName}
            RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.reason = 'trigger-deadlock-{suffix}' THEN
                    RAISE EXCEPTION 'injected deadlock' USING ERRCODE = '40P01';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER {triggerName}
            BEFORE INSERT ON projects_config.native_acs_policy_preparations
            FOR EACH ROW EXECUTE FUNCTION {functionName};
            """,
            connection))
        {
            await createTrigger.ExecuteNonQueryAsync();
        }

        try
        {
            var deadlockRequest = PlatformPreparationRequest(
                current, Artifact("deadlock preparation " + suffix), "acs-prep-deadlock-" + suffix) with
            {
                Reason = $"trigger-deadlock-{suffix}",
            };
            var conflict = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                store.PrepareAsync(caller, deadlockRequest, CancellationToken.None));
            Assert.Equal(Microsoft.AspNetCore.Http.StatusCodes.Status409Conflict, conflict.StatusCode);
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var dropTrigger = new NpgsqlCommand(
                $"""
                DROP TRIGGER IF EXISTS {triggerName} ON projects_config.native_acs_policy_preparations;
                DROP FUNCTION IF EXISTS {functionName};
                """,
                connection);
            await dropTrigger.ExecuteNonQueryAsync();
        }

        var grant = caller.Grants.Single(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.Role == ProjectAuthorityRole.PlatformAdmin);
        await authorityStore.RevokeRoleAssignmentAsync(
            grant.AssignmentId, grant.Revision, "fixture-revoke", CancellationToken.None);

        var revokedRequest = PlatformPreparationRequest(
            current, Artifact("revoked write " + suffix), "acs-prep-revoked-" + suffix);
        var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            store.PrepareAsync(caller, revokedRequest, CancellationToken.None));
        Assert.Equal("forbidden", forbidden.Code);

        await using var verify = CreateDbContext();
        Assert.Equal(beforePreparations, await verify.NativeAcsPolicyPreparations.AsNoTracking().CountAsync());
        Assert.Equal(beforeDecisions, await verify.GovernanceDecisions.AsNoTracking().CountAsync());
        Assert.Equal(beforeOutbox, await verify.GovernanceDecisionOutbox.AsNoTracking().CountAsync());
        var afterHead = await verify.GovernanceDecisionStreamHeads.AsNoTracking()
            .SingleOrDefaultAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId &&
                item.ResourceType == "platform_policy" &&
                item.ResourceId == policyId);
        Assert.Equal(beforeHead?.Sequence, afterHead?.Sequence);
        Assert.Equal(beforeHead?.HeadHash, afterHead?.HeadHash);
    }

    [Fact]
    public async Task NativeAcsPolicyPreparationAllowsPreexistingNullLinkRowsAndIsolatesCrossCallerKeys()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantA = "acs-prep-caller-a-tenant-" + suffix;
        var tenantB = "acs-prep-caller-b-tenant-" + suffix;
        var callerA = await SeedCallerAsync(
            db, authorityStore, "acs-prep-caller-a-" + suffix, tenantA,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var callerB = await SeedCallerAsync(
            db, authorityStore, "acs-prep-caller-b-" + suffix, tenantB,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var store = CreateNativeAcsStore();
        var current = await store.ReadAsync(
            NativeAcsPolicyScopeType.Platform, ProjectAuthorizationOwner.PlatformResourceId, CancellationToken.None);
        var policyId = current?.PolicyId ?? "canonical";

        var sharedIdempotencyKey = "acs-prep-shared-" + suffix;
        var requestA = PlatformPreparationRequest(
            current, Artifact("caller A artifact " + suffix), sharedIdempotencyKey);
        var requestB = PlatformPreparationRequest(
            current, Artifact("caller B artifact " + suffix), sharedIdempotencyKey);

        var receiptA = await store.PrepareAsync(callerA, requestA, CancellationToken.None);
        var receiptB = await store.PrepareAsync(callerB, requestB, CancellationToken.None);

        Assert.NotEqual(receiptA.PreparationId, receiptB.PreparationId);
        Assert.NotEqual(receiptA.GovernanceDecisionId, receiptB.GovernanceDecisionId);
        Assert.NotEqual(receiptA.GovernanceDecisionHash, receiptB.GovernanceDecisionHash);

        var legacyKey = "acs-prep-legacy-" + suffix;
        var legacyRequest = PlatformPreparationRequest(
            current, Artifact("legacy null link " + suffix), legacyKey);
        var legacyScopeDigest = NativeAcsPolicyStore.ComputeScopeDigest(callerA, legacyRequest);
        var legacyRequestDigest = NativeAcsPolicyStore.ComputeRequestDigest(legacyRequest);
        var legacyId = Guid.NewGuid();
        var legacyGrant = callerA.Grants.Single(item =>
            item.ResourceType == ProjectAuthorityResourceType.Platform &&
            item.Role == ProjectAuthorityRole.PlatformAdmin);

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        {
            await using var insertLegacy = new NpgsqlCommand(
                """
                INSERT INTO projects_config.native_acs_policy_preparations
                    (preparation_id, scope_digest, scope_type, scope_id, policy_id,
                     actor_issuer, actor_id, membership_id, membership_revision,
                     role_assignment_id, role_assignment_revision,
                     idempotency_key, request_digest,
                     expected_head_policy_id, expected_head_revision, expected_head_content_sha256,
                     proposed_content_sha256, artifact, reason, correlation_id,
                     governance_decision_id, governance_decision_hash, created_at)
                VALUES
                    (@preparation_id, @scope_digest, 'Platform', 'default', @policy_id,
                     @actor_issuer, @actor_id, @membership_id, @membership_revision,
                     @role_assignment_id, @role_assignment_revision,
                     @idempotency_key, @request_digest,
                     @expected_head_policy_id, @expected_head_revision, @expected_head_content_sha256,
                     @proposed_content_sha256, @artifact::jsonb, @reason, @correlation_id,
                     NULL, NULL, @created_at)
                """,
                connection);
            insertLegacy.Parameters.AddWithValue("preparation_id", legacyId);
            insertLegacy.Parameters.AddWithValue("scope_digest", legacyScopeDigest);
            insertLegacy.Parameters.AddWithValue("policy_id", policyId);
            insertLegacy.Parameters.AddWithValue("actor_issuer", callerA.Issuer);
            insertLegacy.Parameters.AddWithValue("actor_id", callerA.ActorId);
            insertLegacy.Parameters.AddWithValue("membership_id", callerA.MembershipId);
            insertLegacy.Parameters.AddWithValue("membership_revision", callerA.MembershipRevision);
            insertLegacy.Parameters.AddWithValue("role_assignment_id", legacyGrant.AssignmentId);
            insertLegacy.Parameters.AddWithValue("role_assignment_revision", legacyGrant.Revision);
            insertLegacy.Parameters.AddWithValue("idempotency_key", legacyKey);
            insertLegacy.Parameters.AddWithValue("request_digest", legacyRequestDigest);
            insertLegacy.Parameters.AddWithValue(
                "expected_head_policy_id", (object?)current?.PolicyId ?? DBNull.Value);
            insertLegacy.Parameters.AddWithValue(
                "expected_head_revision", (object?)current?.Revision ?? DBNull.Value);
            insertLegacy.Parameters.AddWithValue(
                "expected_head_content_sha256", (object?)current?.ContentSha256 ?? DBNull.Value);
            insertLegacy.Parameters.AddWithValue("proposed_content_sha256", legacyRequest.Artifact.ContentSha256);
            insertLegacy.Parameters.AddWithValue("artifact", JsonSerializer.Serialize(
                legacyRequest.Artifact, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            insertLegacy.Parameters.AddWithValue("reason", legacyRequest.Reason);
            insertLegacy.Parameters.AddWithValue("correlation_id", legacyRequest.CorrelationId);
            insertLegacy.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
            await insertLegacy.ExecuteNonQueryAsync();

            await using var halfNull = new NpgsqlCommand(
                """
                INSERT INTO projects_config.native_acs_policy_preparations
                    (preparation_id, scope_digest, scope_type, scope_id, policy_id,
                     actor_issuer, actor_id, membership_id, membership_revision,
                     role_assignment_id, role_assignment_revision,
                     idempotency_key, request_digest,
                     proposed_content_sha256, artifact, reason, correlation_id,
                     governance_decision_id, governance_decision_hash, created_at)
                VALUES
                    (@preparation_id, @scope_digest, 'Platform', 'default', @policy_id,
                     @actor_issuer, @actor_id, @membership_id, @membership_revision,
                     @role_assignment_id, @role_assignment_revision,
                     @idempotency_key, @request_digest,
                     @proposed_content_sha256, '{}'::jsonb, 'half null', 'half-null-corr',
                     NULL, @bad_hash, @created_at)
                """,
                connection);
            halfNull.Parameters.AddWithValue("preparation_id", Guid.NewGuid());
            halfNull.Parameters.AddWithValue("scope_digest", new string('a', 64));
            halfNull.Parameters.AddWithValue("policy_id", policyId);
            halfNull.Parameters.AddWithValue("actor_issuer", callerA.Issuer);
            halfNull.Parameters.AddWithValue("actor_id", callerA.ActorId);
            halfNull.Parameters.AddWithValue("membership_id", callerA.MembershipId);
            halfNull.Parameters.AddWithValue("membership_revision", callerA.MembershipRevision);
            halfNull.Parameters.AddWithValue("role_assignment_id", legacyGrant.AssignmentId);
            halfNull.Parameters.AddWithValue("role_assignment_revision", legacyGrant.Revision);
            halfNull.Parameters.AddWithValue("idempotency_key", "half-null-" + suffix);
            halfNull.Parameters.AddWithValue("request_digest", new string('b', 64));
            halfNull.Parameters.AddWithValue("proposed_content_sha256", new string('c', 64));
            halfNull.Parameters.AddWithValue("bad_hash", new string('d', 64));
            halfNull.Parameters.AddWithValue("created_at", DateTimeOffset.UtcNow);
            var pairRejected = await Assert.ThrowsAsync<PostgresException>(() => halfNull.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, pairRejected.SqlState);
        }

        var decisionsBeforeLegacyReplay = await db.GovernanceDecisions.AsNoTracking().CountAsync();
        var legacyReplay = await store.PrepareAsync(callerA, legacyRequest, CancellationToken.None);
        Assert.Equal(legacyId, legacyReplay.PreparationId);
        Assert.True(legacyReplay.Replayed);
        Assert.Null(legacyReplay.GovernanceDecisionId);
        Assert.Null(legacyReplay.GovernanceDecisionHash);
        Assert.Equal(decisionsBeforeLegacyReplay, await db.GovernanceDecisions.AsNoTracking().CountAsync());
    }

    private static NativeAcsPolicyWriteRequest PlatformPreparationRequest(
        NativeAcsPolicyReadResult? current,
        NativeAcsArtifact artifact,
        string idempotencyKey) =>
        new(
            NativeAcsPolicyScopeType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId,
            current?.PolicyId ?? "canonical",
            current?.Revision,
            artifact,
            idempotencyKey,
            "Prepare policy update",
            idempotencyKey + "-correlation");

    private async Task<(int Heads, int Revisions, int Idempotency, int Audit, int Outbox, long? Revision)>
        CapturePlatformCanonicalStateAsync()
    {
        await using var db = CreateDbContext();
        return (
            await db.NativeAcsPolicyHeads.AsNoTracking().CountAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId),
            await db.NativeAcsPolicyRevisions.AsNoTracking().CountAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId),
            await db.NativeAcsPolicyIdempotency.AsNoTracking().CountAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId),
            await db.NativeAcsPolicyChangeAudit.AsNoTracking().CountAsync(item =>
                item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId),
            await db.NativeAcsPolicyOutbox.AsNoTracking().CountAsync(),
            await db.NativeAcsPolicyHeads.AsNoTracking()
                .Where(item => item.ScopeType == NativeAcsPolicyScopeType.Platform &&
                    item.ScopeId == ProjectAuthorizationOwner.PlatformResourceId)
                .Select(item => (long?)item.CurrentRevision)
                .SingleOrDefaultAsync());
    }
}
