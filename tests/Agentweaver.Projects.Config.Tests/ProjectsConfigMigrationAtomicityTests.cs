using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Projects.Config.Tests;

public sealed class ProjectsConfigMigrationAtomicityTests(ProjectsConfigPostgresFixture fixture)
    : IClassFixture<ProjectsConfigPostgresFixture>
{
    [Fact]
    public async Task SchemaMustExistForBothBaselineAndAtomicMigration()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var admin = NpgsqlDataSource.Create(container.GetConnectionString());
        await using (var connection = await admin.OpenConnectionAsync())
        await using (var setup = new NpgsqlCommand("""
            CREATE ROLE "agentweaver-id-projects-config-migration"
              LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
            CREATE ROLE "agentweaver-id-projects-config" NOLOGIN;
            """, connection))
            await setup.ExecuteNonQueryAsync();
        var connectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Options = "-c role=agentweaver-id-projects-config-migration",
        }.ConnectionString;
        await using var migration = NpgsqlDataSource.Create(connectionString);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(migration, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema)).Options;
        await using (var baseline = new ProjectsConfigDbContext(options))
        {
            var failure = await Assert.ThrowsAsync<PostgresException>(() => baseline.Database.MigrateAsync());
            Assert.Equal("42501", failure.SqlState);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(migration, options));
        await using var verify = await admin.OpenConnectionAsync();
        await using var query = new NpgsqlCommand("SELECT to_regnamespace('projects_config') IS NULL", verify);
        Assert.True((bool)(await query.ExecuteScalarAsync())!);
    }

    private sealed class ExtraPropertyCustomizer(ModelCustomizerDependencies dependencies)
        : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ProjectRecord>().Property<string>("unmigrated_probe");
        }
    }

    private DbContextOptions<ProjectsConfigDbContext> Options(bool drift = false)
    {
        var builder = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(fixture.MigrationDataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema));
        if (drift)
            builder.ReplaceService<IModelCustomizer, ExtraPropertyCustomizer>();
        return builder.Options;
    }

    private async Task<string[]> HistoryAsync()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT array_agg("MigrationId" ORDER BY "MigrationId") FROM projects_config.__ef_migrations_history
            """, connection);
        return (string[])(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task PendingModelChangesFailInBothMigrationEntries()
    {
        var before = await HistoryAsync();
        var transactional = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var connection = await fixture.MigrationDataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await ProjectsConfigMigrator.MigrateAsync(connection, transaction, Options(drift: true));
        });
        Assert.Contains("PendingModelChangesWarning", transactional.Message, StringComparison.Ordinal);
        var production = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, Options(drift: true)));
        Assert.Contains("PendingModelChangesWarning", production.Message, StringComparison.Ordinal);
        Assert.Equal(before, await HistoryAsync());
    }

    [Fact]
    public async Task RepeatProductionMigrationPreservesHistory()
    {
        var before = await HistoryAsync();
        await ProjectsConfigMigrator.MigrateAsync(fixture.MigrationDataSource, Options());
        Assert.Equal(before, await HistoryAsync());
    }

    [Fact]
    public async Task CheckExactPairAAsync_SerializesBehindConcurrentMigrationCommitAndDetectsDrift()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var admin = NpgsqlDataSource.Create(container.GetConnectionString());
        await using (var setupConn = await admin.OpenConnectionAsync())
        await using (var setupCmd = new NpgsqlCommand("""
            CREATE ROLE "agentweaver-id-projects-config-migration"
              LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS NOINHERIT;
            CREATE ROLE "agentweaver-id-projects-config" NOLOGIN;
            CREATE SCHEMA projects_config AUTHORIZATION "agentweaver-id-projects-config-migration";
            GRANT USAGE ON SCHEMA projects_config TO "agentweaver-id-projects-config";
            """, setupConn))
            await setupCmd.ExecuteNonQueryAsync();

        var migrationConnectionString = new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Options = "-c role=agentweaver-id-projects-config-migration",
        }.ConnectionString;
        await using var migration = NpgsqlDataSource.Create(migrationConnectionString);
        var options = new DbContextOptionsBuilder<ProjectsConfigDbContext>()
            .UseNpgsql(migration, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history", ProjectsConfigDbContext.Schema))
            .Options;

        await using (var context = new ProjectsConfigDbContext(options))
        {
            var migrator = context.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
            await migrator.MigrateAsync("20261127215608_AddNativeAcsProjectPolicyBaselines");
        }

        await using (var grantConn = await migration.OpenConnectionAsync())
        await using (var grantCmd = new NpgsqlCommand("""
            GRANT SELECT, INSERT ON projects_config.native_acs_project_policy_baselines TO "agentweaver-id-projects-config";
            GRANT UPDATE (state, revoked_by_issuer, revoked_by_actor_id, revoked_reason, revoked_correlation_id, revoked_at, consumed_at)
                ON projects_config.native_acs_project_policy_baselines TO "agentweaver-id-projects-config";
            """, grantConn))
            await grantCmd.ExecuteNonQueryAsync();

        var pairAResult = await ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        Assert.True(pairAResult.IsExactPairA, pairAResult.Reason);

        // Missing schema USAGE must fail exact Pair A check.
        await using (var revokeUsageConn = await migration.OpenConnectionAsync())
        await using (var revokeUsageCmd = new NpgsqlCommand("""
            REVOKE USAGE ON SCHEMA projects_config FROM "agentweaver-id-projects-config";
            """, revokeUsageConn))
            await revokeUsageCmd.ExecuteNonQueryAsync();
        var missingUsageResult = await ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        Assert.False(missingUsageResult.IsExactPairA);
        Assert.Equal("runtime_grants_mismatch_pair_a", missingUsageResult.Reason);

        await using (var restoreUsageConn = await migration.OpenConnectionAsync())
        await using (var restoreUsageCmd = new NpgsqlCommand("""
            GRANT USAGE ON SCHEMA projects_config TO "agentweaver-id-projects-config";
            """, restoreUsageConn))
            await restoreUsageCmd.ExecuteNonQueryAsync();

        // Missing a required Pair A column grant (revoked_at) must fail exact Pair A check.
        await using (var missingGrantConn = await migration.OpenConnectionAsync())
        await using (var missingGrantCmd = new NpgsqlCommand("""
            REVOKE UPDATE (revoked_at) ON projects_config.native_acs_project_policy_baselines FROM "agentweaver-id-projects-config";
            """, missingGrantConn))
            await missingGrantCmd.ExecuteNonQueryAsync();
        var missingGrantResult = await ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        Assert.False(missingGrantResult.IsExactPairA);
        Assert.Equal("runtime_grants_mismatch_pair_a", missingGrantResult.Reason);

        await using (var restoreRevokedAtConn = await migration.OpenConnectionAsync())
        await using (var restoreRevokedAtCmd = new NpgsqlCommand("""
            GRANT UPDATE (revoked_at) ON projects_config.native_acs_project_policy_baselines TO "agentweaver-id-projects-config";
            """, restoreRevokedAtConn))
            await restoreRevokedAtCmd.ExecuteNonQueryAsync();

        // Grant drift (UPDATE on consumed_revision without 20261128012056) must fail exact Pair A check.
        await using (var driftConn = await migration.OpenConnectionAsync())
        await using (var driftCmd = new NpgsqlCommand("""
            GRANT UPDATE (consumed_revision) ON projects_config.native_acs_project_policy_baselines TO "agentweaver-id-projects-config";
            """, driftConn))
            await driftCmd.ExecuteNonQueryAsync();
        var driftResult = await ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        Assert.False(driftResult.IsExactPairA);
        Assert.Equal("runtime_grants_mismatch_pair_a", driftResult.Reason);

        // Restore exact Pair A grant before testing trigger drift and concurrent migration-commit serialization.
        await using (var restoreConn = await migration.OpenConnectionAsync())
        await using (var restoreCmd = new NpgsqlCommand("""
            REVOKE UPDATE (consumed_revision) ON projects_config.native_acs_project_policy_baselines FROM "agentweaver-id-projects-config";
            """, restoreConn))
            await restoreCmd.ExecuteNonQueryAsync();

        // Trigger scoped to UPDATE OF state (bypassing consumed_at updates) must fail exact Pair A check.
        await using (var triggerDriftConn = await migration.OpenConnectionAsync())
        await using (var triggerDriftCmd = new NpgsqlCommand("""
            DROP TRIGGER native_acs_project_policy_baselines_transition_guard
                ON projects_config.native_acs_project_policy_baselines;
            CREATE TRIGGER native_acs_project_policy_baselines_transition_guard
                BEFORE UPDATE OF state ON projects_config.native_acs_project_policy_baselines
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_native_acs_project_policy_baseline_transition();
            """, triggerDriftConn))
            await triggerDriftCmd.ExecuteNonQueryAsync();
        var triggerDriftResult = await ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        Assert.False(triggerDriftResult.IsExactPairA);
        Assert.Equal("transition_trigger_mismatch_pair_a", triggerDriftResult.Reason);

        await using (var restoreTriggerConn = await migration.OpenConnectionAsync())
        await using (var restoreTriggerCmd = new NpgsqlCommand("""
            DROP TRIGGER native_acs_project_policy_baselines_transition_guard
                ON projects_config.native_acs_project_policy_baselines;
            CREATE TRIGGER native_acs_project_policy_baselines_transition_guard
                BEFORE UPDATE ON projects_config.native_acs_project_policy_baselines
                FOR EACH ROW EXECUTE FUNCTION projects_config.guard_native_acs_project_policy_baseline_transition();
            """, restoreTriggerConn))
            await restoreTriggerCmd.ExecuteNonQueryAsync();

        // Hold the migration advisory lock across 20261128012056 + grants, start CheckExactPairAAsync,
        // and verify PostgreSQL reports it waiting on the migration lock before committing.
        await using var concurrentConn = await migration.OpenConnectionAsync();
        await using var pidCmd = new NpgsqlCommand("SELECT pg_backend_pid()", concurrentConn);
        var writerPid = (int)(await pidCmd.ExecuteScalarAsync())!;
        await using var concurrentTx = await concurrentConn.BeginTransactionAsync();
        await ProjectsConfigMigrator.MigrateAsync(concurrentConn, concurrentTx, options);

        var verifierTask = ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        var waitingOnLock = false;
        await using (var observerConn = await admin.OpenConnectionAsync())
        {
            await using var waitCmd = new NpgsqlCommand("""
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_locks l
                    JOIN pg_stat_activity a ON a.pid = l.pid
                    WHERE l.locktype = 'advisory'
                      AND NOT l.granted
                      AND a.wait_event_type = 'Lock'
                      AND a.wait_event = 'advisory'
                      AND @writerPid = ANY(pg_blocking_pids(l.pid))
                )
                """, observerConn);
            waitCmd.Parameters.AddWithValue("writerPid", writerPid);
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if ((bool)(await waitCmd.ExecuteScalarAsync())!)
                {
                    waitingOnLock = true;
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25));
            }
        }
        Assert.True(waitingOnLock, "PostgreSQL did not report the Pair A verifier waiting on the migration advisory lock.");
        Assert.False(verifierTask.IsCompleted);

        await concurrentTx.CommitAsync();
        var postCommitResult = await verifierTask;
        Assert.False(postCommitResult.IsExactPairA);
        Assert.Equal("consume_guard_migration_present", postCommitResult.Reason);

        await using (var pairBCheckConn = await admin.OpenConnectionAsync())
        await using (var pairBCheckCmd = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1
                FROM projects_config.__ef_migrations_history
                WHERE "MigrationId" = '20261128012056_AddNativeAcsProjectPolicyBaselineConsumeGuard'
            ) AND has_column_privilege(
                'agentweaver-id-projects-config',
                'projects_config.native_acs_project_policy_baselines',
                'consumed_revision',
                'UPDATE')
            """, pairBCheckConn))
        {
            Assert.True((bool)(await pairBCheckCmd.ExecuteScalarAsync())!);
        }

        // Verify the session advisory lock was released so a second check completes immediately.
        var followUp = await ProjectsConfigMigrator.CheckExactPairAAsync(migration);
        Assert.False(followUp.IsExactPairA);
        Assert.Equal("consume_guard_migration_present", followUp.Reason);
    }
}
