using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlatformSwitchPinsFreshStartsAndExactReplaySurvivesLaterDeactivation(bool interactive)
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "switch-tenant-" + suffix;
            var admin = await SeedCallerAsync(db, authority, "switch-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, authority, "switch-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var initial = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                (await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None)).Revision,
                PlatformDefaults() with
                {
                    ModelTaskDefaults =
                    [
                        .. ModelTaskSelectionDefaults.Create()
                            .Select(item => item.TaskKind == ModelTaskKind.Planning
                                ? new ModelTaskSelectionOverride(
                                    item.TaskKind, new ModelSelectionOverride("explicit-planning", "low"))
                                : item),
                    ],
                    ModelRoleDefaults = [new("reviewer", new("explicit-role", "high"))],
                    ModelAgentDefaults = [new("writer", new("explicit-agent", "medium"))],
                },
                CancellationToken.None);
            var provider = await SeedAvailablePlatformByokProviderAsync(
                tests, db, admin, "switch-provider-" + suffix);

            var revisionError = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ActivatePlatformModelProviderAsync(
                    admin, provider.ProviderId, initial.Revision - 1, provider.ProviderRevision,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status400BadRequest, revisionError.StatusCode);
            var providerError = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ActivatePlatformModelProviderAsync(
                    admin, provider.ProviderId, initial.Revision, provider.ProviderRevision + 1,
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, providerError.StatusCode);

            var activated = await service.ActivatePlatformModelProviderAsync(
                admin, provider.ProviderId, initial.Revision, provider.ProviderRevision,
                CancellationToken.None);
            Assert.Equal("active", activated.Mode);
            Assert.Equal(provider.ProviderId, activated.ActiveProviderId);
            var activeSettings = await service.GetPlatformRuntimeDefaultsAsync(
                admin, CancellationToken.None);
            Assert.Equal(activated.SettingsRevision, activeSettings.Revision);
            Assert.Equal(provider.ProviderId, activeSettings.Defaults!.ModelSelection!.Reference);
            Assert.Equal(provider.ProviderId,
                Assert.Single(activeSettings.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.CodeWriting).Selection.Reference);
            Assert.Equal("explicit-planning",
                Assert.Single(activeSettings.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.Planning).Selection.Reference);
            Assert.Equal("explicit-role", activeSettings.Defaults.ModelRoleDefaults[0].Selection.Reference);
            Assert.Equal("explicit-agent", activeSettings.Defaults.ModelAgentDefaults[0].Selection.Reference);

            var ordinary = activeSettings.Defaults! with
            {
                RunLimits = activeSettings.Defaults.RunLimits with { MaxToolCalls = 99 },
            };
            var edited = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin, activeSettings.Revision, ordinary, CancellationToken.None);
            Assert.Equal(PlatformModelDefaultSwitchMode.Active,
                edited.Defaults!.ModelDefaultSwitch.Mode);
            Assert.Equal(provider.ProviderId, edited.Defaults.ModelSelection!.Reference);
            Assert.Equal(99, edited.Defaults.RunLimits.MaxToolCalls);

            var project = await service.CreateProjectAsync(owner, "Switch project", CancellationToken.None);
            var projectSettings = await service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision,
                ProjectSettings() with
                {
                    ModelSelection = null,
                    AgentCharters =
                    [
                        new("writer", "Writer", "writer-role", "Write"),
                        new("reviewer-agent", "Reviewer", "reviewer", "Review"),
                    ],
                    Casting =
                    [
                        new("writer", "writer-role", 0),
                        new("reviewer-agent", "reviewer", 1),
                    ],
                    ModelTaskOverrides =
                    [
                        new(ModelTaskKind.CodeWriting, new ModelSelectionOverride(
                            ReasoningEffort: "low")),
                    ],
                    ModelRoleOverrides =
                    [
                        new("reviewer", new ModelSelectionOverride("explicit-project-role", "high")),
                    ],
                },
                CancellationToken.None);
            var orchestratorId = "switch-orch-" + suffix;
            var membership = await SeedMembershipAsync(authority, orchestratorId, tenantId);
            await authority.AssignRoleAsync(
                membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var runId = "switch-run-" + suffix;
            var orchestrator = await ResolveCallerAsync(
                db, orchestratorId, tenantId, ["api.read", "projects.orchestrator"],
                project.ProjectId, runId);

            AcceptRunSelectionRequest request = RunRequest(projectSettings.Revision, edited.Revision) with
            {
                Context = RunRequest(projectSettings.Revision, edited.Revision).Context with
                {
                    AvailableModelSelectionReferences =
                        RunRequest(projectSettings.Revision, edited.Revision).Context
                            .AvailableModelSelectionReferences
                            .Add(provider.ProviderId)
                            .Add("explicit-planning")
                            .Add("explicit-role")
                            .Add("explicit-agent")
                            .Add("explicit-project-role"),
                },
            };
            var accepted = interactive
                ? await service.AcceptInteractiveRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None)
                : await service.AcceptRunSelectionAsync(
                    orchestrator, project.ProjectId, runId, request, CancellationToken.None);
            Assert.Equal(provider.ProviderId,
                accepted.ModelSelections
                    .Where(item => item.Selection.Reference == provider.ProviderId)
                    .Single(item => item.Selection.ReasoningEffort == "low")
                    .Selection.Reference);
            Assert.Equal(provider.ProviderRevision,
                Assert.Single(accepted.ModelSelections,
                    item => item.Selection.Reference == provider.ProviderId &&
                        item.Selection.ReasoningEffort == "low")
                    .Selection.ProviderBindingRevision!.ProviderRevision);
            var explicitRoleSelection = Assert.Single(accepted.ModelSelections,
                item => item.Selection.Reference == "explicit-project-role");
            Assert.Null(explicitRoleSelection.Selection.SourceMode);
            Assert.Null(explicitRoleSelection.Selection.ConnectionId);
            Assert.Null(explicitRoleSelection.Selection.CredentialReference);
            Assert.Null(explicitRoleSelection.Selection.CredentialIdentity);
            Assert.Null(explicitRoleSelection.Selection.ModelBindingPin);
            var explicitPlanningSelection = Assert.Single(accepted.ModelSelections,
                item => item.Selection.Reference == "explicit-planning");
            Assert.Null(explicitPlanningSelection.Selection.SourceMode);
            Assert.Null(explicitPlanningSelection.Selection.ConnectionId);
            Assert.Null(explicitPlanningSelection.Selection.CredentialReference);
            Assert.Null(explicitPlanningSelection.Selection.CredentialIdentity);
            Assert.Null(explicitPlanningSelection.Selection.ModelBindingPin);
            var explicitAgentSelections = accepted.ModelSelections
                .Where(item => item.Selection.Reference == "explicit-agent")
                .ToArray();
            Assert.NotEmpty(explicitAgentSelections);
            Assert.All(explicitAgentSelections, item =>
            {
                Assert.Null(item.Selection.SourceMode);
                Assert.Null(item.Selection.ConnectionId);
                Assert.Null(item.Selection.CredentialReference);
                Assert.Null(item.Selection.CredentialIdentity);
                Assert.Null(item.Selection.ModelBindingPin);
            });

            var deactivated = await service.DeactivatePlatformModelProviderAsync(
                admin, edited.Revision, CancellationToken.None);
            Assert.Equal("explicitHosted", deactivated.Mode);
            Assert.Null(deactivated.ActiveProviderId);
            var replay = interactive
                ? await service.AcceptInteractiveRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None)
                : await service.AcceptRunSelectionAsync(
                    orchestrator, project.ProjectId, runId, request, CancellationToken.None);
            Assert.Equal(accepted.PlatformRuntimeRevision, replay.PlatformRuntimeRevision);
            Assert.Equal(accepted.ModelSelection, replay.ModelSelection);

            var hostedRunId = "switch-hosted-" + suffix;
            var hostedOrchestrator = await ResolveCallerAsync(
                db, orchestratorId, tenantId, ["api.read", "projects.orchestrator"],
                project.ProjectId, hostedRunId);
            var fresh = interactive
                ? await service.AcceptInteractiveRunSelectionAsync(
                    hostedOrchestrator, project.ProjectId, hostedRunId,
                    new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None)
                : await service.AcceptRunSelectionAsync(
                    hostedOrchestrator, project.ProjectId, hostedRunId,
                    request with
                    {
                        ExpectedPlatformRuntimeRevision = deactivated.SettingsRevision,
                    },
                    CancellationToken.None);
            Assert.DoesNotContain(fresh.ModelSelections,
                item => item.Selection.Reference == provider.ProviderId);
            Assert.Equal(deactivated.SettingsRevision, fresh.PlatformRuntimeRevision);

            var adminGrant = Assert.Single(
                admin.Grants, item => item.Role == ProjectAuthorityRole.PlatformAdmin);
            await authority.RevokeRoleAssignmentAsync(
                adminGrant.AssignmentId, adminGrant.Revision, "test", CancellationToken.None);
            var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ActivatePlatformModelProviderAsync(
                    admin, provider.ProviderId, deactivated.SettingsRevision,
                    provider.ProviderRevision, CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        });
    }

    private static async Task<ModelProviderBindingRevision> SeedAvailablePlatformByokProviderAsync(
        ProjectsConfigPostgresTests tests,
        ProjectsConfigDbContext db,
        ProjectAuthorizationContext admin,
        string providerId)
    {
        var credentialSlotId = Guid.NewGuid();
        var secretId = "platform-slot-" + providerId;
        var rateCard = new RuntimeRateCardPin(
            "card-" + providerId,
            "v1",
            new string('c', 64),
            new ManualRuntimeRateCardReadProvenance(
                "https://pricing.example/platform-card", new DateOnly(2026, 1, 1)));
        await using var connection = await tests.OpenOwnerConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var disable = new NpgsqlCommand(
            "ALTER TABLE projects_config.model_providers DISABLE TRIGGER USER; " +
            "ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER USER;",
            connection, transaction))
            await disable.ExecuteNonQueryAsync();
        try
        {
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO projects_config.model_providers
                  (provider_id, scope, project_id, display_name, type, base_url, deployment, wire_api,
                   reasoning_efforts, prompt_capacity_tokens, state, current_revision,
                   created_by_actor_id, created_at, updated_by_actor_id, updated_at)
                VALUES (@provider, 'Platform', NULL, 'Switched model', 'openai',
                        'https://provider.example', 'deployment-1', 'responses',
                        ARRAY['low', 'high'], 32768, 'Available', 1,
                        @actor, now(), @actor, now());
                INSERT INTO projects_config.model_provider_revisions
                  (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                   display_name, type, base_url, deployment, wire_api, reasoning_efforts,
                   prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
                   credential_secret_id, rate_card_id, rate_card_version, rate_card_pin_json,
                   created_by_actor_id, created_at)
                VALUES (@provider, 1, 'Platform', NULL, @operation, repeat('a', 64),
                        'Switched model', 'openai', 'https://provider.example', 'deployment-1',
                        'responses', ARRAY['low', 'high'], 32768, repeat('b', 64), 'v1',
                        @slot, @secret, @card_id, @card_version, @pin, @actor, now());
                """,
                connection, transaction);
            command.Parameters.AddWithValue("provider", providerId);
            command.Parameters.AddWithValue("actor", admin.ActorId);
            command.Parameters.AddWithValue("operation", Guid.NewGuid());
            command.Parameters.AddWithValue("slot", credentialSlotId);
            command.Parameters.AddWithValue("secret", secretId);
            command.Parameters.AddWithValue("card_id", rateCard.RateCardId);
            command.Parameters.AddWithValue("card_version", rateCard.RateCardVersion);
            command.Parameters.AddWithValue("pin", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(rateCard, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var enable = new NpgsqlCommand(
                "ALTER TABLE projects_config.model_providers ENABLE TRIGGER USER; " +
                "ALTER TABLE projects_config.model_provider_revisions ENABLE TRIGGER USER;",
                connection, transaction);
            await enable.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return (await new ModelProviderDraftService(db, TimeProvider.System).ReadAsync(
            admin, ModelProviderBindingScope.Platform, null, providerId,
            CancellationToken.None)).Binding;
    }

    [Fact]
    public async Task PlatformSwitchDeactivateRestoresShippedTasksAfterBaseOnlyAndEffortOverrides()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "baseonly-tenant-" + suffix;
            var admin = await SeedCallerAsync(db, authority, "baseonly-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var initial = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                0,
                PlatformDefaults() with
                {
                    ModelSelection = new ModelSelectionSettings(
                        "base-hosted", null, ModelSourceMode.HostedCopilot, Guid.NewGuid()),
                    ModelTaskDefaults =
                    [
                        new(ModelTaskKind.Planning, new("explicit-planning", "low")),
                        new(ModelTaskKind.CodeWriting, new(Reference: null, ReasoningEffort: "low")),
                    ],
                    ModelDefaultSwitch = new PlatformModelDefaultSwitch
                    {
                        Mode = PlatformModelDefaultSwitchMode.ExplicitHosted,
                    },
                },
                CancellationToken.None);
            Assert.Equal(PlatformModelDefaultSwitchMode.Untouched, initial.Defaults!.ModelDefaultSwitch.Mode);

            var provider = await SeedAvailablePlatformByokProviderAsync(
                tests, db, admin, "baseonly-provider-" + suffix);
            var activated = await service.ActivatePlatformModelProviderAsync(
                admin, provider.ProviderId, initial.Revision, provider.ProviderRevision,
                CancellationToken.None);
            var deactivated = await service.DeactivatePlatformModelProviderAsync(
                admin, activated.SettingsRevision, CancellationToken.None);
            var after = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            Assert.Equal(deactivated.SettingsRevision, after.Revision);
            Assert.Null(after.Defaults!.ModelSelection);
            Assert.Equal(PlatformModelDefaultSwitchMode.ExplicitHosted, after.Defaults.ModelDefaultSwitch.Mode);
            var shipped = ModelTaskSelectionDefaults.Create().ToDictionary(item => item.TaskKind);
            Assert.Equal(shipped.Count, after.Defaults.ModelTaskDefaults.Length);
            Assert.Equal("explicit-planning",
                Assert.Single(after.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.Planning).Selection.Reference);
            var codeWriting = Assert.Single(after.Defaults.ModelTaskDefaults,
                item => item.TaskKind == ModelTaskKind.CodeWriting);
            Assert.Equal(shipped[ModelTaskKind.CodeWriting].Selection.Reference, codeWriting.Selection.Reference);
            Assert.Equal("low", codeWriting.Selection.ReasoningEffort);
            Assert.Equal(shipped[ModelTaskKind.Review].Selection,
                Assert.Single(after.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.Review).Selection);
        });
    }

    [Fact]
    public async Task PlatformSwitchEffortOverrideRejectsStaleProviderBindingAndConcurrencyMapsTo409()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db1 = tests.CreateDbContext();
            await using var db2 = tests.CreateDbContext();
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var service1 = new ProjectsConfigService(db1, CreateProviderCatalog(), TimeProvider.System);
            var service2 = new ProjectsConfigService(db2, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "stale-tenant-" + suffix;
            var admin = await SeedCallerAsync(db1, authority, "stale-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db1, authority, "stale-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var initial = await service1.UpdatePlatformRuntimeDefaultsAsync(
                admin, 0, PlatformDefaults(), CancellationToken.None);
            var provider = await SeedAvailablePlatformByokProviderAsync(
                tests, db1, admin, "stale-provider-" + suffix);

            var first = service1.ActivatePlatformModelProviderAsync(
                admin, provider.ProviderId, initial.Revision, provider.ProviderRevision,
                CancellationToken.None);
            var second = service2.ActivatePlatformModelProviderAsync(
                admin, provider.ProviderId, initial.Revision, provider.ProviderRevision,
                CancellationToken.None);
            var outcomes = await Task.WhenAll(
                first.ContinueWith(task => task.Exception?.GetBaseException()),
                second.ContinueWith(task => task.Exception?.GetBaseException()));
            Assert.Single(outcomes, ex => ex is null);
            var conflict = Assert.IsType<ProjectConfigException>(Assert.Single(outcomes, ex => ex is not null));
            Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);

            var active = await service1.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            await using (var ownerConnection = await tests.OpenOwnerConnectionAsync())
            {
                await using var bump = new NpgsqlCommand(
                    """
                    ALTER TABLE projects_config.model_providers DISABLE TRIGGER USER;
                    ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER USER;
                    INSERT INTO projects_config.model_provider_revisions
                      (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                       display_name, type, base_url, deployment, wire_api, reasoning_efforts,
                       prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
                       credential_secret_id, rate_card_id, rate_card_version, rate_card_pin_json,
                       created_by_actor_id, created_at)
                    SELECT provider_id, 2, scope, project_id, gen_random_uuid(), repeat('d', 64),
                           display_name, type, base_url, 'deployment-2', wire_api, reasoning_efforts,
                           prompt_capacity_tokens, repeat('e', 64), credential_version, credential_slot_id,
                           credential_secret_id, rate_card_id, rate_card_version, rate_card_pin_json,
                           created_by_actor_id, now()
                    FROM projects_config.model_provider_revisions
                    WHERE provider_id = @provider AND provider_revision = 1;
                    UPDATE projects_config.model_providers
                    SET current_revision = 2
                    WHERE provider_id = @provider;
                    ALTER TABLE projects_config.model_providers ENABLE TRIGGER USER;
                    ALTER TABLE projects_config.model_provider_revisions ENABLE TRIGGER USER;
                    """,
                    ownerConnection);
                bump.Parameters.AddWithValue("provider", provider.ProviderId);
                await bump.ExecuteNonQueryAsync();
            }

            var project = await service1.CreateProjectAsync(owner, "Stale pin project", CancellationToken.None);
            var projectSettings = await service1.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision,
                ProjectSettings() with
                {
                    ModelSelection = null,
                    ModelTaskOverrides = [new(ModelTaskKind.CodeWriting, new(ReasoningEffort: "low"))],
                },
                CancellationToken.None);
            var orchestratorId = "stale-orch-" + suffix;
            var membership = await SeedMembershipAsync(authority, orchestratorId, tenantId);
            await authority.AssignRoleAsync(
                membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var runId = "stale-run-" + suffix;
            var orchestrator = await ResolveCallerAsync(
                db1, orchestratorId, tenantId, ["api.read", "projects.orchestrator"],
                project.ProjectId, runId);

            var staleError = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service1.AcceptInteractiveRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None));
            Assert.Equal(StatusCodes.Status422UnprocessableEntity, staleError.StatusCode);
            Assert.Equal("model_provider_binding_stale", staleError.Code);
        });
    }

    [Fact]
    public async Task PlatformSwitchSucceedsUnderDeployedSelectOnlyRuntimeRole()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var adminDb = tests.CreateDbContext();
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var adminService = new ProjectsConfigService(adminDb, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "role-tenant-" + suffix;
            var admin = await SeedCallerAsync(adminDb, authority, "role-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var initial = await adminService.UpdatePlatformRuntimeDefaultsAsync(
                admin, 0, PlatformDefaults(), CancellationToken.None);
            var provider = await SeedAvailablePlatformByokProviderAsync(
                tests, adminDb, admin, "role-provider-" + suffix);

            await using var runtimeDb = tests.CreateDbContext();
            await runtimeDb.Database.OpenConnectionAsync();
            try
            {
                await runtimeDb.Database.ExecuteSqlRawAsync(
                    """SET ROLE "agentweaver-id-projects-config";""");
                var runtimeService = new ProjectsConfigService(
                    runtimeDb, CreateProviderCatalog(), TimeProvider.System);
                var activated = await runtimeService.ActivatePlatformModelProviderAsync(
                    admin, provider.ProviderId, initial.Revision, provider.ProviderRevision,
                    CancellationToken.None);
                Assert.Equal("active", activated.Mode);
                var deactivated = await runtimeService.DeactivatePlatformModelProviderAsync(
                    admin, activated.SettingsRevision, CancellationToken.None);
                Assert.Equal("explicitHosted", deactivated.Mode);
            }
            finally
            {
                await runtimeDb.Database.ExecuteSqlRawAsync("RESET ROLE;");
            }
        });
    }

    [Fact]
    public async Task SameProviderRefreshAdvancesManagedTasksAndPreservesRestoredUnmanagedAndCustomTasks()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "refresh-tenant-" + suffix;
            var admin = await SeedCallerAsync(db, authority, "refresh-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, authority, "refresh-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);

            var shipped = ModelTaskSelectionDefaults.Create().ToDictionary(item => item.TaskKind);
            var initial = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                (await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None)).Revision,
                PlatformDefaults() with
                {
                    ModelTaskDefaults =
                    [
                        .. ModelTaskSelectionDefaults.Create().Select(item => item.TaskKind switch
                        {
                            ModelTaskKind.Planning => new ModelTaskSelectionOverride(
                                item.TaskKind, new ModelSelectionOverride("explicit-planning", "low")),
                            ModelTaskKind.Review => new ModelTaskSelectionOverride(
                                item.TaskKind, new ModelSelectionOverride("explicit-review", "high")),
                            _ => item,
                        }),
                    ],
                },
                CancellationToken.None);

            var providerA = await SeedAvailablePlatformByokProviderAsync(
                tests, db, admin, "refresh-a-" + suffix);
            var providerB = await SeedAvailablePlatformByokProviderAsync(
                tests, db, admin, "refresh-b-" + suffix);

            var activated1 = await service.ActivatePlatformModelProviderAsync(
                admin, providerA.ProviderId, initial.Revision, 1, CancellationToken.None);

            var project = await service.CreateProjectAsync(owner, "Refresh project", CancellationToken.None);
            var projectSettings = await service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision,
                ProjectSettings() with
                {
                    ModelSelection = null,
                    ModelTaskOverrides =
                    [
                        new(ModelTaskKind.CodeWriting, new ModelSelectionOverride(ReasoningEffort: "low")),
                    ],
                },
                CancellationToken.None);

            var orchestratorId = "refresh-orch-" + suffix;
            var membership = await SeedMembershipAsync(authority, orchestratorId, tenantId);
            await authority.AssignRoleAsync(
                membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);

            var runBeforeId = "refresh-before-" + suffix;
            var orchBefore = await ResolveCallerAsync(
                db, orchestratorId, tenantId, ["api.read", "projects.orchestrator"],
                project.ProjectId, runBeforeId);
            var acceptedBefore = await service.AcceptInteractiveRunSelectionAsync(
                orchBefore, project.ProjectId, runBeforeId,
                new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None);
            Assert.Equal(1,
                Assert.Single(acceptedBefore.ModelSelections,
                    item => item.Selection.Reference == providerA.ProviderId &&
                        item.Selection.ReasoningEffort == "low")
                    .Selection.ProviderBindingRevision!.ProviderRevision);

            // Restore Planning back to the shipped installation default while keeping Review custom.
            var activeBeforeRestore = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            var restored = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                activeBeforeRestore.Revision,
                activeBeforeRestore.Defaults! with
                {
                    ModelTaskDefaults =
                    [
                        .. activeBeforeRestore.Defaults.ModelTaskDefaults.Select(item =>
                            item.TaskKind == ModelTaskKind.Planning ? shipped[ModelTaskKind.Planning] : item),
                    ],
                },
                CancellationToken.None);
            Assert.DoesNotContain(ModelTaskKind.Planning, restored.Defaults!.ModelDefaultSwitch.ManagedTaskKinds);
            Assert.DoesNotContain(ModelTaskKind.Review, restored.Defaults.ModelDefaultSwitch.ManagedTaskKinds);

            // Bump providerA to revision 2 (rotated key + rechecked revision).
            await using (var ownerConnection = await tests.OpenOwnerConnectionAsync())
            {
                await using var bump = new NpgsqlCommand(
                    """
                    ALTER TABLE projects_config.model_providers DISABLE TRIGGER USER;
                    ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER USER;
                    INSERT INTO projects_config.model_provider_revisions
                      (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                       display_name, type, base_url, deployment, wire_api, reasoning_efforts,
                       prompt_capacity_tokens, configuration_hash, credential_version, credential_slot_id,
                       credential_secret_id, rate_card_id, rate_card_version, rate_card_pin_json,
                       created_by_actor_id, created_at)
                    SELECT provider_id, 2, scope, project_id, gen_random_uuid(), repeat('f', 64),
                           display_name, type, base_url, 'deployment-2', wire_api, reasoning_efforts,
                           prompt_capacity_tokens, repeat('1', 64), 'v2', credential_slot_id,
                           credential_secret_id, rate_card_id, rate_card_version, rate_card_pin_json,
                           created_by_actor_id, now()
                    FROM projects_config.model_provider_revisions
                    WHERE provider_id = @provider AND provider_revision = 1;
                    UPDATE projects_config.model_providers
                    SET current_revision = 2
                    WHERE provider_id = @provider;
                    ALTER TABLE projects_config.model_providers ENABLE TRIGGER USER;
                    ALTER TABLE projects_config.model_provider_revisions ENABLE TRIGGER USER;
                    """,
                    ownerConnection);
                bump.Parameters.AddWithValue("provider", providerA.ProviderId);
                await bump.ExecuteNonQueryAsync();
            }

            // Positive stale settings revision and stale provider revision both fail with 409.
            var staleSettings = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ActivatePlatformModelProviderAsync(
                    admin, providerA.ProviderId, activated1.SettingsRevision, 2, CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, staleSettings.StatusCode);

            var staleProvider = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ActivatePlatformModelProviderAsync(
                    admin, providerA.ProviderId, restored.Revision, 1, CancellationToken.None));
            Assert.Equal(StatusCodes.Status409Conflict, staleProvider.StatusCode);

            // Same-provider refresh to revision 2 advances base + managed tasks, preserves Planning and Review.
            var refreshed = await service.ActivatePlatformModelProviderAsync(
                admin, providerA.ProviderId, restored.Revision, 2, CancellationToken.None);
            var afterRefresh = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            Assert.Equal(2, afterRefresh.Defaults!.ModelDefaultSwitch.ProviderRevision);
            Assert.Equal("2", afterRefresh.Defaults.ModelSelection!.ModelBindingPin!.ConfigurationRevision);
            Assert.Equal("v2", afterRefresh.Defaults.ModelSelection.CredentialReference!.Version);
            Assert.Equal(restored.Defaults.ModelDefaultSwitch.ManagedTaskKinds.ToArray(),
                afterRefresh.Defaults.ModelDefaultSwitch.ManagedTaskKinds.ToArray());

            var refreshedCodeWriting = Assert.Single(afterRefresh.Defaults.ModelTaskDefaults,
                item => item.TaskKind == ModelTaskKind.CodeWriting);
            Assert.Equal(providerA.ProviderId, refreshedCodeWriting.Selection.Reference);
            Assert.Equal("2", refreshedCodeWriting.Selection.ModelBindingPin!.ConfigurationRevision);

            var preservedPlanning = Assert.Single(afterRefresh.Defaults.ModelTaskDefaults,
                item => item.TaskKind == ModelTaskKind.Planning);
            Assert.Equal(shipped[ModelTaskKind.Planning].Selection, preservedPlanning.Selection);

            var preservedReview = Assert.Single(afterRefresh.Defaults.ModelTaskDefaults,
                item => item.TaskKind == ModelTaskKind.Review);
            Assert.Equal("explicit-review", preservedReview.Selection.Reference);

            // New run gets revision 2 for managed tasks and keeps shipped Planning; old run retains revision 1.
            var runAfterId = "refresh-after-" + suffix;
            var orchAfter = await ResolveCallerAsync(
                db, orchestratorId, tenantId, ["api.read", "projects.orchestrator"],
                project.ProjectId, runAfterId);
            var acceptedAfter = await service.AcceptInteractiveRunSelectionAsync(
                orchAfter, project.ProjectId, runAfterId,
                new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None);
            Assert.Equal(2,
                Assert.Single(acceptedAfter.ModelSelections,
                    item => item.Selection.Reference == providerA.ProviderId &&
                        item.Selection.ReasoningEffort == "low")
                    .Selection.ProviderBindingRevision!.ProviderRevision);
            Assert.Contains(acceptedAfter.ModelSelections,
                item => item.Selection.Reference == shipped[ModelTaskKind.Planning].Selection.Reference);

            var replayBefore = await service.AcceptInteractiveRunSelectionAsync(
                orchBefore, project.ProjectId, runBeforeId,
                new InteractiveRunSelectionRequest(projectSettings.Revision), CancellationToken.None);
            Assert.Equal(1,
                Assert.Single(replayBefore.ModelSelections,
                    item => item.Selection.Reference == providerA.ProviderId &&
                        item.Selection.ReasoningEffort == "low")
                    .Selection.ProviderBindingRevision!.ProviderRevision);

            // Switching to a DIFFERENT provider still takes over tasks at shipped installation defaults.
            var switchedOther = await service.ActivatePlatformModelProviderAsync(
                admin, providerB.ProviderId, refreshed.SettingsRevision, 1, CancellationToken.None);
            var afterOther = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            Assert.Contains(ModelTaskKind.Planning, afterOther.Defaults!.ModelDefaultSwitch.ManagedTaskKinds);
            Assert.Equal(providerB.ProviderId,
                Assert.Single(afterOther.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.Planning).Selection.Reference);
            Assert.Equal("explicit-review",
                Assert.Single(afterOther.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.Review).Selection.Reference);

            // Revoked PlatformAdmin authority fails.
            var adminGrant = Assert.Single(admin.Grants, item => item.Role == ProjectAuthorityRole.PlatformAdmin);
            await authority.RevokeRoleAssignmentAsync(
                adminGrant.AssignmentId, adminGrant.Revision, "test", CancellationToken.None);
            var forbidden = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                service.ActivatePlatformModelProviderAsync(
                    admin, providerB.ProviderId, switchedOther.SettingsRevision, 1, CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        });
    }

    [Fact]
    public async Task PlatformSwitchRejectsMalformedManagedTaskKindsAndHandlesMissingTaskDefaults()
    {
        var invalidDefaults = PlatformDefaults() with
        {
            ModelDefaultSwitch = new PlatformModelDefaultSwitch
            {
                Mode = PlatformModelDefaultSwitchMode.Active,
                ActiveProviderId = "provider-1",
                ProviderRevision = 1,
                ManagedTaskKinds = [(ModelTaskKind)999],
            },
        };
        var invalidError = Assert.Throws<ProjectConfigException>(() =>
            ProjectConfigurationValidator.Validate(invalidDefaults));
        Assert.Equal(StatusCodes.Status400BadRequest, invalidError.StatusCode);

        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var authority = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "missing-task-tenant-" + suffix;
            var admin = await SeedCallerAsync(db, authority, "missing-task-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);

            var initial = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                0,
                PlatformDefaults() with
                {
                    ModelTaskDefaults =
                    [
                        new(ModelTaskKind.Planning, new("explicit-planning", "low")),
                        new(ModelTaskKind.CodeWriting, ModelTaskSelectionDefaults.Create()
                            .Single(item => item.TaskKind == ModelTaskKind.CodeWriting).Selection),
                        new(ModelTaskKind.Straightforward, ModelTaskSelectionDefaults.Create()
                            .Single(item => item.TaskKind == ModelTaskKind.Straightforward).Selection),
                    ],
                },
                CancellationToken.None);

            var provider = await SeedAvailablePlatformByokProviderAsync(
                tests, db, admin, "missing-task-provider-" + suffix);
            var activated = await service.ActivatePlatformModelProviderAsync(
                admin, provider.ProviderId, initial.Revision, 1, CancellationToken.None);

            // Simulate a stored revision where Straightforward is in ManagedTaskKinds but missing from ModelTaskDefaults.
            await using (var ownerConnection = await tests.OpenOwnerConnectionAsync())
            {
                await using var updateRevision = new NpgsqlCommand(
                    """
                    ALTER TABLE projects_config.platform_runtime_revisions DISABLE TRIGGER USER;
                    UPDATE projects_config.platform_runtime_revisions
                    SET configuration = jsonb_set(
                        configuration,
                        '{modelTaskDefaults}',
                        COALESCE(
                            (SELECT jsonb_agg(item)
                             FROM jsonb_array_elements(configuration->'modelTaskDefaults') AS item
                             WHERE item->>'taskKind' <> 'straightforward'),
                            '[]'::jsonb))
                    WHERE revision = @revision;
                    ALTER TABLE projects_config.platform_runtime_revisions ENABLE TRIGGER USER;
                    """,
                    ownerConnection);
                updateRevision.Parameters.AddWithValue("revision", activated.SettingsRevision);
                await updateRevision.ExecuteNonQueryAsync();
            }

            await service.ActivatePlatformModelProviderAsync(
                admin, provider.ProviderId, activated.SettingsRevision, 1, CancellationToken.None);
            var after = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            Assert.Equal([ModelTaskKind.CodeWriting, ModelTaskKind.Straightforward],
                after.Defaults!.ModelDefaultSwitch.ManagedTaskKinds.ToArray());
            Assert.DoesNotContain(after.Defaults.ModelTaskDefaults,
                item => item.TaskKind == ModelTaskKind.Straightforward);
            Assert.Equal("explicit-planning",
                Assert.Single(after.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.Planning).Selection.Reference);
            Assert.Equal(provider.ProviderId,
                Assert.Single(after.Defaults.ModelTaskDefaults,
                    item => item.TaskKind == ModelTaskKind.CodeWriting).Selection.Reference);
        });
    }
}
