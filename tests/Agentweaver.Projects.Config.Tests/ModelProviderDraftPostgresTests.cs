using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ModelProviderDraftOrdinaryCreatePreservesLegacyFingerprintGoldens()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-golden-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "provider-golden-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        const string providerId = "byok-00000000000000000000000000000001";
        var request = new SaveModelProviderDraftRequest(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            0, "Example", "openai", "https://example.com", "model-1");

        var created = await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        var revision = await db.ModelProviderDraftRevisions.AsNoTracking()
            .SingleAsync(item => item.ProviderId == providerId && item.ProviderRevision == 1);
        Assert.Equal("177ac20040cdb6081c5ab040877ee5b555af2f63ae546afbfb71cd73e9132dda",
            revision.ConfigurationHash);
        Assert.Equal("77142077a56d8ecde6e11b557b81e6ac6848251cf7fae277467b088c0dfa50d0",
            revision.RequestFingerprint);
        Assert.Equal(revision.ConfigurationHash, created.Binding.ConfigurationHash);

        await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId,
            request with { OperationId = Guid.NewGuid(), ExpectedRevision = 1, DisplayName = "Renamed" },
            CancellationToken.None);
        var replay = await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        Assert.Equal(created.Binding.ProviderRevision, replay.Binding.ProviderRevision);
        Assert.Equal(created.Binding.ConfigurationHash, replay.Binding.ConfigurationHash);
        Assert.Equal("Example", replay.Binding.DisplayName);
        Assert.Equal(2, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
    }

    [Fact]
    public async Task PlatformDraftReadsAndWritesRequireBothScopes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-platform-tenant-" + suffix;
        var subject = "provider-platform-admin-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, subject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var apiReadOnly = await ResolveCallerAsync(db, subject, tenantId, ["api.read"]);
        var adminScopeOnly = await ResolveCallerAsync(db, subject, tenantId, ["projects.admin"]);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var providerId = "provider-" + suffix;
        var request = DraftRequest(Guid.NewGuid(), 0);

        var saved = await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        Assert.Equal(ModelProviderDraftState.Draft, saved.State);

        foreach (var caller in new[] { apiReadOnly, adminScopeOnly })
        {
            var listDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ListAsync(caller, ModelProviderBindingScope.Platform, null, CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, listDenied.StatusCode);

            var readDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ReadAsync(
                    caller, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, readDenied.StatusCode);

            var writeDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.SaveAsync(
                    caller,
                    ModelProviderBindingScope.Platform,
                    null,
                    providerId,
                    DraftRequest(Guid.NewGuid(), 1),
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, writeDenied.StatusCode);
        }

        var unchanged = await service.ReadAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None);
        Assert.Equal(1, unchanged.Binding.ProviderRevision);
        Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));

        var platformGrant = Assert.Single(admin.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Platform &&
            grant.ResourceId == ProjectAuthorizationOwner.PlatformResourceId &&
            grant.Role == ProjectAuthorityRole.PlatformAdmin));
        await authorityStore.RevokeRoleAssignmentAsync(
            platformGrant.AssignmentId, platformGrant.Revision, admin.ActorId);

        var revokedRead = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ReadAsync(admin, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedRead.StatusCode);

        var revokedList = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ListAsync(admin, ModelProviderBindingScope.Platform, null, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedList.StatusCode);

        var revokedReplay = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.SaveAsync(admin, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedReplay.StatusCode);
    }

    [Fact]
    public async Task ProjectDraftsRequireExactProjectOwnerBindingAndNoRunBinding()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-project-tenant-" + suffix;
        var subject = "provider-project-owner-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, subject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenantId, ProjectAuthorityRole.TenantAdmin);
        var projects = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var project = await projects.CreateProjectAsync(tenantAdmin, "Provider draft", CancellationToken.None);
        var owner = await ResolveCallerAsync(
            db, subject, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);
        var viewerSubject = "provider-project-viewer-" + suffix;
        var viewer = await SeedCallerAsync(
            db, authorityStore, viewerSubject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Project, project.ProjectId, ProjectAuthorityRole.Viewer);
        viewer = await ResolveCallerAsync(
            db, viewerSubject, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);
        var unboundOwner = await ResolveCallerAsync(db, subject, tenantId, ["api.read", "projects.admin"]);
        var wrongProjectOwner = await ResolveCallerAsync(
            db, subject, tenantId, ["api.read", "projects.admin"], boundProjectId: "other-" + suffix);
        var runBoundOwner = await ResolveCallerAsync(
            db, subject, tenantId, ["api.read", "projects.admin"],
            boundProjectId: project.ProjectId, boundRunId: "run-" + suffix);
        var foreignTenantAdmin = await SeedCallerAsync(
            db, authorityStore, "provider-foreign-admin-" + suffix, "other-tenant-" + suffix,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            "other-tenant-" + suffix, ProjectAuthorityRole.TenantAdmin);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var providerId = "project-provider-" + suffix;
        var ownerRequest = DraftRequest(Guid.NewGuid(), 0);
        var saved = await service.SaveAsync(
            owner,
            ModelProviderBindingScope.Project,
            project.ProjectId,
            providerId,
            ownerRequest,
            CancellationToken.None);
        Assert.Equal(project.ProjectId, saved.Binding.ProjectId);
        var platformSubject = "provider-project-platform-" + suffix;
        await SeedCallerAsync(
            db, authorityStore, platformSubject, tenantId, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Platform, ProjectAuthorizationOwner.PlatformResourceId,
            ProjectAuthorityRole.PlatformAdmin);
        var boundPlatformAdmin = await ResolveCallerAsync(
            db, platformSubject, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);
        var platformRead = await service.ReadAsync(
            boundPlatformAdmin, ModelProviderBindingScope.Project, project.ProjectId, providerId,
            CancellationToken.None);
        Assert.Equal(providerId, platformRead.Binding.ProviderId);

        foreach (var caller in new[] { viewer, unboundOwner, wrongProjectOwner, runBoundOwner, foreignTenantAdmin })
        {
            var listDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ListAsync(
                    caller,
                    ModelProviderBindingScope.Project,
                    project.ProjectId,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, listDenied.StatusCode);

            var readDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ReadAsync(
                    caller,
                    ModelProviderBindingScope.Project,
                    project.ProjectId,
                    providerId,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, readDenied.StatusCode);

            var writeDenied = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.SaveAsync(
                    caller,
                    ModelProviderBindingScope.Project,
                    project.ProjectId,
                    providerId,
                    DraftRequest(Guid.NewGuid(), 1),
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, writeDenied.StatusCode);
        }

        var ownerGrant = Assert.Single(owner.Grants.Where(grant =>
            grant.ResourceType == ProjectAuthorityResourceType.Project &&
            grant.ResourceId == project.ProjectId &&
            grant.Role == ProjectAuthorityRole.Owner));
        await SeedCallerAsync(
            db, authorityStore, "provider-project-second-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Project,
            project.ProjectId, ProjectAuthorityRole.Owner);
        await authorityStore.RevokeRoleAssignmentAsync(ownerGrant.AssignmentId, ownerGrant.Revision, owner.ActorId);

        var revokedRead = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ReadAsync(
                owner, ModelProviderBindingScope.Project, project.ProjectId, providerId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedRead.StatusCode);

        var revokedList = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.ListAsync(owner, ModelProviderBindingScope.Project, project.ProjectId, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedList.StatusCode);

        var revokedReplay = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            service.SaveAsync(
                owner, ModelProviderBindingScope.Project, project.ProjectId, providerId, ownerRequest,
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, revokedReplay.StatusCode);
    }

    [Fact]
    public async Task DraftRevisionIsIdempotentRedactedImmutableAndAcceptsTheUrlLimit()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-revision-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "provider-revision-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var providerId = "revision-provider-" + suffix;
        var baseUrlPrefix = "https://provider.example/";
        var baseUrl = baseUrlPrefix + new string('a', ModelProviderDraftLimits.BaseUrl - baseUrlPrefix.Length);
        var operationId = Guid.NewGuid();
        var firstRequest = DraftRequest(operationId, 0) with { BaseUrl = baseUrl };

        var first = await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, firstRequest, CancellationToken.None);
        Assert.Equal(ModelProviderDraftLimits.BaseUrl, first.Binding.BaseUrl.Length);
        Assert.Null(first.Binding.CredentialVersion);
        Assert.Null(first.Binding.RateCardId);
        Assert.Null(first.Binding.RateCardVersion);

        var replay = await service.SaveAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, firstRequest, CancellationToken.None);
        Assert.Equal(first.Binding.ProviderRevision, replay.Binding.ProviderRevision);
        Assert.Equal(first.Binding.ConfigurationHash, replay.Binding.ConfigurationHash);
        Assert.Equal(first.Binding.ReasoningEfforts.ToArray(), replay.Binding.ReasoningEfforts.ToArray());

        var second = await service.SaveAsync(
            admin,
            ModelProviderBindingScope.Platform,
            null,
            providerId,
            firstRequest with { OperationId = Guid.NewGuid(), ExpectedRevision = 1 },
            CancellationToken.None);
        Assert.Equal(2, second.Binding.ProviderRevision);
        Assert.Equal(first.Binding.ConfigurationHash, second.Binding.ConfigurationHash);

        var reusedOperation = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.SaveAsync(
                admin,
                ModelProviderBindingScope.Platform,
                null,
                providerId,
                firstRequest with { DisplayName = "Changed" },
                CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", reusedOperation.Code);

        var stale = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.SaveAsync(
                admin,
                ModelProviderBindingScope.Platform,
                null,
                providerId,
                DraftRequest(Guid.NewGuid(), 1) with { BaseUrl = baseUrl },
                CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", stale.Code);

        var tooLongUrl = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.SaveAsync(
                admin,
                ModelProviderBindingScope.Platform,
                null,
                "too-long-" + suffix,
                DraftRequest(Guid.NewGuid(), 0) with { BaseUrl = baseUrl + "a" },
                CancellationToken.None));
        Assert.Equal("model_provider_draft_invalid_request", tooLongUrl.Code);

        await AssertImmutableAsync(
            """
            UPDATE projects_config.model_provider_revisions
            SET display_name = 'changed'
            WHERE provider_id = @provider_id AND provider_revision = 1
            """,
            ("provider_id", providerId));
        await AssertImmutableAsync(
            """
            DELETE FROM projects_config.model_provider_revisions
            WHERE provider_id = @provider_id AND provider_revision = 1
            """,
            ("provider_id", providerId));
        await RunWithIsolatedProjectsAsync(test => test.AssertProviderRevisionTruncateGuardsAsync());

        var read = await service.ReadAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None);
        Assert.Equal(2, read.Binding.ProviderRevision);
        Assert.Contains(
            await service.ListAsync(admin, ModelProviderBindingScope.Platform, null, CancellationToken.None),
            item => item.Binding.ProviderId == providerId);
    }

    private async Task AssertProviderRevisionTruncateGuardsAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var command = new NpgsqlCommand(
                "TRUNCATE TABLE projects_config.model_provider_revisions CASCADE", connection, transaction);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
            Assert.Equal("records in projects_config.model_provider_revisions are immutable", error.MessageText);
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var disable = new NpgsqlCommand(
                "ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER model_provider_revisions_no_truncate",
                connection, transaction);
            await disable.ExecuteNonQueryAsync();
            await using var command = new NpgsqlCommand(
                "TRUNCATE TABLE projects_config.model_provider_revisions CASCADE", connection, transaction);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
            Assert.Equal("records in projects_config.model_provider_reference_reservations are immutable", error.MessageText);
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task CredentialPublicationAppendsReplaysAfterRenameAndPreservesTheVersion()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-publication-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "provider-publication-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var providerId = "publication-provider-" + suffix;
        var initial = await service.SaveAsync(
            admin,
            ModelProviderBindingScope.Platform,
            null,
            providerId,
            DraftRequest(Guid.NewGuid(), 0),
            CancellationToken.None);
        var publication = PublicationRequest(
            Guid.NewGuid(), initial.Binding.ProviderRevision, ModelProviderBindingScope.Platform,
            null, providerId, "0123456789abcdef0123456789abcdef");

        var receipt = await service.PublishCredentialAsync(publication, admin.ActorId, CancellationToken.None);

        Assert.Equal(publication.OperationId, receipt.OperationId);
        Assert.Equal(2, receipt.ProviderRevision);
        Assert.Equal(publication.CredentialVersion, receipt.CredentialVersion);
        Assert.Equal(publication.CredentialIdentity, receipt.CredentialIdentity);
        Assert.Equal(publication.CredentialReference, receipt.CredentialReference);
        Assert.Equal(initial.Binding.ConfigurationHash, receipt.ConfigurationHash);

        var renamed = await service.SaveAsync(
            admin,
            ModelProviderBindingScope.Platform,
            null,
            providerId,
            DraftRequest(Guid.NewGuid(), receipt.ProviderRevision) with { DisplayName = "Renamed model" },
            CancellationToken.None);
        Assert.Equal(3, renamed.Binding.ProviderRevision);
        Assert.Equal(publication.CredentialVersion, renamed.Binding.CredentialVersion);
        Assert.Equal(publication.CredentialIdentity, renamed.Binding.CredentialIdentity);
        Assert.Equal(publication.CredentialReference, renamed.Binding.CredentialReference);
        Assert.True(ModelProviderDraftEndpoints.Project(renamed).HasCredential);

        var replay = await service.PublishCredentialAsync(publication, admin.ActorId, CancellationToken.None);
        Assert.Equal(receipt, replay);
        Assert.Equal(3, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));

        var changedReplay = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.PublishCredentialAsync(
                publication with
                {
                    CredentialVersion = "fedcba9876543210fedcba9876543210",
                    CredentialReference = new(
                        publication.CredentialReference.Id, "fedcba9876543210fedcba9876543210")
                },
                admin.ActorId,
                CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", changedReplay.Code);

        var stale = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.PublishCredentialAsync(
                publication with { OperationId = Guid.NewGuid(), ExpectedProviderRevision = 1 },
                admin.ActorId,
                CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", stale.Code);
    }

    [Fact]
    public async Task CredentialPublicationRequiresVerifiedRateCardAndAReplyUsesAFreshOperationToRetry()
    {
        await using var db = CreateDbContext();
        var providerId = "rate-card-provider-" + Guid.NewGuid().ToString("N");
        const string actorId = "broker-agent";
        const string cardId = "manual:azure-card";
        const string cardVersion = "20261110";
        await SeedProviderWithRateCardAsync(db, providerId, actorId, cardId, cardVersion);
        var owner = new RuntimeRateCardTestClient(false, true, true);
        var service = new ModelProviderDraftService(db, TimeProvider.System, owner);
        var firstRequest = PublicationRequest(Guid.NewGuid(), 1, ModelProviderBindingScope.Platform,
            null, providerId, "0123456789abcdef0123456789abcdef");

        var firstReceipt = await service.PublishCredentialAsync(firstRequest, actorId, CancellationToken.None);
        Assert.Equal(2, firstReceipt.ProviderRevision);
        Assert.Equal(
            ModelProviderDraftState.Draft,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());

        var replay = await service.PublishCredentialAsync(firstRequest, actorId, CancellationToken.None);
        Assert.Equal(firstReceipt, replay);
        Assert.Single(owner.Requests);
        Assert.Equal((cardId, cardVersion), Assert.Single(owner.Requests));

        var retryRequest = firstRequest with
        {
            OperationId = Guid.NewGuid(),
            ExpectedProviderRevision = firstReceipt.ProviderRevision,
        };
        var retryReceipt = await service.PublishCredentialAsync(retryRequest, actorId, CancellationToken.None);
        Assert.Equal(3, retryReceipt.ProviderRevision);
        Assert.Equal(
            ModelProviderDraftState.Available,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());
        Assert.Equal(2, owner.Requests.Count);
        Assert.All(owner.Requests, request => Assert.Equal((cardId, cardVersion), request));

        var rotation = await service.PublishCredentialAsync(
            retryRequest with
            {
                OperationId = Guid.NewGuid(),
                ExpectedProviderRevision = retryReceipt.ProviderRevision,
                CredentialVersion = "fedcba9876543210fedcba9876543210",
                CredentialReference = new(
                    retryRequest.CredentialReference.Id, "fedcba9876543210fedcba9876543210"),
            },
            actorId,
            CancellationToken.None);
        Assert.Equal(4, rotation.ProviderRevision);
        Assert.Equal(
            ModelProviderDraftState.Available,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());
        Assert.Equal(3, owner.Requests.Count);
        Assert.All(owner.Requests, request => Assert.Equal((cardId, cardVersion), request));
    }

    [Fact]
    public async Task FailedCredentialRotationMovesAvailableProviderBackToDraftAndReplaysTheResult()
    {
        await using var db = CreateDbContext();
        var providerId = "rate-card-rotation-" + Guid.NewGuid().ToString("N");
        const string actorId = "broker-agent";
        const string cardId = "manual:azure-card";
        const string cardVersion = "20261110";
        await SeedProviderWithRateCardAsync(db, providerId, actorId, cardId, cardVersion);
        var owner = new RuntimeRateCardTestClient(true, false);
        var service = new ModelProviderDraftService(db, TimeProvider.System, owner);
        var initialRequest = PublicationRequest(Guid.NewGuid(), 1, ModelProviderBindingScope.Platform,
            null, providerId, "0123456789abcdef0123456789abcdef");

        var initialReceipt = await service.PublishCredentialAsync(initialRequest, actorId, CancellationToken.None);
        Assert.Equal(2, initialReceipt.ProviderRevision);
        Assert.Equal(
            ModelProviderDraftState.Available,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());

        var rotationRequest = initialRequest with
        {
            OperationId = Guid.NewGuid(),
            ExpectedProviderRevision = initialReceipt.ProviderRevision,
            CredentialVersion = "fedcba9876543210fedcba9876543210",
            CredentialReference = new(
                initialRequest.CredentialReference.Id, "fedcba9876543210fedcba9876543210"),
        };
        var rotationReceipt = await service.PublishCredentialAsync(rotationRequest, actorId, CancellationToken.None);

        Assert.Equal(3, rotationReceipt.ProviderRevision);
        Assert.Equal(rotationRequest.CredentialVersion, rotationReceipt.CredentialVersion);
        Assert.Equal(
            ModelProviderDraftState.Draft,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());

        var replay = await service.PublishCredentialAsync(rotationRequest, actorId, CancellationToken.None);
        Assert.Equal(rotationReceipt, replay);
        Assert.Equal(2, owner.Requests.Count);
        Assert.Equal(3, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
        Assert.Equal(
            ModelProviderDraftState.Draft,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());
    }

    [Fact]
    public async Task CredentialPublicationWithoutSavedRateCardStaysDraft()
    {
        await using var db = CreateDbContext();
        var providerId = "rate-card-missing-" + Guid.NewGuid().ToString("N");
        const string actorId = "broker-agent";
        await SeedProviderWithRateCardAsync(db, providerId, actorId, null, null);
        var owner = new RuntimeRateCardTestClient(true);
        var service = new ModelProviderDraftService(db, TimeProvider.System, owner);

        var receipt = await service.PublishCredentialAsync(
            PublicationRequest(Guid.NewGuid(), 1, ModelProviderBindingScope.Platform,
                null, providerId, "0123456789abcdef0123456789abcdef"),
            actorId,
            CancellationToken.None);

        Assert.Equal(2, receipt.ProviderRevision);
        Assert.Equal(ModelProviderDraftState.Draft,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.State)
                .SingleAsync());
        Assert.Empty(owner.Requests);
    }

    [Fact]
    public async Task ConcurrentSavesUseCasAndSameOperationRetriesReturnOneRevision()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-cas-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "provider-cas-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var providerId = "cas-provider-" + suffix;
        var initial = await new ModelProviderDraftService(db, TimeProvider.System).SaveAsync(
            admin,
            ModelProviderBindingScope.Platform,
            null,
            providerId,
            DraftRequest(Guid.NewGuid(), 0),
            CancellationToken.None);
        await using var firstDb = CreateDbContext();
        await using var secondDb = CreateDbContext();
        var firstService = new ModelProviderDraftService(firstDb, TimeProvider.System);
        var secondService = new ModelProviderDraftService(secondDb, TimeProvider.System);

        // Separate contexts avoid shared tracked state while both requests use the same expected revision.
        async Task<(ModelProviderDraft? Draft, ModelProviderDraftException? Error)> TrySaveAsync(
            ModelProviderDraftService service,
            string displayName)
        {
            try
            {
                var draft = await service.SaveAsync(
                    admin,
                    ModelProviderBindingScope.Platform,
                    null,
                    providerId,
                    DraftRequest(Guid.NewGuid(), initial.Binding.ProviderRevision) with
                    {
                        DisplayName = displayName,
                    },
                    CancellationToken.None);
                return (draft, null);
            }
            catch (ModelProviderDraftException exception)
            {
                return (null, exception);
            }
        }

        var outcomes = await Task.WhenAll(
            TrySaveAsync(firstService, "Concurrent A"),
            TrySaveAsync(secondService, "Concurrent B"));
        Assert.Single(outcomes, outcome => outcome.Draft is not null);
        Assert.Equal(
            "model_provider_draft_conflict",
            Assert.Single(outcomes, outcome => outcome.Error is not null).Error!.Code);
        Assert.Equal(
            2,
            await db.ModelProviderDrafts.AsNoTracking()
                .Where(head => head.ProviderId == providerId)
                .Select(head => head.CurrentRevision)
                .SingleAsync());
        Assert.Equal(2, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));

        var retryRequest = DraftRequest(Guid.NewGuid(), 2) with { DisplayName = "Concurrent retry" };
        var retries = await Task.WhenAll(
            firstService.SaveAsync(
                admin,
                ModelProviderBindingScope.Platform,
                null,
                providerId,
                retryRequest,
                CancellationToken.None),
            secondService.SaveAsync(
                admin,
                ModelProviderBindingScope.Platform,
                null,
                providerId,
                retryRequest,
                CancellationToken.None));
        Assert.All(retries, retry => Assert.Equal(3, retry.Binding.ProviderRevision));
        Assert.Equal(retries[0].Binding.ConfigurationHash, retries[1].Binding.ConfigurationHash);
        Assert.Equal(3, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
    }

    [Fact]
    public async Task FailedDatabaseSaveDetachesDraftEntriesBeforeRetry()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "provider-retry-tenant-" + suffix;
        var admin = await SeedCallerAsync(
            db, authorityStore, "provider-retry-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
        var service = new ModelProviderDraftService(db, TimeProvider.System);
        var providerId = "retry-provider-" + suffix;
        var rejectedOperationId = Guid.NewGuid();
        var constraintName = "ck_provider_retry_" + suffix;

        // Reject this operation in PostgreSQL so the save fails after the service has tracked draft rows.
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var addConstraint = new NpgsqlCommand(
            $"ALTER TABLE projects_config.model_provider_revisions " +
            $"ADD CONSTRAINT \"{constraintName}\" " +
            $"CHECK (operation_id <> '{rejectedOperationId:D}'::uuid)",
            connection))
            await addConstraint.ExecuteNonQueryAsync();

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => service.SaveAsync(
                admin,
                ModelProviderBindingScope.Platform,
                null,
                providerId,
                DraftRequest(rejectedOperationId, 0),
                CancellationToken.None));
            Assert.Empty(db.ChangeTracker.Entries<ModelProviderDraftHeadRecord>());
            Assert.Empty(db.ChangeTracker.Entries<ModelProviderDraftRevisionRecord>());
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var dropConstraint = new NpgsqlCommand(
                $"ALTER TABLE projects_config.model_provider_revisions DROP CONSTRAINT \"{constraintName}\"",
                connection);
            await dropConstraint.ExecuteNonQueryAsync();
        }

        var retry = await service.SaveAsync(
            admin,
            ModelProviderBindingScope.Platform,
            null,
            providerId,
            DraftRequest(Guid.NewGuid(), 0),
            CancellationToken.None);
        Assert.Equal(1, retry.Binding.ProviderRevision);
    }

    private static async Task SeedProviderWithRateCardAsync(
        ProjectsConfigDbContext db,
        string providerId,
        string actorId,
        string? rateCardId,
        string? rateCardVersion)
    {
        var now = DateTimeOffset.UtcNow;
        db.ModelProviderDrafts.Add(new ModelProviderDraftHeadRecord
        {
            ProviderId = providerId,
            Scope = ModelProviderBindingScope.Platform,
            DisplayName = "Customer model",
            ProviderType = "azure",
            BaseUrl = "https://provider.example",
            Deployment = "deployment-1",
            ReasoningEfforts = ["low"],
            State = ModelProviderDraftState.Draft,
            CurrentRevision = 1,
            CreatedByActorId = actorId,
            CreatedAt = now,
            UpdatedByActorId = actorId,
            UpdatedAt = now,
        });
        db.ModelProviderDraftRevisions.Add(new ModelProviderDraftRevisionRecord
        {
            ProviderId = providerId,
            Scope = ModelProviderBindingScope.Platform,
            ProviderRevision = 1,
            OperationId = Guid.NewGuid(),
            RequestFingerprint = new string('b', 64),
            DisplayName = "Customer model",
            ProviderType = "azure",
            BaseUrl = "https://provider.example",
            Deployment = "deployment-1",
            ReasoningEfforts = ["low"],
            ConfigurationHash = new string('a', 64),
            RateCardId = rateCardId,
            RateCardVersion = rateCardVersion,
            CreatedByActorId = actorId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private sealed class RuntimeRateCardTestClient(params bool[] results) : IRuntimeRateCardOwnerClient
    {
        private int _nextResult;

        public List<(string Id, string Version)> Requests { get; } = [];

        public async Task<bool> VerifyAsync(string id, string version, CancellationToken cancellationToken) =>
            await ReadPinAsync(id, version, cancellationToken) is not null;

        public Task<RuntimeRateCardPin?> ReadPinAsync(
            string id, string version, CancellationToken cancellationToken)
        {
            Requests.Add((id, version));
            var available = results[_nextResult++];
            RuntimeRateCardPin? pin = available
                ? new RuntimeRateCardPin(id, version, new string('c', 64),
                    new ManualRuntimeRateCardReadProvenance(
                        "https://pricing.example/rate-card", new DateOnly(2026, 1, 1)))
                : null;
            return Task.FromResult(pin);
        }
    }

    private static ModelProviderCredentialPublicationRequest PublicationRequest(
        Guid operationId, long expectedRevision, ModelProviderBindingScope scope,
        string? projectId, string providerId, string credentialVersion)
    {
        var identity = new ModelCredentialIdentity(Guid.NewGuid(), scope, projectId, providerId);
        return new ModelProviderCredentialPublicationRequest(
            operationId, expectedRevision, scope, projectId, providerId, credentialVersion,
            identity, new SecretRef($"secret:{providerId}", credentialVersion));
    }

    private static SaveModelProviderDraftRequest DraftRequest(Guid operationId, long expectedRevision) =>
        new(
            operationId,
            expectedRevision,
            "Customer model",
            "openai",
            "https://provider.example",
            "deployment-1",
            WireApi: "responses",
            ReasoningEfforts: ["low", "high"],
            PromptCapacityTokens: 32768);
}
