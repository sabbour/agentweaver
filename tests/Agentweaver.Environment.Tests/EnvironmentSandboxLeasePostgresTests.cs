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
            Json("{\"image\":\"agenthost:1\"}"),
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
            Json("{\"image\":\"agenthost:1\"}"),
            Json("{\"namespace\":\"sandbox-system\"}"));
        return new SandboxProvisionedResource(
            resource,
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("cluster-1"),
            ImmutableHashSet.Create(StringComparer.Ordinal, SandboxCapabilities.VmIsolation),
            [],
            binding);
    }

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
}
