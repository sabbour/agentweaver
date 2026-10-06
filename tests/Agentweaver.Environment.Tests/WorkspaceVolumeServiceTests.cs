using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class WorkspaceVolumeServiceTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Fact]
    public async Task ProvisionReplayDoesNotRepeatProviderEffect()
    {
        var setup = await CreateRequestedVolumeAsync();
        var request = Transition(
            setup.Specification.VolumeId,
            WorkspaceVolumeTransitionKind.Provision,
            1,
            0,
            "provision-once");

        var first = await setup.Service.ProvisionAsync(setup.Fence, request);
        var replay = await setup.Service.ProvisionAsync(setup.Fence, request);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, first.Completion!.TransitionState);
        Assert.True(replay.Reservation.Replayed);
        Assert.Null(replay.Completion);
        Assert.Equal(first.Completion.TargetResource, replay.CurrentVolume!.Resource);
        Assert.Single(setup.Provider.ProvisionRequests);
        Assert.NotNull(current);
        Assert.Equal(first.Completion.TargetResource, current.Resource);
    }

    [Fact]
    public async Task BindAndUnbindUseThePinnedOwnerResource()
    {
        var setup = await CreateRequestedVolumeAsync();
        var provision = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "provision-for-binding"));

        var bind = await setup.Service.BindAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Bind,
                2,
                1,
                "bind-volume"));
        var unbind = await setup.Service.UnbindAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Unbind,
                3,
                1,
                "unbind-volume"));
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.Completed,
            bind.Completion!.TransitionState);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.Completed,
            unbind.Completion!.TransitionState);
        Assert.Equal(provision.Completion!.TargetResource, bind.Completion.ExpectedResource);
        Assert.Equal(bind.Completion.ExpectedResource, unbind.Completion.ExpectedResource);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current!.Phase);
        Assert.Equal(1, current.ResourceGeneration);
        Assert.Empty(setup.Provider.ReleaseRequests);
    }

    [Fact]
    public async Task ReleaseCompletesAfterValidPinnedResourceReceipt()
    {
        var setup = await CreateRequestedVolumeAsync();
        var provision = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "provision-for-release"));

        var release = await setup.Service.ReleaseAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Release,
                2,
                1,
                "release-volume"));
        var replay = await setup.Service.ReleaseAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Release,
                2,
                1,
                "release-volume"));
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.Completed,
            release.Completion!.TransitionState);
        Assert.Equal(provision.Completion!.TargetResource, release.Completion.ExpectedResource);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, current!.Phase);
        Assert.Null(current.Resource);
        var providerRequest = Assert.Single(setup.Provider.ReleaseRequests);
        Assert.Equal(provision.Completion.TargetResource, providerRequest.Resource);
        Assert.Equal(setup.Specification.ReclaimPolicy, providerRequest.ReclaimPolicy);
        Assert.Equal("release-volume", providerRequest.IdempotencyKey);
        Assert.True(replay.Reservation.Replayed);
        Assert.Null(replay.Completion);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, replay.CurrentVolume!.Phase);
    }

    [Fact]
    public async Task ReleaseOfUnprovisionedVolumeCompletesWithoutProviderEffect()
    {
        var setup = await CreateRequestedVolumeAsync();

        var release = await setup.Service.ReleaseAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Release,
                1,
                0,
                "release-unprovisioned"));
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.Completed,
            release.Reservation.TransitionState);
        Assert.Null(release.Completion);
        Assert.NotNull(release.CurrentVolume);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, release.CurrentVolume.Phase);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, current!.Phase);
        Assert.Equal(0, current.ResourceGeneration);
        Assert.Null(current.Resource);
        Assert.Empty(setup.Provider.ReleaseRequests);
    }

    [Fact]
    public async Task ReleaseFailureRemainsDurablyReconcilable()
    {
        var setup = await CreateRequestedVolumeAsync();
        var provision = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "provision-for-failed-release"));
        setup.Provider.ReleaseFailure = new InvalidOperationException("release outcome uncertain");
        var request = Transition(
            setup.Specification.VolumeId,
            WorkspaceVolumeTransitionKind.Release,
            2,
            1,
            "release-outcome-uncertain");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Service.ReleaseAsync(setup.Fence, request));
        var replay = await setup.Service.ReleaseAsync(setup.Fence, request);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal("release outcome uncertain", exception.Message);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            replay.Reservation.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current!.Phase);
        Assert.Equal(1, current.ResourceGeneration);
        Assert.Equal(provision.Completion!.TargetResource, current.Resource);
        Assert.Single(setup.Provider.ReleaseRequests);
    }

    [Fact]
    public async Task ReleaseCompletionSurvivesCallerCancellationAfterProviderEffect()
    {
        var setup = await CreateRequestedVolumeAsync();
        await ProvisionAsync(setup);
        using var cancellation = new CancellationTokenSource();
        setup.Provider.BeforeReleaseAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        var result = await setup.Service.ReleaseAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Release,
                2,
                1,
                "release-cancel-after-effect"),
            cancellation.Token);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.Completed,
            result.Completion!.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, current!.Phase);
        Assert.Null(current.Resource);
        Assert.Single(setup.Provider.ReleaseRequests);
    }

    [Fact]
    public async Task StaleFenceAfterReleaseEffectDoesNotCommitRelease()
    {
        var setup = await CreateRequestedVolumeAsync();
        var provision = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "provision-for-stale-release"));
        setup.Provider.BeforeReleaseAsync = async _ =>
        {
            await setup.Store.TransitionAsync(
                new(
                    setup.Owner,
                    setup.Fence.LifecycleGeneration,
                    EnvironmentLifecycleState.Active,
                    "advance-release-fence"),
                CancellationToken.None);
        };

        var result = await setup.Service.ReleaseAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Release,
                2,
                1,
                "release-stale-fence"));
        var currentFence = new EnvironmentGenerationFence(
            setup.Owner, setup.Fence.LifecycleGeneration + 1);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            currentFence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            result.Completion!.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current!.Phase);
        Assert.Equal(1, current.ResourceGeneration);
        Assert.Equal(provision.Completion!.TargetResource, current.Resource);
        Assert.Single(setup.Provider.ReleaseRequests);
    }

    [Fact]
    public async Task StaleFenceAfterProvisionEffectDoesNotCommitProviderResource()
    {
        var setup = await CreateRequestedVolumeAsync();
        setup.Provider.BeforeProvisionAsync = async _ =>
        {
            await setup.Store.TransitionAsync(
                new(
                    setup.Owner,
                    setup.Fence.LifecycleGeneration,
                    EnvironmentLifecycleState.Active,
                    "advance-fence"),
                CancellationToken.None);
        };

        var result = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "stale-provision"));
        var currentFence = new EnvironmentGenerationFence(
            setup.Owner, setup.Fence.LifecycleGeneration + 1);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            currentFence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            result.Completion!.TransitionState);
        Assert.NotNull(current);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current.Phase);
        Assert.Equal(0, current.ResourceGeneration);
        Assert.Null(current.Resource);
        Assert.Single(setup.Provider.ProvisionRequests);
    }

    [Fact]
    public async Task ProvisionCompletionSurvivesCallerCancellationAfterProviderEffect()
    {
        var setup = await CreateRequestedVolumeAsync();
        using var cancellation = new CancellationTokenSource();
        setup.Provider.BeforeProvisionAsync = _ =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        };

        var result = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "provision-cancel-after-effect"),
            cancellation.Token);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.Completed,
            result.Completion!.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current!.Phase);
        Assert.Equal(result.Completion.TargetResource, current.Resource);
        Assert.Single(setup.Provider.ProvisionRequests);
    }

    [Fact]
    public async Task FlushWithoutDurableCapabilityDoesNotAdvanceDataGeneration()
    {
        var setup = await CreateRequestedVolumeAsync();
        var pinnedResource = await ProvisionAsync(setup);
        var request = new WorkspaceVolumeFlushRequest(
            setup.Specification,
            pinnedResource.Resource.Generation,
            setup.Fence,
            pinnedResource.Resource,
            0,
            "flush-without-capability");

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            setup.Service.FlushAsync(request, pinnedResource));
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Contains("does not advertise durable flush", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current!.Phase);
        Assert.Equal(0, current.DataGeneration);
        Assert.Equal(1, current.ResourceGeneration);
        Assert.Equal(pinnedResource.Resource, current.Resource);
    }

    [Fact]
    public async Task ReleaseValidatesReceiptBeforeCompletingOwnerTransition()
    {
        var setup = await CreateRequestedVolumeAsync(WorkspaceVolumeReclaimPolicy.Retain);
        var provision = await ProvisionAsync(setup);
        setup.Provider.ReleaseReceiptFactory = request => new WorkspaceVolumeReleaseReceipt(
            request.Resource,
            "wrong-idempotency-key",
            WorkspaceVolumeReleaseDisposition.Retained);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            setup.Service.ReleaseAsync(
                setup.Fence,
                Transition(
                    setup.Specification.VolumeId,
                    WorkspaceVolumeTransitionKind.Release,
                    2,
                    1,
                    "release-retained")));
        var pending = await setup.Service.ReleaseAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Release,
                2,
                1,
                "release-retained"));
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Contains("exact provider resource", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            pending.Reservation.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current!.Phase);
        Assert.Equal(provision.Resource, current.Resource);
        Assert.Single(setup.Provider.ReleaseRequests);
    }

    [Fact]
    public async Task ProviderFailureLeavesProvisionDurablyReconcilable()
    {
        var setup = await CreateRequestedVolumeAsync();
        setup.Provider.ProvisionFailure = new InvalidOperationException("provider unavailable");
        var request = Transition(
            setup.Specification.VolumeId,
            WorkspaceVolumeTransitionKind.Provision,
            1,
            0,
            "provision-failure");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Service.ProvisionAsync(setup.Fence, request));
        var replay = await setup.Service.ProvisionAsync(setup.Fence, request);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Equal("provider unavailable", exception.Message);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            replay.Reservation.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current!.Phase);
        Assert.Equal(0, current.ResourceGeneration);
        Assert.Null(current.Resource);
        Assert.Single(setup.Provider.ProvisionRequests);
    }

    [Fact]
    public async Task ProvisionWithInsufficientCapabilitiesRemainsReconcilable()
    {
        var setup = await CreateRequestedVolumeAsync();
        setup.Provider.ProvisionedCapabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            WorkspaceVolumeCapabilities.ReadOnlyMount);
        var request = Transition(
            setup.Specification.VolumeId,
            WorkspaceVolumeTransitionKind.Provision,
            1,
            0,
            "provision-wrong-capabilities");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Service.ProvisionAsync(setup.Fence, request));
        var replay = await setup.Service.ProvisionAsync(setup.Fence, request);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Contains("access mode", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            replay.Reservation.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current!.Phase);
        Assert.Equal(0, current.ResourceGeneration);
        Assert.Null(current.Resource);
        Assert.Single(setup.Provider.ProvisionRequests);
    }

    [Fact]
    public async Task ProvisionWithWrongResourceGenerationRemainsReconcilable()
    {
        var setup = await CreateRequestedVolumeAsync();
        setup.Provider.ProvisionedResourceGeneration = 2;
        var request = Transition(
            setup.Specification.VolumeId,
            WorkspaceVolumeTransitionKind.Provision,
            1,
            0,
            "provision-wrong-generation");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            setup.Service.ProvisionAsync(setup.Fence, request));
        var replay = await setup.Service.ProvisionAsync(setup.Fence, request);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);

        Assert.Contains("resource generation", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired,
            replay.Reservation.TransitionState);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current!.Phase);
        Assert.Equal(0, current.ResourceGeneration);
        Assert.Null(current.Resource);
        Assert.Single(setup.Provider.ProvisionRequests);
    }

    [Fact]
    public async Task ReplaceFailsBeforeReadingOwnerOrCallingProvider()
    {
        var setup = await CreateRequestedVolumeAsync();
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            setup.Service.ReplaceAsync(
                setup.Fence,
                Transition(
                    setup.Specification.VolumeId,
                    WorkspaceVolumeTransitionKind.Replace,
                    1,
                    1,
                    "replace-unavailable")));

        Assert.Contains("cleanup", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(setup.Provider.ProvisionRequests);
        Assert.Empty(setup.Provider.ReleaseRequests);
        var current = await setup.Store.GetWorkspaceVolumeAsync(
            setup.Fence, setup.Specification.VolumeId, CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current!.Phase);
        Assert.Equal(1, current.TransitionRevision);
        Assert.Equal(0, current.ResourceGeneration);
    }

    [Fact]
    public async Task CreateRejectsSpecificationsOutsideAuthorizedScope()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var activated = await store.TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "activate"),
            CancellationToken.None);
        var provider = new RecordingWorkspaceVolumeProvider();
        var service = new WorkspaceVolumeService(store, provider);
        var wrongProject = CreateSpecification("another-project", owner.RunId);
        var wrongRun = CreateSpecification(owner.ProjectId, "another-run");
        var wrongShare = new WorkspaceVolumeSpec(
            "shared-volume",
            owner.ProjectId,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Team, "workspace-team"),
            null,
            WorkspaceVolumeBindingMode.Shared,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            8,
            "azure-files",
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Retain,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            ["another-environment"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            activated.Snapshot.Fence,
            CreateRequest(wrongProject)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            activated.Snapshot.Fence,
            CreateRequest(wrongRun)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            activated.Snapshot.Fence,
            CreateRequest(wrongShare)));

        Assert.Null(await store.GetWorkspaceVolumeAsync(
            activated.Snapshot.Fence, "workspace-volume", CancellationToken.None));
        Assert.Null(await store.GetWorkspaceVolumeAsync(
            activated.Snapshot.Fence, "shared-volume", CancellationToken.None));
        Assert.Empty(provider.ProvisionRequests);
        Assert.Empty(provider.ReleaseRequests);

        WorkspaceVolumeSpec CreateSpecification(string projectId, string runId) =>
            new(
                "workspace-volume",
                projectId,
                new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, runId),
                owner.EnvironmentId,
                WorkspaceVolumeBindingMode.Environment,
                WorkspaceVolumeAccessMode.ReadWriteMany,
                8,
                "azure-files",
                WorkspaceVolumeConsistency.Strict,
                WorkspaceVolumeReclaimPolicy.Delete,
                WorkspaceVolumeOwnerDeletionPolicy.Retain,
                []);

        static WorkspaceVolumeTransitionRequest CreateRequest(WorkspaceVolumeSpec specification) =>
            new(
                specification.VolumeId,
                WorkspaceVolumeTransitionKind.Create,
                0,
                0,
                0,
                null,
                "create-out-of-scope",
                specification);
    }

    private async Task<Setup> CreateRequestedVolumeAsync(
        WorkspaceVolumeReclaimPolicy reclaimPolicy = WorkspaceVolumeReclaimPolicy.Delete)
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var activated = await store.TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "activate"),
            CancellationToken.None);
        var specification = new WorkspaceVolumeSpec(
            "workspace-volume",
            owner.ProjectId,
            new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, owner.RunId),
            owner.EnvironmentId,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            8,
            "azure-files",
            WorkspaceVolumeConsistency.Strict,
            reclaimPolicy,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            []);
        var provider = new RecordingWorkspaceVolumeProvider();
        var service = new WorkspaceVolumeService(store, provider);
        var create = new WorkspaceVolumeTransitionRequest(
            specification.VolumeId,
            WorkspaceVolumeTransitionKind.Create,
            0,
            0,
            0,
            null,
            "create-volume",
            specification);

        await service.CreateAsync(activated.Snapshot.Fence, create);
        return new Setup(store, service, provider, owner, activated.Snapshot.Fence, specification);
    }

    private static async Task<WorkspaceVolumeResource> ProvisionAsync(Setup setup)
    {
        var result = await setup.Service.ProvisionAsync(
            setup.Fence,
            Transition(
                setup.Specification.VolumeId,
                WorkspaceVolumeTransitionKind.Provision,
                1,
                0,
                "provision-for-test"));
        return new WorkspaceVolumeResource(
            result.Completion!.TargetResource!,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                WorkspaceVolumeCapabilities.ReadWriteMany));
    }

    private static WorkspaceVolumeTransitionRequest Transition(
        string volumeId,
        WorkspaceVolumeTransitionKind operation,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        string idempotencyKey) =>
        new(
            volumeId,
            operation,
            expectedTransitionRevision,
            expectedResourceGeneration,
            0,
            null,
            idempotencyKey);

    private static EnvironmentOwnerIdentity NewOwner()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new("tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
    }

    private sealed record Setup(
        EnvironmentLifecycleStore Store,
        WorkspaceVolumeService Service,
        RecordingWorkspaceVolumeProvider Provider,
        EnvironmentOwnerIdentity Owner,
        EnvironmentGenerationFence Fence,
        WorkspaceVolumeSpec Specification);

    private sealed class RecordingWorkspaceVolumeProvider : IWorkspaceVolumeProvider
    {
        public List<WorkspaceVolumeProvisionRequest> ProvisionRequests { get; } = [];
        public List<WorkspaceVolumeReleaseRequest> ReleaseRequests { get; } = [];
        public Func<WorkspaceVolumeProvisionRequest, Task>? BeforeProvisionAsync { get; set; }
        public Func<WorkspaceVolumeReleaseRequest, Task>? BeforeReleaseAsync { get; set; }
        public Func<WorkspaceVolumeReleaseRequest, WorkspaceVolumeReleaseReceipt>? ReleaseReceiptFactory { get; set; }
        public ImmutableHashSet<string>? ProvisionedCapabilities { get; set; }
        public long? ProvisionedResourceGeneration { get; set; }
        public Exception? ProvisionFailure { get; set; }
        public Exception? ReleaseFailure { get; set; }

        public async Task<WorkspaceVolumeResource> ProvisionAsync(
            WorkspaceVolumeProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionRequests.Add(request);
            if (BeforeProvisionAsync is not null)
                await BeforeProvisionAsync(request).ConfigureAwait(false);
            if (ProvisionFailure is not null)
                throw ProvisionFailure;

            return new WorkspaceVolumeResource(
                new ProviderResourceRef(
                    ProviderSeam.Storage,
                    "test-storage",
                    "resource-" + request.ResourceGeneration,
                    ProvisionedResourceGeneration ?? request.ResourceGeneration),
                ProvisionedCapabilities ?? ImmutableHashSet.Create(
                    StringComparer.Ordinal,
                    WorkspaceVolumeCapabilities.ForAccessMode(request.Spec.AccessMode)));
        }

        public async Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
            WorkspaceVolumeReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseRequests.Add(request);
            if (BeforeReleaseAsync is not null)
                await BeforeReleaseAsync(request).ConfigureAwait(false);
            if (ReleaseFailure is not null)
                throw ReleaseFailure;
            var receipt = ReleaseReceiptFactory?.Invoke(request)
                ?? new WorkspaceVolumeReleaseReceipt(
                    request.Resource,
                    request.IdempotencyKey,
                    request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Retain
                        ? WorkspaceVolumeReleaseDisposition.Retained
                        : WorkspaceVolumeReleaseDisposition.Released);
            return receipt;
        }
    }
}
