using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed partial class ProjectsConfigPostgresTests
{
    [Fact]
    public async Task InstallationIdentitySurvivesRestoreButDoesNotReconstructLostReceipts()
    {
        var isolated = new ProjectsConfigPostgresFixture();
        await isolated.InitializeAsync();
        try
        {
            var test = new ProjectsConfigPostgresTests(isolated);
            ProjectAuthorizationContext caller;
            ProjectAuthorizationContext otherTenant;
            var operation = Guid.NewGuid();
            var providerId = "byok-" + operation.ToString("N");
            var reference = "Restore-" + operation.ToString("N");
            var request = DraftRequest(operation, 0) with
            {
                Reference = reference,
                TargetInstallationId = isolated.InstallationId,
            };
            await using (var seed = test.CreateDbContext())
            {
                caller = await test.CreateReferenceAdminAsync(seed);
                var authority = new ProjectsConfigPrivilegedAuthorityStore(test.CreateDbContextOptions(), TimeProvider.System);
                var otherTenantId = "restore-other-" + operation.ToString("N");
                otherTenant = await SeedCallerAsync(seed, authority, caller.ActorId, otherTenantId,
                    ["api.read", "projects.admin"], ProjectAuthorityResourceType.Platform,
                    ProjectAuthorizationOwner.PlatformResourceId, ProjectAuthorityRole.PlatformAdmin);
            }

            var suffix = Guid.NewGuid().ToString("N");
            var before = "before_" + suffix;
            var after = "after_" + suffix;
            var restored = "restored_" + suffix;
            var fork = "fork_" + suffix;
            async Task CopyAsync(string source, string destination)
            {
                isolated.DataSource.Clear();
                isolated.MigrationDataSource.Clear();
                var adminConnection = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
                {
                    Database = "postgres",
                    Pooling = false,
                }.ConnectionString;
                await using var connection = new NpgsqlConnection(adminConnection);
                await connection.OpenAsync();
                await using var copy = new NpgsqlCommand(
                    $"CREATE DATABASE \"{destination}\" TEMPLATE \"{source}\"", connection);
                await copy.ExecuteNonQueryAsync();
            }
            await CopyAsync("agentweaver", before);
            await using (var db = test.CreateDbContext())
            {
                var service = test.ReferenceService(db);
                var saved = await service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                    providerId, request, CancellationToken.None);
                Assert.Equal(reference, saved.Reference);
                var otherOperation = Guid.NewGuid();
                await service.SaveAsync(otherTenant, ModelProviderBindingScope.Platform, null,
                    "byok-" + otherOperation.ToString("N"),
                    DraftRequest(otherOperation, 0) with
                    {
                        Reference = "OtherTenant-" + otherOperation.ToString("N"),
                        TargetInstallationId = isolated.InstallationId,
                    }, CancellationToken.None);
            }
            var migrationOptions = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                .UseNpgsql(isolated.MigrationDataSource, config => config.MigrationsHistoryTable(
                    "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            await ProjectsConfigMigrator.MigrateAsync(isolated.MigrationDataSource, migrationOptions);
            await CopyAsync("agentweaver", after);
            await CopyAsync(after, restored);
            await CopyAsync(after, fork);

            NpgsqlDataSource OpenDatabase(string name, bool runtime)
            {
                var builder = new NpgsqlConnectionStringBuilder(isolated.ConnectionString)
                {
                    Database = name,
                    Pooling = false,
                    Options = runtime ? "-c role=agentweaver-id-projects-config" :
                        "-c role=agentweaver-id-projects-config-migration",
                };
                return NpgsqlDataSource.Create(builder.ConnectionString);
            }
            DbContextOptions<ProjectsConfigDbContext> Options(NpgsqlDataSource source) =>
                new DbContextOptionsBuilder<ProjectsConfigDbContext>()
                    .UseNpgsql(source, config => config.MigrationsHistoryTable(
                        "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
            async Task<Guid> ReadIdAsync(NpgsqlDataSource source)
            {
                await using var connection = await source.OpenConnectionAsync();
                await using var read = new NpgsqlCommand(
                    "SELECT installation_id FROM projects_config.installation_identity WHERE singleton", connection);
                return (Guid)(await read.ExecuteScalarAsync())!;
            }

            await using var restoreRuntime = OpenDatabase(restored, true);
            await using var secondReplica = OpenDatabase(restored, true);
            Assert.Equal(Guid.Parse(isolated.InstallationId), await ReadIdAsync(restoreRuntime));
            Assert.Equal(await ReadIdAsync(restoreRuntime), await ReadIdAsync(secondReplica));
            await using (var db = new ProjectsConfigDbContext(Options(restoreRuntime)))
            {
                var replay = await test.ReferenceService(db, enabled: false).SaveAsync(
                    caller, ModelProviderBindingScope.Platform, null, providerId, request, CancellationToken.None);
                Assert.Equal(reference, replay.Reference);
                Assert.Equal(1, replay.Binding.ProviderRevision);
                Assert.Equal(Guid.Parse(isolated.InstallationId), (await db.ModelProviderReferenceReservations.AsNoTracking()
                    .SingleAsync(row => row.OperationId == operation)).InstallationId);
            }
            await using var pointInTime = OpenDatabase(before, true);
            Assert.Equal(Guid.Parse(isolated.InstallationId), await ReadIdAsync(pointInTime));
            await using (var db = new ProjectsConfigDbContext(Options(pointInTime)))
            {
                Assert.False(await db.ModelProviderDrafts.AnyAsync(row => row.ProviderId == providerId));
                Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == operation));
                Assert.False(await db.ModelProviderReferenceReservations.AnyAsync(row => row.OperationId == operation));
            }

            await using var forkOwner = OpenDatabase(fork, false);
            var newId = Guid.NewGuid();
            await using (var connection = await forkOwner.OpenConnectionAsync())
            await using (var transaction = await connection.BeginTransactionAsync())
            await using (var rekey = new NpgsqlCommand("""
                SELECT pg_catalog.pg_advisory_xact_lock(
                    pg_catalog.hashtextextended('agentweaver.projects_config.model_provider_create', 0));
                UPDATE projects_config.installation_identity SET installation_id = @id WHERE singleton;
                """, connection, transaction))
            {
                rekey.Parameters.AddWithValue("id", newId);
                await rekey.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            }
            Assert.NotEqual(Guid.Parse(isolated.InstallationId), newId);
            await using var forkRuntime = OpenDatabase(fork, true);
            Assert.Equal(newId, await ReadIdAsync(forkRuntime));
            await using (var db = new ProjectsConfigDbContext(Options(forkRuntime)))
            {
                var service = test.ReferenceService(db);
                var mismatch = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                        providerId, request, CancellationToken.None));
                Assert.Equal("target_installation_mismatch", mismatch.Code);
                var foreign = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    service.SaveAsync(caller, ModelProviderBindingScope.Platform, null, providerId,
                        request with { TargetInstallationId = newId.ToString("D") }, CancellationToken.None));
                Assert.Equal("reference_reconciliation_required", foreign.Code);
                var otherTenantReplay = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    service.SaveAsync(otherTenant, ModelProviderBindingScope.Platform, null, providerId,
                        request with { TargetInstallationId = newId.ToString("D") }, CancellationToken.None));
                Assert.Equal("model_provider_draft_conflict", otherTenantReplay.Code);
                var nextOperation = Guid.NewGuid();
                var conflict = await Assert.ThrowsAsync<ModelProviderDraftException>(() =>
                    service.SaveAsync(caller, ModelProviderBindingScope.Platform, null,
                        "byok-" + nextOperation.ToString("N"),
                        request with { OperationId = nextOperation, TargetInstallationId = newId.ToString("D") },
                        CancellationToken.None));
                Assert.Equal("reference_conflict", conflict.Code);
                Assert.Equal(Guid.Parse(isolated.InstallationId), (await db.ModelProviderReferenceReservations.AsNoTracking()
                    .SingleAsync(row => row.OperationId == operation)).InstallationId);
                Assert.False(await db.ModelProviderDraftRevisions.AnyAsync(row => row.OperationId == nextOperation));
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
}
