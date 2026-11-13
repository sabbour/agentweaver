using System.Collections.Immutable;
using System.Text;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task NativeAcsPolicyWriteStoresFullArtifactAndReplaysAfterHeadAdvances()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(caller, "ACS storage", CancellationToken.None);
        var store = CreateNativeAcsStore();
        var artifact1 = Artifact("package one");
        var request1 = Request(project.ProjectId, artifact1, expectedRevision: null, "acs-key-" + suffix);

        var first = await store.WriteAsync(caller, request1, CancellationToken.None);
        var request2 = Request(project.ProjectId, Artifact("package two"), first.Revision, "acs-key-2-" + suffix);
        var second = await CreateNativeAcsStore().WriteAsync(caller, request2, CancellationToken.None);
        var replay = await CreateNativeAcsStore().WriteAsync(caller, request1, CancellationToken.None);

        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.True(replay.Replayed);
        Assert.Equal(first with { Replayed = true }, replay);
        await using var stored = CreateDbContext();
        var revision = await stored.NativeAcsPolicyRevisions.AsNoTracking().SingleAsync(item =>
            item.ScopeId == project.ProjectId && item.Revision == first.Revision);
        var file = await stored.NativeAcsPolicyArtifactFiles.AsNoTracking().SingleAsync(item =>
            item.ScopeId == project.ProjectId && item.Revision == first.Revision);
        Assert.Equal(artifact1.ManifestBytes.ToArray(), revision.ManifestBytes);
        Assert.Equal(artifact1.Files[0].Bytes.ToArray(), file.Bytes);
        Assert.Equal(artifact1.Files[0].RelativePath, file.RelativePath);
        Assert.Equal(2, await stored.NativeAcsPolicyChangeAudit.CountAsync(item => item.ScopeId == project.ProjectId));
        var outboxPayloads = await stored.NativeAcsPolicyOutbox.AsNoTracking()
            .Select(item => item.PayloadJson)
            .ToArrayAsync();
        Assert.Equal(2, outboxPayloads.Count(payload => payload.Contains(project.ProjectId, StringComparison.Ordinal)));
        var idempotencyDigest = await stored.NativeAcsPolicyIdempotency.AsNoTracking()
            .Where(item => item.IdempotencyKey == request1.IdempotencyKey)
            .Select(item => item.ScopeDigest)
            .SingleAsync();

        var reusedKey = await Assert.ThrowsAsync<ProjectConfigException>(() =>
            CreateNativeAcsStore().WriteAsync(
                caller,
                request1 with { Reason = "different request" },
                CancellationToken.None));
        Assert.Equal("idempotency_conflict", reusedKey.Code);

        await using var runtimeConnection = await fixture.DataSource.OpenConnectionAsync();
        await using (var setRole = new NpgsqlCommand(
            """SET ROLE "agentweaver-id-projects-config" """,
            runtimeConnection))
            await setRole.ExecuteNonQueryAsync();
        try
        {
            await using (var lateFile = new NpgsqlCommand(
                """
                INSERT INTO projects_config.native_acs_policy_artifact_files
                    (scope_type, scope_id, policy_id, revision, relative_path, bytes)
                VALUES ('Project', @scope_id, 'baseline', 1, 'policies/late.rego', decode('00', 'hex'))
                """,
                runtimeConnection))
            {
                lateFile.Parameters.AddWithValue("scope_id", project.ProjectId);
                var denied = await Assert.ThrowsAsync<PostgresException>(() => lateFile.ExecuteNonQueryAsync());
                Assert.Contains(denied.SqlState, new[]
                {
                    PostgresErrorCodes.ObjectNotInPrerequisiteState,
                    PostgresErrorCodes.InsufficientPrivilege,
                });
            }

            foreach (var (sql, parameterName, value) in new[]
            {
                ("UPDATE projects_config.native_acs_policy_change_audit SET reason = 'changed' WHERE audit_id = @id",
                    "id", (object)first.AuditId),
                ("DELETE FROM projects_config.native_acs_policy_change_audit WHERE audit_id = @id",
                    "id", (object)first.AuditId),
                ("UPDATE projects_config.native_acs_policy_outbox SET event_type = 'changed' WHERE outbox_id = @id",
                    "id", (object)first.OutboxId),
                ("DELETE FROM projects_config.native_acs_policy_outbox WHERE outbox_id = @id",
                    "id", (object)first.OutboxId),
            })
            {
                await using var command = new NpgsqlCommand(sql, runtimeConnection);
                command.Parameters.AddWithValue(parameterName, value);
                var denied = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            }
        }
        finally
        {
            await using var resetRole = new NpgsqlCommand("RESET ROLE", runtimeConnection);
            await resetRole.ExecuteNonQueryAsync();
        }

        await AssertImmutableAsync(
            "UPDATE projects_config.native_acs_policy_revisions SET reason = 'changed' " +
            "WHERE scope_id = @scope_id AND revision = 1",
            ("scope_id", project.ProjectId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.native_acs_policy_revisions WHERE scope_id = @scope_id AND revision = 1",
            ("scope_id", project.ProjectId));
        await AssertImmutableAsync(
            "UPDATE projects_config.native_acs_policy_artifact_files SET relative_path = 'changed' " +
            "WHERE scope_id = @scope_id AND revision = 1",
            ("scope_id", project.ProjectId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.native_acs_policy_artifact_files " +
            "WHERE scope_id = @scope_id AND revision = 1",
            ("scope_id", project.ProjectId));
        await AssertImmutableAsync(
            "UPDATE projects_config.native_acs_policy_idempotency SET receipt = '{}'::jsonb " +
            "WHERE scope_digest = @digest",
            ("digest", idempotencyDigest));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.native_acs_policy_idempotency WHERE scope_digest = @digest",
            ("digest", idempotencyDigest));
        await AssertImmutableAsync(
            "UPDATE projects_config.native_acs_policy_change_audit SET reason = 'changed' WHERE audit_id = @id",
            ("id", first.AuditId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.native_acs_policy_change_audit WHERE audit_id = @id",
            ("id", first.AuditId));
        await AssertImmutableAsync(
            "UPDATE projects_config.native_acs_policy_outbox SET event_type = 'changed' WHERE outbox_id = @id",
            ("id", first.OutboxId));
        await AssertImmutableAsync(
            "DELETE FROM projects_config.native_acs_policy_outbox WHERE outbox_id = @id",
            ("id", second.OutboxId));
    }

    [Fact]
    public async Task NativeAcsPolicyIdempotencyKeysAreIsolatedAcrossProjectScopes()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-scope-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-scope-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var service = new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System);
        var firstProject = await service.CreateProjectAsync(caller, "ACS scope one", CancellationToken.None);
        var secondProject = await service.CreateProjectAsync(caller, "ACS scope two", CancellationToken.None);
        var idempotencyKey = "acs-shared-key-" + suffix;

        var first = await CreateNativeAcsStore().WriteAsync(
            caller, Request(firstProject.ProjectId, Artifact("first scope"), null, idempotencyKey),
            CancellationToken.None);
        var second = await CreateNativeAcsStore().WriteAsync(
            caller, Request(secondProject.ProjectId, Artifact("second scope"), null, idempotencyKey),
            CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.False(second.Replayed);
        Assert.Equal(firstProject.ProjectId, first.ScopeId);
        Assert.Equal(secondProject.ProjectId, second.ScopeId);
        Assert.Equal(1, first.Revision);
        Assert.Equal(1, second.Revision);
    }

    [Fact]
    public async Task NativeAcsPolicyHeadsAllowOnlyOnePolicyIdPerScope()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-head-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-head-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(caller, "ACS head uniqueness", CancellationToken.None);
        var store = CreateNativeAcsStore();
        await store.WriteAsync(
            caller,
            Request(project.ProjectId, Artifact("first"), null, "acs-head-first-" + suffix),
            CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<ProjectConfigException>(() => CreateNativeAcsStore().WriteAsync(
            caller,
            Request(
                project.ProjectId,
                Artifact("second"),
                null,
                "acs-head-second-" + suffix,
                policyId: "another-policy"),
            CancellationToken.None));

        Assert.Equal("revision_conflict", conflict.Code);
        await using var verify = CreateDbContext();
        Assert.Single(await verify.NativeAcsPolicyHeads.AsNoTracking()
            .Where(item => item.ScopeId == project.ProjectId)
            .ToArrayAsync());
        Assert.Single(await verify.NativeAcsPolicyRevisions.AsNoTracking()
            .Where(item => item.ScopeId == project.ProjectId)
            .ToArrayAsync());
    }

    [Fact]
    public async Task NativeAcsPolicyHeadMigrationKeepsOneExistingHeadAndRefusesDuplicateHeads()
    {
        const string beforeHeadIndex = "20261111192724_AddNativeAcsPolicyStorage";
        const string headIndex = "20261113221434_EnforceOneNativeAcsPolicyHeadPerScope";
        await using var db = CreateDbContext();
        var latestMigration = db.Database.GetMigrations().Last();
        await using var migrationDb = CreateMigrationDbContext();
        var migrator = migrationDb.GetService<IMigrator>();
        var suffix = Guid.NewGuid().ToString("N");
        var singleScope = "acs-migration-single-" + suffix;
        var duplicateScope = "acs-migration-duplicate-" + suffix;
        var seeded = new[]
        {
            (Scope: singleScope, PolicyId: "legacy-" + suffix[..8]),
            (Scope: duplicateScope, PolicyId: "first-" + suffix[..8]),
            (Scope: duplicateScope, PolicyId: "second-" + suffix[..8]),
        };
        try
        {
            await migrator.MigrateAsync(beforeHeadIndex);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            {
                foreach (var head in seeded)
                {
                    await using var insert = new NpgsqlCommand(
                        """
                        INSERT INTO projects_config.native_acs_policy_heads
                            (scope_type, scope_id, policy_id, current_revision, created_at, updated_at)
                        VALUES ('Project', @scope_id, @policy_id, 3, now(), now())
                        """,
                        connection);
                    insert.Parameters.AddWithValue("scope_id", head.Scope);
                    insert.Parameters.AddWithValue("policy_id", head.PolicyId);
                    await insert.ExecuteNonQueryAsync();
                }
            }
            var before = await ReadMigrationHeadsAsync(singleScope, duplicateScope);

            var refused = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(latestMigration));

            Assert.Contains("more than one policy head", refused.MessageText, StringComparison.Ordinal);
            Assert.Contains($"Project/{duplicateScope}", refused.MessageText, StringComparison.Ordinal);
            Assert.Contains(seeded[1].PolicyId, refused.MessageText, StringComparison.Ordinal);
            Assert.Contains(seeded[2].PolicyId, refused.MessageText, StringComparison.Ordinal);
            Assert.DoesNotContain(singleScope, refused.MessageText, StringComparison.Ordinal);
            Assert.DoesNotContain(headIndex, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(before, await ReadMigrationHeadsAsync(singleScope, duplicateScope));

            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var removeDuplicate = new NpgsqlCommand(
                "DELETE FROM projects_config.native_acs_policy_heads WHERE scope_id = @scope_id",
                connection))
            {
                removeDuplicate.Parameters.AddWithValue("scope_id", duplicateScope);
                await removeDuplicate.ExecuteNonQueryAsync();
            }
            await migrator.MigrateAsync(latestMigration);

            Assert.Contains(headIndex, await db.Database.GetAppliedMigrationsAsync());
            Assert.Equal(
                before.Where(head => head.StartsWith(singleScope, StringComparison.Ordinal)),
                await ReadMigrationHeadsAsync(singleScope, duplicateScope));
            var kept = await new NativeAcsPolicyStore(
                    new TestDbContextFactory(CreateDbContextOptions()), TimeProvider.System)
                .FindPolicyIdAsync(NativeAcsPolicyScopeType.Project, singleScope, CancellationToken.None);
            Assert.Equal(seeded[0].PolicyId, kept);
        }
        finally
        {
            await migrator.MigrateAsync(beforeHeadIndex);
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var cleanup = new NpgsqlCommand(
                "DELETE FROM projects_config.native_acs_policy_heads WHERE scope_id = ANY(@scope_ids)",
                connection))
            {
                cleanup.Parameters.AddWithValue("scope_ids", new[] { singleScope, duplicateScope });
                await cleanup.ExecuteNonQueryAsync();
            }
            await migrator.MigrateAsync(latestMigration);
        }
    }

    private async Task<string[]> ReadMigrationHeadsAsync(params string[] scopeIds)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var read = new NpgsqlCommand(
            """
            SELECT scope_id || '|' || policy_id || '|' || current_revision::text
                || '|' || created_at::text || '|' || updated_at::text
            FROM projects_config.native_acs_policy_heads
            WHERE scope_id = ANY(@scope_ids)
            ORDER BY scope_id, policy_id
            """,
            connection);
        read.Parameters.AddWithValue("scope_ids", scopeIds);
        var rows = new List<string>();
        await using var reader = await read.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add(reader.GetString(0));
        return [.. rows];
    }

    [Fact]
    public async Task ConcurrentNativeAcsPolicyWritesAcceptOneExpectedRevision()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-race-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-race-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(caller, "ACS race", CancellationToken.None);
        var first = await CreateNativeAcsStore().WriteAsync(
            caller, Request(project.ProjectId, Artifact("base"), null, "acs-base-" + suffix), CancellationToken.None);
        var left = Request(project.ProjectId, Artifact("left"), first.Revision, "acs-left-" + suffix);
        var right = Request(project.ProjectId, Artifact("right"), first.Revision, "acs-right-" + suffix);

        async Task<ProjectConfigException?> TryWriteAsync(NativeAcsPolicyWriteRequest request)
        {
            try
            {
                await CreateNativeAcsStore().WriteAsync(caller, request, CancellationToken.None);
                return null;
            }
            catch (ProjectConfigException exception)
            {
                return exception;
            }
        }

        var outcomes = await Task.WhenAll(TryWriteAsync(left), TryWriteAsync(right));
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Equal("revision_conflict", Assert.Single(outcomes, outcome => outcome is not null)!.Code);
        await using var verify = CreateDbContext();
        Assert.Equal(2, await verify.NativeAcsPolicyHeads
            .Where(item => item.ScopeId == project.ProjectId)
            .Select(item => item.CurrentRevision)
            .SingleAsync());
        Assert.Equal(2, await verify.NativeAcsPolicyRevisions.CountAsync(item => item.ScopeId == project.ProjectId));
    }

    [Fact]
    public async Task NativeAcsPolicyWriteRollsBackHeadRevisionIdempotencyAuditAndOutboxTogether()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-rollback-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-rollback-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(caller, "ACS rollback", CancellationToken.None);
        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var constraint = new NpgsqlCommand(
            """
            ALTER TABLE projects_config.native_acs_policy_outbox
            ADD CONSTRAINT ck_native_acs_policy_outbox_reject_rollback
            CHECK (payload ->> 'policyId' <> 'rollback')
            """,
            connection))
            await constraint.ExecuteNonQueryAsync();

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => CreateNativeAcsStore().WriteAsync(
                caller,
                Request(project.ProjectId, Artifact("rollback"), null, "acs-rollback-" + suffix, "rollback"),
                CancellationToken.None));
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand(
                """
                ALTER TABLE projects_config.native_acs_policy_outbox
                DROP CONSTRAINT ck_native_acs_policy_outbox_reject_rollback
                """,
                connection);
            await drop.ExecuteNonQueryAsync();
        }

        await using (var connection = await fixture.DataSource.OpenConnectionAsync())
        await using (var constraint = new NpgsqlCommand(
            """
            ALTER TABLE projects_config.native_acs_policy_change_audit
            ADD CONSTRAINT ck_native_acs_policy_change_audit_reject_rollback
            CHECK (reason <> 'rollback')
            """,
            connection))
            await constraint.ExecuteNonQueryAsync();

        try
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => CreateNativeAcsStore().WriteAsync(
                caller,
                Request(project.ProjectId, Artifact("audit rollback"), null, "acs-audit-rollback-" + suffix, "audit-rollback")
                    with { Reason = "rollback" },
                CancellationToken.None));
        }
        finally
        {
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var drop = new NpgsqlCommand(
                """
                ALTER TABLE projects_config.native_acs_policy_change_audit
                DROP CONSTRAINT ck_native_acs_policy_change_audit_reject_rollback
                """,
                connection);
            await drop.ExecuteNonQueryAsync();
        }

        await using var verify = CreateDbContext();
        foreach (var policyId in new[] { "rollback", "audit-rollback" })
        {
            Assert.False(await verify.NativeAcsPolicyHeads.AnyAsync(item =>
                item.ScopeId == project.ProjectId && item.PolicyId == policyId));
            Assert.False(await verify.NativeAcsPolicyRevisions.AnyAsync(item =>
                item.ScopeId == project.ProjectId && item.PolicyId == policyId));
            Assert.False(await verify.NativeAcsPolicyIdempotency.AnyAsync(item =>
                item.ScopeId == project.ProjectId && item.PolicyId == policyId));
            Assert.False(await verify.NativeAcsPolicyChangeAudit.AnyAsync(item =>
                item.ScopeId == project.ProjectId && item.PolicyId == policyId));
        }
        var payloads = await verify.NativeAcsPolicyOutbox.AsNoTracking()
            .Select(item => item.PayloadJson)
            .ToArrayAsync();
        Assert.DoesNotContain(payloads, payload =>
            payload.Contains("\"policyId\":\"rollback\"", StringComparison.Ordinal) ||
            payload.Contains("\"policyId\":\"audit-rollback\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeAcsPolicyWriteRejectsARevokedRoleSnapshot()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-revoked-tenant-" + suffix;
        var tenantAdmin = await SeedCallerAsync(
            db, authorityStore, "acs-revoking-admin-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(tenantAdmin, "ACS revoked", CancellationToken.None);
        var ownerSubject = "acs-revoked-owner-" + suffix;
        var membershipId = await SeedMembershipAsync(authorityStore, ownerSubject, tenantId);
        var assignment = await authorityStore.AssignRoleAsync(
            membershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            "fixture");
        var owner = await ResolveCallerAsync(db, ownerSubject, tenantId, ["api.read", "projects.admin"]);
        await authorityStore.RevokeRoleAssignmentAsync(
            assignment.AssignmentId, assignment.Revision, "fixture", CancellationToken.None);
        var replacement = await authorityStore.AssignRoleAsync(
            membershipId,
            ProjectAuthorityResourceType.Project,
            project.ProjectId,
            ProjectAuthorityRole.Owner,
            "fixture");
        Assert.NotEqual(assignment.AssignmentId, replacement.AssignmentId);

        var denied = await Assert.ThrowsAsync<ProjectConfigException>(() => CreateNativeAcsStore().WriteAsync(
            owner,
            Request(project.ProjectId, Artifact("revoked"), null, "acs-revoked-" + suffix),
            CancellationToken.None));
        Assert.Equal("forbidden", denied.Code);
    }

    [Fact]
    public async Task NativeAcsPolicyWriteWorksWithRestrictedRuntimeIdentity()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-runtime-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-runtime-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);
        var project = await new ProjectsConfigService(db, CreateProviderCatalog(), TimeProvider.System)
            .CreateProjectAsync(caller, "ACS runtime", CancellationToken.None);
        await using var runtimeDataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
            {
                Options = "-c role=agentweaver-id-projects-config",
                Pooling = false,
            }.ConnectionString);
        var runtimeOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(runtimeDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;
        var store = new NativeAcsPolicyStore(new TestDbContextFactory(runtimeOptions), TimeProvider.System);

        var receipt = await store.WriteAsync(
            caller,
            Request(project.ProjectId, Artifact("runtime identity"), null, "acs-runtime-" + suffix),
            CancellationToken.None);

        Assert.Equal(1, receipt.Revision);
    }

    [Fact]
    public async Task NativeAcsPolicyWriteReturnsNotFoundForMissingProject()
    {
        await using var db = CreateDbContext();
        var authorityStore = new ProjectsConfigPrivilegedAuthorityStore(CreateDbContextOptions(), TimeProvider.System);
        var suffix = Guid.NewGuid().ToString("N");
        var tenantId = "acs-missing-tenant-" + suffix;
        var caller = await SeedCallerAsync(
            db, authorityStore, "acs-missing-owner-" + suffix, tenantId,
            ["api.read", "projects.admin"], ProjectAuthorityResourceType.Tenant,
            tenantId, ProjectAuthorityRole.TenantAdmin);

        var denied = await Assert.ThrowsAsync<ProjectConfigException>(() => CreateNativeAcsStore().WriteAsync(
            caller,
            Request("missing-project-" + suffix[..12], Artifact("missing project"), null, "acs-missing-" + suffix),
            CancellationToken.None));

        Assert.Equal("not_found", denied.Code);
    }

    private NativeAcsPolicyStore CreateNativeAcsStore() =>
        new(new TestDbContextFactory(CreateDbContextOptions()), TimeProvider.System);

    private static NativeAcsPolicyWriteRequest Request(
        string projectId,
        NativeAcsArtifact artifact,
        long? expectedRevision,
        string idempotencyKey,
        string policyId = "baseline") =>
        new(
            NativeAcsPolicyScopeType.Project,
            projectId,
            policyId,
            expectedRevision,
            artifact,
            idempotencyKey,
            "Test policy update",
            "acs-correlation");

    private static NativeAcsArtifact Artifact(string text)
    {
        var artifact = new NativeAcsArtifact(
            ImmutableArray.CreateRange(Encoding.UTF8.GetBytes($"manifest:{text}")),
            [new NativeAcsArtifactFile(
                "policies/main.rego",
                ImmutableArray.CreateRange(Encoding.UTF8.GetBytes($"package policy\n# {text}\n")))],
            string.Empty);
        return artifact with { ContentSha256 = NativeAcsArtifactHash.Compute(artifact) };
    }

    private sealed class TestDbContextFactory(DbContextOptions<ProjectsConfigDbContext> options)
        : IDbContextFactory<ProjectsConfigDbContext>
    {
        public ProjectsConfigDbContext CreateDbContext() => new(options);

        public Task<ProjectsConfigDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
