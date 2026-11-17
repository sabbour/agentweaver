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
