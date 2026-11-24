using System.Security.Cryptography;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.AgentRuntime;
using Agentweaver.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    private sealed class CountingDbContextFactory(
        Func<ProjectsConfigDbContext> createDbContext,
        Action? onCreate = null)
        : IDbContextFactory<ProjectsConfigDbContext>
    {
        public int CreatedCount { get; private set; }

        public ProjectsConfigDbContext CreateDbContext()
        {
            CreatedCount++;
            onCreate?.Invoke();
            return createDbContext();
        }

        public Task<ProjectsConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunSelectionHoldsOrderedByokProviderShareLocksThroughCommitAndRetainsOriginalPins(bool interactive)
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "fence-tenant-" + suffix;
            var runId = "fence-run-" + suffix;

            var admin = await SeedCallerAsync(db, store, "fence-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "fence-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            var platform = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await service.CreateProjectAsync(owner, "Fence project", CancellationToken.None);
            var boundOwner = await ResolveCallerAsync(
                db, owner.ActorId, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);
            var orchSubject = "fence-orch-" + suffix;
            var membership = await SeedMembershipAsync(store, orchSubject, tenantId);
            await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var orchestrator = await ResolveCallerAsync(
                db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

            var (providerA, pinA, cardA) = await SeedAvailableByokProviderAsync(
                tests, db, boundOwner, project.ProjectId, "prov-a-" + suffix, "cred-v1", "aaaaaaaa");
            var (providerB, pinB, cardB) = await SeedAvailableByokProviderAsync(
                tests, db, boundOwner, project.ProjectId, "prov-b-" + suffix, "cred-v1", "bbbbbbbb");

            var primarySelection = new ModelSelectionSettings(
                providerB.ProviderId, providerB.CredentialReference, ModelSourceMode.Byok)
            {
                CredentialIdentity = providerB.CredentialIdentity,
                ModelBindingPin = pinB,
                ReasoningEffort = "high",
            };
            var primaryOverride = new ModelSelectionOverride(providerB.ProviderId, "high", pinB)
            {
                CredentialReference = providerB.CredentialReference,
                CredentialIdentity = providerB.CredentialIdentity,
            };
            var taskSelection = new ModelSelectionOverride(providerA.ProviderId, "high", pinA)
            {
                CredentialReference = providerA.CredentialReference,
                CredentialIdentity = providerA.CredentialIdentity,
            };
            var config = await service.UpdateProjectConfigurationAsync(
                owner,
                project.ProjectId,
                project.ConfigurationRevision,
                ProjectSettings() with
                {
                    ModelSelection = null,
                    ModelTaskOverrides =
                    [
                        new ModelTaskSelectionOverride(ModelTaskKind.Planning, primaryOverride),
                        new ModelTaskSelectionOverride(ModelTaskKind.CodeWriting, taskSelection),
                    ],
                },
                CancellationToken.None);
            platform = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                platform.Revision,
                PlatformDefaults() with { ModelSelection = primarySelection },
                CancellationToken.None);

            var triggerFunction = "tf_run_sel_fence_" + suffix;
            var triggerName = "tr_run_sel_fence_" + suffix;
            var lockKey1 = unchecked((int)0x51A00001);
            var lockKey2 = Guid.NewGuid().GetHashCode();

            await using var barrierConnection = await tests.AcquireAdvisoryBarrierAsync(lockKey1, lockKey2);
            await tests.ExecuteOwnerSqlAsync($"""
                CREATE FUNCTION projects_config.{triggerFunction}() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                BEGIN
                    PERFORM pg_advisory_xact_lock({lockKey1}, {lockKey2});
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON projects_config.project_run_selections
                FOR EACH ROW EXECUTE FUNCTION projects_config.{triggerFunction}();
                """);

            try
            {
                Task<EffectiveRunSelection> acceptanceTask = interactive
                    ? service.AcceptInteractiveRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                    : service.AcceptRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        RunRequest(config.Revision, platform.Revision) with
                        {
                            Context = RunRequest(config.Revision, platform.Revision).Context with
                            {
                                AvailableModelSelectionReferences =
                                    RunRequest(config.Revision, platform.Revision).Context
                                        .AvailableModelSelectionReferences
                                        .Add(providerA.ProviderId)
                                        .Add(providerB.ProviderId),
                            },
                        },
                        CancellationToken.None);

                await tests.WaitForRunSelectionFenceActivityAsync(
                    "wait_event = 'advisory' AND query ILIKE '%project_run_selections%'",
                    acceptanceTask);

                // Concurrent exclusive lock on the provider head must block on the FOR SHARE lock held by the selection transaction.
                await using var updateConn = await tests.OpenOwnerConnectionAsync();
                await using var updateCmd = new NpgsqlCommand(
                    """
                    SELECT provider_id
                    FROM projects_config.model_providers
                    WHERE provider_id = @provider_id
                    FOR UPDATE
                    """,
                    updateConn);
                updateCmd.Parameters.AddWithValue("provider_id", providerA.ProviderId);
                var updateTask = updateCmd.ExecuteScalarAsync();

                await tests.WaitForRunSelectionFenceActivityAsync(
                    "wait_event_type = 'Lock' AND query ILIKE '%model_providers%'");
                Assert.False(updateTask.IsCompleted);

                await tests.ReleaseAdvisoryBarrierAsync(barrierConnection, lockKey1, lockKey2);

                var accepted = await acceptanceTask;
                var lockedProviderId = (string?)await updateTask;
                Assert.Equal(providerA.ProviderId, lockedProviderId);

                var primaryPin = accepted.ModelSelection;
                Assert.Equal(providerB.ProviderId, primaryPin.Reference);
                Assert.Equal(1L, primaryPin.ProviderBindingRevision!.ProviderRevision);
                Assert.Equal(pinB, primaryPin.ModelBindingPin);
                Assert.Equal(cardB, primaryPin.ProviderBindingRevision.RateCardPin);

                var taskEffective = Assert.Single(accepted.ModelSelections.Where(s => s.Selection.Reference == providerA.ProviderId));
                Assert.Equal(1L, taskEffective.Selection.ProviderBindingRevision!.ProviderRevision);
                Assert.Equal(pinA, taskEffective.Selection.ModelBindingPin);
                Assert.Equal(cardA, taskEffective.Selection.ProviderBindingRevision.RateCardPin);
            }
            finally
            {
                await tests.ExecuteOwnerSqlAsync(
                    $"DROP TRIGGER IF EXISTS {triggerName} ON projects_config.project_run_selections; " +
                    $"DROP FUNCTION IF EXISTS projects_config.{triggerFunction}();");
            }
        });
    }

    [Fact]
    public async Task RunSelectionWaitsForCastingConfirmationOnTheSharedFenceFirst()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var seedDb = tests.CreateDbContext();
            var store = new ProjectsConfigPrivilegedAuthorityStore(
                tests.CreateDbContextOptions(), TimeProvider.System);
            var seedService = new ProjectsConfigService(
                seedDb, CreateProviderCatalog(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "accept-confirm-tenant-" + suffix;
            var runId = "accept-confirm-run-" + suffix;
            var admin = await SeedCallerAsync(
                seedDb,
                store,
                "accept-confirm-admin-" + suffix,
                tenantId,
                ["api.read", "projects.admin"],
                ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId,
                ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(
                seedDb,
                store,
                "accept-confirm-owner-" + suffix,
                tenantId,
                ["api.read", "projects.admin"],
                ProjectAuthorityResourceType.Tenant,
                tenantId,
                ProjectAuthorityRole.TenantAdmin);
            var platform = await seedService.UpdatePlatformRuntimeDefaultsAsync(
                admin,
                (await seedService.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None)).Revision,
                PlatformDefaults(),
                CancellationToken.None);
            var project = await seedService.CreateProjectAsync(
                owner, "Acceptance fence project", CancellationToken.None);
            var membership = await SeedMembershipAsync(store, "accept-confirm-orch-" + suffix, tenantId);
            await store.AssignRoleAsync(
                membership,
                ProjectAuthorityResourceType.Project,
                project.ProjectId,
                ProjectAuthorityRole.Orchestrator,
                "test",
                CancellationToken.None);
            var orchestrator = await ResolveCallerAsync(
                seedDb,
                "accept-confirm-orch-" + suffix,
                tenantId,
                ["api.read", "projects.orchestrator"],
                project.ProjectId,
                runId);
            var configuration = await seedService.GetProjectConfigurationAsync(
                owner, project.ProjectId, null, CancellationToken.None);
            var proposal = await seedService.CreateCastingProposalAsync(
                owner,
                project.ProjectId,
                configuration.Revision,
                configuration.Configuration,
                CancellationToken.None);

            var lockKey = RandomNumberGenerator.GetInt32(int.MaxValue);
            var trigger = "test_accept_casting_fence_" + suffix;
            var function = "pause_accept_casting_fence_" + suffix;
            await using var blocker = await tests.OpenOwnerConnectionAsync();
            await using (var install = new NpgsqlCommand($"""
                CREATE FUNCTION projects_config.{function}() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    PERFORM pg_catalog.pg_advisory_xact_lock({lockKey});
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER {trigger}
                BEFORE INSERT ON projects_config.project_configuration_revisions
                FOR EACH ROW EXECUTE FUNCTION projects_config.{function}();
                SELECT pg_catalog.pg_advisory_lock({lockKey});
                """, blocker))
                await install.ExecuteNonQueryAsync();

            Task<ProjectCastingProposal>? confirmation = null;
            Task<EffectiveRunSelection>? acceptance = null;
            try
            {
                await using var confirmDb = tests.CreateDbContext();
                await using var acceptDb = tests.CreateDbContext();
                var confirmService = new ProjectsConfigService(
                    confirmDb, CreateProviderCatalog(), TimeProvider.System);
                var acceptService = new ProjectsConfigService(
                    acceptDb,
                    CreateProviderCatalog(),
                    TimeProvider.System,
                    new CountingDbContextFactory(tests.CreateDbContext));
                confirmation = confirmService.ConfirmCastingProposalAsync(
                    owner,
                    project.ProjectId,
                    proposal.ProposalId,
                    proposal.DraftRevision,
                    CancellationToken.None);
                await tests.WaitForRunSelectionFenceActivityAsync(
                    "wait_event = 'advisory' AND query ILIKE '%project_configuration_revisions%'",
                    confirmation);

                acceptance = acceptService.AcceptRunSelectionAsync(
                    orchestrator,
                    project.ProjectId,
                    runId,
                    RunRequest(configuration.Revision + 1, platform.Revision),
                    CancellationToken.None);
                await tests.WaitForRunSelectionFenceActivityAsync(
                    "wait_event_type = 'Lock' AND query ILIKE '%project_skill_admission_fence%'",
                    acceptance);
                Assert.False(acceptance.IsCompleted);

                await using (var unlock = new NpgsqlCommand(
                    $"SELECT pg_catalog.pg_advisory_unlock({lockKey})", blocker))
                    await unlock.ExecuteScalarAsync();
                await confirmation.WaitAsync(TimeSpan.FromSeconds(20));
                var accepted = await acceptance.WaitAsync(TimeSpan.FromSeconds(20));
                Assert.Equal(configuration.Revision + 1, accepted.ProjectConfigurationRevision);
            }
            finally
            {
                await using var cleanup = new NpgsqlCommand($"""
                    SELECT pg_catalog.pg_advisory_unlock({lockKey});
                    DROP TRIGGER IF EXISTS {trigger}
                        ON projects_config.project_configuration_revisions;
                    DROP FUNCTION IF EXISTS projects_config.{function}();
                    """, blocker);
                await cleanup.ExecuteNonQueryAsync();
                if (confirmation is not null)
                    try { await confirmation; } catch { }
                if (acceptance is not null)
                    try { await acceptance; } catch { }
            }
        });
    }

    [Fact]
    public async Task ProviderUpdateBeforeSelectionLockRejectsStalePinWithoutSavingRunSelection()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "stale-prov-tenant-" + suffix;
            var runId = "stale-run-" + suffix;

            var admin = await SeedCallerAsync(db, store, "stale-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "stale-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            _ = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await service.CreateProjectAsync(owner, "Stale provider project", CancellationToken.None);
            var boundOwner = await ResolveCallerAsync(
                db, owner.ActorId, tenantId, ["api.read", "projects.admin"], boundProjectId: project.ProjectId);
            var orchSubject = "stale-orch-" + suffix;
            var membership = await SeedMembershipAsync(store, orchSubject, tenantId);
            await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var orchestrator = await ResolveCallerAsync(
                db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

            var (provider, pin, _) = await SeedAvailableByokProviderAsync(
                tests, db, boundOwner, project.ProjectId, "stale-prov-" + suffix, "cred-v1", "cccccccc");
            var selection = new ModelSelectionSettings(
                provider.ProviderId, provider.CredentialReference, ModelSourceMode.Byok)
            {
                CredentialIdentity = provider.CredentialIdentity,
                ModelBindingPin = pin,
                ReasoningEffort = "high",
            };
            var config = await service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision,
                ProjectSettings() with { ModelSelection = selection }, CancellationToken.None);

            await tests.ExecuteOwnerSqlAsync(
                "ALTER TABLE projects_config.model_providers DISABLE TRIGGER USER; " +
                "ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER USER;");
            try
            {
                // Hold an exclusive row lock on the provider head, advance its credential version, and commit.
                await using var updateConn = await tests.OpenOwnerConnectionAsync();
                await using var updateTx = await updateConn.BeginTransactionAsync();
                await using (var lockCmd = new NpgsqlCommand(
                    "SELECT provider_id FROM projects_config.model_providers WHERE provider_id = @id FOR UPDATE",
                    updateConn, updateTx))
                {
                    lockCmd.Parameters.AddWithValue("id", provider.ProviderId);
                    await lockCmd.ExecuteScalarAsync();
                }

                var acceptTask = service.AcceptInteractiveRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None);

                await tests.WaitForRunSelectionFenceActivityAsync(
                    "wait_event_type = 'Lock' AND query ILIKE '%FOR SHARE%' AND query ILIKE '%model_providers%'",
                    acceptTask);

                await using (var rotateCmd = new NpgsqlCommand(
                    """
                    INSERT INTO projects_config.model_provider_revisions (
                        provider_id, scope, project_id, provider_revision, operation_id, request_fingerprint,
                        display_name, type, base_url, deployment, wire_api, azure_api_version,
                        reasoning_efforts, prompt_capacity_tokens, configuration_hash,
                        credential_version, credential_slot_id, credential_secret_id,
                        rate_card_id, rate_card_version, rate_card_pin_json,
                        created_by_actor_id, created_at)
                    SELECT provider_id, scope, project_id, 2, @op_id, request_fingerprint,
                        display_name, type, base_url, deployment, wire_api, azure_api_version,
                        reasoning_efforts, prompt_capacity_tokens, configuration_hash,
                        'cred-v2', credential_slot_id, credential_secret_id,
                        rate_card_id, rate_card_version, rate_card_pin_json,
                        created_by_actor_id, created_at
                    FROM projects_config.model_provider_revisions
                    WHERE provider_id = @id AND provider_revision = 1;
                    UPDATE projects_config.model_providers
                    SET current_revision = 2
                    WHERE provider_id = @id;
                    """,
                    updateConn, updateTx))
                {
                    rotateCmd.Parameters.AddWithValue("id", provider.ProviderId);
                    rotateCmd.Parameters.AddWithValue("op_id", Guid.NewGuid());
                    await rotateCmd.ExecuteNonQueryAsync();
                }
                await updateTx.CommitAsync();

                var ex = await Assert.ThrowsAsync<ProjectConfigException>(() => acceptTask);
                Assert.Equal("model_credential_unavailable", ex.Code);
                Assert.Equal(StatusCodes.Status422UnprocessableEntity, ex.StatusCode);

                await using var verify = tests.CreateDbContext();
                Assert.False(await verify.RunSelections.AsNoTracking().AnyAsync(r => r.RunId == runId));
            }
            finally
            {
                await tests.ExecuteOwnerSqlAsync(
                    "ALTER TABLE projects_config.model_providers ENABLE TRIGGER USER; " +
                    "ALTER TABLE projects_config.model_provider_revisions ENABLE TRIGGER USER;");
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SerializationFailureRetriesWithFreshContextAndOriginalRequestUpToThreeAttempts(bool interactive)
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "retry-40001-tenant-" + suffix;

            var admin = await SeedCallerAsync(db, store, "r40001-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "r40001-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            var platform = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await service.CreateProjectAsync(owner, "Retry 40001 project", CancellationToken.None);
            var orchSubject = "r40001-orch-" + suffix;
            var membership = await SeedMembershipAsync(store, orchSubject, tenantId);
            await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);

            var config = await service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision, ProjectSettings(), CancellationToken.None);

            var counterSeq = "seq_retry_40001_" + suffix;
            var triggerFunction = "tf_retry_40001_" + suffix;
            var triggerName = "tr_retry_40001_" + suffix;

            await tests.ExecuteOwnerSqlAsync($"""
                CREATE SEQUENCE projects_config.{counterSeq} START 1;
                CREATE FUNCTION projects_config.{triggerFunction}() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                DECLARE n bigint;
                BEGIN
                    n := nextval('projects_config.{counterSeq}');
                    IF n <= 2 THEN
                        RAISE EXCEPTION 'simulated serialization failure' USING ERRCODE = '40001';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON projects_config.project_run_selections
                FOR EACH ROW EXECUTE FUNCTION projects_config.{triggerFunction}();
                """);

            try
            {
                var runId = "retry-40001-run-" + suffix;
                var orchestrator = await ResolveCallerAsync(
                    db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

                var accepted = interactive
                    ? await service.AcceptInteractiveRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                    : await service.AcceptRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        RunRequest(config.Revision, platform.Revision), CancellationToken.None);

                Assert.Equal(runId, accepted.RunId);
                Assert.Equal(3, factory.CreatedCount);

                // Exhausting 3 attempts on a new run must return 409 Conflict without saving.
                await tests.ExecuteOwnerSqlAsync($"ALTER SEQUENCE projects_config.{counterSeq} RESTART WITH 1;");
                await tests.ExecuteOwnerSqlAsync(
                    $"CREATE OR REPLACE FUNCTION projects_config.{triggerFunction}() RETURNS trigger " +
                    "LANGUAGE plpgsql AS $body$ BEGIN " +
                    "RAISE EXCEPTION 'simulated serialization failure' USING ERRCODE = '40001'; " +
                    "END; $body$;");
                var runId2 = "retry-40001-exhausted-" + suffix;
                var orchestrator2 = await ResolveCallerAsync(
                    db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId2);
                var beforeCount = factory.CreatedCount;

                var conflict = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                    interactive
                        ? service.AcceptInteractiveRunSelectionAsync(
                            orchestrator2, project.ProjectId, runId2,
                            new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                        : service.AcceptRunSelectionAsync(
                            orchestrator2, project.ProjectId, runId2,
                            RunRequest(config.Revision, platform.Revision), CancellationToken.None));

                Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
                Assert.Equal(3, factory.CreatedCount - beforeCount);
                await using var verify = tests.CreateDbContext();
                Assert.False(await verify.RunSelections.AsNoTracking().AnyAsync(r => r.RunId == runId2));
            }
            finally
            {
                await tests.ExecuteOwnerSqlAsync(
                    $"DROP TRIGGER IF EXISTS {triggerName} ON projects_config.project_run_selections; " +
                    $"DROP FUNCTION IF EXISTS projects_config.{triggerFunction}(); " +
                    $"DROP SEQUENCE IF EXISTS projects_config.{counterSeq};");
            }
        });
    }

    [Fact]
    public async Task NonPrimaryKeyUniqueViolationAndCancellationDoNotRetry()
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "nonpk-tenant-" + suffix;
            var runId = "nonpk-run-" + suffix;

            var admin = await SeedCallerAsync(db, store, "nonpk-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "nonpk-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);
            var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            _ = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await service.CreateProjectAsync(owner, "Non-PK project", CancellationToken.None);
            var orchSubject = "nonpk-orch-" + suffix;
            var membership = await SeedMembershipAsync(store, orchSubject, tenantId);
            await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var config = await service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision, ProjectSettings(), CancellationToken.None);
            var orchestrator = await ResolveCallerAsync(
                db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

            // Cancelled token propagates without creating a context or retrying.
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.AcceptInteractiveRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    new InteractiveRunSelectionRequest(config.Revision), cts.Token));
            Assert.Equal(0, factory.CreatedCount);

            var triggerFunction = "tf_nonpk_" + suffix;
            var triggerName = "tr_nonpk_" + suffix;
            await tests.ExecuteOwnerSqlAsync($"""
                CREATE FUNCTION projects_config.{triggerFunction}() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                BEGIN
                    RAISE EXCEPTION 'other unique violation'
                        USING ERRCODE = '23505', CONSTRAINT = 'IX_other_unique_constraint';
                END;
                $body$;
                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON projects_config.project_run_selections
                FOR EACH ROW EXECUTE FUNCTION projects_config.{triggerFunction}();
                """);

            try
            {
                await Assert.ThrowsAsync<DbUpdateException>(() =>
                    service.AcceptInteractiveRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None));

                Assert.Equal(1, factory.CreatedCount);
            }
            finally
            {
                await tests.ExecuteOwnerSqlAsync(
                    $"DROP TRIGGER IF EXISTS {triggerName} ON projects_config.project_run_selections; " +
                    $"DROP FUNCTION IF EXISTS projects_config.{triggerFunction}();");
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExactReplayRechecksCurrentRoleAndTenantAuthorityOnBothRoutes(bool interactive)
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "pk-auth-tenant-" + suffix;
            var runId = "pk-auth-run-" + suffix;

            var admin = await SeedCallerAsync(db, store, "pk-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "pk-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);

            var factory = new CountingDbContextFactory(tests.CreateDbContext);
            var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System, factory);
            var head = await service.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            var platform = await service.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await service.CreateProjectAsync(owner, "PK auth project", CancellationToken.None);
            var orchSubject = "pk-orch-" + suffix;
            var membership = await SeedMembershipAsync(store, orchSubject, tenantId);
            var roleAssignment = await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var config = await service.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision, ProjectSettings(), CancellationToken.None);
            var orchestrator = await ResolveCallerAsync(
                db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

            var accepted = interactive
                ? await service.AcceptInteractiveRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                : await service.AcceptRunSelectionAsync(
                    orchestrator, project.ProjectId, runId,
                    RunRequest(config.Revision, platform.Revision), CancellationToken.None);
            Assert.Equal(runId, accepted.RunId);

            // Wrong tenant is denied on exact-fingerprint replay.
            var otherTenantId = "other-tenant-" + suffix;
            _ = await SeedMembershipAsync(store, orchSubject, otherTenantId);
            var otherTenantOrch = await ResolveCallerAsync(
                db, orchSubject, otherTenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);
            var wrongTenant = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                interactive
                    ? service.AcceptInteractiveRunSelectionAsync(
                        otherTenantOrch, project.ProjectId, runId,
                        new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                    : service.AcceptRunSelectionAsync(
                        otherTenantOrch, project.ProjectId, runId,
                        RunRequest(config.Revision, platform.Revision), CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, wrongTenant.StatusCode);

            // Revoke Orchestrator role and verify exact-fingerprint replay is denied on both routes.
            await store.RevokeRoleAssignmentAsync(
                roleAssignment.AssignmentId, roleAssignment.Revision, "test-revoke", CancellationToken.None);

            var deniedReplay = await Assert.ThrowsAsync<ProjectConfigException>(() =>
                interactive
                    ? service.AcceptInteractiveRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                    : service.AcceptRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        RunRequest(config.Revision, platform.Revision), CancellationToken.None));
            Assert.Equal(StatusCodes.Status403Forbidden, deniedReplay.StatusCode);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PrimaryKeyCollisionWinnerCommitThenRetryHonorsAuthorityOnBothRoutes(
        bool interactive,
        bool revokeAuthority)
    {
        await RunWithIsolatedProjectsAsync(async tests =>
        {
            await using var db = tests.CreateDbContext();
            var store = new ProjectsConfigPrivilegedAuthorityStore(tests.CreateDbContextOptions(), TimeProvider.System);
            var suffix = Guid.NewGuid().ToString("N")[..12];
            var tenantId = "pk-race-tenant-" + suffix;
            var runId = "pk-race-run-" + suffix;

            var admin = await SeedCallerAsync(db, store, "pk-race-admin-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            var owner = await SeedCallerAsync(db, store, "pk-race-owner-" + suffix, tenantId,
                ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
                tenantId, ProjectAuthorityRole.TenantAdmin);

            var initService = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
            var head = await initService.GetPlatformRuntimeDefaultsAsync(admin, CancellationToken.None);
            var platform = await initService.UpdatePlatformRuntimeDefaultsAsync(
                admin, head.Revision, PlatformDefaults(), CancellationToken.None);
            var project = await initService.CreateProjectAsync(owner, "PK race project", CancellationToken.None);
            var orchSubject = "pk-race-orch-" + suffix;
            var membership = await SeedMembershipAsync(store, orchSubject, tenantId);
            var roleAssignment = await store.AssignRoleAsync(membership, ProjectAuthorityResourceType.Project, project.ProjectId,
                ProjectAuthorityRole.Orchestrator, "test", CancellationToken.None);
            var config = await initService.UpdateProjectConfigurationAsync(
                owner, project.ProjectId, project.ConfigurationRevision, ProjectSettings(), CancellationToken.None);
            var orchestrator = await ResolveCallerAsync(
                db, orchSubject, tenantId, ["api.read", "projects.orchestrator"], project.ProjectId, runId);

            // Trigger simulates a PK_project_run_selections collision on attempt 1.
            var triggerFunction = "tf_pk_revoke_" + suffix;
            var triggerName = "tr_pk_revoke_" + suffix;
            var counterSeq = "pk_revoke_counter_" + suffix;
            await tests.ExecuteOwnerSqlAsync($"""
                CREATE SEQUENCE projects_config.{counterSeq} START 1;
                CREATE FUNCTION projects_config.{triggerFunction}() RETURNS trigger
                LANGUAGE plpgsql AS $body$
                DECLARE n bigint;
                BEGIN
                    n := nextval('projects_config.{counterSeq}');
                    IF n = 1 THEN
                        RAISE EXCEPTION 'duplicate key value violates unique constraint "PK_project_run_selections"'
                            USING ERRCODE = '23505', CONSTRAINT = 'PK_project_run_selections';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER {triggerName}
                BEFORE INSERT ON projects_config.project_run_selections
                FOR EACH ROW EXECUTE FUNCTION projects_config.{triggerFunction}();
                """);

            try
            {
                await using var winnerDb = tests.CreateDbContext();
                var winnerService = new ProjectsConfigService(
                    winnerDb, CreateProviderCatalog(), TimeProvider.System);
                EffectiveRunSelection? committedWinner = null;
                var factoryCallCount = 0;
                var factory = new CountingDbContextFactory(
                    tests.CreateDbContext,
                    () =>
                    {
                        factoryCallCount++;
                        if (factoryCallCount == 2)
                        {
                            committedWinner = AcceptAsync(winnerService).GetAwaiter().GetResult();
                            if (revokeAuthority)
                            {
                                store.RevokeRoleAssignmentAsync(
                                    roleAssignment.AssignmentId, roleAssignment.Revision,
                                    "revoke-before-collision-retry", CancellationToken.None).GetAwaiter().GetResult();
                            }
                        }
                    });
                var service = new ProjectsConfigService(
                    db, CreateProviderCatalog(), TimeProvider.System, factory);

                if (revokeAuthority)
                {
                    var denied = await Assert.ThrowsAsync<ProjectConfigException>(() => AcceptAsync(service));
                    Assert.Equal(StatusCodes.Status403Forbidden, denied.StatusCode);
                    Assert.Equal(2, factory.CreatedCount);
                }
                else
                {
                    var replayed = await AcceptAsync(service);
                    Assert.Equal(2, factory.CreatedCount);
                    Assert.NotNull(committedWinner);
                    Assert.Equal(JsonSerializer.Serialize(committedWinner), JsonSerializer.Serialize(replayed));
                }

                Assert.NotNull(committedWinner);
                await using var verify = tests.CreateDbContext();
                var saved = await verify.RunSelections.AsNoTracking()
                    .SingleAsync(selection => selection.RunId == runId);
                var savedSelection = ProjectsConfigService.Deserialize<EffectiveRunSelection>(saved.SnapshotJson);
                Assert.Equal(
                    JsonSerializer.Serialize(committedWinner),
                    JsonSerializer.Serialize(savedSelection));
            }
            finally
            {
                await tests.ExecuteOwnerSqlAsync(
                    $"DROP TRIGGER IF EXISTS {triggerName} ON projects_config.project_run_selections; " +
                    $"DROP FUNCTION IF EXISTS projects_config.{triggerFunction}(); " +
                    $"DROP SEQUENCE IF EXISTS projects_config.{counterSeq};");
            }

            Task<EffectiveRunSelection> AcceptAsync(ProjectsConfigService service) =>
                interactive
                    ? service.AcceptInteractiveRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        new InteractiveRunSelectionRequest(config.Revision), CancellationToken.None)
                    : service.AcceptRunSelectionAsync(
                        orchestrator, project.ProjectId, runId,
                        RunRequest(config.Revision, platform.Revision), CancellationToken.None);
        });
    }

    private static async Task<(ModelProviderBindingRevision Binding, RuntimeModelBindingPin Pin, RuntimeRateCardPin Card)>
        SeedAvailableByokProviderAsync(
            ProjectsConfigPostgresTests tests,
            ProjectsConfigDbContext db,
            ProjectAuthorizationContext owner,
            string projectId,
            string providerId,
            string credentialVersion,
            string cardHashSeed)
    {
        var publication = PublicationRequest(
            Guid.NewGuid(),
            1,
            ModelProviderBindingScope.Project,
            projectId,
            providerId,
            "0123456789abcdef0123456789abcdef");
        var rateCardId = "card-" + providerId;
        var rateCardVersion = "v1";
        var card = new RuntimeRateCardPin(
            rateCardId,
            rateCardVersion,
            new string(cardHashSeed[0], 64),
            new ManualRuntimeRateCardReadProvenance("https://pricing.example/rate-card", new DateOnly(2026, 1, 1)));

        await using var conn = await tests.OpenOwnerConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var disableCmd = new NpgsqlCommand(
            "ALTER TABLE projects_config.model_providers DISABLE TRIGGER USER; " +
            "ALTER TABLE projects_config.model_provider_revisions DISABLE TRIGGER USER;",
            conn, tx))
            await disableCmd.ExecuteNonQueryAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(
                """
                INSERT INTO projects_config.model_providers
                  (provider_id, scope, project_id, display_name, type, base_url, deployment, wire_api,
                   reasoning_efforts, prompt_capacity_tokens, state, current_revision, created_by_actor_id,
                   created_at, updated_by_actor_id, updated_at)
                VALUES (@provider, 'Project', @project, 'Customer model', 'openai', 'https://provider.example',
                        'deployment-1', 'responses', ARRAY['high'], 32768, 'Available', 1,
                        @actor, now(), @actor, now());
                INSERT INTO projects_config.model_provider_revisions
                  (provider_id, provider_revision, scope, project_id, operation_id, request_fingerprint,
                   display_name, type, base_url, deployment, wire_api, reasoning_efforts, prompt_capacity_tokens,
                   configuration_hash, credential_version, credential_slot_id, credential_secret_id,
                   rate_card_id, rate_card_version, rate_card_pin_json, created_by_actor_id, created_at)
                VALUES (@provider, 1, 'Project', @project, @operation, repeat('a',64), 'Customer model',
                        'openai', 'https://provider.example', 'deployment-1', 'responses', ARRAY['high'],
                        32768, repeat('b',64), @version, @slot, @secret, @card, @card_version, @pin,
                        @actor, now());
                """,
                conn, tx);
            cmd.Parameters.AddWithValue("provider", providerId);
            cmd.Parameters.AddWithValue("project", projectId);
            cmd.Parameters.AddWithValue("actor", owner.ActorId);
            cmd.Parameters.AddWithValue("operation", publication.OperationId);
            cmd.Parameters.AddWithValue("version", credentialVersion);
            cmd.Parameters.AddWithValue("slot", publication.CredentialIdentity.CredentialSlotId);
            cmd.Parameters.AddWithValue("secret", publication.CredentialReference.Id);
            cmd.Parameters.AddWithValue("card", rateCardId);
            cmd.Parameters.AddWithValue("card_version", rateCardVersion);
            cmd.Parameters.AddWithValue("pin", NpgsqlDbType.Jsonb,
                JsonSerializer.Serialize(card, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await using var enableCmd = new NpgsqlCommand(
                "ALTER TABLE projects_config.model_providers ENABLE TRIGGER USER; " +
                "ALTER TABLE projects_config.model_provider_revisions ENABLE TRIGGER USER;",
                conn, tx);
            await enableCmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();

        var drafts = new ModelProviderDraftService(db, TimeProvider.System);
        var available = await drafts.ReadAsync(
            owner, ModelProviderBindingScope.Project, projectId, providerId, CancellationToken.None);
        var pin = RuntimeModelBindingsResolver.Pin(available.Binding, "high");
        return (available.Binding, pin, card);
    }

    private Task<NpgsqlConnection> OpenOwnerConnectionAsync() =>
        fixture.MigrationDataSource.OpenConnectionAsync().AsTask();

    private async Task ExecuteOwnerSqlAsync(string sql)
    {
        await using var connection = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> AcquireAdvisoryBarrierAsync(int key1, int key2)
    {
        var connection = await fixture.MigrationDataSource.OpenConnectionAsync();
        await using var lockCommand = new NpgsqlCommand("SELECT pg_advisory_lock(@key1, @key2)", connection);
        lockCommand.Parameters.AddWithValue("key1", key1);
        lockCommand.Parameters.AddWithValue("key2", key2);
        await lockCommand.ExecuteNonQueryAsync();
        return connection;
    }

    private async Task ReleaseAdvisoryBarrierAsync(NpgsqlConnection connection, int key1, int key2)
    {
        await using var unlockCommand = new NpgsqlCommand("SELECT pg_advisory_unlock(@key1, @key2)", connection);
        unlockCommand.Parameters.AddWithValue("key1", key1);
        unlockCommand.Parameters.AddWithValue("key2", key2);
        await unlockCommand.ExecuteScalarAsync();
    }

    private async Task WaitForRunSelectionFenceActivityAsync(string predicate, Task? watchedTask = null)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (watchedTask is { IsCompleted: true })
                await watchedTask;
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                $"SELECT 1 FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND {predicate} LIMIT 1",
                connection);
            if (await command.ExecuteScalarAsync() is not null)
                return;
            await Task.Delay(25);
        }

        await using var diagConn = await fixture.DataSource.OpenConnectionAsync();
        await using var diagCmd = new NpgsqlCommand(
            "SELECT state, wait_event_type, wait_event, left(query, 120) FROM pg_stat_activity WHERE pid <> pg_backend_pid() AND datname = current_database()",
            diagConn);
        var details = new List<string>();
        await using (var reader = await diagCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                details.Add($"{reader[0]}|{reader[1]}|{reader[2]}|{reader[3]}");
        }

        throw new TimeoutException(
            $"Timed out waiting for PostgreSQL activity matching: {predicate}. Active: {string.Join("; ", details)}");
    }
}
