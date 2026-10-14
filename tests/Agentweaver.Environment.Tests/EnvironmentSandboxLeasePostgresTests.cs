using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxLeasePostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task LeaseRevisionAdvancesWithCasAndExpiryAloneDoesNotRetireLease()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, time);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, time);

        var reservation = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        Assert.Equal(1, reservation.Lease.LeaseRevision);
        Assert.Equal(time.GetUtcNow().AddSeconds(60), reservation.Lease.LeaseExpiresAt);

        time.Advance(TimeSpan.FromMinutes(2));
        var expired = await store.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(expired);
        Assert.Equal(SandboxLeaseState.Provisioning, expired.State);
        Assert.True(expired.IsCurrent);
        Assert.Equal(1, expired.LeaseRevision);
        Assert.True(expired.LeaseExpiresAt < time.GetUtcNow());
        var capacity = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveProvisionAsync(fence, "provision-2", Intent(), CancellationToken.None));
        Assert.Equal("sandbox_lease_capacity_exceeded", capacity.Code);

        var saved = await store.SaveProviderRequestAsync(
            reservation.Lease.OperationId,
            fence,
            Json("{\"requestVersion\":1}"),
            CancellationToken.None);
        Assert.Equal(2, saved.LeaseRevision);
        Assert.Equal(time.GetUtcNow().AddSeconds(60), saved.LeaseExpiresAt);
        var replay = await store.SaveProviderRequestAsync(
            reservation.Lease.OperationId,
            fence,
            Json("{\"requestVersion\":1}"),
            CancellationToken.None);
        Assert.Equal(saved.LeaseRevision, replay.LeaseRevision);
        Assert.Equal(saved.LeaseExpiresAt, replay.LeaseExpiresAt);

        var resource = Provisioned(reservation.Lease.ResourceGeneration);
        var active = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            reservation.Lease.OperationId,
            fence,
            resource,
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(3, active.LeaseRevision);
        Assert.Equal(time.GetUtcNow().AddSeconds(60), active.LeaseExpiresAt);

        var retiring = await store.BeginRetirementAsync(
            fence,
            active.ResourceGeneration,
            active.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 1),
            terminalEvidence: null,
            CancellationToken.None);
        Assert.Equal(4, retiring.LeaseRevision);
        Assert.Equal(time.GetUtcNow().AddSeconds(60), retiring.LeaseExpiresAt);

        var released = await SandboxLeaseProviderAttemptTestSupport.CompleteReleaseAsync(
            store,
            retiring,
            new SandboxReleaseReceipt(
                resource.Resource,
                "abandon-1",
                SandboxReleaseDisposition.Released),
            CancellationToken.None);
        Assert.Equal(5, released.LeaseRevision);
        Assert.Null(released.LeaseExpiresAt);
    }

    [Fact]
    public async Task LeaseCapacityIsOwnerCasBoundAndAbandonRetiresPlacementWithoutReleasingEnvironment()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        var replay = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);

        Assert.False(reservation.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(reservation.Lease.OperationId, replay.Lease.OperationId);
        var capacity = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveProvisionAsync(fence, "provision-2", Intent(), CancellationToken.None));
        Assert.Equal("sandbox_lease_capacity_exceeded", capacity.Code);

        var provisioned = Provisioned(reservation.Lease.ResourceGeneration);
        var active = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            reservation.Lease.OperationId, fence, provisioned, effectMayHaveApplied: true, CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Active, active.State);
        var duplicateActive = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            active.OperationId,
            fence,
            SemanticDuplicate(provisioned),
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Active, duplicateActive.State);
        Assert.Equal(active.UpdatedAt, duplicateActive.UpdatedAt);
        Assert.Equal(provisioned.Resource, duplicateActive.ProvisionedResource!.Resource);

        var transition = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            lifecycle.TransitionAsync(
                new EnvironmentLifecycleTransitionRequest(
                    owner,
                    fence.LifecycleGeneration,
                    EnvironmentLifecycleState.Active,
                    "advance"),
                CancellationToken.None));
        Assert.Equal("environment_sandbox_lease_active", transition.Code);

        var retiring = await store.BeginRetirementAsync(
            fence,
            active.ResourceGeneration,
            active.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 7),
            terminalEvidence: null,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Releasing, retiring.State);
        Assert.True(retiring.CurrentFencingGeneration > retiring.ProviderFencingGeneration);
        Assert.Equal("actor-1", retiring.RetiringActorId);

        var receipt = new SandboxReleaseReceipt(
            provisioned.Resource,
            "abandon-1",
            SandboxReleaseDisposition.Released);
        var releaseChecks = 0;
        await Assert.ThrowsAsync<RuntimeAuthorizationException>(() =>
            SandboxLeaseProviderAttemptTestSupport.CompleteReleaseAsync(
            store,
            retiring,
            receipt,
            CancellationToken.None,
            _ =>
            {
                if (++releaseChecks == 2)
                    throw new RuntimeAuthorizationException("suspend_manifest_changed");
                return Task.CompletedTask;
            }));
        Assert.Equal(2, releaseChecks);
        var notReleased = await store.GetCurrentAsync(fence, CancellationToken.None);
        Assert.NotNull(notReleased);
        Assert.Equal(SandboxLeaseState.Releasing, notReleased.State);
        Assert.Equal(retiring.LeaseRevision, notReleased.LeaseRevision);
        releaseChecks = 0;
        var released = await SandboxLeaseProviderAttemptTestSupport.CompleteReleaseAsync(
            store,
            retiring,
            receipt,
            CancellationToken.None,
            _ =>
            {
                releaseChecks++;
                return Task.CompletedTask;
            });
        Assert.Equal(2, releaseChecks);
        Assert.Equal(SandboxLeaseState.Released, released.State);
        var duplicateCompletion = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            released.OperationId,
            fence,
            SemanticDuplicate(provisioned),
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, duplicateCompletion.State);
        Assert.False(duplicateCompletion.IsCurrent);
        Assert.Equal(released.UpdatedAt, duplicateCompletion.UpdatedAt);
        Assert.Null(await store.GetCurrentAsync(fence, CancellationToken.None));
        Assert.Equal(
            EnvironmentLifecycleState.Active,
            (await lifecycle.GetAsync(owner, CancellationToken.None))!.State);
    }

    [Fact]
    public async Task TerminalReleaseRequiresExactProviderEvidenceAndKeepsOldResourceGeneration()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var first = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        var provisioned = Provisioned(first.Lease.ResourceGeneration);
        var active = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            first.Lease.OperationId, fence, provisioned, effectMayHaveApplied: true, CancellationToken.None);

        var mismatch = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.BeginRetirementAsync(
                fence,
                active.ResourceGeneration,
                active.ProviderFencingGeneration,
                SandboxRetirementReason.Finished,
                "release-1",
                authorization: null,
                new SandboxTerminalEvidence(
                    1, "foreign-claim", "sandbox-uid", active.ProviderFencingGeneration,
                    active.ResourceGeneration, SandboxTerminalReason.PodSucceeded, DateTimeOffset.UtcNow),
                CancellationToken.None));
        Assert.Equal("sandbox_terminal_evidence_mismatch", mismatch.Code);

        var finished = await store.BeginRetirementAsync(
            fence,
            active.ResourceGeneration,
            active.ProviderFencingGeneration,
            SandboxRetirementReason.Finished,
            "release-1",
            authorization: null,
            new SandboxTerminalEvidence(
                1,
                provisioned.Resource.ResourceId,
                "sandbox-uid",
                active.ProviderFencingGeneration,
                active.ResourceGeneration,
                SandboxTerminalReason.PodSucceeded,
                DateTimeOffset.UtcNow),
            CancellationToken.None);
        var receipt = new SandboxReleaseReceipt(
            provisioned.Resource,
            "release-1",
            SandboxReleaseDisposition.KnownOwnedAbsent);
        var released = await SandboxLeaseProviderAttemptTestSupport.CompleteReleaseAsync(
            store,
            finished,
            receipt,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, released.State);

        var second = await store.ReserveProvisionAsync(fence, "provision-2", Intent(), CancellationToken.None);
        Assert.Equal(first.Lease.ResourceGeneration + 1, second.Lease.ResourceGeneration);
        Assert.True(second.Lease.ProviderFencingGeneration > finished.CurrentFencingGeneration);
        Assert.Equal(first.Lease.OperationId, released.OperationId);
    }

    [Fact]
    public async Task AbandonDuringProvisionFencesLateCompletionAndDoesNotClaimAbsence()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        var attempt = await SandboxLeaseProviderAttemptTestSupport.BeginProvisionAttemptAsync(
            store, reservation.Lease, CancellationToken.None);
        var retiring = await store.BeginRetirementAsync(
            fence,
            reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 9),
            terminalEvidence: null,
            CancellationToken.None);

        var late = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            reservation.Lease.OperationId,
            fence,
            Provisioned(reservation.Lease.ResourceGeneration),
            effectMayHaveApplied: true,
            CancellationToken.None,
            providerAttemptId: attempt.AttemptId);
        Assert.Equal(SandboxLeaseState.Released, late.State);
        Assert.NotNull(late.ProvisionedResource);
        Assert.Equal(retiring.CurrentFencingGeneration, late.CurrentFencingGeneration);
        Assert.Equal("abandon-1", late.ReleaseIdempotencyKey);
        Assert.False(late.IsCurrent);
        var pendingCleanup = await store.GetLateResourceCleanupRecordsAsync(fence, CancellationToken.None);
        var cleanupRecord = Assert.Single(pendingCleanup.Items);
        Assert.Equal(attempt.AttemptId, cleanupRecord.Receipt.ProvisionAttempt?.AttemptId);
        Assert.Null(cleanupRecord.Receipt.ReleaseReceipt);
    }

    [Fact]
    public async Task AbandonBeforeProviderRequestIsPersistedCompletesWithoutProviderEffect()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        var retiring = await store.BeginRetirementAsync(
            fence,
            reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-before-dispatch",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 10),
            terminalEvidence: null,
            CancellationToken.None);

        Assert.Equal(SandboxLeaseState.Releasing, retiring.State);
        Assert.Null(retiring.ProviderRequestFingerprint);
        var released = await store.CompleteProvisionAsync(
            reservation.Lease.OperationId,
            fence,
            provisionedResource: null,
            effectMayHaveApplied: false,
            CancellationToken.None);

        Assert.Equal(SandboxLeaseState.Released, released.State);
        Assert.False(released.IsCurrent);
        Assert.Null(await store.GetCurrentAsync(fence, CancellationToken.None));
    }

    [Fact]
    public async Task PartialOwnedResourceReleaseRequiresReceiptAndIsOwnerCasBound()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        _ = await store.SaveProviderRequestAsync(
            reservation.Lease.OperationId,
            fence,
            Json("{\"contractVersion\":1,\"provisionRequest\":{}}"),
            CancellationToken.None);
        var retiring = await store.BeginRetirementAsync(
            fence,
            reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-partial",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 1),
            terminalEvidence: null,
            CancellationToken.None);
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            retiring.ProvisionIntent.ProviderId,
            retiring.ProvisionIntent.OptionsRevision,
            fence,
            retiring.ResourceGeneration,
            retiring.ProviderFencingGeneration,
            retiring.OperationId);
        var suffix = planned.ResourceId["aw-claim-".Length..];
        var receipt = new SandboxPartialReleaseReceipt(
            retiring.OperationId,
            retiring.ResourceGeneration,
            retiring.ProviderFencingGeneration,
            retiring.CurrentFencingGeneration,
            retiring.LeaseRevision,
            retiring.ReleaseIdempotencyKey!,
            "sandbox-system",
            planned.ResourceId,
            $"aw-template-{suffix}",
            "template-uid-1",
            $"aw-pool-{suffix}",
            "pool-uid-1",
            ClaimAbsent: true,
            SandboxesAbsent: true,
            PodsAbsent: true,
            SandboxReleaseDisposition.Released);

        var released = await SandboxLeaseProviderAttemptTestSupport.CompletePartialReleaseAsync(
            store, retiring, receipt, CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, released.State);
        Assert.False(released.IsCurrent);
        Assert.Null(released.ProvisionedResource);
        Assert.Null(released.LeaseExpiresAt);
        Assert.Equal(receipt, released.PartialReleaseReceipt);

        var replay = await SandboxLeaseProviderAttemptTestSupport.CompletePartialReleaseAsync(
            store, retiring, receipt, CancellationToken.None);
        Assert.Equal(released.LeaseRevision, replay.LeaseRevision);
        Assert.Equal(released.UpdatedAt, replay.UpdatedAt);
    }

    [Fact]
    public async Task UnresolvedProvisionAttemptBlocksReleaseAndLateCleanupUntilExactReceiptIsRecorded()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, time);
        var originalFence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, time);
        var reservation = await store.ReserveProvisionAsync(
            originalFence, "provision-1", Intent(), CancellationToken.None);
        var provisionAttempt = await SandboxLeaseProviderAttemptTestSupport.BeginProvisionAttemptAsync(
            store, reservation.Lease, CancellationToken.None);
        var retiring = await store.BeginRetirementAsync(
            originalFence,
            reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 1),
            terminalEvidence: null,
            CancellationToken.None);
        var blockedRelease = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.BeginProviderAttemptAsync(
                SandboxProviderAttemptRequest.ForPartialRelease(retiring),
                CancellationToken.None));
        Assert.Equal("sandbox_provider_attempt_unresolved", blockedRelease.Code);

        var lateResource = Provisioned(reservation.Lease.ResourceGeneration);
        var lateLease = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            reservation.Lease.OperationId,
            originalFence,
            lateResource,
            effectMayHaveApplied: true,
            CancellationToken.None,
            providerAttemptId: provisionAttempt.AttemptId);
        Assert.Equal(SandboxLeaseState.Released, lateLease.State);

        var cleanupClaim = await store.ClaimNextLateResourceCleanupAsync(
            originalFence, CancellationToken.None);
        Assert.NotNull(cleanupClaim);
        _ = await store.BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest.ForLateResourceRelease(cleanupClaim),
            CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(3));
        Assert.Null(await store.ClaimNextLateResourceCleanupAsync(originalFence, CancellationToken.None));

        var receipt = new SandboxReleaseReceipt(
            lateResource.Resource,
            cleanupClaim.IdempotencyKey,
            SandboxReleaseDisposition.Released);
        await SandboxLeaseProviderAttemptTestSupport.CompleteLateResourceCleanupAsync(
            store,
            originalFence,
            cleanupClaim,
            receipt,
            CancellationToken.None);
        var durableLease = await store.GetAsync(
            originalFence, lateLease.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(durableLease);
        Assert.Equal(SandboxLeaseState.Released, durableLease.State);
        Assert.Null(await store.ClaimNextLateResourceCleanupAsync(originalFence, CancellationToken.None));

        var advanced = await lifecycle.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner,
                originalFence.LifecycleGeneration,
                EnvironmentLifecycleState.Active,
                "advance-after-cleanup"),
            CancellationToken.None);
        var releasedOwner = await lifecycle.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner,
                advanced.Snapshot.Fence.LifecycleGeneration,
                EnvironmentLifecycleState.Released,
                "release-after-cleanup"),
            CancellationToken.None);
        Assert.Equal(EnvironmentLifecycleState.Released, releasedOwner.Snapshot.State);
    }

    [Fact]
    public async Task SandboxLeaseMigrationCanBeRolledBackAndReappliedOnPostgres()
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
            await context.GetService<IMigrator>()
                .MigrateAsync("20261006160000_WorkspaceVolumeCleanup");
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            SELECT to_regclass('environment.sandbox_late_resource_cleanups') IS NULL
               AND to_regclass('environment.sandbox_leases') IS NULL
            """,
            connection))
            Assert.True((bool)(await command.ExecuteScalarAsync())!);

        await using (var context = new EnvironmentDbContext(options))
            await context.GetService<IMigrator>()
                .MigrateAsync("20261006170000_SandboxLeases");
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            SELECT to_regclass('environment.sandbox_late_resource_cleanups') IS NOT NULL
               AND to_regclass('environment.sandbox_leases') IS NOT NULL
            """,
            connection))
            Assert.True((bool)(await command.ExecuteScalarAsync())!);

        var owner = new EnvironmentOwnerIdentity(
            "tenant-legacy", "project-legacy", "run-legacy", "environment-legacy");
        var operationId = Guid.NewGuid();
        var legacyFingerprint = new string('a', 64);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO environment.owners
                (tenant_id, project_id, run_id, environment_id, lifecycle_generation, state, updated_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, 1, 'Active', @now);

            INSERT INTO environment.sandbox_leases
                (tenant_id, project_id, run_id, environment_id, lifecycle_generation,
                 resource_generation, provider_fencing_generation, current_fencing_generation,
                 operation_id, provision_idempotency_key, request_fingerprint, provider_id,
                 adapter_version, options_schema_version, options_revision, options_snapshot_json,
                 selection_snapshot_json, provider_request_json, provider_request_fingerprint,
                 lease_state, retirement_reason, retiring_issuer, retiring_actor_id,
                 retiring_membership_revision, release_idempotency_key, retirement_fingerprint,
                 is_current, created_at, updated_at, lease_revision, lease_expires_at)
            VALUES
                (@tenant_id, @project_id, @run_id, @environment_id, 1,
                 1, 1, 2, @operation_id, 'legacy-provision', @request_fingerprint,
                 'agent-sandbox', '1.0.0', 1, 'legacy-options', '{}'::jsonb, '{}'::jsonb,
                 '{}'::jsonb, @provider_request_fingerprint, 'Released', 'AuthorizedAbandon',
                 'https://projects.example', 'legacy-actor', 1, 'legacy-release',
                 @request_fingerprint, FALSE, @now, @now, 1, NULL)
            """,
            connection))
        {
            command.Parameters.AddWithValue("tenant_id", owner.TenantId);
            command.Parameters.AddWithValue("project_id", owner.ProjectId);
            command.Parameters.AddWithValue("run_id", owner.RunId);
            command.Parameters.AddWithValue("environment_id", owner.EnvironmentId);
            command.Parameters.AddWithValue("operation_id", operationId);
            command.Parameters.AddWithValue("request_fingerprint", new string('b', 64));
            command.Parameters.AddWithValue("provider_request_fingerprint", legacyFingerprint);
            command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
            Assert.Equal(2, await command.ExecuteNonQueryAsync());
        }

        await EnvironmentMigrator.MigrateAsync(dataSource, options);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand(
            """
            SELECT to_regclass('environment.sandbox_provider_attempts') IS NOT NULL
               AND EXISTS (
                   SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'environment'
                     AND table_name = 'sandbox_late_resource_cleanups'
                     AND column_name = 'provision_attempt_id')
               AND EXISTS (
                   SELECT 1 FROM information_schema.columns
                   WHERE table_schema = 'environment'
                     AND table_name = 'sandbox_late_resource_cleanups'
                     AND column_name = 'release_attempt_id')
            """,
            connection))
            Assert.True((bool)(await command.ExecuteScalarAsync())!);

        var fence = new EnvironmentGenerationFence(owner, 1);
        var attempts = await new EnvironmentSandboxLeaseStore(dataSource, TimeProvider.System)
            .GetProviderAttemptsAsync(fence, operationId, CancellationToken.None);
        var legacyAttempt = Assert.Single(attempts);
        Assert.Equal(SandboxProviderAttemptState.LegacyUnknown, legacyAttempt.State);
        Assert.Equal(SandboxProviderEffectKind.LegacyUnknown, legacyAttempt.Request.EffectKind);
        var releaseBlocked = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            new EnvironmentLifecycleStore(dataSource, TimeProvider.System).TransitionAsync(
                new EnvironmentLifecycleTransitionRequest(
                    owner, 1, EnvironmentLifecycleState.Released, "legacy-release"),
                CancellationToken.None));
        Assert.Equal("environment_resources_not_released", releaseBlocked.Code);
    }

    [Fact]
    public async Task PartialReleaseCannotPassAnUnresolvedProvisionAttempt()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(
            fence, "provision-partial", Intent(), CancellationToken.None);
        _ = await SandboxLeaseProviderAttemptTestSupport.BeginProvisionAttemptAsync(
            store, reservation.Lease, CancellationToken.None);
        var retiring = await store.BeginRetirementAsync(
            fence,
            reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-partial",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 1),
            terminalEvidence: null,
            CancellationToken.None);
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            retiring.ProvisionIntent.ProviderId,
            retiring.ProvisionIntent.OptionsRevision,
            fence,
            retiring.ResourceGeneration,
            retiring.ProviderFencingGeneration,
            retiring.OperationId);
        _ = SandboxResourceIdentity.CreatePlannedReference(
            retiring.ProvisionIntent.ProviderId,
            retiring.ProvisionIntent.OptionsRevision,
            fence,
            retiring.ResourceGeneration,
            retiring.ProviderFencingGeneration,
            retiring.OperationId);
        var blocked = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.BeginProviderAttemptAsync(
                SandboxProviderAttemptRequest.ForPartialRelease(retiring),
                CancellationToken.None));
        Assert.Equal("sandbox_provider_attempt_unresolved", blocked.Code);
    }

    [Fact]
    public async Task ProvisionRequestIsWriteOnceAndAnAbandonReplayReturnsTheDurableRelease()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(fence, "provision-1", Intent(), CancellationToken.None);
        var firstRequest = Json("{\"contractVersion\":1,\"mountPath\":\"/workspace/data\"}");
        var saved = await store.SaveProviderRequestAsync(
            reservation.Lease.OperationId, fence, firstRequest, CancellationToken.None);
        var replay = await store.SaveProviderRequestAsync(
            reservation.Lease.OperationId, fence, firstRequest, CancellationToken.None);
        Assert.Equal(saved.ProviderRequestFingerprint, replay.ProviderRequestFingerprint);

        var conflict = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.SaveProviderRequestAsync(
                reservation.Lease.OperationId,
                fence,
                Json("{\"contractVersion\":1,\"mountPath\":\"/workspace/other\"}"),
                CancellationToken.None));
        Assert.Equal("sandbox_provider_request_conflict", conflict.Code);

        var provisioned = Provisioned(reservation.Lease.ResourceGeneration);
        var active = await SandboxLeaseProviderAttemptTestSupport.CompleteProvisionAsync(
            store,
            reservation.Lease.OperationId, fence, provisioned, effectMayHaveApplied: true, CancellationToken.None);
        var authorization = new SandboxRetirementAuthorization("https://projects.example", "actor-1", 4);
        var retiring = await store.BeginRetirementAsync(
            fence,
            active.ResourceGeneration,
            active.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            authorization,
            terminalEvidence: null,
            CancellationToken.None);
        var released = await SandboxLeaseProviderAttemptTestSupport.CompleteReleaseAsync(
            store,
            retiring,
            new SandboxReleaseReceipt(
                provisioned.Resource,
                "abandon-1",
                SandboxReleaseDisposition.Released),
            CancellationToken.None);
        var retirementReplay = await store.BeginRetirementAsync(
            fence,
            released.ResourceGeneration,
            released.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            authorization,
            terminalEvidence: null,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, retirementReplay.State);
        Assert.False(retirementReplay.IsCurrent);
    }

    private static async Task<EnvironmentGenerationFence> RegisterAsync(
        EnvironmentLifecycleStore lifecycle,
        EnvironmentOwnerIdentity owner)
    {
        var result = await lifecycle.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner, 0, EnvironmentLifecycleState.Active, "register"),
            CancellationToken.None);
        return result.Snapshot.Fence;
    }

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
            "claim-uid-1",
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

    private static SandboxProvisionedResource SemanticDuplicate(SandboxProvisionedResource resource) =>
        resource with
        {
            NegotiatedCapabilities = resource.NegotiatedCapabilities
                .ToImmutableHashSet(StringComparer.Ordinal),
            StartupPhases =
            [
                new SandboxStartupPhaseObservation(
                    SandboxStartupPhase.Started,
                    1,
                    new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero))
            ],
            ProviderBinding = resource.ProviderBinding with
            {
                OptionsSnapshot = Json("{\"namespace\":\"sandbox-system\",\"image\":\"agenthost:1\"}"),
                ReleaseDescriptor = Json("{\"claimName\":\"claim-1\",\"namespace\":\"sandbox-system\"}")
            }
        };

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static EnvironmentOwnerIdentity NewOwner()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new("tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
