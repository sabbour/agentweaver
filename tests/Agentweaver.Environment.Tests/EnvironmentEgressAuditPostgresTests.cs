using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentEgressAuditPostgresTests(EnvironmentPostgresFixture database)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task OwnerAndFlowsPersistAcrossLifecycleReleaseAndUnknownOwnersAreKept()
    {
        var owner = NewOwner();
        var audit = new EnvironmentEgressAuditStore(database.DataSource);
        var fingerprint = await audit.RegisterOwnerAsync(owner, CancellationToken.None);
        Assert.Equal(AgentSandboxProvider.GetOwnerFingerprint(owner), fingerprint);
        Assert.Equal(fingerprint, await audit.RegisterOwnerAsync(owner, CancellationToken.None));

        var lifecycle = database.CreateStore();
        await lifecycle.TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "audit-register"),
            CancellationToken.None);
        await lifecycle.TransitionAsync(
            new(owner, 1, EnvironmentLifecycleState.Released, "audit-release"),
            CancellationToken.None);

        var lateFlow = Flow(fingerprint, "late.example");
        var attributed = await audit.AppendFlowAsync(lateFlow, CancellationToken.None);
        Assert.True(attributed.Attributed);
        Assert.NotEmpty(attributed.FlowId);
        var retry = await audit.AppendFlowAsync(lateFlow with
        {
            FirstSeen = lateFlow.FirstSeen.ToUniversalTime(),
            LastSeen = lateFlow.LastSeen.ToUniversalTime()
        }, CancellationToken.None);
        Assert.Equal(attributed.FlowId, retry.FlowId);

        var unknown = Flow("unregistered-workload", "unknown.example");
        var unattributed = await audit.AppendFlowAsync(unknown, CancellationToken.None);
        Assert.False(unattributed.Attributed);

        await using var verify = await database.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*) FROM environment.sandbox_owner_mappings
                 WHERE owner_fingerprint = @fingerprint AND tenant_id = @tenant_id
                   AND project_id = @project_id AND run_id = @run_id AND environment_id = @environment_id),
                (SELECT count(*) FROM environment.sandbox_egress_flows_attributed
                 WHERE flow_id = @attributed_id AND owner_fingerprint = @fingerprint),
                (SELECT count(*) FROM environment.sandbox_egress_flows_unattributed
                 WHERE flow_id = @unattributed_id AND workload_ref = 'unregistered-workload'),
                (SELECT first_seen FROM environment.sandbox_egress_flows_attributed
                 WHERE flow_id = @attributed_id)
            """,
            verify);
        command.Parameters.AddWithValue("fingerprint", fingerprint);
        command.Parameters.AddWithValue("tenant_id", owner.TenantId);
        command.Parameters.AddWithValue("project_id", owner.ProjectId);
        command.Parameters.AddWithValue("run_id", owner.RunId);
        command.Parameters.AddWithValue("environment_id", owner.EnvironmentId);
        command.Parameters.AddWithValue("attributed_id", attributed.FlowId);
        command.Parameters.AddWithValue("unattributed_id", unattributed.FlowId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(TimeSpan.Zero, reader.GetFieldValue<DateTimeOffset>(3).Offset);
    }

    [Fact]
    public async Task OwnerFingerprintConflictFailsInsteadOfChangingTheMapping()
    {
        var owner = NewOwner();
        var fingerprint = AgentSandboxProvider.GetOwnerFingerprint(owner);
        await using (var insert = database.DataSource.CreateCommand(
            """
            INSERT INTO environment.sandbox_owner_mappings
                (owner_fingerprint, tenant_id, project_id, run_id, environment_id)
            VALUES
                (@fingerprint, 'other-tenant', 'other-project', 'other-run', 'other-environment')
            """))
        {
            insert.Parameters.AddWithValue("fingerprint", fingerprint);
            await insert.ExecuteNonQueryAsync();
        }

        var conflict = await Assert.ThrowsAsync<EnvironmentEgressAuditConflictException>(() =>
            new EnvironmentEgressAuditStore(database.DataSource)
                .RegisterOwnerAsync(owner, CancellationToken.None));

        Assert.Contains("already bound", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlowReplayAfterOwnerRegistrationDoesNotDuplicateAcrossAuditTables()
    {
        var owner = NewOwner();
        var fingerprint = AgentSandboxProvider.GetOwnerFingerprint(owner);
        var audit = new EnvironmentEgressAuditStore(database.DataSource);
        var flow = Flow(fingerprint, "late-owner.example");

        var first = await audit.AppendFlowAsync(flow, CancellationToken.None);
        Assert.False(first.Attributed);
        await audit.RegisterOwnerAsync(owner, CancellationToken.None);

        var retry = await audit.AppendFlowAsync(flow, CancellationToken.None);
        Assert.Equal(first.FlowId, retry.FlowId);
        Assert.False(retry.Attributed);

        await using var command = database.DataSource.CreateCommand(
            """
            SELECT
                (SELECT count(*) FROM environment.sandbox_egress_flows_attributed
                 WHERE flow_id = @flow_id),
                (SELECT count(*) FROM environment.sandbox_egress_flows_unattributed
                 WHERE flow_id = @flow_id)
            """);
        command.Parameters.AddWithValue("flow_id", first.FlowId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
    }

    [Fact]
    public async Task AuditMigrationRollsBackAndReappliesOnPostgres()
    {
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(database.DataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history",
                EnvironmentDbContext.Schema))
            .Options;

        try
        {
            await using (var context = new EnvironmentDbContext(options))
                await context.GetService<IMigrator>()
                    .MigrateAsync("20261117145425_PlatformEgressApplyReservations");
            Assert.False(await AuditTablesExistAsync(database.DataSource));

            await using (var context = new EnvironmentDbContext(options))
                await context.GetService<IMigrator>().MigrateAsync();
            Assert.True(await AuditTablesExistAsync(database.DataSource));
        }
        finally
        {
            await using var context = new EnvironmentDbContext(options);
            await context.GetService<IMigrator>().MigrateAsync();
        }
    }

    private static async Task<bool> AuditTablesExistAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT to_regclass('environment.sandbox_owner_mappings') IS NOT NULL
               AND to_regclass('environment.sandbox_egress_flows_attributed') IS NOT NULL
               AND to_regclass('environment.sandbox_egress_flows_unattributed') IS NOT NULL
            """);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static EnvironmentOwnerIdentity NewOwner()
    {
        var id = Guid.NewGuid().ToString("N");
        return new("tenant-" + id, "project-" + id, "run-" + id, "environment-" + id);
    }

    private static EgressFlowObservation Flow(string? workloadRef, string host)
    {
        var time = DateTimeOffset.UtcNow.AddMinutes(-5).ToOffset(TimeSpan.FromHours(2));
        return new(host, null, 443, EgressFlowVerdict.Allowed, time, time, 1, workloadRef);
    }
}
