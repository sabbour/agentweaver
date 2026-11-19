using Agentweaver.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Data;
using System.Collections.Immutable;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task ModelProviderDraftReservationDoesNotFenceLaterProjectSelectors()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            await new ProjectsConfigPostgresTests(isolated).AssertLaterProjectSelectorsAsync();
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private async Task AssertLaterProjectSelectorsAsync()
    {
        await using var db = CreateDbContext();
        var admin = await CreateReferenceAdminAsync(db);
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "selector-" + suffix;
        var owner = await SeedCallerAsync(db, authority, "owner-" + suffix, tenant,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System,
            modelBindings: ProjectModelChoicesTests.SampleBindings());
        var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
        var platform = await service.UpdatePlatformRuntimeDefaultsAsync(admin, head.Revision, PlatformDefaults(), CancellationToken.None);
        var project = await service.CreateProjectAsync(owner, "Selector limit", CancellationToken.None);
        var membership = await SeedMembershipAsync(authority, "orchestrator-" + suffix, tenant);
        await authority.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
            ProjectAuthorityRole.Orchestrator, "fixture");
        var caller = await ResolveCallerAsync(db, "orchestrator-" + suffix, tenant,
            ["api.read", "projects.orchestrator"]);
        var settings = ProjectSettings() with { ModelSelection = new ModelSelectionSettings("ACME") };
        var before = await service.UpdateProjectConfigurationAsync(
            owner, project.ProjectId, project.ConfigurationRevision, settings, CancellationToken.None);
        var beforeChoices = await service.GetProjectConfigurationChoicesAsync(owner, project.ProjectId, CancellationToken.None);
        var beforeRequest = RunRequest(before.Revision, platform.Revision);
        beforeRequest = beforeRequest with { Context = beforeRequest.Context with
        {
            AvailableModelSelectionReferences = beforeRequest.Context.AvailableModelSelectionReferences.Add("ACME"),
        } };
        var beforeRun = await service.AcceptRunSelectionAsync(
            caller, project.ProjectId, "before-" + suffix, beforeRequest, CancellationToken.None);
        var cleared = await service.UpdateProjectConfigurationAsync(
            owner, project.ProjectId, before.Revision, ProjectSettings(), CancellationToken.None);

        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        await ReferenceService(db).SaveAsync(admin, ModelProviderBindingScope.Platform, null, providerId,
            DraftRequest(operation, 0) with { Reference = "Acme", TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
        var reservationsBefore = await db.ModelProviderReferenceReservations.AsNoTracking()
            .OrderBy(item => item.ReferenceFolded).ToArrayAsync();
        var after = await service.UpdateProjectConfigurationAsync(
            owner, project.ProjectId, cleared.Revision, settings, CancellationToken.None);
        Assert.Equal("ACME", after.Configuration.ModelSelection!.Reference);
        var reservationsAfter = await db.ModelProviderReferenceReservations.AsNoTracking()
            .OrderBy(item => item.ReferenceFolded).ToArrayAsync();
        Assert.Equal(JsonSerializer.Serialize(reservationsBefore), JsonSerializer.Serialize(reservationsAfter));
        Assert.Single(reservationsAfter, item => item.ProviderId == providerId && item.Reference == "Acme");
        var afterChoices = await service.GetProjectConfigurationChoicesAsync(owner, project.ProjectId, CancellationToken.None);
        var afterRun = await service.AcceptRunSelectionAsync(caller, project.ProjectId, "after-" + suffix,
            beforeRequest with { ExpectedProjectConfigRevision = after.Revision }, CancellationToken.None);
        Assert.Equal("ACME", beforeRun.ModelSelection.Reference);
        Assert.Equal(beforeRun.ModelSelection, afterRun.ModelSelection);
        Assert.Equal(JsonSerializer.Serialize(beforeChoices.Models), JsonSerializer.Serialize(afterChoices.Models));
        Assert.DoesNotContain(afterChoices.Models.Choices, choice => choice.Reference == "Acme" || choice.Reference == "ACME");
        Assert.Contains(afterChoices.Models.SavedReferences, choice => choice.Reference == "ACME");
    }

    [Fact]
    public async Task ModelProviderDraftReferenceCreatesReplayAfterRenameAndDisable()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Exact.Model-" + Guid.NewGuid().ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        var enabled = ReferenceService(db);
        var created = await enabled.SaveAsync(
            caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        Assert.Equal(reference, created.Reference);
        Assert.Equal(ModelProviderDraftState.Draft, created.State);

        await enabled.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
            DraftRequest(Guid.NewGuid(), 1) with { DisplayName = "Renamed" }, CancellationToken.None);
        var disabled = ReferenceService(db, enabled: false);
        var replay = await disabled.SaveAsync(
            caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        Assert.Equal(reference, replay.Reference);
        Assert.Equal(created.Binding.DisplayName, replay.Binding.DisplayName);
        Assert.Equal(1, replay.Binding.ProviderRevision);
        Assert.Equal(reference, (await disabled.ReadAsync(
            caller, ModelProviderBindingScope.Platform, null, providerId, CancellationToken.None)).Reference);
        Assert.Contains((await disabled.ListPageAsync(
            caller, ModelProviderBindingScope.Platform, null, 100, null, CancellationToken.None)).Providers,
            item => item.Binding.ProviderId == providerId && item.Reference == reference);
        var newOperation = Guid.NewGuid();
        var unavailable = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            disabled.SaveAsync(caller, ModelProviderBindingScope.Platform, null, "byok-" + newOperation.ToString("N"),
                DraftRequest(newOperation, 0) with { Reference = reference + "new", TargetInstallationId = fixture.InstallationId }, CancellationToken.None));
        Assert.Equal("model_provider_draft_unavailable", unavailable.Code);
        var mismatch = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            disabled.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                request with { Reference = reference.ToLowerInvariant() }, CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", mismatch.Code);
        var dropped = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            disabled.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                request with { Reference = null, TargetInstallationId = null }, CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", dropped.Code);
    }

    [Fact]
    public async Task ModelProviderDraftConcurrentReferenceCreatesReplayAndRejectCaseCollisions()
    {
        await using var seed = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(seed);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Concurrency-" + Guid.NewGuid().ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        await using var firstDb = CreateDbContext();
        await using var secondDb = CreateDbContext();
        var first = ReferenceService(firstDb).SaveAsync(
            caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        var second = ReferenceService(secondDb).SaveAsync(
            caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result =>
        {
            Assert.Equal(reference, result.Reference);
            Assert.Equal(1, result.Binding.ProviderRevision);
        });
        Assert.Equal(1, await seed.ModelProviderReferenceReservations.CountAsync(item => item.ProviderId == providerId));
        Assert.Equal(1, await seed.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
        var other = Guid.NewGuid();
        var conflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(seed).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                "byok-" + other.ToString("N"),
                DraftRequest(other, 0) with { Reference = reference.ToLowerInvariant(), TargetInstallationId = fixture.InstallationId }, CancellationToken.None));
        Assert.Equal("reference_conflict", conflict.Code);
        Assert.False(await seed.ModelProviderDrafts.AnyAsync(item => item.ProviderId == "byok-" + other.ToString("N")));
    }

    [Fact]
    public async Task ModelProviderDraftReferenceConcurrentCaseClaimsHaveOneWinner()
    {
        await using var seed = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(seed);
        var reference = "CaseClaim-" + Guid.NewGuid().ToString("N");
        var firstOperation = Guid.NewGuid();
        var secondOperation = Guid.NewGuid();
        await using var firstDb = CreateDbContext();
        await using var secondDb = CreateDbContext();
        async Task<string> Claim(ProjectsConfigDbContext context, Guid operation, string value)
        {
            try
            {
                var result = await ReferenceService(context).SaveAsync(caller, ModelProviderBindingScope.Platform,
                    null, "byok-" + operation.ToString("N"),
                    DraftRequest(operation, 0) with { Reference = value, TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
                return result.Reference!;
            }
            catch (ModelProviderDraftException exception)
            {
                return exception.Code;
            }
        }
        var results = await Task.WhenAll(
            Claim(firstDb, firstOperation, reference),
            Claim(secondDb, secondOperation, reference.ToLowerInvariant()));
        Assert.Single(results, item => item == "reference_conflict");
        Assert.Single(results, item => item == reference || item == reference.ToLowerInvariant());
        Assert.Equal(1, await seed.ModelProviderReferenceReservations.CountAsync(item =>
            item.ReferenceFolded == reference.ToLowerInvariant()));
        Assert.Equal(1, await seed.ModelProviderDraftRevisions.CountAsync(item =>
            item.OperationId == firstOperation || item.OperationId == secondOperation));
    }

    [Fact]
    public async Task ModelProviderDraftReferenceReplayRequiresOriginalActor()
    {
        await using var db = CreateDbContext();
        var first = await CreateReferenceAdminAsync(db);
        var second = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = "Actor-" + Guid.NewGuid().ToString("N"), TargetInstallationId = fixture.InstallationId };
        await ReferenceService(db).SaveAsync(first, ModelProviderBindingScope.Platform, null, providerId,
            request, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(second, ModelProviderBindingScope.Platform, null, providerId,
                request, CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", error.Code);
        Assert.Equal(1, await db.ModelProviderReferenceReservations.CountAsync(item => item.OperationId == operation));
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("tenant")]
    [InlineData("subject")]
    public async Task ModelProviderDraftReferenceReplayRejectsEachChangedIdentityComponent(string changed)
    {
        await using var db = CreateDbContext();
        var first = await CreateReferenceAdminAsync(db);
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var issuer = changed == "issuer" ? "https://other-issuer.example/" : first.Issuer;
        var tenant = changed == "tenant" ? first.TenantId + "-other" : first.TenantId;
        var subject = changed == "subject" ? first.ActorId + "-other" : first.ActorId;
        var membership = await authority.GrantMembershipAsync(issuer, subject, tenant, "fixture");
        var role = await authority.AssignRoleAsync(membership.MembershipId, ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
        var other = new ProjectAuthorizationContext(issuer, subject, tenant, membership.MembershipId,
            membership.Revision,
            ImmutableArray.Create(new ProjectAuthorizationGrant(role.AssignmentId, role.ResourceType,
                role.ResourceId, role.Role, role.Revision)),
            ImmutableHashSet.Create(StringComparer.Ordinal, "api.read", "projects.admin"), null, null, null);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = "Identity-" + operation.ToString("N"), TargetInstallationId = fixture.InstallationId };
        await ReferenceService(db).SaveAsync(first, ModelProviderBindingScope.Platform, null,
            providerId, request, CancellationToken.None);
        var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(other, ModelProviderBindingScope.Platform, null,
                providerId, request, CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", error.Code);
    }

    [Theory]
    [InlineData("Byok", false, false, true)]
    [InlineData("Byok", true, false, false)]
    [InlineData("HostedCopilot", false, false, false)]
    [InlineData("HostedCopilot", true, false, false)]
    [InlineData("Byok", false, true, true)]
    [InlineData("Byok", true, true, false)]
    [InlineData("HostedCopilot", false, true, false)]
    [InlineData("HostedCopilot", true, true, false)]
    public async Task ModelProviderDraftReferenceChecksEnabledAndDisabledLocalBindings(
        string source, bool caseVariant, bool bindingEnabled, bool allowed)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var reference = "Binding-" + Guid.NewGuid().ToString("N");
        var binding = caseVariant ? reference.ToLowerInvariant() : reference;
        var operation = Guid.NewGuid();
        var service = ReferenceService(db, bindingReference: binding, source: source, bindingEnabled: bindingEnabled);
        var save = () => service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
            "byok-" + operation.ToString("N"), DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId },
            CancellationToken.None);
        if (allowed)
            Assert.Equal(reference, (await save()).Reference);
        else
            Assert.Equal("reference_conflict", (await Assert.ThrowsAsync<ModelProviderDraftException>(save)).Code);
    }

    [Fact]
    public async Task ModelProviderDraftReferenceChecksArchivedProjectSavedSelectors()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "reference-project-" + suffix;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var owner = await SeedCallerAsync(db, authority, "reference-owner-" + suffix, tenant,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Reference selectors", CancellationToken.None);
        var prefix = "Archived-" + suffix;
        await SeedHistoricalConfigurationAsync(db, project, owner.ActorId, new ProjectConfiguration
        {
            ModelSelection = new(prefix + "-primary"),
            ModelTaskOverrides = [new(ModelTaskKind.Planning, new(prefix + "-task"))],
            ModelRoleOverrides = [new("role", new(prefix + "-role"))],
            ModelAgentOverrides = [new("agent", new(prefix + "-agent"))],
        });
        var row = await db.Projects.SingleAsync(item => item.ProjectId == project.ProjectId);
        row.State = ProjectLifecycleState.Archived;
        await db.SaveChangesAsync();
        var savedBefore = await db.ProjectConfigurationRevisions.AsNoTracking()
            .Where(item => item.ProjectId == project.ProjectId && item.Revision == row.ConfigurationRevision)
            .Select(item => item.ConfigurationJson).SingleAsync();
        foreach (var target in new[] { "primary", "task", "role", "agent" })
        {
            var reference = prefix + "-" + target;
            var operation = Guid.NewGuid();
            var conflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                    "byok-" + operation.ToString("N"),
                    DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId }, CancellationToken.None));
            Assert.Equal("reference_conflict", conflict.Code);
        }
        var matched = prefix + "-primary";
        var allowedOperation = Guid.NewGuid();
        var result = await ReferenceService(db, bindingReference: matched).SaveAsync(
            caller, ModelProviderBindingScope.Platform, null, "byok-" + allowedOperation.ToString("N"),
            DraftRequest(allowedOperation, 0) with { Reference = matched, TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
        Assert.Equal(matched, result.Reference);
        var caseOperation = Guid.NewGuid();
        var caseConflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db, bindingReference: prefix + "-task").SaveAsync(
                caller, ModelProviderBindingScope.Platform, null, "byok-" + caseOperation.ToString("N"),
                DraftRequest(caseOperation, 0) with { Reference = (prefix + "-task").ToLowerInvariant(), TargetInstallationId = fixture.InstallationId },
                CancellationToken.None));
        Assert.Equal("reference_conflict", caseConflict.Code);
        Assert.Equal(savedBefore, await db.ProjectConfigurationRevisions.AsNoTracking()
            .Where(item => item.ProjectId == project.ProjectId && item.Revision == row.ConfigurationRevision)
            .Select(item => item.ConfigurationJson).SingleAsync());
        Assert.Equal(2, row.ConfigurationRevision);
    }

    [Fact]
    public async Task ModelProviderDraftReferenceChecksCurrentPlatformSelectors()
    {
        await RunWithIsolatedProjectsAsync(test => test.CheckCurrentPlatformSelectorsCoreAsync());
    }

    private async Task CheckCurrentPlatformSelectorsCoreAsync()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var head = await db.PlatformRuntimeHeads.SingleAsync(item => item.Id == PlatformRuntimeHeadRecord.SingletonId);
        var previous = head.CurrentRevision;
        var next = (await db.PlatformRuntimeRevisions.MaxAsync(item => (long?)item.Revision) ?? 0) + 1;
        var prefix = "Platform-" + Guid.NewGuid().ToString("N");
        var generatedOperation = Guid.NewGuid();
        var generatedId = "byok-" + generatedOperation.ToString("N");
        var defaults = new PlatformRuntimeDefaults
        {
            ModelSelection = new(prefix + "-primary"),
            ModelTaskDefaults = [new(ModelTaskKind.Planning, new(prefix + "-task"))],
            ModelRoleDefaults = [new("role", new(prefix + "-role")), new("generated", new(generatedId))],
            ModelAgentDefaults = [new("agent", new(prefix + "-agent"))],
        };
        db.PlatformRuntimeRevisions.Add(new PlatformRuntimeRevisionRecord
        {
            HeadId = PlatformRuntimeHeadRecord.SingletonId,
            Revision = next,
            ConfigurationJson = JsonSerializer.Serialize(defaults, LegacyJsonOptions()),
            UpdatedByActorId = caller.ActorId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        head.CurrentRevision = next;
        await db.SaveChangesAsync();
        try
        {
            foreach (var target in new[] { "primary", "task", "role", "agent" })
            {
                var operation = Guid.NewGuid();
                var reference = prefix + "-" + target;
                var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                        "byok-" + operation.ToString("N"),
                        DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId }, CancellationToken.None));
                Assert.Equal("reference_conflict", error.Code);
            }
            var acceptedOperation = Guid.NewGuid();
            var acceptedReference = prefix + "-agent";
            var accepted = await ReferenceService(db, bindingReference: acceptedReference).SaveAsync(
                caller, ModelProviderBindingScope.Platform, null, "byok-" + acceptedOperation.ToString("N"),
                DraftRequest(acceptedOperation, 0) with { Reference = acceptedReference, TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
            Assert.Equal(acceptedReference, accepted.Reference);
            var generatedConflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                ReferenceService(db).SaveAsync(
                    caller, ModelProviderBindingScope.Platform, null, generatedId,
                    DraftRequest(generatedOperation, 0) with { Reference = prefix + "-different", TargetInstallationId = fixture.InstallationId },
                    CancellationToken.None));
            Assert.Equal("reference_conflict", generatedConflict.Code);
        }
        finally
        {
            head.CurrentRevision = previous;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task ModelProviderDraftReferenceDatabaseGuardsProtectBothInsertDirections()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var reference = "Guard-" + Guid.NewGuid().ToString("N");
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
            DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
        var ordinaryId = "ordinary-" + Guid.NewGuid().ToString("N");
        var ordinaryOperation = Guid.NewGuid();
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, ordinaryId,
            DraftRequest(ordinaryOperation, 0), CancellationToken.None);

        var runtimeConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(runtimeConnection);
        await using var connection = await source.OpenConnectionAsync();
        await using (var update = new NpgsqlCommand("""
            UPDATE projects_config.model_providers SET provider_id = @collision WHERE provider_id = @source
            """, connection))
        {
            update.Parameters.AddWithValue("collision", reference.ToLowerInvariant());
            update.Parameters.AddWithValue("source", ordinaryId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }
        await using (var update = new NpgsqlCommand("""
            UPDATE projects_config.model_providers SET provider_id = provider_id, display_name = 'Mutable'
            WHERE provider_id = @source
            """, connection))
        {
            update.Parameters.AddWithValue("source", ordinaryId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_model_provider_write_guard", error.ConstraintName);
        }
        await using (var constraints = new NpgsqlCommand("""
            SELECT array_agg(conname::text ORDER BY conname)
            FROM pg_catalog.pg_constraint
            WHERE conrelid = 'projects_config.model_provider_reference_reservations'::regclass
              AND contype = 'u'
            """, connection))
        {
            Assert.Equal(
                ["ux_model_provider_reference_reservations_operation_id", "ux_model_provider_reference_reservations_provider_id"],
                (string[])(await constraints.ExecuteScalarAsync() ?? Array.Empty<string>()));
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO projects_config.model_providers
                (provider_id, scope, project_id, display_name, type, base_url, deployment, wire_api,
                 azure_api_version, reasoning_efforts, prompt_capacity_tokens, state, current_revision,
                 created_by_actor_id, created_at, updated_by_actor_id, updated_at)
            SELECT @collision, scope, project_id, display_name, type, base_url, deployment, wire_api,
                   azure_api_version, reasoning_efforts, prompt_capacity_tokens, state, current_revision,
                   created_by_actor_id, created_at, updated_by_actor_id, updated_at
            FROM projects_config.model_providers WHERE provider_id = @source
            """, connection))
        {
            insert.Parameters.AddWithValue("collision", reference.ToLowerInvariant());
            insert.Parameters.AddWithValue("source", ordinaryId);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_model_provider_reference_cross_table", error.ConstraintName);
        }
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO projects_config.model_provider_reference_reservations
                (reference, reference_folded, provider_id, creation_revision, operation_id,
                 issuer, tenant_id, subject, created_at, installation_id)
            VALUES (@reference, @folded, @provider, 1, @operation, 'issuer', 'tenant', 'subject', now(),
                    (SELECT installation_id FROM projects_config.installation_identity WHERE singleton))
            """, connection))
        {
            insert.Parameters.AddWithValue("reference", ordinaryId.ToUpperInvariant());
            insert.Parameters.AddWithValue("folded", ordinaryId.ToLowerInvariant());
            insert.Parameters.AddWithValue("provider", ordinaryId);
            insert.Parameters.AddWithValue("operation", Guid.NewGuid());
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_model_provider_reference_cross_table", error.ConstraintName);
        }
        foreach (var invalidReference in new[] { "_invalid", "invalid_", "a__b", "invalid\n", "café" })
        {
            await using var invalid = new NpgsqlCommand("""
                INSERT INTO projects_config.model_provider_reference_reservations
                    (reference, reference_folded, provider_id, creation_revision, operation_id,
                     issuer, tenant_id, subject, created_at, installation_id)
                VALUES (@reference, @reference, @provider, 1, @operation, 'issuer', 'tenant', 'subject', now(),
                        (SELECT installation_id FROM projects_config.installation_identity WHERE singleton))
                """, connection);
            invalid.Parameters.AddWithValue("reference", invalidReference);
            invalid.Parameters.AddWithValue("provider", ordinaryId);
            invalid.Parameters.AddWithValue("operation", Guid.NewGuid());
            var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_provider_reference_syntax", error.ConstraintName);
        }
        foreach (var sql in new[]
        {
            "UPDATE projects_config.model_provider_reference_reservations SET subject = 'changed'",
            "DELETE FROM projects_config.model_provider_reference_reservations",
            "TRUNCATE projects_config.model_provider_reference_reservations",
        })
        {
            await using var forbidden = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => forbidden.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        Assert.Equal(1, await db.ModelProviderReferenceReservations.CountAsync(item => item.ProviderId == providerId));
        Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(item => item.ProviderId == ordinaryId));
        await AssertImmutableAsync(
            "UPDATE projects_config.model_provider_reference_reservations SET subject = 'changed' WHERE provider_id = @id",
            ("id", providerId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.model_provider_reference_reservations WHERE provider_id = @id",
            ("id", providerId));
        await AssertImmutableAsync("TRUNCATE projects_config.model_provider_reference_reservations");
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task ModelProviderDraftReferenceGuardSerializesDirectConcurrentProviderInsert(IsolationLevel isolation)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var sourceId = "source-" + Guid.NewGuid().ToString("N");
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, sourceId,
            DraftRequest(Guid.NewGuid(), 0), CancellationToken.None);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Direct-" + operation.ToString("N");
        var locked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var createDb = CreateDbContext();
        var service = ReferenceService(createDb);
        service.AfterReferenceAuthorityLocked = async (pid, token) =>
        {
            locked.TrySetResult(pid);
            await release.Task.WaitAsync(token);
        };
        var create = service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
            DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId }, timeout.Token);
        Task<int>? insertTask = null;
        try
        {
            var createPid = await locked.Task.WaitAsync(timeout.Token);
            var appName = "reference-direct-" + operation.ToString("N");
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
                ApplicationName = appName,
            }.ConnectionString;
            await using var directSource = NpgsqlDataSource.Create(connectionString);
            await using var connection = await directSource.OpenConnectionAsync(timeout.Token);
            await using var transaction = await connection.BeginTransactionAsync(isolation, timeout.Token);
            await using (var snapshot = new NpgsqlCommand(
                "SELECT count(*) FROM projects_config.model_provider_reference_reservations", connection, transaction))
                await snapshot.ExecuteScalarAsync(timeout.Token);
            await using var insert = new NpgsqlCommand("""
                INSERT INTO projects_config.model_providers
                    (provider_id, scope, project_id, display_name, type, base_url, deployment, wire_api,
                     azure_api_version, reasoning_efforts, prompt_capacity_tokens, state, current_revision,
                     created_by_actor_id, created_at, updated_by_actor_id, updated_at)
                SELECT @collision, scope, project_id, display_name, type, base_url, deployment, wire_api,
                       azure_api_version, reasoning_efforts, prompt_capacity_tokens, state, current_revision,
                       created_by_actor_id, created_at, updated_by_actor_id, updated_at
                FROM projects_config.model_providers WHERE provider_id = @source
                """, connection, transaction);
            insert.Parameters.AddWithValue("collision", reference.ToLowerInvariant());
            insert.Parameters.AddWithValue("source", sourceId);
            insertTask = insert.ExecuteNonQueryAsync(timeout.Token);
            await using var observer = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            while (true)
            {
                await using var query = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity WHERE application_name = @name
                          AND wait_event_type = 'Lock' AND @create_pid = ANY(pg_blocking_pids(pid)))
                    """, observer);
                query.Parameters.AddWithValue("name", appName);
                query.Parameters.AddWithValue("create_pid", createPid);
                if ((bool)(await query.ExecuteScalarAsync(timeout.Token) ?? false))
                    break;
                Assert.False(insertTask.IsCompleted);
                await Task.Delay(20, timeout.Token);
            }
            release.TrySetResult();
            await create.WaitAsync(timeout.Token);
            var error = await Assert.ThrowsAsync<PostgresException>(() => insertTask);
            if (isolation == IsolationLevel.ReadCommitted)
            {
                Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
                Assert.Equal("ck_model_provider_reference_cross_table", error.ConstraintName);
            }
            else
                Assert.Equal(PostgresErrorCodes.SerializationFailure, error.SqlState);
            Assert.False(await db.ModelProviderDrafts.AsNoTracking()
                .AnyAsync(item => item.ProviderId == reference.ToLowerInvariant()));
        }
        finally
        {
            timeout.Cancel();
            release.TrySetResult();
            try { await create; } catch { }
            if (insertTask is not null)
                try { await insertTask; } catch { }
        }
    }

    [Fact]
    public async Task ModelProviderDraftCreateUsesReadCommittedWhenDatabaseDefaultIsSerializable()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config -c default_transaction_isolation=serializable",
            ApplicationName = "reference-isolation-" + Guid.NewGuid().ToString("N"),
        }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(source, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        await using var runtime = new ProjectsConfigDbContext(options);
        var firstOperation = Guid.NewGuid();
        var reference = "Isolation-" + firstOperation.ToString("N");
        var first = await ReferenceService(runtime).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
            "byok-" + firstOperation.ToString("N"),
            DraftRequest(firstOperation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
        Assert.Equal(reference, first.Reference);
        var ordinaryOperation = Guid.NewGuid();
        var ordinary = await ReferenceService(runtime).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
            "byok-" + ordinaryOperation.ToString("N"), DraftRequest(ordinaryOperation, 0), CancellationToken.None);
        Assert.Null(ordinary.Reference);
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.RepeatableRead)]
    [InlineData(IsolationLevel.Serializable)]
    public async Task ModelProviderDraftReservationInsertRequiresReadCommitted(IsolationLevel isolation)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var providerId = "ordinary-" + Guid.NewGuid().ToString("N");
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
            DraftRequest(Guid.NewGuid(), 0), CancellationToken.None);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync(isolation);
        await using var insert = new NpgsqlCommand("""
            INSERT INTO projects_config.model_provider_reference_reservations
                (reference, reference_folded, provider_id, creation_revision, operation_id,
                 issuer, tenant_id, subject, created_at, installation_id)
            VALUES (@reference, @reference, @provider, 1, @operation, 'issuer', 'tenant', 'subject', now(),
                    (SELECT installation_id FROM projects_config.installation_identity WHERE singleton))
            """, connection, transaction);
        insert.Parameters.AddWithValue("reference", "isolation-" + Guid.NewGuid().ToString("N"));
        insert.Parameters.AddWithValue("provider", providerId);
        insert.Parameters.AddWithValue("operation", Guid.NewGuid());
        if (isolation == IsolationLevel.ReadCommitted)
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.SerializationFailure, error.SqlState);
        }
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ModelProviderDraftReferenceRejectsInheritedColumnUpdatePrivilege()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var candidate = "ref_candidate_" + suffix;
        var extra = "ref_extra_" + suffix;
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($"""
            CREATE ROLE "{candidate}" NOLOGIN INHERIT;
            CREATE ROLE "{extra}" NOLOGIN;
            GRANT "agentweaver-id-projects-config" TO "{candidate}";
            GRANT "{extra}" TO "{candidate}";
            GRANT UPDATE (subject) ON projects_config.model_provider_reference_reservations TO "{extra}";
            """, admin))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Options = "-c role=" + candidate,
            }.ConnectionString;
            await using var candidateSource = NpgsqlDataSource.Create(connectionString);
            await using var probe = await candidateSource.OpenConnectionAsync();
            await using var inherited = new NpgsqlCommand("""
                SELECT has_column_privilege(current_user,
                    'projects_config.model_provider_reference_reservations', 'subject', 'UPDATE')
                """, probe);
            Assert.True((bool)(await inherited.ExecuteScalarAsync() ?? false));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(candidateSource));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"""
                DROP OWNED BY "{extra}";
                DROP ROLE "{candidate}";
                DROP ROLE "{extra}";
                """, admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task ModelProviderDraftReferenceRollsBackHeadAndRevisionAfterReservationFault()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Fault-" + operation.ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        var functionName = "reference_fault_" + operation.ToString("N");
        await using var owner = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using (var install = new NpgsqlCommand($"""
            CREATE FUNCTION projects_config.{functionName}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.operation_id = '{operation}'::uuid THEN
                    RAISE EXCEPTION 'injected reservation fault' USING ERRCODE = 'XX000';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {functionName} BEFORE INSERT
            ON projects_config.model_provider_reference_reservations
            FOR EACH ROW EXECUTE FUNCTION projects_config.{functionName}();
            """, owner))
            await install.ExecuteNonQueryAsync();
        try
        {
            var failure = await Assert.ThrowsAsync<DbUpdateException>(() =>
                ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                    providerId, request, CancellationToken.None));
            Assert.Equal("XX000", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            Assert.False(await db.ModelProviderDrafts.AnyAsync(item => item.ProviderId == providerId));
            Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(item => item.OperationId == operation));
            Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(item => item.OperationId == operation));
        }
        finally
        {
            await using var remove = new NpgsqlCommand($"""
                DROP TRIGGER IF EXISTS {functionName} ON projects_config.model_provider_reference_reservations;
                DROP FUNCTION IF EXISTS projects_config.{functionName}();
                """, owner);
            await remove.ExecuteNonQueryAsync();
        }
        var recovered = await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform,
            null, providerId, request, CancellationToken.None);
        Assert.Equal(reference, recovered.Reference);
    }

    [Theory]
    [InlineData("23505", "pk_model_provider_reference_reservations", false)]
    [InlineData("23514", "ck_model_provider_reference_cross_table", false)]
    [InlineData("23505", "pk_model_provider_reference_reservations", true)]
    [InlineData("23514", "ck_model_provider_reference_cross_table", true)]
    public async Task ModelProviderDraftReferenceRecoversNamedDatabaseConflicts(
        string sqlState, string constraint, bool concurrentReplay)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Conflict-" + operation.ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        var functionName = "reference_conflict_" + operation.ToString("N");
        await using var owner = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using (var install = new NpgsqlCommand($"""
            CREATE FUNCTION projects_config.{functionName}() RETURNS trigger LANGUAGE plpgsql AS $body$
            BEGIN
                IF NEW.operation_id = '{operation}'::uuid THEN
                    RAISE EXCEPTION 'injected conflict' USING ERRCODE = '{sqlState}', CONSTRAINT = '{constraint}';
                END IF;
                RETURN NEW;
            END;
            $body$;
            CREATE TRIGGER {functionName} BEFORE INSERT
            ON projects_config.model_provider_reference_reservations
            FOR EACH ROW EXECUTE FUNCTION projects_config.{functionName}();
            """, owner))
            await install.ExecuteNonQueryAsync();
        var service = ReferenceService(db);
        service.AfterCreateConflict = async token =>
        {
            Assert.False(await db.ModelProviderDrafts.AsNoTracking().AnyAsync(item => item.ProviderId == providerId, token));
            Assert.False(await db.ModelProviderDraftRevisions.AsNoTracking().AnyAsync(item => item.OperationId == operation, token));
            Assert.False(await db.ModelProviderReferenceReservations.AsNoTracking().AnyAsync(item => item.OperationId == operation, token));
            if (concurrentReplay)
            {
                await using var remove = new NpgsqlCommand($"""
                    DROP TRIGGER {functionName} ON projects_config.model_provider_reference_reservations;
                    DROP FUNCTION projects_config.{functionName}();
                    """, owner);
                await remove.ExecuteNonQueryAsync(token);
                await using var winnerDb = CreateDbContext();
                await ReferenceService(winnerDb).SaveAsync(caller, ModelProviderBindingScope.Platform,
                    null, providerId, request, token);
            }
        };
        try
        {
            if (concurrentReplay)
            {
                var replay = await service.SaveAsync(caller, ModelProviderBindingScope.Platform,
                    null, providerId, request, CancellationToken.None);
                Assert.Equal(reference, replay.Reference);
                Assert.Equal(1, replay.Binding.ProviderRevision);
            }
            else
            {
                var conflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    service.SaveAsync(caller, ModelProviderBindingScope.Platform,
                        null, providerId, request, CancellationToken.None));
                Assert.Equal("reference_conflict", conflict.Code);
            }
        }
        finally
        {
            await using var remove = new NpgsqlCommand($"""
                DROP TRIGGER IF EXISTS {functionName} ON projects_config.model_provider_reference_reservations;
                DROP FUNCTION IF EXISTS projects_config.{functionName}();
                """, owner);
            await remove.ExecuteNonQueryAsync();
        }
        var result = await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform,
            null, providerId, request, CancellationToken.None);
        Assert.Equal(reference, result.Reference);
        Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(item => item.OperationId == operation));
        Assert.Equal(1, await db.ModelProviderReferenceReservations.CountAsync(item => item.OperationId == operation));
    }

    [Fact]
    public async Task ModelProviderDraftOrdinaryConcurrentCreatesReplayOneRevision()
    {
        await using var seed = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(seed);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var request = DraftRequest(operation, 0);
        await using var firstDb = CreateDbContext();
        await using var secondDb = CreateDbContext();
        var results = await Task.WhenAll(
            ReferenceService(firstDb).SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None),
            ReferenceService(secondDb).SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None));
        Assert.All(results, result => Assert.Equal(1, result.Binding.ProviderRevision));
        Assert.Equal(1, await seed.ModelProviderDraftRevisions.CountAsync(item => item.OperationId == operation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelProviderDraftReferenceMigrationDownPreservesPopulatedReservations(bool populated)
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeToAsync("20261119132116_ReserveModelProviderReferences");
        try
        {
        if (populated)
        {
            await using var connection = await isolated.DataSource.OpenConnectionAsync();
            await using var seed = new NpgsqlCommand("""
                INSERT INTO projects_config.model_providers
                    (provider_id, scope, project_id, display_name, type, base_url, deployment,
                     reasoning_efforts, state, current_revision, created_by_actor_id, created_at,
                     updated_by_actor_id, updated_at)
                VALUES ('migration-provider', 'Platform', NULL, 'Example', 'openai',
                    'https://example.com', 'model', ARRAY[]::text[], 'Draft', 1, 'actor', now(), 'actor', now());
                INSERT INTO projects_config.model_provider_revisions
                    (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                     display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                     created_by_actor_id, created_at)
                VALUES ('migration-provider', 1, 'Platform', NULL, @operation, repeat('a',64), 'Example',
                    'openai', 'https://example.com', 'model', ARRAY[]::text[], repeat('b',64), 'actor', now());
                INSERT INTO projects_config.model_provider_reference_reservations
                    (reference_folded, reference, provider_id, creation_revision, operation_id,
                     issuer, tenant_id, subject, created_at)
                VALUES ('retained-ref', 'Retained-Ref', 'migration-provider', 1, @operation,
                    'issuer', 'tenant', 'actor', now());
                """, connection);
            seed.Parameters.AddWithValue("operation", Guid.NewGuid());
            await seed.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(isolated.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        await using var context = new ProjectsConfigDbContext(options);
        var migrator = context.GetService<IMigrator>();
        if (populated)
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.MigrateAsync("20261118082708_PersistRuntimeProviderPins"));
            Assert.Contains("provider reference reservations must be retained", failure.MessageText);
            Assert.Equal(1, await context.ModelProviderReferenceReservations.CountAsync());
        }
        else
        {
            await migrator.MigrateAsync("20261118082708_PersistRuntimeProviderPins");
            await using var connection = await isolated.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT to_regclass('projects_config.model_provider_reference_reservations') IS NULL", connection);
            Assert.True((bool)(await command.ExecuteScalarAsync() ?? false));
        }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("Stable.Model", "5f466e3c59b9d7ac835aeb20aec7d8cdd0169e835402261d4583359032062b3f")]
    [InlineData("stable.model", "a668c83405e07f856e39757346262874e8623300e39c22062d39181b10051b4e")]
    public async Task ModelProviderDraftReferencePreservesConfigurationHashAndFingerprintsExactCase(
        string reference, string fingerprint)
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var db = new ProjectsConfigDbContext(options);
            var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            var caller = await SeedCallerAsync(db, authority, "golden-admin", "golden-tenant",
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var operation = Guid.Parse("00000000-0000-0000-0000-000000000001");
            const string providerId = "byok-00000000000000000000000000000001";
            var request = new SaveModelProviderDraftRequest(
                operation, 0, "Example", "openai", "https://example.com", "model-1")
            {
                Reference = reference,
                TargetInstallationId = isolated.InstallationId,
            };
            await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                providerId, request, CancellationToken.None);
            var stored = await db.ModelProviderDraftRevisions.AsNoTracking()
                .SingleAsync(item => item.ProviderId == providerId);
            Assert.Equal("177ac20040cdb6081c5ab040877ee5b555af2f63ae546afbfb71cd73e9132dda",
                stored.ConfigurationHash);
            Assert.Equal(fingerprint, stored.RequestFingerprint);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task ModelProviderDraftLegacyReceiptReplaysAfterReferenceMigration()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeToAsync("20261119132116_ReserveModelProviderReferences");
        try
        {
            var migrationOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var migrationDb = new ProjectsConfigDbContext(migrationOptions);
            var migrator = migrationDb.GetService<IMigrator>();
            await migrator.MigrateAsync("20261118082708_PersistRuntimeProviderPins");
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var db = new ProjectsConfigDbContext(options);
            var authority = new ProjectsConfigPrivilegedAuthorityStore(options, TimeProvider.System);
            var caller = await SeedCallerAsync(db, authority, "legacy-admin", "legacy-tenant",
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var operation = Guid.Parse("00000000-0000-0000-0000-000000000001");
            const string providerId = "byok-00000000000000000000000000000001";
            await using (var connection = await isolated.DataSource.OpenConnectionAsync())
            await using (var seed = new NpgsqlCommand("""
                INSERT INTO projects_config.model_providers
                    (provider_id, scope, project_id, display_name, type, base_url, deployment,
                     reasoning_efforts, state, current_revision, created_by_actor_id, created_at,
                     updated_by_actor_id, updated_at)
                VALUES (@id, 'Platform', NULL, 'Example', 'openai', 'https://example.com', 'model-1',
                    ARRAY[]::text[], 'Draft', 1, @actor, now(), @actor, now());
                INSERT INTO projects_config.model_provider_revisions
                    (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                     display_name, type, base_url, deployment, reasoning_efforts, configuration_hash,
                     created_by_actor_id, created_at)
                VALUES (@id, 1, 'Platform', NULL, @operation,
                    '77142077a56d8ecde6e11b557b81e6ac6848251cf7fae277467b088c0dfa50d0',
                    'Example', 'openai', 'https://example.com', 'model-1', ARRAY[]::text[],
                    '177ac20040cdb6081c5ab040877ee5b555af2f63ae546afbfb71cd73e9132dda', @actor, now());
                """, connection))
            {
                seed.Parameters.AddWithValue("id", providerId);
                seed.Parameters.AddWithValue("actor", caller.ActorId);
                seed.Parameters.AddWithValue("operation", operation);
                await seed.ExecuteNonQueryAsync();
            }
            await migrator.MigrateAsync();
            var request = new SaveModelProviderDraftRequest(
                operation, 0, "Example", "openai", "https://example.com", "model-1");
            var replay = await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform,
                null, providerId, request, CancellationToken.None);
            Assert.Null(replay.Reference);
            Assert.Equal(1, replay.Binding.ProviderRevision);
            Assert.Equal("177ac20040cdb6081c5ab040877ee5b555af2f63ae546afbfb71cd73e9132dda",
                replay.Binding.ConfigurationHash);
            Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("_start")]
    [InlineData("end_")]
    [InlineData("has__gap")]
    [InlineData("space here")]
    [InlineData("café")]
    [InlineData("")]
    public async Task ModelProviderDraftReferenceRejectsInvalidSyntax(string reference)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                "byok-" + operation.ToString("N"), DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId },
                CancellationToken.None));
        Assert.Equal("model_provider_draft_invalid_request", error.Code);
    }

    [Fact]
    public async Task ModelProviderDraftReferenceChecksLengthAndMissingBindingCopy()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Z" + new string('a', 255);
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        var missing = new ModelProviderDraftService(
            db, TimeProvider.System, ModelBindingCapabilities.Unavailable, true);
        var unavailable = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            missing.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None));
        Assert.Equal("model_provider_draft_unavailable", unavailable.Code);
        var invalid = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                request with { Reference = reference + "a" }, CancellationToken.None));
        Assert.Equal("model_provider_draft_invalid_request", invalid.Code);
        var created = await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform,
            null, providerId, request, CancellationToken.None);
        Assert.Equal(reference, created.Reference);
    }

    [Fact]
    public async Task ModelProviderDraftReferenceProtectsGeneratedIdsAndOrdinaryCreates()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var blocked = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db, bindingReference: providerId).SaveAsync(
                caller, ModelProviderBindingScope.Platform, null, providerId,
                DraftRequest(operation, 0) with { Reference = "Different-" + Guid.NewGuid().ToString("N"), TargetInstallationId = fixture.InstallationId },
                CancellationToken.None));
        Assert.Equal("reference_conflict", blocked.Code);
        var reserved = "Reserved-" + Guid.NewGuid().ToString("N");
        var firstOperation = Guid.NewGuid();
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
            "byok-" + firstOperation.ToString("N"),
            DraftRequest(firstOperation, 0) with { Reference = reserved, TargetInstallationId = fixture.InstallationId }, CancellationToken.None);
        var ordinary = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                reserved.ToLowerInvariant(), DraftRequest(Guid.NewGuid(), 0), CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", ordinary.Code);
    }

    [Fact]
    public async Task ModelProviderDraftReferenceCollidesWithProjectProviderIdsBothWays()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "reference-project-id-" + suffix;
        var subject = "reference-project-owner-" + suffix;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var owner = await SeedCallerAsync(db, authority, subject, tenant, ["api.read", "projects.admin"],
            ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Reference ID protection", CancellationToken.None);
        var bound = await ResolveCallerAsync(db, subject, tenant, ["api.read", "projects.admin"],
            boundProjectId: project.ProjectId);
        var projectOperation = Guid.NewGuid();
        var projectId = "byok-" + projectOperation.ToString("N");
        await ReferenceService(db).SaveAsync(bound, ModelProviderBindingScope.Project, project.ProjectId,
            projectId, DraftRequest(projectOperation, 0), CancellationToken.None);
        var platformOperation = Guid.NewGuid();
        var conflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                "byok-" + platformOperation.ToString("N"),
                DraftRequest(platformOperation, 0) with { Reference = projectId.ToUpperInvariant(), TargetInstallationId = fixture.InstallationId },
                CancellationToken.None));
        Assert.Equal("reference_conflict", conflict.Code);

        var futureProjectOperation = Guid.NewGuid();
        var futureProjectId = "byok-" + futureProjectOperation.ToString("N");
        var reservationOperation = Guid.NewGuid();
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
            "byok-" + reservationOperation.ToString("N"),
            DraftRequest(reservationOperation, 0) with { Reference = futureProjectId.ToUpperInvariant(), TargetInstallationId = fixture.InstallationId },
            CancellationToken.None);
        var projectConflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            ReferenceService(db).SaveAsync(bound, ModelProviderBindingScope.Project, project.ProjectId,
                futureProjectId, DraftRequest(futureProjectOperation, 0), CancellationToken.None));
        Assert.Equal("model_provider_draft_conflict", projectConflict.Code);
    }

    [Fact]
    public async Task ModelProviderDraftReferenceFailsClosedOnMissingCurrentProjectRevision()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = "reference-missing-" + suffix;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var owner = await SeedCallerAsync(db, authority, "reference-owner-" + suffix, tenant,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(owner, "Missing revision", CancellationToken.None);
        var row = await db.Projects.SingleAsync(item => item.ProjectId == project.ProjectId);
        row.ConfigurationRevision = 999999;
        await db.SaveChangesAsync();
        try
        {
            var operation = Guid.NewGuid();
            var providerId = "byok-" + operation.ToString("N");
            var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                    DraftRequest(operation, 0) with { Reference = "Missing-" + suffix, TargetInstallationId = fixture.InstallationId }, CancellationToken.None));
            Assert.Equal("model_provider_draft_unavailable", error.Code);
            Assert.False(await db.ModelProviderDrafts.AnyAsync(item => item.ProviderId == providerId));
        }
        finally
        {
            row.ConfigurationRevision = project.ConfigurationRevision;
            await db.SaveChangesAsync();
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ModelProviderDraftReferenceAuthorityLockOrdersRevocation(bool membership, bool replay)
    {
        await using var seed = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(seed);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Locked-" + Guid.NewGuid().ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        if (replay)
            await ReferenceService(seed).SaveAsync(
                caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);

        var runtimeConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
            ApplicationName = "reference-create-" + operation.ToString("N"),
        }.ConnectionString;
        await using var runtimeSource = NpgsqlDataSource.Create(runtimeConnection);
        var runtimeOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(runtimeSource, options => options.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        await using var runtimeDb = new ProjectsConfigDbContext(runtimeOptions);
        var service = ReferenceService(runtimeDb);
        var locked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        service.AfterReferenceAuthorityLocked = async (pid, token) =>
        {
            Assert.Equal(replay, await runtimeDb.ModelProviderDrafts.AsNoTracking()
                .AnyAsync(item => item.ProviderId == providerId, token));
            locked.TrySetResult(pid);
            await release.Task.WaitAsync(token);
        };
        var create = service.SaveAsync(
            caller, ModelProviderBindingScope.Platform, null, providerId, request, timeout.Token);
        Task? revoke = null;
        try
        {
            var createPid = await locked.Task.WaitAsync(timeout.Token);
            var appName = "reference-revoke-" + operation.ToString("N");
            var revokeConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                ApplicationName = appName,
            }.ConnectionString;
            var revokeOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(revokeConnection, options => options.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            var authority = new ProjectsConfigPrivilegedAuthorityStore(revokeOptions, TimeProvider.System);
            var grant = Assert.Single(caller.Grants.Where(item => item.Role == ProjectAuthorityRole.PlatformAdmin));
            revoke = membership
                ? authority.RevokeMembershipAsync(caller.MembershipId, caller.MembershipRevision, "fixture", timeout.Token)
                : authority.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, "fixture", timeout.Token);
            await using var observer = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            while (true)
            {
                await using var query = new NpgsqlCommand("""
                    SELECT EXISTS (
                        SELECT 1 FROM pg_stat_activity
                        WHERE application_name = @name AND wait_event_type = 'Lock'
                          AND @create_pid = ANY(pg_blocking_pids(pid)))
                    """, observer);
                query.Parameters.AddWithValue("name", appName);
                query.Parameters.AddWithValue("create_pid", createPid);
                if ((bool)(await query.ExecuteScalarAsync(timeout.Token) ?? false))
                    break;
                Assert.False(revoke.IsCompleted);
                await Task.Delay(20, timeout.Token);
            }
            Assert.False(revoke.IsCompleted);
            release.TrySetResult();
            var result = await create.WaitAsync(timeout.Token);
            await revoke.WaitAsync(timeout.Token);
            Assert.Equal(reference, result.Reference);
            Assert.Equal(1, await seed.ModelProviderDrafts.CountAsync(item => item.ProviderId == providerId));
            Assert.Equal(1, await seed.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
            Assert.Equal(1, await seed.ModelProviderReferenceReservations.CountAsync(item =>
                item.ProviderId == providerId && item.Reference == reference &&
                item.OperationId == operation && item.Issuer == caller.Issuer &&
                item.TenantId == caller.TenantId && item.Subject == caller.ActorId));

            var deniedReplay = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                ReferenceService(seed).SaveAsync(
                    caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, deniedReplay.StatusCode);
            var nextOperation = Guid.NewGuid();
            var deniedNew = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                ReferenceService(seed).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                    "byok-" + nextOperation.ToString("N"),
                    DraftRequest(nextOperation, 0) with { Reference = reference + "-new", TargetInstallationId = fixture.InstallationId },
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, deniedNew.StatusCode);
            Assert.Equal(1, await seed.ModelProviderReferenceReservations.CountAsync(item => item.ProviderId == providerId));
            Assert.False(await seed.ModelProviderDraftRevisions.AnyAsync(item => item.OperationId == nextOperation));
        }
        finally
        {
            release.TrySetResult();
            timeout.Cancel();
            try { await create; } catch { }
            if (revoke is not null)
                try { await revoke; } catch { }
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task ModelProviderDraftReferenceRevocationBeforeLockDeniesCreateAndReplay(bool membership, bool replay)
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var providerId = "byok-" + operation.ToString("N");
        var reference = "Revoked-" + Guid.NewGuid().ToString("N");
        var request = DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = fixture.InstallationId };
        if (replay)
            await ReferenceService(db).SaveAsync(
                caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var grant = Assert.Single(caller.Grants.Where(item => item.Role == ProjectAuthorityRole.PlatformAdmin));
        if (membership)
            await authority.RevokeMembershipAsync(caller.MembershipId, caller.MembershipRevision, "fixture");
        else
            await authority.RevokeRoleAssignmentAsync(grant.AssignmentId, grant.Revision, "fixture");
        var error = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            ReferenceService(db).SaveAsync(
                caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None));
        Assert.Equal(StatusCodes.Status403Forbidden, error.StatusCode);
        Assert.Equal(replay ? 1 : 0, await db.ModelProviderDrafts.CountAsync(item => item.ProviderId == providerId));
        Assert.Equal(replay ? 1 : 0, await db.ModelProviderDraftRevisions.CountAsync(item => item.ProviderId == providerId));
        Assert.Equal(replay ? 1 : 0,
            await db.ModelProviderReferenceReservations.CountAsync(item => item.ProviderId == providerId));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ModelProviderDraftReferenceLockTestDetectsMissingAuthorityLock(bool membership)
    {
        await using var connection = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using var read = new NpgsqlCommand("""
            SELECT pg_get_functiondef(
                'projects_config.lock_native_acs_policy_authority(uuid,text,text,text,bigint,text,text,uuid[],text[],text[],text[],bigint[])'::regprocedure)
            """, connection);
        var original = (string)(await read.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("The authority function is missing."));
        var locks = Regex.Matches(original, @"\bFOR\s+SHARE\b", RegexOptions.IgnoreCase);
        Assert.Equal(3, locks.Count);
        var selected = locks[membership ? 0 : 2];
        var mutant = original.Remove(selected.Index, selected.Length);
        try
        {
            await using var mutate = new NpgsqlCommand(mutant, connection);
            await mutate.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<Xunit.Sdk.FalseException>(() =>
                ModelProviderDraftReferenceAuthorityLockOrdersRevocation(membership, false));
        }
        finally
        {
            await using var restore = new NpgsqlCommand(original, connection);
            await restore.ExecuteNonQueryAsync();
        }
    }

    private ModelProviderDraftService ReferenceService(
        ProjectsConfigDbContext db, bool enabled = true, string? bindingReference = null, string source = "Byok",
        bool bindingEnabled = false)
    {
        var values = new Dictionary<string, string?>
        {
            ["AgentHost:ModelBindingsRevision"] = "reference-test-v1",
            ["AgentHost:ModelBindings:" + (bindingReference ?? "fixture-binding") + ":SourceMode"] = source,
            ["AgentHost:ModelBindings:" + (bindingReference ?? "fixture-binding") + ":Enabled"] =
                bindingEnabled ? "true" : "false",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ModelProviderDraftService(db, TimeProvider.System,
            ModelBindingCapabilities.FromConfiguration(configuration), enabled);
    }

    private async Task<ProjectAuthorizationContext> CreateReferenceAdminAsync(ProjectsConfigDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "reference-tenant-" + suffix;
        var authority = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        return await SeedCallerAsync(db, authority, "reference-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
            ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
    }
}
