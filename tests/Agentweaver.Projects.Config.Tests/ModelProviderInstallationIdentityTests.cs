using Agentweaver.Abstractions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InstallationIdentityDownAlwaysRetainsNamespaceAndReceipts(bool populated, bool missing)
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var db = test.CreateDbContext();
            var operation = Guid.NewGuid();
            if (populated)
            {
                var caller = await test.CreateReferenceAdminAsync(db);
                await test.ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                    "byok-" + operation.ToString("N"),
                    DraftRequest(operation, 0) with
                    {
                        Reference = "Down-" + operation.ToString("N"),
                        TargetInstallationId = isolated.InstallationId
                    }, CancellationToken.None);
            }
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            if (missing)
            {
                await using var owner = await isolated.MigrationDataSource.OpenConnectionAsync();
                await using var delete = new NpgsqlCommand("DELETE FROM projects_config.installation_identity", owner);
                await delete.ExecuteNonQueryAsync();
            }
            await using var migration = new ProjectsConfigDbContext(options);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migration.GetService<IMigrator>().MigrateAsync("20261119132116_ReserveModelProviderReferences"));
            var postgres = Assert.IsType<PostgresException>(failure.InnerException);
            Assert.Equal("55000", postgres.SqlState);
            Assert.Equal("installation_identity_must_be_retained", postgres.MessageText);
            await using var connection = await isolated.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection);
            if (missing)
                Assert.Null(await read.ExecuteScalarAsync());
            else
                Assert.Equal(Guid.Parse(isolated.InstallationId), await read.ExecuteScalarAsync());
            if (populated)
                Assert.Equal(Guid.Parse(isolated.InstallationId), (await db.ModelProviderReferenceReservations.AsNoTracking()
                    .SingleAsync(item => item.OperationId == operation)).InstallationId);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task InstallationPinRejectsMismatchAndForkReceiptWithoutWrites()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var db = test.CreateDbContext();
            var caller = await test.CreateReferenceAdminAsync(db);
            var operation = Guid.NewGuid();
            var id = "byok-" + operation.ToString("N");
            var request = DraftRequest(operation, 0) with
            {
                Reference = "Pin-" + operation.ToString("N"),
                TargetInstallationId = isolated.InstallationId,
            };
            var service = test.ReferenceService(db);
            var foreign = Guid.NewGuid().ToString("D");
            var mismatch = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
                    request with { TargetInstallationId = foreign }, CancellationToken.None));
            Assert.Equal("target_installation_mismatch", mismatch.Code);
            Assert.False(await db.ModelProviderDrafts.AnyAsync(item => item.ProviderId == id));
            Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(item => item.OperationId == operation));
            await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, request, CancellationToken.None);

            await using (var owner = await isolated.MigrationDataSource.OpenConnectionAsync())
            await using (var rekey = new NpgsqlCommand("""
                SELECT pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                UPDATE projects_config.installation_identity SET installation_id = @new WHERE singleton;
                """, owner))
            {
                rekey.Parameters.AddWithValue("new", Guid.Parse(foreign));
                await rekey.ExecuteNonQueryAsync();
            }
            mismatch = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, request, CancellationToken.None));
            Assert.Equal("target_installation_mismatch", mismatch.Code);
            var reconciliation = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
                    request with { TargetInstallationId = foreign }, CancellationToken.None));
            Assert.Equal("reference_reconciliation_required", reconciliation.Code);
            Assert.Equal(Guid.Parse(isolated.InstallationId), (await db.ModelProviderReferenceReservations.AsNoTracking()
                .SingleAsync(item => item.OperationId == operation)).InstallationId);
            Assert.Equal(1, await db.ModelProviderDraftRevisions.CountAsync(item => item.OperationId == operation));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacyReservationDoesNotAcquireTheNewNamespace()
    {
        var isolated = new ProjectsConfigPostgresFixture { StopBeforeInstallationIdentity = true };
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var db = test.CreateDbContext();
            var caller = await test.CreateReferenceAdminAsync(db);
            var operation = Guid.Parse("00000000-0000-0000-0000-000000000001");
            var id = "byok-" + operation.ToString("N");
            const string reference = "Stable.Model";
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
                        '5f466e3c59b9d7ac835aeb20aec7d8cdd0169e835402261d4583359032062b3f', 'Example',
                        'openai', 'https://example.com', 'model-1', ARRAY[]::text[],
                        '177ac20040cdb6081c5ab040877ee5b555af2f63ae546afbfb71cd73e9132dda', @actor, now());
                    INSERT INTO projects_config.model_provider_reference_reservations
                        (reference_folded, reference, provider_id, creation_revision, operation_id,
                         issuer, tenant_id, subject, created_at)
                    VALUES (lower(@reference), @reference, @id, 1, @operation, @issuer, @tenant, @actor, now());
                    """, connection))
            {
                seed.Parameters.AddWithValue("id", id);
                seed.Parameters.AddWithValue("operation", operation);
                seed.Parameters.AddWithValue("reference", reference);
                seed.Parameters.AddWithValue("actor", caller.ActorId);
                seed.Parameters.AddWithValue("issuer", caller.Issuer);
                seed.Parameters.AddWithValue("tenant", caller.TenantId);
                await seed.ExecuteNonQueryAsync();
            }
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using (var migration = new ProjectsConfigDbContext(options))
                await migration.GetService<IMigrator>().MigrateAsync();
            await using var owner = await isolated.DataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", owner);
            var installation = ((Guid)(await read.ExecuteScalarAsync())!).ToString("D");
            var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                test.ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
                    new SaveModelProviderDraftRequest(operation, 0, "Example", "openai", "https://example.com", "model-1")
                    {
                        Reference = reference,
                        TargetInstallationId = installation,
                    },
                    CancellationToken.None));
            Assert.Equal("reference_reconciliation_required", error.Code);
            var authority = new ProjectsConfigPrivilegedAuthorityStore(test.CreateDbContextOptions(), TimeProvider.System);
            const string otherIssuer = "https://other-issuer.test/";
            var otherMembership = await authority.GrantMembershipAsync(otherIssuer, caller.ActorId, caller.TenantId, "fixture");
            await authority.AssignRoleAsync(otherMembership.MembershipId, ProjectAuthorityResourceType.Platform,
                ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin, "fixture");
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", caller.ActorId), new Claim("scope", "api.read projects.admin")], "test"));
            var otherIssuerCaller = await new ProjectAuthorizationOwner(db, new ProjectsConfigIdentityOptions(otherIssuer))
                .ResolveAsync(principal, [caller.TenantId], CancellationToken.None);
            foreach (var other in new[]
            {
                await SeedCallerAsync(db, authority, "other-" + Guid.NewGuid().ToString("N"), caller.TenantId,
                    ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin),
                otherIssuerCaller,
                await SeedCallerAsync(db, authority, caller.ActorId, "other-" + Guid.NewGuid().ToString("N"),
                    ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin),
            })
            {
                var denied = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    test.ReferenceService(db).SaveAsync(other, ModelProviderBindingScope.Platform, null, id,
                        new SaveModelProviderDraftRequest(operation, 0, "Example", "openai", "https://example.com", "model-1")
                        {
                            Reference = reference,
                            TargetInstallationId = installation,
                        }, CancellationToken.None));
                Assert.Equal("model_provider_draft_conflict", denied.Code);
            }
            Assert.Null((await db.ModelProviderReferenceReservations.AsNoTracking()
                .SingleAsync(item => item.OperationId == operation)).InstallationId);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task InstallationIdentityRuntimeCanReadButCannotLockOrModify()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using (var read = new NpgsqlCommand(
            "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection))
            Assert.Equal(Guid.Parse(fixture.InstallationId), await read.ExecuteScalarAsync());
        foreach (var sql in new[]
        {
                "SELECT installation_id FROM projects_config.installation_identity FOR SHARE",
                "UPDATE projects_config.installation_identity SET installation_id = gen_random_uuid()",
                "DELETE FROM projects_config.installation_identity",
            })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
    }

    [Theory]
    [InlineData("UPDATE (installation_id)")]
    [InlineData("INSERT (installation_id)")]
    [InlineData("REFERENCES (installation_id)")]
    [InlineData("TRIGGER")]
    public async Task InstallationIdentityStartupRejectsInheritedExtraPrivileges(string privilege)
    {
        var role = "installation_extra_" + Guid.NewGuid().ToString("N");
        await using var admin = await fixture.DataSource.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand($"""
            CREATE ROLE "{role}" NOLOGIN INHERIT;
            GRANT "agentweaver-id-projects-config" TO "{role}";
            GRANT {privilege} ON projects_config.installation_identity TO "{role}";
            """, admin))
            await setup.ExecuteNonQueryAsync();
        try
        {
            var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Options = "-c role=" + role,
            }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(connectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(source));
        }
        finally
        {
            await using var cleanup = new NpgsqlCommand($"""
                DROP OWNED BY "{role}";
                DROP ROLE "{role}";
                """, admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task RuntimeGrantPlanRejectsMutatedInstallationGuardSource()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            await using var owner = await isolated.MigrationDataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand("""
                SELECT pg_get_functiondef('projects_config.guard_provider_reference_installation()'::regprocedure)
                """, owner);
            var original = Assert.IsType<string>(await read.ExecuteScalarAsync());
            var runtimeConnection = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
            }.ConnectionString;
            await using var runtime = NpgsqlDataSource.Create(runtimeConnection);
            try
            {
                Assert.Contains("'read committed'", original);
                foreach (var mutated in new[]
                {
                    original.Replace("'read committed'", "'read  committed'", StringComparison.Ordinal),
                    """
                    CREATE OR REPLACE FUNCTION projects_config.guard_provider_reference_installation()
                    RETURNS trigger LANGUAGE plpgsql SET search_path = pg_catalog AS $body$
                    BEGIN RETURN NEW; END;
                    $body$;
                    """,
                })
                {
                    await using var mutate = new NpgsqlCommand(mutated, owner);
                    await mutate.ExecuteNonQueryAsync();
                    var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        ProjectsConfigRuntimeGrantPlan.VerifyRuntimeGrantsAsync(owner, CancellationToken.None));
                    Assert.Contains("installation guard", failure.Message, StringComparison.Ordinal);
                    failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                        ProjectsConfigMigrator.VerifyRuntimeAuthorityReadOnlyAsync(runtime));
                    Assert.Contains("installation guard", failure.Message, StringComparison.Ordinal);
                }
            }
            finally
            {
                await using var restore = new NpgsqlCommand(original, owner);
                await restore.ExecuteNonQueryAsync();
            }
            await ProjectsConfigRuntimeGrantPlan.VerifyRuntimeGrantsAsync(owner, CancellationToken.None);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task NewOwnerRejectsPreIdentitySchemaAndConcurrentMigrationsKeepOneIdentity()
    {
        var isolated = new ProjectsConfigPostgresFixture { StopBeforeInstallationIdentity = true };
        await isolated.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, config => config.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            var unavailable = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(isolated.MigrationDataSource, options));
            Assert.Contains("BindProviderInstallationIdentity", unavailable.Message, StringComparison.Ordinal);
            await Task.WhenAll(
                ProjectsConfigMigrator.MigrateAsync(isolated.MigrationDataSource, options),
                ProjectsConfigMigrator.MigrateAsync(isolated.MigrationDataSource, options));
            await ProjectsConfigMigrator.VerifyMigrationsAppliedAsync(isolated.MigrationDataSource, options);
            await using var connection = await isolated.MigrationDataSource.OpenConnectionAsync();
            await using var read = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection);
            var original = Assert.IsType<Guid>(await read.ExecuteScalarAsync());
            Assert.NotEqual(Guid.Empty, original);
            await ProjectsConfigMigrator.MigrateAsync(isolated.MigrationDataSource, options);
            Assert.Equal(original, await read.ExecuteScalarAsync());
            foreach (var sql in new[]
            {
                "INSERT INTO projects_config.installation_identity VALUES (true, gen_random_uuid())",
                "INSERT INTO projects_config.installation_identity VALUES (false, gen_random_uuid())",
                "UPDATE projects_config.installation_identity SET installation_id = '00000000-0000-0000-0000-000000000000'",
            })
            {
                await using var invalid = new NpgsqlCommand(sql, connection);
                var error = await Assert.ThrowsAsync<PostgresException>(() => invalid.ExecuteNonQueryAsync());
                Assert.Contains(error.SqlState, new[] { PostgresErrorCodes.UniqueViolation, PostgresErrorCodes.CheckViolation });
                Assert.Equal(original, await read.ExecuteScalarAsync());
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task MissingInstallationBlocksReferencesButNotOrdinaryCreatesOrUpdates()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var db = test.CreateDbContext();
            var caller = await test.CreateReferenceAdminAsync(db);
            await using (var owner = await isolated.MigrationDataSource.OpenConnectionAsync())
            await using (var delete = new NpgsqlCommand("DELETE FROM projects_config.installation_identity", owner))
                await delete.ExecuteNonQueryAsync();
            var operation = Guid.NewGuid();
            var id = "byok-" + operation.ToString("N");
            var reference = DraftRequest(operation, 0) with
            {
                Reference = "Missing-" + operation.ToString("N"),
                TargetInstallationId = isolated.InstallationId,
            };
            var service = test.ReferenceService(db);
            var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, reference, CancellationToken.None));
            Assert.Equal("model_provider_draft_unavailable", error.Code);
            Assert.False(await db.ModelProviderDrafts.AnyAsync(item => item.ProviderId == id));
            var ordinary = DraftRequest(operation, 0);
            Assert.Equal(1, (await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                id, ordinary, CancellationToken.None)).Binding.ProviderRevision);
            Assert.Equal(1, (await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                id, ordinary, CancellationToken.None)).Binding.ProviderRevision);
            Assert.Equal(2, (await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                id, DraftRequest(Guid.NewGuid(), 1), CancellationToken.None)).Binding.ProviderRevision);
            var suffix = Guid.NewGuid().ToString("N");
            var tenant = "ordinary-project-" + suffix;
            var subject = "ordinary-owner-" + suffix;
            var authority = new ProjectsConfigPrivilegedAuthorityStore(test.CreateDbContextOptions(), TimeProvider.System);
            var tenantOwner = await SeedCallerAsync(db, authority, subject, tenant, ["api.read", "projects.admin"],
                ProjectAuthorityResourceType.Tenant, tenant, ProjectAuthorityRole.TenantAdmin);
            var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
                .CreateProjectAsync(tenantOwner, "Ordinary draft", CancellationToken.None);
            var bound = await ResolveCallerAsync(db, subject, tenant, ["api.read", "projects.admin"],
                boundProjectId: project.ProjectId);
            var projectOperation = Guid.NewGuid();
            var projectId = "byok-" + projectOperation.ToString("N");
            var projectRequest = DraftRequest(projectOperation, 0);
            Assert.Equal(1, (await service.SaveAsync(bound, ModelProviderBindingScope.Project, project.ProjectId,
                projectId, projectRequest, CancellationToken.None)).Binding.ProviderRevision);
            Assert.Equal(1, (await service.SaveAsync(bound, ModelProviderBindingScope.Project, project.ProjectId,
                projectId, projectRequest, CancellationToken.None)).Binding.ProviderRevision);
            Assert.Equal(2, (await service.SaveAsync(bound, ModelProviderBindingScope.Project, project.ProjectId,
                projectId, DraftRequest(Guid.NewGuid(), 1), CancellationToken.None)).Binding.ProviderRevision);
            var invalidProjectOperation = Guid.NewGuid();
            var wrongScope = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(bound, ModelProviderBindingScope.Project, project.ProjectId,
                    "byok-" + invalidProjectOperation.ToString("N"),
                    DraftRequest(invalidProjectOperation, 0) with
                    {
                        Reference = "WrongScope",
                        TargetInstallationId = isolated.InstallationId,
                    }, CancellationToken.None));
            Assert.Equal("model_provider_draft_invalid_request", wrongScope.Code);
            Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == invalidProjectOperation));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task UnreadableInstallationBlocksReplayButNotOrdinaryDrafts()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var seed = test.CreateDbContext();
            var caller = await test.CreateReferenceAdminAsync(seed);
            var connectionString = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
            }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(connectionString);
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(source, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var db = new ProjectsConfigDbContext(options);
            var operation = Guid.NewGuid();
            var id = "byok-" + operation.ToString("N");
            var request = DraftRequest(operation, 0) with
            {
                Reference = "Unreadable-" + operation.ToString("N"),
                TargetInstallationId = isolated.InstallationId,
            };
            var service = test.ReferenceService(db);
            await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, request, CancellationToken.None);
            await using (var owner = await isolated.MigrationDataSource.OpenConnectionAsync())
            await using (var revoke = new NpgsqlCommand("""
                REVOKE SELECT ON projects_config.installation_identity FROM "agentweaver-id-projects-config"
                """, owner))
                await revoke.ExecuteNonQueryAsync();
            var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, request, CancellationToken.None));
            Assert.Equal("model_provider_draft_unavailable", error.Code);
            Assert.Equal(1, await seed.ModelProviderReferenceReservations.CountAsync(row => row.OperationId == operation));
            var ordinaryOperation = Guid.NewGuid();
            Assert.Equal(1, (await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                "byok-" + ordinaryOperation.ToString("N"), DraftRequest(ordinaryOperation, 0),
                CancellationToken.None)).Binding.ProviderRevision);
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Fact]
    public async Task InternalDraftCallsCannotBypassInstallationPinValidation()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var service = ReferenceService(db);
        var operation = Guid.NewGuid();
        var id = "byok-" + operation.ToString("N");
        var reference = "Internal-" + operation.ToString("N");
        foreach (var target in new string?[]
        {
            null, "", "invalid", Guid.Empty.ToString("D"), "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA",
            Guid.Parse(fixture.InstallationId).ToString("N"),
        })
        {
            var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
                    DraftRequest(operation, 0) with { Reference = reference, TargetInstallationId = target },
                    CancellationToken.None));
            Assert.Equal("model_provider_draft_invalid_request", error.Code);
        }
        var ordinaryWithPin = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
                DraftRequest(operation, 0) with { TargetInstallationId = fixture.InstallationId },
                CancellationToken.None));
        Assert.Equal("model_provider_draft_invalid_request", ordinaryWithPin.Code);
        var updateWithPin = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
            service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
                DraftRequest(Guid.NewGuid(), 1) with { TargetInstallationId = fixture.InstallationId },
                CancellationToken.None));
        Assert.Equal("model_provider_draft_invalid_request", updateWithPin.Code);
        Assert.False(await db.ModelProviderDrafts.AnyAsync(row => row.ProviderId == id));
        Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == operation));
        Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(row => row.OperationId == operation));
    }

    [Fact]
    public async Task RuntimeCannotInsertMissingOrForeignReservationInstallation()
    {
        await using var db = CreateDbContext();
        var caller = await CreateReferenceAdminAsync(db);
        var operation = Guid.NewGuid();
        var id = "byok-" + operation.ToString("N");
        await ReferenceService(db).SaveAsync(caller, ModelProviderBindingScope.Platform, null, id,
            DraftRequest(operation, 0), CancellationToken.None);
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Options = "-c role=agentweaver-id-projects-config",
        }.ConnectionString;
        await using var source = NpgsqlDataSource.Create(connectionString);
        await using var connection = await source.OpenConnectionAsync();
        foreach (var target in new Guid?[] { null, Guid.Empty, Guid.NewGuid() })
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO projects_config.model_provider_reference_reservations
                    (reference_folded, reference, provider_id, creation_revision, operation_id,
                     issuer, tenant_id, subject, created_at, installation_id)
                VALUES (@reference, @reference, @provider, 1, @operation, 'issuer', 'tenant', 'actor', now(), @target)
                """, connection);
            command.Parameters.AddWithValue("reference", "direct-" + Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("provider", id);
            command.Parameters.AddWithValue("operation", Guid.NewGuid());
            command.Parameters.AddWithValue("target", NpgsqlTypes.NpgsqlDbType.Uuid, (object?)target ?? DBNull.Value);
            var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.Equal("ck_provider_reference_installation_id", error.ConstraintName);
        }
        Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(item => item.ProviderId == id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictRetryRechecksInstallationBeforeReadingReceipt(bool competingReceipt)
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var db = test.CreateDbContext();
            var caller = await test.CreateReferenceAdminAsync(db);
            var operation = Guid.NewGuid();
            var providerId = "byok-" + operation.ToString("N");
            var name = "force_conflict_" + operation.ToString("N");
            await using var owner = await isolated.MigrationDataSource.OpenConnectionAsync();
            await using (var install = new NpgsqlCommand($"""
                CREATE FUNCTION projects_config.{name}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.operation_id = '{operation}'::uuid THEN
                        RAISE EXCEPTION 'injected conflict'
                            USING ERRCODE = '23505', CONSTRAINT = 'pk_model_provider_reference_reservations';
                    END IF;
                    RETURN NEW;
                END;
                $body$;
                CREATE TRIGGER {name} BEFORE INSERT
                ON projects_config.model_provider_reference_reservations
                FOR EACH ROW EXECUTE FUNCTION projects_config.{name}();
                """, owner))
                await install.ExecuteNonQueryAsync();
            var service = test.ReferenceService(db);
            var rekeyed = false;
            var newId = Guid.NewGuid();
            var reference = "Retry-" + operation.ToString("N");
            service.AfterCreateConflict = async token =>
            {
                await using var transaction = await owner.BeginTransactionAsync(token);
                await using var rekey = new NpgsqlCommand("""
                    SELECT pg_catalog.pg_advisory_xact_lock(
                        pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                    UPDATE projects_config.installation_identity SET installation_id = @new WHERE singleton;
                    """, owner, transaction);
                rekey.Parameters.AddWithValue("new", newId);
                await rekey.ExecuteNonQueryAsync(token);
                await transaction.CommitAsync(token);
                if (competingReceipt)
                {
                    await using var remove = new NpgsqlCommand($"""
                        DROP TRIGGER {name} ON projects_config.model_provider_reference_reservations;
                        DROP FUNCTION projects_config.{name}();
                        """, owner);
                    await remove.ExecuteNonQueryAsync(token);
                    await using var winnerDb = test.CreateDbContext();
                    await test.ReferenceService(winnerDb).SaveAsync(caller, ModelProviderBindingScope.Platform,
                        null, providerId, DraftRequest(operation, 0) with
                        {
                            Reference = reference,
                            TargetInstallationId = newId.ToString("D"),
                        }, token);
                }
                rekeyed = true;
            };
            try
            {
                var error = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                        DraftRequest(operation, 0) with
                        {
                            Reference = reference,
                            TargetInstallationId = isolated.InstallationId,
                        }, CancellationToken.None));
                Assert.True(rekeyed);
                Assert.Equal("target_installation_mismatch", error.Code);
                Assert.Equal(competingReceipt ? 1 : 0,
                    await db.ModelProviderDrafts.AsNoTracking().CountAsync(row => row.ProviderId == providerId));
                Assert.Equal(competingReceipt ? 1 : 0,
                    await db.ModelProviderDraftRevisions.AsNoTracking().CountAsync(row => row.OperationId == operation));
                Assert.Equal(competingReceipt ? 1 : 0,
                    await db.ModelProviderReferenceReservations.AsNoTracking().CountAsync(row => row.OperationId == operation));
            }
            finally
            {
                await using var cleanup = new NpgsqlCommand($"""
                    DROP TRIGGER IF EXISTS {name} ON projects_config.model_provider_reference_reservations;
                    DROP FUNCTION IF EXISTS projects_config.{name}();
                    """, owner);
                await cleanup.ExecuteNonQueryAsync();
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallationRekeyAndCreateSerializeUnderTheSameLock(bool createFirst)
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            await using var seed = test.CreateDbContext();
            var caller = await test.CreateReferenceAdminAsync(seed);
            var operation = Guid.NewGuid();
            var id = "byok-" + operation.ToString("N");
            var request = DraftRequest(operation, 0) with
            {
                Reference = "Race-" + operation.ToString("N"),
                TargetInstallationId = isolated.InstallationId,
            };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var runtimeConnection = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
                ApplicationName = "installation-create-" + operation.ToString("N"),
            }.ConnectionString;
            await using var source = NpgsqlDataSource.Create(runtimeConnection);
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(source, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var db = new ProjectsConfigDbContext(options);
            var service = test.ReferenceService(db);
            var locked = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.AfterReferenceAuthorityLocked = async (pid, token) =>
            {
                locked.TrySetResult(pid);
                await release.Task.WaitAsync(token);
            };
            await using var owner = await isolated.MigrationDataSource.OpenConnectionAsync(timeout.Token);
            await using var transaction = await owner.BeginTransactionAsync(timeout.Token);
            await using var rekeyLock = new NpgsqlCommand("""
                SELECT pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0))
                """, owner, transaction);
            Task<ModelProviderDraft>? create = null;
            try
            {
                if (createFirst)
                {
                    create = service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, request, timeout.Token);
                    var createPid = await locked.Task.WaitAsync(timeout.Token);
                    var lockTask = rekeyLock.ExecuteNonQueryAsync(timeout.Token);
                    await WaitForBlockedAsync(isolated.DataSource, owner.ProcessID, createPid, timeout.Token);
                    release.TrySetResult();
                    await create.WaitAsync(timeout.Token);
                    await lockTask;
                }
                else
                    await rekeyLock.ExecuteNonQueryAsync(timeout.Token);

                await using (var update = new NpgsqlCommand(
                    "UPDATE projects_config.installation_identity SET installation_id = @new WHERE singleton",
                    owner, transaction))
                {
                    update.Parameters.AddWithValue("new", Guid.NewGuid());
                    await update.ExecuteNonQueryAsync(timeout.Token);
                }
                if (!createFirst)
                {
                    create = service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, id, request, timeout.Token);
                    await using var observer = await isolated.DataSource.OpenConnectionAsync(timeout.Token);
                    while (true)
                    {
                        await using var query = new NpgsqlCommand("""
                            SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                                WHERE application_name = @name AND @owner = ANY(pg_blocking_pids(pid)))
                            """, observer);
                        query.Parameters.AddWithValue("name", "installation-create-" + operation.ToString("N"));
                        query.Parameters.AddWithValue("owner", owner.ProcessID);
                        if ((bool)(await query.ExecuteScalarAsync(timeout.Token) ?? false)) break;
                        Assert.False(create.IsCompleted);
                        await Task.Delay(20, timeout.Token);
                    }
                    release.TrySetResult();
                }
                await transaction.CommitAsync(timeout.Token);
                if (!createFirst)
                    Assert.Equal("target_installation_mismatch",
                        (await Assert.ThrowsAsync<ModelProviderDraftException>(() => create!)).Code);
                Assert.Equal(createFirst ? 1 : 0,
                    await seed.ModelProviderReferenceReservations.CountAsync(item => item.OperationId == operation));
            }
            finally
            {
                release.TrySetResult();
                timeout.Cancel();
                if (create is not null)
                    try { await create; } catch { }
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }

    private static async Task WaitForBlockedAsync(
        NpgsqlDataSource source, int blocked, int blocker, CancellationToken token)
    {
        await using var observer = await source.OpenConnectionAsync(token);
        while (true)
        {
            await using var query = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@blocked))", observer);
            query.Parameters.AddWithValue("blocker", blocker);
            query.Parameters.AddWithValue("blocked", blocked);
            if ((bool)(await query.ExecuteScalarAsync(token) ?? false)) return;
            await Task.Delay(20, token);
        }
    }

    [Fact]
    public async Task FailedIdentityMigrationRollsBackReservationColumnAndHistory()
    {
        var isolated = new ProjectsConfigPostgresFixture { StopBeforeInstallationIdentity = true };
        await isolated.InitializeAsync();
        try
        {
            await using var connection = await isolated.MigrationDataSource.OpenConnectionAsync();
            await using (var conflict = new NpgsqlCommand(
                "CREATE TABLE projects_config.installation_identity (sentinel integer)", connection))
                await conflict.ExecuteNonQueryAsync();
            var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await using var db = new ProjectsConfigDbContext(options);
            await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync());
            await using (var verify = new NpgsqlCommand("""
                    SELECT NOT EXISTS (SELECT 1 FROM information_schema.columns
                        WHERE table_schema = 'projects_config' AND table_name = 'model_provider_reference_reservations'
                          AND column_name = 'installation_id')
                       AND NOT EXISTS (SELECT 1 FROM projects_config.__ef_migrations_history
                        WHERE "MigrationId" = '20261119175249_BindProviderInstallationIdentity')
                    """, connection))
                Assert.True((bool)(await verify.ExecuteScalarAsync() ?? false));
            await using (var cleanup = new NpgsqlCommand("DROP TABLE projects_config.installation_identity", connection))
                await cleanup.ExecuteNonQueryAsync();
            await db.GetService<IMigrator>().MigrateAsync();
            await using var read = new NpgsqlCommand(
                "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection);
            Assert.NotEqual(Guid.Empty, Assert.IsType<Guid>(await read.ExecuteScalarAsync()));
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
}
