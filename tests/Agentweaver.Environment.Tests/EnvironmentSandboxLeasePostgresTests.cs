using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
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
        var active = await store.CompleteProvisionAsync(
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

        var released = await store.CompleteReleaseAsync(
            retiring.OperationId,
            fence,
            retiring.ProviderFencingGeneration,
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
        var active = await store.CompleteProvisionAsync(
            reservation.Lease.OperationId, fence, provisioned, effectMayHaveApplied: true, CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Active, active.State);
        var duplicateActive = await store.CompleteProvisionAsync(
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
        var released = await store.CompleteReleaseAsync(
            retiring.OperationId,
            fence,
            retiring.ProviderFencingGeneration,
            receipt,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, released.State);
        var duplicateCompletion = await store.CompleteProvisionAsync(
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
        var active = await store.CompleteProvisionAsync(
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
        var released = await store.CompleteReleaseAsync(
            finished.OperationId,
            fence,
            finished.ProviderFencingGeneration,
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
        var retiring = await store.BeginRetirementAsync(
            fence,
            reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 9),
            terminalEvidence: null,
            CancellationToken.None);

        var late = await store.CompleteProvisionAsync(
            reservation.Lease.OperationId,
            fence,
            Provisioned(reservation.Lease.ResourceGeneration),
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Releasing, late.State);
        Assert.NotNull(late.ProvisionedResource);
        Assert.Equal(retiring.CurrentFencingGeneration, late.CurrentFencingGeneration);
        Assert.Equal("abandon-1", late.ReleaseIdempotencyKey);
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

        var released = await store.CompletePartialReleaseAsync(
            new SandboxPartialReleaseCompletionRequest(fence, receipt),
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, released.State);
        Assert.False(released.IsCurrent);
        Assert.Null(released.ProvisionedResource);
        Assert.Null(released.LeaseExpiresAt);
        Assert.Equal(receipt, released.PartialReleaseReceipt);

        var replay = await store.CompletePartialReleaseAsync(
            new SandboxPartialReleaseCompletionRequest(fence, receipt),
            CancellationToken.None);
        Assert.Equal(released.LeaseRevision, replay.LeaseRevision);
        Assert.Equal(released.UpdatedAt, replay.UpdatedAt);
    }

    [Fact]
    public async Task DifferentLateResourceAfterReleasePreservesTerminalLeaseAndUsesOriginalFenceForCleanup()
    {
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, time);
        var originalFence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, time);
        var reservation = await store.ReserveProvisionAsync(
            originalFence, "provision-1", Intent(), CancellationToken.None);
        var provisioned = Provisioned(reservation.Lease.ResourceGeneration);
        var active = await store.CompleteProvisionAsync(
            reservation.Lease.OperationId,
            originalFence,
            provisioned,
            effectMayHaveApplied: true,
            CancellationToken.None);
        var retiring = await store.BeginRetirementAsync(
            originalFence,
            active.ResourceGeneration,
            active.ProviderFencingGeneration,
            SandboxRetirementReason.AuthorizedAbandon,
            "abandon-1",
            new SandboxRetirementAuthorization("https://projects.example", "actor-1", 1),
            terminalEvidence: null,
            CancellationToken.None);
        var released = await store.CompleteReleaseAsync(
            retiring.OperationId,
            originalFence,
            retiring.ProviderFencingGeneration,
            new SandboxReleaseReceipt(
                provisioned.Resource,
                retiring.ReleaseIdempotencyKey!,
                SandboxReleaseDisposition.Released),
            CancellationToken.None);
        var lateResource = provisioned with
        {
            Resource = provisioned.Resource with { ResourceId = "claim-uid-late" },
            Endpoint = new SandboxEndpointReference(Guid.NewGuid())
        };

        var afterLateCompletion = await store.CompleteProvisionAsync(
            released.OperationId,
            originalFence,
            lateResource,
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, afterLateCompletion.State);
        Assert.False(afterLateCompletion.IsCurrent);
        Assert.Equal(provisioned.Resource, afterLateCompletion.ProvisionedResource!.Resource);
        Assert.Equal(released.UpdatedAt, afterLateCompletion.UpdatedAt);
        Assert.Null(await store.GetCurrentAsync(originalFence, CancellationToken.None));

        var nextFence = (await lifecycle.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner, originalFence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance-1"),
            CancellationToken.None)).Snapshot.Fence;
        var currentFence = (await lifecycle.TransitionAsync(
            new EnvironmentLifecycleTransitionRequest(
                owner, nextFence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance-2"),
            CancellationToken.None)).Snapshot.Fence;
        var firstClaim = await store.ClaimNextLateResourceCleanupAsync(currentFence, CancellationToken.None);
        Assert.NotNull(firstClaim);
        Assert.Equal(originalFence, firstClaim.Lease.Fence);
        Assert.Equal(lateResource.Resource, firstClaim.ProvisionedResource.Resource);
        Assert.Null(await store.ClaimNextLateResourceCleanupAsync(currentFence, CancellationToken.None));

        time.Advance(TimeSpan.FromMinutes(2));
        var retryClaim = await store.ClaimNextLateResourceCleanupAsync(currentFence, CancellationToken.None);
        Assert.NotNull(retryClaim);
        Assert.NotEqual(firstClaim.ClaimToken, retryClaim.ClaimToken);
        var releaseReceipt = new SandboxReleaseReceipt(
            lateResource.Resource,
            retryClaim.IdempotencyKey,
            SandboxReleaseDisposition.Released);
        var staleClaim = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.CompleteLateResourceCleanupAsync(
                currentFence, firstClaim, releaseReceipt, CancellationToken.None));
        Assert.Equal("sandbox_late_cleanup_claim_stale", staleClaim.Code);

        await store.CompleteLateResourceCleanupAsync(
            currentFence, retryClaim, releaseReceipt, CancellationToken.None);
        await store.CompleteLateResourceCleanupAsync(
            currentFence, retryClaim, releaseReceipt, CancellationToken.None);
        Assert.Null(await store.ClaimNextLateResourceCleanupAsync(currentFence, CancellationToken.None));
        var durableLease = await store.GetAsync(
            currentFence, released.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(durableLease);
        Assert.Equal(SandboxLeaseState.Released, durableLease.State);
        Assert.Equal(provisioned.Resource, durableLease.ProvisionedResource!.Resource);
    }

    [Fact]
    public async Task DifferentLateResourceAfterPartialReleasePreservesPartialReceiptAndQueuesCleanup()
    {
        var owner = NewOwner();
        var lifecycle = new EnvironmentLifecycleStore(fixture.DataSource, TimeProvider.System);
        var fence = await RegisterAsync(lifecycle, owner);
        var store = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var reservation = await store.ReserveProvisionAsync(
            fence, "provision-partial", Intent(), CancellationToken.None);
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
        var partialReceipt = new SandboxPartialReleaseReceipt(
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
        var released = await store.CompletePartialReleaseAsync(
            new SandboxPartialReleaseCompletionRequest(fence, partialReceipt),
            CancellationToken.None);
        var lateResource = Provisioned(released.ResourceGeneration);

        var afterLateCompletion = await store.CompleteProvisionAsync(
            released.OperationId,
            fence,
            lateResource,
            effectMayHaveApplied: true,
            CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, afterLateCompletion.State);
        Assert.False(afterLateCompletion.IsCurrent);
        Assert.Null(afterLateCompletion.ProvisionedResource);
        Assert.Equal(partialReceipt, afterLateCompletion.PartialReleaseReceipt);
        Assert.Equal(released.UpdatedAt, afterLateCompletion.UpdatedAt);

        var cleanup = await store.ClaimNextLateResourceCleanupAsync(fence, CancellationToken.None);
        Assert.NotNull(cleanup);
        Assert.Equal(lateResource.Resource, cleanup.ProvisionedResource.Resource);
        await store.CompleteLateResourceCleanupAsync(
            fence,
            cleanup,
            new SandboxReleaseReceipt(
                lateResource.Resource,
                cleanup.IdempotencyKey,
                SandboxReleaseDisposition.Released),
            CancellationToken.None);
        Assert.Null(await store.ClaimNextLateResourceCleanupAsync(fence, CancellationToken.None));
        var durableLease = await store.GetAsync(fence, released.ResourceGeneration, CancellationToken.None);
        Assert.NotNull(durableLease);
        Assert.Null(durableLease.ProvisionedResource);
        Assert.Equal(partialReceipt, durableLease.PartialReleaseReceipt);
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
        var active = await store.CompleteProvisionAsync(
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
        var released = await store.CompleteReleaseAsync(
            retiring.OperationId,
            fence,
            retiring.ProviderFencingGeneration,
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
