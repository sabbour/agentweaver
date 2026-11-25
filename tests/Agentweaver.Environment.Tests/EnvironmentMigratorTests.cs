using Agentweaver.Environment;
using Agentweaver.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentMigratorTests
{
    [Fact]
    public void MigrationCatalogMatchesTheReviewedOrderedSchemaVersions()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql("Host=localhost;Database=design_time;Username=design_time")
            .Options;

        Assert.Equal(Enumerable.Range(1, 18), EnvironmentMigrator.ValidateMigrationCatalog(options));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("superseded")]
    public async Task UpgradeRejectsHistoricalInvalidHoldsWithoutChangingEvidence(string binding)
    {
        var fixture = new EnvironmentPostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var context = Context(fixture.DataSource);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261119034552_PlatformPolicyProofReceipts");
            var reservations = new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System);
            var target = "migration-" + Guid.NewGuid().ToString("N");
            var first = await reservations.ReserveAsync(Request(target, "first", 1), CancellationToken.None);
            var reservation = binding == "missing" ? Guid.NewGuid() : first.ReservationId;
            if (binding == "foreign")
                target += "-other";
            if (binding == "superseded")
                await reservations.ReserveAsync(Request(target, "newer", 2), CancellationToken.None);
            await InsertHistoricalHoldAsync(fixture.DataSource, target, reservation);

            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.MigrateAsync("20261119050000_PlatformPolicyHoldReservationFence"));
            Assert.Equal("PF002", error.SqlState);
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM environment.platform_policy_proof_holds"));
            Assert.Equal(0L, await ScalarAsync(fixture.DataSource,
                """SELECT count(*) FROM environment."__ef_migrations_history" WHERE "MigrationId"='20261119050000_PlatformPolicyHoldReservationFence'"""));
            Assert.Equal(0L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname='platform_policy_proof_holds_reservation_fence'"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task PreviousSchemaCanCountReceiptAfterReservationIsSuperseded()
    {
        var fixture = new EnvironmentPostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var context = Context(fixture.DataSource);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261119034552_PlatformPolicyProofReceipts");
            var target = "historical-" + Guid.NewGuid().ToString("N");
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(Request(target, "first", 1), CancellationToken.None);
            var holdId = await InsertHistoricalHoldAsync(fixture.DataSource, target, reservation.ReservationId);
            var hash = new string('a', 64);
            var proof = new PlatformPolicyProofStore(fixture.DataSource, TimeProvider.System);
            await proof.PersistReceiptAsync(
                new(holdId, holdId, target, reservation.ReservationId, 1, 1, "source",
                    hash, hash, "compiler", "evaluator", "ceiling", hash, hash, hash, hash,
                    "rv", "uid", 1, """{"schemaVersion":1}""", DateTimeOffset.UtcNow),
                CancellationToken.None);
            await using (var supersede = fixture.DataSource.CreateCommand(
                """
                INSERT INTO environment.platform_policy_apply_reservations
                    (reservation_id,target_id,idempotency_key,request_fingerprint,source_epoch,generation,
                     source_id,source_revision,source_hash,intent_hash,object_uid,resource_version,
                     provider_request_json,created_at)
                SELECT @new_id,target_id,'newer',request_fingerprint,source_epoch+1,generation+1,
                       source_id,source_revision,source_hash,intent_hash,object_uid,resource_version,
                       provider_request_json,created_at
                FROM environment.platform_policy_apply_reservations WHERE reservation_id=@id
                """))
            {
                supersede.Parameters.AddWithValue("new_id", Guid.NewGuid());
                supersede.Parameters.AddWithValue("id", reservation.ReservationId);
                await supersede.ExecuteNonQueryAsync();
            }
            await proof.RequireAllTargetsAsync(1, "preparation", [target], CancellationToken.None);
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                migrator.MigrateAsync("20261119050000_PlatformPolicyHoldReservationFence"));
            Assert.Equal("PF002", error.SqlState);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task DowngradeWithHoldKeepsTablesGuardsAndEvidence()
    {
        var fixture = new EnvironmentPostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var context = Context(fixture.DataSource);
            var target = "downgrade-" + Guid.NewGuid().ToString("N");
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(Request(target, "first", 1), CancellationToken.None);
            await InsertHistoricalHoldAsync(fixture.DataSource, target, reservation.ReservationId);
            var error = await Assert.ThrowsAsync<PostgresException>(() =>
                context.GetService<IMigrator>().MigrateAsync("20261117221544_EnvironmentSandboxEgressAudit"));
            Assert.Equal("PF003", error.SqlState);
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM environment.platform_policy_proof_holds"));
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname='platform_policy_proof_holds_reservation_fence'"));
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                """SELECT count(*) FROM environment."__ef_migrations_history" WHERE "MigrationId"='20261119050000_PlatformPolicyHoldReservationFence'"""));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task FenceMigrationCanDowngradeAndUpgradeWithoutHolds()
    {
        var fixture = new EnvironmentPostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var context = Context(fixture.DataSource);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261119034552_PlatformPolicyProofReceipts");
            Assert.Equal(0L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname IN ('platform_policy_apply_reservations_active_hold_fence', 'platform_policy_proof_holds_reservation_fence')"));
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname='platform_policy_apply_reservations_consistent_identity'"));
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(Request("migration-" + Guid.NewGuid().ToString("N"), "first", 1), CancellationToken.None);
            await migrator.MigrateAsync("20261119050000_PlatformPolicyHoldReservationFence");
            Assert.Equal(2L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname IN ('platform_policy_apply_reservations_active_hold_fence', 'platform_policy_proof_holds_reservation_fence') AND tgenabled='O'"));
            var latest = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .FindLatestAsync(reservation.TargetId, CancellationToken.None);
            Assert.Equal(reservation.ReservationId, latest?.ReservationId);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpgradeLockTimeoutLeavesSchemaAndEvidenceUnchanged()
    {
        var fixture = new EnvironmentPostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            await using var context = Context(fixture.DataSource);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20261119034552_PlatformPolicyProofReceipts");
            var target = "timeout-" + Guid.NewGuid().ToString("N");
            var reservation = await new PlatformEgressApplyReservationStore(fixture.DataSource, TimeProvider.System)
                .ReserveAsync(Request(target, "first", 1), CancellationToken.None);
            await InsertHistoricalHoldAsync(fixture.DataSource, target, reservation.ReservationId);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var readLock = new NpgsqlCommand(
                "LOCK TABLE environment.platform_policy_apply_reservations IN ACCESS SHARE MODE",
                connection, transaction))
                await readLock.ExecuteNonQueryAsync();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                migrator.MigrateAsync("20261119050000_PlatformPolicyHoldReservationFence"));
            Assert.Equal("55P03", Assert.IsType<PostgresException>(error.InnerException).SqlState);
            await transaction.RollbackAsync();
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM environment.platform_policy_proof_holds"));
            Assert.Equal(0L, await ScalarAsync(fixture.DataSource,
                """SELECT count(*) FROM environment."__ef_migrations_history" WHERE "MigrationId"='20261119050000_PlatformPolicyHoldReservationFence'"""));
            await migrator.MigrateAsync("20261119050000_PlatformPolicyHoldReservationFence");
            Assert.Equal(1L, await ScalarAsync(fixture.DataSource,
                "SELECT count(*) FROM pg_catalog.pg_trigger WHERE tgname='platform_policy_proof_holds_reservation_fence'"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [Fact]
    public async Task StartupRejectsDisabledFenceTrigger()
    {
        var fixture = new EnvironmentPostgresFixture();
        await fixture.InitializeAsync();
        try
        {
            var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
                .UseNpgsql(fixture.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                    "__ef_migrations_history", EnvironmentDbContext.Schema)).Options;
            await EnvironmentMigrator.VerifyMigrationsAppliedAsync(fixture.DataSource, options);
            await using (var disable = fixture.DataSource.CreateCommand(
                """
                ALTER TABLE environment.platform_policy_apply_reservations
                    DISABLE TRIGGER platform_policy_apply_reservations_active_hold_fence
                """))
                await disable.ExecuteNonQueryAsync();
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EnvironmentMigrator.VerifyMigrationsAppliedAsync(fixture.DataSource, options));
            Assert.Contains("policy fence triggers", error.Message);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static EnvironmentDbContext Context(NpgsqlDataSource dataSource) =>
        new(new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(dataSource, options => options.MigrationsHistoryTable(
                "__ef_migrations_history", EnvironmentDbContext.Schema)).Options);

    private static PlatformEgressApplyReservationRequest Request(string target, string key, long generation)
    {
        var policy = PlatformEgressPolicy.Create(generation, PlatformEgressMode.Allowlist,
            [new PlatformEgressDestination("api.example.com", 443)], new string('a', 64));
        return new(target, key, generation, generation, "source", "uid", "rv", policy);
    }

    private static async Task<Guid> InsertHistoricalHoldAsync(NpgsqlDataSource dataSource, string target, Guid reservation)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO environment.platform_policy_proof_holds
                (hold_id,target_id,physical_target,namespace,namespace_uid,seal_epoch,guard_digest,
                 profile_digest,registry_revision,preparation_id,reservation_id,request_fingerprint,created_at)
            VALUES (@id,@target,'cluster','agentweaver',@uid,1,@hash,@hash,1,'preparation',@reservation,@hash,now())
            """);
        var id = Guid.NewGuid();
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("target", target);
        command.Parameters.AddWithValue("uid", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("hash", new string('a', 64));
        command.Parameters.AddWithValue("reservation", reservation);
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static async Task<long> ScalarAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var command = dataSource.CreateCommand(sql);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
