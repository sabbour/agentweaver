using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentProviderLifecycleReportPostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task ProviderReportIsDurableAndIdenticalRetryKeepsTheSamePendingOperation()
    {
        var setup = await CreateActiveLeaseAsync();
        var reportedAt = new DateTimeOffset(2020, 3, 1, 2, 15, 0, TimeSpan.FromMinutes(330));
        var request = Report(
            setup.Fence,
            setup.Resource,
            setup.Lease.ProviderFencingGeneration,
            reportedAt: reportedAt);
        var store = new EnvironmentProviderLifecycleReportStore(fixture.DataSource, TimeProvider.System);

        var first = await store.ReserveAsync(request, "agent-sandbox", CancellationToken.None);
        var restarted = new EnvironmentProviderLifecycleReportStore(fixture.DataSource, TimeProvider.System);
        var replay = await restarted.ReserveAsync(request, "agent-sandbox", CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ProviderEventId, replay.ProviderEventId);
        Assert.Equal(first.SandboxOperationId, replay.SandboxOperationId);
        Assert.Equal(setup.Lease.OperationId, replay.SandboxOperationId);
        Assert.Equal(first.LeaseRevision, replay.LeaseRevision);
        Assert.Equal(first.CoreOperationKey, replay.CoreOperationKey);
        Assert.Equal(first.RequestFingerprint, replay.RequestFingerprint);
        Assert.Equal(reportedAt, first.ReportedAt);
        Assert.Equal(TimeSpan.Zero, first.ReportedAt.Offset);
        Assert.NotEqual(first.CreatedAt, first.ReportedAt);
        Assert.Equal(first.ReportedAt, replay.ReportedAt);
        Assert.Equal(EnvironmentProviderLifecycleReportState.Pending, replay.State);
        Assert.Null(replay.CoreExecutionFence);
        Assert.Null(replay.LastErrorCode);
    }

    [Fact]
    public async Task OneSandboxLeaseCanHaveMultipleDistinctProviderReports()
    {
        var setup = await CreateActiveLeaseAsync();
        var store = new EnvironmentProviderLifecycleReportStore(fixture.DataSource, TimeProvider.System);
        var first = await store.ReserveAsync(
            Report(setup.Fence, setup.Resource, setup.Lease.ProviderFencingGeneration),
            "agent-sandbox",
            CancellationToken.None);
        var second = await store.ReserveAsync(
            Report(
                setup.Fence,
                setup.Resource,
                setup.Lease.ProviderFencingGeneration,
                providerEventId: Guid.NewGuid(),
                kind: EnvironmentProviderLifecycleReportKind.Relocation),
            "agent-sandbox",
            CancellationToken.None);

        Assert.Equal(first.SandboxOperationId, second.SandboxOperationId);
        Assert.NotEqual(first.ProviderEventId, second.ProviderEventId);
        Assert.NotEqual(first.CoreOperationKey, second.CoreOperationKey);
        Assert.Equal(EnvironmentProviderLifecycleReportState.Pending, first.State);
        Assert.Equal(EnvironmentProviderLifecycleReportState.Pending, second.State);
    }

    [Fact]
    public async Task ConcurrentIdenticalReportsReserveOneDurableOperation()
    {
        var setup = await CreateActiveLeaseAsync();
        var request = Report(setup.Fence, setup.Resource, setup.Lease.ProviderFencingGeneration);
        var store = new EnvironmentProviderLifecycleReportStore(fixture.DataSource, TimeProvider.System);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            store.ReserveAsync(request, "agent-sandbox", CancellationToken.None)));

        Assert.Single(results, result => !result.Replayed);
        Assert.Equal(7, results.Count(result => result.Replayed));
        Assert.Single(results.Select(result => result.CoreOperationKey).Distinct());
        Assert.All(results, result => Assert.Equal(EnvironmentProviderLifecycleReportState.Pending, result.State));
        Assert.All(results, result => Assert.Null(result.CoreExecutionFence));
    }

    [Fact]
    public async Task ConflictingDuplicateAndMismatchedProviderLeaseReportsFailClosed()
    {
        var setup = await CreateActiveLeaseAsync();
        var store = new EnvironmentProviderLifecycleReportStore(fixture.DataSource, TimeProvider.System);
        var accepted = Report(setup.Fence, setup.Resource, setup.Lease.ProviderFencingGeneration);
        var reservation = await store.ReserveAsync(accepted, "agent-sandbox", CancellationToken.None);

        var changedEvent = new EnvironmentProviderLifecycleReportRequest(
            accepted.ContractVersion,
            accepted.ProviderEventId,
            EnvironmentProviderLifecycleReportKind.Relocation,
            accepted.Fence,
            accepted.AdapterVersion,
            accepted.Resource,
            accepted.ProviderFencingGeneration,
            accepted.ReportedAt);
        var conflict = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveAsync(changedEvent, "agent-sandbox", CancellationToken.None));
        Assert.Equal("environment_provider_event_conflict", conflict.Code);

        var wrongProvider = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveAsync(accepted, "foreign-provider", CancellationToken.None));
        Assert.Equal("environment_provider_identity_mismatch", wrongProvider.Code);

        var wrongResource = new EnvironmentProviderLifecycleReportRequest(
            accepted.ContractVersion,
            Guid.NewGuid(),
            accepted.Kind,
            accepted.Fence,
            accepted.AdapterVersion,
            accepted.Resource with { ResourceId = "foreign-resource" },
            accepted.ProviderFencingGeneration,
            accepted.ReportedAt);
        var resourceMismatch = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveAsync(wrongResource, "agent-sandbox", CancellationToken.None));
        Assert.Equal("sandbox_provider_binding_mismatch", resourceMismatch.Code);

        var staleFence = new EnvironmentProviderLifecycleReportRequest(
            accepted.ContractVersion,
            Guid.NewGuid(),
            accepted.Kind,
            new EnvironmentGenerationFence(
                accepted.Fence.Owner,
                accepted.Fence.LifecycleGeneration + 1),
            accepted.AdapterVersion,
            accepted.Resource,
            accepted.ProviderFencingGeneration,
            accepted.ReportedAt);
        var stale = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveAsync(staleFence, "agent-sandbox", CancellationToken.None));
        Assert.Equal("environment_fence_stale", stale.Code);

        var wrongProviderFence = new EnvironmentProviderLifecycleReportRequest(
            accepted.ContractVersion,
            Guid.NewGuid(),
            accepted.Kind,
            accepted.Fence,
            accepted.AdapterVersion,
            accepted.Resource,
            accepted.ProviderFencingGeneration + 1,
            accepted.ReportedAt);
        var fenceMismatch = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveAsync(wrongProviderFence, "agent-sandbox", CancellationToken.None));
        Assert.Equal("sandbox_fence_stale", fenceMismatch.Code);

        var retry = await store.ReserveAsync(accepted, "agent-sandbox", CancellationToken.None);
        Assert.True(retry.Replayed);
        Assert.Equal(reservation.CoreOperationKey, retry.CoreOperationKey);
    }

    [Fact]
    public async Task UnsupportedReportVersionStaysPendingAndDoesNotInventCoreFence()
    {
        var setup = await CreateActiveLeaseAsync();
        var original = Report(setup.Fence, setup.Resource, setup.Lease.ProviderFencingGeneration);
        var request = new EnvironmentProviderLifecycleReportRequest(
            2,
            original.ProviderEventId,
            original.Kind,
            original.Fence,
            original.AdapterVersion,
            original.Resource,
            original.ProviderFencingGeneration,
            original.ReportedAt);
        var store = new EnvironmentProviderLifecycleReportStore(fixture.DataSource, TimeProvider.System);

        var result = await store.ReserveAsync(request, "agent-sandbox", CancellationToken.None);

        Assert.Equal(EnvironmentProviderLifecycleReportState.Pending, result.State);
        Assert.Equal("environment_provider_report_version_unsupported", result.LastErrorCode);
        Assert.Null(result.CoreExecutionFence);
    }

    [Fact]
    public async Task ProviderLifecycleReportMigrationCanBeRolledBackAndReapplied()
    {
        await using var container = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await container.StartAsync();
        await using var dataSource = NpgsqlDataSource.Create(container.GetConnectionString());
        var options = new DbContextOptionsBuilder<EnvironmentDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(
                "__ef_migrations_history",
                EnvironmentDbContext.Schema))
            .Options;

        await EnvironmentMigrator.MigrateAsync(dataSource, options);
        await using (var context = new EnvironmentDbContext(options))
            await context.GetService<IMigrator>().MigrateAsync("20261006170000_SandboxLeases");
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            "SELECT to_regclass('environment.provider_lifecycle_reports') IS NULL",
            connection))
            Assert.True((bool)(await command.ExecuteScalarAsync())!);

        await using (var context = new EnvironmentDbContext(options))
            await context.GetService<IMigrator>()
                .MigrateAsync("20261009060000_ProviderLifecycleReports");
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            SELECT to_regclass('environment.provider_lifecycle_reports') IS NOT NULL
               AND to_regclass('environment.sandbox_leases') IS NOT NULL
            """,
            connection))
            Assert.True((bool)(await command.ExecuteScalarAsync())!);
    }

    private async Task<ActiveLease> CreateActiveLeaseAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var owner = new EnvironmentOwnerIdentity(
            "tenant-" + suffix,
            "project-" + suffix,
            "run-" + suffix,
            "environment-" + suffix);
        var lifecycle = await fixture.CreateStore().TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner,
                0,
                EnvironmentLifecycleState.Active,
                "register-" + suffix),
            CancellationToken.None);
        var leaseStore = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await leaseStore.ReserveProvisionAsync(
            lifecycle.Snapshot.Fence,
            "provision-" + suffix,
            Intent(),
            CancellationToken.None);
        var resource = Provisioned(reservation.Lease.ResourceGeneration);
        var lease = await leaseStore.CompleteProvisionAsync(
            reservation.Lease.OperationId,
            lifecycle.Snapshot.Fence,
            resource,
            effectMayHaveApplied: true,
            CancellationToken.None);
        return new(owner, lifecycle.Snapshot.Fence, lease, resource);
    }

    private static EnvironmentProviderLifecycleReportRequest Report(
        EnvironmentGenerationFence fence,
        SandboxProvisionedResource resource,
        long providerFencingGeneration,
        Guid? providerEventId = null,
        EnvironmentProviderLifecycleReportKind kind = EnvironmentProviderLifecycleReportKind.Suspend,
        DateTimeOffset? reportedAt = null) =>
        new(
            1,
            providerEventId ?? Guid.NewGuid(),
            kind,
            fence,
            resource.ProviderBinding.AdapterVersion,
            resource.Resource,
            providerFencingGeneration,
            reportedAt ?? DateTimeOffset.UtcNow);

    private static SandboxLeaseProvisionIntent Intent() =>
        new(
            "agent-sandbox",
            "1.0.0",
            1,
            "options-1",
            Json("{\"image\":\"agenthost:1\",\"namespace\":\"sandbox-system\"}"),
            Json("{\"projectRevision\":2,\"runRevision\":3}"),
            Json("{\"workspaceVolumeId\":\"workspace-1\"}"));

    private static SandboxProvisionedResource Provisioned(long generation)
    {
        var resource = new ProviderResourceRef(
            ProviderSeam.Sandbox,
            "agent-sandbox",
            "claim-uid-" + Guid.NewGuid().ToString("N"),
            generation);
        var binding = new SandboxProviderBindingSnapshot(
            "agent-sandbox",
            "1.0.0",
            1,
            "options-1",
            Json("{\"image\":\"agenthost:1\",\"namespace\":\"sandbox-system\"}"),
            Json("{\"namespace\":\"sandbox-system\",\"claimName\":\"claim-1\"}"));
        return new SandboxProvisionedResource(
            resource,
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("cluster-1"),
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                SandboxCapabilities.VmIsolation,
                SandboxCapabilities.WorkspacePersistentVolumeClaim),
            [new SandboxStartupPhaseObservation(
                SandboxStartupPhase.Started,
                1,
                new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero))],
            binding);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed record ActiveLease(
        EnvironmentOwnerIdentity Owner,
        EnvironmentGenerationFence Fence,
        SandboxLeaseSnapshot Lease,
        SandboxProvisionedResource Resource);
}
