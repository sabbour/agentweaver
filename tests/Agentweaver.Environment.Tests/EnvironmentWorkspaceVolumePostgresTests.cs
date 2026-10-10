using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Npgsql;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentWorkspaceVolumePostgresTests(EnvironmentPostgresFixture fixture)
    : IClassFixture<EnvironmentPostgresFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedLeaseAndWorkspaceReadRetainsOneOwnerLockAcrossCompetingWrites(bool replaceWorkspace)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var owner = NewOwner();
        var lifecycle = fixture.CreateStore();
        var fence = (await RegisterAsync(lifecycle, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        await lifecycle.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", timeout.Token);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        var provision = await lifecycle.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", timeout.Token);
        await lifecycle.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId, fence, true, storage, Binding(storage), true, timeout.Token);
        var expectedWorkspace = await lifecycle.GetWorkspaceVolumeAsync(fence, volumeId, timeout.Token);
        var leases = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var intent = new SandboxLeaseProvisionIntent("agent-sandbox", "1.0.0", 1, "options-1",
            JsonSerializer.SerializeToElement(new { namespaceName = "agentweaver" }),
            JsonSerializer.SerializeToElement(new { acceptedSelection = "module-only" }),
            JsonSerializer.SerializeToElement(new { volumeId }));
        var reservation = await leases.ReserveProvisionAsync(fence, "sandbox", intent, timeout.Token);
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            intent.ProviderId, intent.OptionsRevision, fence, reservation.Lease.ResourceGeneration,
            reservation.Lease.ProviderFencingGeneration, reservation.Lease.OperationId);
        var sandbox = new SandboxProvisionedResource(planned, new(Guid.NewGuid()), new("controlled-placement"),
            ImmutableHashSet.Create(SandboxCapabilities.VmIsolation), [],
            new(intent.ProviderId, intent.AdapterVersion, intent.OptionsSchemaVersion, intent.OptionsRevision,
                intent.OptionsSnapshot, JsonSerializer.SerializeToElement(new { claim = planned.ResourceId })));
        var expectedLease = await leases.CompleteProvisionAsync(
            reservation.Lease.OperationId, fence, sandbox, true, timeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restarted = new EnvironmentSandboxLeaseStore(fixture.DataSource, TimeProvider.System);
        var read = restarted.GetCurrentWithWorkspaceAsync(fence, volumeId, async (lease, workspace, token) =>
        {
            Assert.NotNull(lease);
            Assert.NotNull(workspace);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return (Lease: lease, Workspace: workspace);
        }, timeout.Token);
        await Task.WhenAny(entered.Task, read).WaitAsync(timeout.Token);
        if (!entered.Task.IsCompleted)
        {
            await read;
            throw new InvalidOperationException("The combined owner read did not reach its callback.");
        }
        var application = "combined-workspace-writer-" + Guid.NewGuid().ToString("N");
        await using var writerSource = fixture.CreateDataSource(application);
        async Task WriteAsync()
        {
            if (replaceWorkspace)
                await new EnvironmentLifecycleStore(writerSource, TimeProvider.System).ReserveWorkspaceVolumeReplaceAsync(
                    fence, volumeId, 2, 1, 0, "replace", timeout.Token);
            else
                await new EnvironmentSandboxLeaseStore(writerSource, TimeProvider.System).BeginRetirementAsync(
                    fence, expectedLease.ResourceGeneration, expectedLease.ProviderFencingGeneration,
                    SandboxRetirementReason.AuthorizedAbandon, "retire",
                    new("https://module-identity.test", "module-actor", 1), null, timeout.Token);
        }
        var writer = WriteAsync();
        var error = await Record.ExceptionAsync(async () =>
        {
            await using var observer = await fixture.DataSource.OpenConnectionAsync(timeout.Token);
            await using var blocked = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks locks JOIN pg_stat_activity activity ON activity.pid = locks.pid " +
                "WHERE activity.application_name = @application AND locks.locktype = 'advisory' AND NOT locks.granted",
                observer);
            blocked.Parameters.AddWithValue("application", application);
            while (Convert.ToInt64(await blocked.ExecuteScalarAsync(timeout.Token)) == 0)
            {
                if (writer.IsCompleted)
                    throw new InvalidOperationException("The writer bypassed the combined owner lock.",
                        await Record.ExceptionAsync(() => writer));
                await using var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot()", observer);
                await clear.ExecuteNonQueryAsync(timeout.Token);
                await Task.Delay(10, timeout.Token);
            }
            Assert.False(writer.IsCompleted);
            release.TrySetResult();
            var snapshot = await read;
            Assert.Equal(expectedLease.LeaseRevision, snapshot.Lease.LeaseRevision);
            Assert.Equal(sandbox.Resource, snapshot.Lease.ProvisionedResource!.Resource);
            Assert.Equal(expectedWorkspace!.TransitionRevision, snapshot.Workspace.TransitionRevision);
            Assert.Equal(storage, snapshot.Workspace.Resource);
            Assert.Equal(0, snapshot.Workspace.DataGeneration);
            Assert.Equal(owner, snapshot.Workspace.EnvironmentFence.Owner);
            Assert.Equal(volumeId, snapshot.Workspace.VolumeId);
            await writer;
        });
        release.TrySetResult();
        var cleanupError = await Record.ExceptionAsync(() => Task.WhenAll(read, writer));
        if (error is not null && cleanupError is not null)
            throw new AggregateException("Combined owner read and cleanup failed.", error, cleanupError);
        if (error is not null || cleanupError is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error ?? cleanupError!).Throw();
    }

    [Fact]
    public async Task ProvisionAndReplaceAdvanceOnlyResourceGeneration()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var specification = CreateSpecification(owner, volumeId);
        var resource1 = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        var resource2 = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-2", 2);

        var created = await store.CreateWorkspaceVolumeAsync(
            fence,
            volumeId,
            specification,
            "create",
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, created.Phase);
        Assert.Equal(1, created.TransitionRevision);
        Assert.Equal(0, created.ResourceGeneration);
        Assert.Equal(0, created.DataGeneration);
        Assert.Null(created.Resource);
        Assert.Equal(volumeId, created.Specification.GetProperty("VolumeId").GetString());

        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        Assert.Null(provision.CurrentResource);
        Assert.Equal(2, provision.TargetTransitionRevision);
        Assert.Equal(1, provision.TargetResourceGeneration);
        Assert.Equal(0, provision.TargetDataGeneration);

        var provisioned = await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource1,
            Binding(resource1),
            effectVerified: true,
            CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, provisioned.TransitionState);
        Assert.Equal(2, provisioned.TargetTransitionRevision);
        Assert.Equal(1, provisioned.TargetResourceGeneration);
        Assert.Equal(0, provisioned.TargetDataGeneration);
        Assert.Null(provisioned.ExpectedResource);
        Assert.Equal(resource1, provisioned.TargetResource);

        var replacement = await store.ReserveWorkspaceVolumeReplaceAsync(
            fence, volumeId, 2, 1, 0, "replace", CancellationToken.None);
        Assert.Equal(resource1, replacement.CurrentResource);
        Assert.Equal(3, replacement.TargetTransitionRevision);
        Assert.Equal(2, replacement.TargetResourceGeneration);
        Assert.Equal(0, replacement.TargetDataGeneration);

        var replaced = await store.CompleteWorkspaceVolumeReplaceAsync(
            replacement.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource2,
            Binding(resource2),
            effectVerified: true,
            CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, replaced.TransitionState);
        Assert.Equal(3, replaced.TargetTransitionRevision);
        Assert.Equal(2, replaced.TargetResourceGeneration);
        Assert.Equal(0, replaced.TargetDataGeneration);
        Assert.Equal(resource1, replaced.ExpectedResource);
        Assert.Equal(resource2, replaced.TargetResource);

        var bind = await store.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, 3, 2, 0, "bind", CancellationToken.None);
        Assert.Equal(resource2, bind.CurrentResource);
        var bound = await store.CompleteWorkspaceVolumeBindAsync(
            bind.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource2,
            effectVerified: true,
            CancellationToken.None);
        Assert.Equal(4, bound.TargetTransitionRevision);
        Assert.Equal(2, bound.TargetResourceGeneration);
        Assert.Equal(0, bound.TargetDataGeneration);
        Assert.Equal(resource2, bound.ExpectedResource);
        Assert.Equal(resource2, bound.TargetResource);

        var attach = await store.ReserveWorkspaceVolumeAttachAsync(
            fence, volumeId, 4, 2, 0, "attach", CancellationToken.None);
        await store.CompleteWorkspaceVolumeAttachAsync(
            attach.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource2,
            effectVerified: true,
            CancellationToken.None);
        var detach = await store.ReserveWorkspaceVolumeDetachAsync(
            fence, volumeId, 5, 2, 0, "detach", CancellationToken.None);
        await store.CompleteWorkspaceVolumeDetachAsync(
            detach.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource2,
            effectVerified: true,
            CancellationToken.None);
        var unbind = await store.ReserveWorkspaceVolumeUnbindAsync(
            fence, volumeId, 6, 2, 0, "unbind", CancellationToken.None);
        await store.CompleteWorkspaceVolumeUnbindAsync(
            unbind.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource2,
            effectVerified: true,
            CancellationToken.None);

        var current = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, current.Phase);
        Assert.Equal(7, current.TransitionRevision);
        Assert.Equal(2, current.ResourceGeneration);
        Assert.Equal(0, current.DataGeneration);
        Assert.Equal(resource2, current.Resource);
    }

    [Fact]
    public async Task ReplacementCleanupIsAtomicallyQueuedFromTheExactPreviousBinding()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var specification = CreateSpecification(owner, volumeId);
        var oldResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "old-resource", 1);
        var newResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "new-resource", 2);
        var oldBinding = Binding(oldResource);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, specification, "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            oldResource,
            oldBinding,
            effectVerified: true,
            CancellationToken.None);
        var replacement = await store.ReserveWorkspaceVolumeReplaceAsync(
            fence, volumeId, 2, 1, 0, "replace", CancellationToken.None);

        Assert.Null(await store.ClaimWorkspaceVolumeCleanupAsync(
            fence, replacement.OperationId, TimeSpan.FromSeconds(30), CancellationToken.None));
        var completed = await store.CompleteWorkspaceVolumeReplaceAsync(
            replacement.OperationId,
            fence,
            effectMayHaveApplied: true,
            newResource,
            Binding(newResource),
            effectVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, completed.TransitionState);
        var cleanup = await store.ClaimWorkspaceVolumeCleanupAsync(
            fence, replacement.OperationId, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(cleanup);
        Assert.Equal(replacement.OperationId, cleanup.SourceReplaceOperationId);
        Assert.Equal(1, cleanup.ResourceGeneration);
        Assert.Equal(oldResource, cleanup.ReleaseRequest.Resource);
        Assert.Equal(1, cleanup.ReleaseRequest.Volume.ResourceGeneration);
        Assert.Equal(oldBinding.ProviderId, cleanup.ReleaseRequest.ProviderBinding.ProviderId);
        Assert.Equal(oldBinding.AdapterVersion, cleanup.ReleaseRequest.ProviderBinding.AdapterVersion);
        Assert.Equal(oldBinding.OptionsSchemaVersion, cleanup.ReleaseRequest.ProviderBinding.OptionsSchemaVersion);
        Assert.Equal(oldBinding.OptionsRevision, cleanup.ReleaseRequest.ProviderBinding.OptionsRevision);
        Assert.Equal(WorkspaceVolumeBindingMode.Environment, cleanup.ReleaseRequest.BindingMode);
        Assert.Equal(WorkspaceVolumeOwnerDeletionPolicy.Retain, cleanup.ReleaseRequest.OwnerDeletionPolicy);
        Assert.Equal(WorkspaceVolumeReclaimPolicy.Retain, cleanup.ReleaseRequest.ReclaimPolicy);
        Assert.True(JsonElement.DeepEquals(
            oldBinding.OptionsSnapshot,
            cleanup.ReleaseRequest.ProviderBinding.OptionsSnapshot));
        Assert.True(JsonElement.DeepEquals(
            oldBinding.ReleaseDescriptor,
            cleanup.ReleaseRequest.ProviderBinding.ReleaseDescriptor));

        var absent = new WorkspaceVolumeReleaseReceipt(
            oldResource,
            cleanup.ReleaseRequest.IdempotencyKey,
            WorkspaceVolumeReleaseDisposition.AlreadyAbsent);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.CompleteWorkspaceVolumeCleanupAsync(cleanup, absent, CancellationToken.None));
        var completedCleanup = await store.CompleteWorkspaceVolumeCleanupAsync(
            cleanup,
            new WorkspaceVolumeReleaseReceipt(
                oldResource,
                cleanup.ReleaseRequest.IdempotencyKey,
                WorkspaceVolumeReleaseDisposition.Retained),
            CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeCleanupState.Blocked, completedCleanup.State);
    }

    [Fact]
    public async Task SharedRetainReceiptPersistsBlockedCleanupAndGatesEnvironmentRelease()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "shared-workspace-volume";
        var specification = CreateSpecification(
            owner,
            volumeId,
            WorkspaceVolumeBindingMode.Shared,
            WorkspaceVolumeReclaimPolicy.Retain);
        var oldResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "old-shared", 1);
        var newResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "new-shared", 2);
        await store.CreateWorkspaceVolumeAsync(fence, volumeId, specification, "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId, fence, true, oldResource, Binding(oldResource), true, CancellationToken.None);
        var replacement = await store.ReserveWorkspaceVolumeReplaceAsync(
            fence, volumeId, 2, 1, 0, "replace-shared", CancellationToken.None);
        await store.CompleteWorkspaceVolumeReplaceAsync(
            replacement.OperationId, fence, true, newResource, Binding(newResource), true, CancellationToken.None);

        var cleanup = await store.ClaimWorkspaceVolumeCleanupAsync(
            fence, replacement.OperationId, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(cleanup);
        Assert.Equal(WorkspaceVolumeBindingMode.Shared, cleanup.ReleaseRequest.BindingMode);
        Assert.Equal(WorkspaceVolumeReclaimPolicy.Retain, cleanup.ReleaseRequest.ReclaimPolicy);
        var blocked = await store.CompleteWorkspaceVolumeCleanupAsync(
            cleanup,
            new WorkspaceVolumeReleaseReceipt(
                oldResource,
                cleanup.ReleaseRequest.IdempotencyKey,
                WorkspaceVolumeReleaseDisposition.Retained),
            CancellationToken.None);
        var persisted = await store.GetWorkspaceVolumeCleanupStatusAsync(
            fence, replacement.OperationId, CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeCleanupState.Blocked, blocked.State);
        Assert.Equal(EnvironmentWorkspaceVolumeCleanupState.Blocked, persisted!.State);
        Assert.Null(await store.ClaimWorkspaceVolumeCleanupAsync(
            fence, replacement.OperationId, TimeSpan.FromSeconds(30), CancellationToken.None));
        var release = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.TransitionAsync(
                new(owner, fence.LifecycleGeneration, EnvironmentLifecycleState.Released, "release-owner"),
                CancellationToken.None));
        Assert.Equal("environment_resources_not_released", release.Code);
    }

    [Fact]
    public async Task ExpiredCleanupLeaseCannotCompleteAfterANewerLeaseIsClaimed()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var oldResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "old-resource", 1);
        var newResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "new-resource", 2);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            oldResource,
            Binding(oldResource),
            effectVerified: true,
            CancellationToken.None);
        var replacement = await store.ReserveWorkspaceVolumeReplaceAsync(
            fence, volumeId, 2, 1, 0, "replace", CancellationToken.None);
        await store.CompleteWorkspaceVolumeReplaceAsync(
            replacement.OperationId,
            fence,
            effectMayHaveApplied: true,
            newResource,
            Binding(newResource),
            effectVerified: true,
            CancellationToken.None);

        var firstLease = await store.ClaimWorkspaceVolumeCleanupAsync(
            fence, replacement.OperationId, TimeSpan.FromMilliseconds(1), CancellationToken.None);
        Assert.NotNull(firstLease);
        await Task.Delay(TimeSpan.FromMilliseconds(25));
        var secondLease = await store.ClaimWorkspaceVolumeCleanupAsync(
            fence, replacement.OperationId, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(secondLease);
        Assert.Equal(firstLease.LeaseRevision + 1, secondLease.LeaseRevision);
        Assert.NotEqual(firstLease.LeaseId, secondLease.LeaseId);

        var staleCompletion = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.CompleteWorkspaceVolumeCleanupAsync(
                firstLease,
                new WorkspaceVolumeReleaseReceipt(
                    oldResource,
                    firstLease.ReleaseRequest.IdempotencyKey,
                    WorkspaceVolumeReleaseDisposition.Retained),
                CancellationToken.None));
        Assert.Equal("environment_volume_cleanup_lease_stale", staleCompletion.Code);

        var completed = await store.CompleteWorkspaceVolumeCleanupAsync(
            secondLease,
            new WorkspaceVolumeReleaseReceipt(
                oldResource,
                secondLease.ReleaseRequest.IdempotencyKey,
                WorkspaceVolumeReleaseDisposition.Retained),
            CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeCleanupState.Blocked, completed.State);
        Assert.Equal(secondLease.LeaseRevision, completed.LeaseRevision);
    }

    [Fact]
    public async Task ReleasePreservesLastResourceAndDataGenerations()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            Binding(resource),
            effectVerified: true,
            CancellationToken.None);

        var release = await store.ReserveWorkspaceVolumeReleaseAsync(
            fence, volumeId, 2, 1, 0, "release", CancellationToken.None);
        Assert.Equal(resource, release.CurrentResource);
        var released = await store.CompleteWorkspaceVolumeReleaseAsync(
            release.OperationId,
            fence,
            effectMayHaveApplied: true,
            releaseVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, released.TransitionState);
        Assert.Equal(3, released.TargetTransitionRevision);
        Assert.Equal(1, released.TargetResourceGeneration);
        Assert.Equal(0, released.TargetDataGeneration);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, released.TargetPhase);
        Assert.Equal(resource, released.ExpectedResource);
        Assert.Null(released.TargetResource);
        var current = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Released, current.Phase);
        Assert.Equal(1, current.ResourceGeneration);
        Assert.Equal(0, current.DataGeneration);
        Assert.Null(current.Resource);
    }

    [Fact]
    public async Task FlushAdvancesDataGenerationOnlyAfterDurabilityIsVerified()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            Binding(resource),
            effectVerified: true,
            CancellationToken.None);

        var flush = await store.ReserveWorkspaceVolumeFlushAsync(
            fence, volumeId, 2, 1, 0, 1, "flush-unverified", CancellationToken.None);
        Assert.Equal(resource, flush.CurrentResource);
        var unverified = await store.CompleteWorkspaceVolumeFlushAsync(
            flush.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            effectVerified: true,
            durableFlushVerified: false,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, unverified.TransitionState);
        Assert.Equal(resource, unverified.ExpectedResource);
        Assert.Equal(resource, unverified.TargetResource);
        var unchanged = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Equal(2, unchanged.TransitionRevision);
        Assert.Equal(1, unchanged.ResourceGeneration);
        Assert.Equal(0, unchanged.DataGeneration);

        await store.MarkWorkspaceVolumeFlushReconciledAsync(
            flush.OperationId, fence, CancellationToken.None);
        var retry = await store.ReserveWorkspaceVolumeFlushAsync(
            fence, volumeId, 2, 1, 0, 1, "flush-durable", CancellationToken.None);
        var completed = await store.CompleteWorkspaceVolumeFlushAsync(
            retry.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            effectVerified: true,
            durableFlushVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, completed.TransitionState);
        Assert.Equal(3, completed.TargetTransitionRevision);
        Assert.Equal(1, completed.TargetResourceGeneration);
        Assert.Equal(1, completed.TargetDataGeneration);
        Assert.Equal(resource, completed.ExpectedResource);
        Assert.Equal(resource, completed.TargetResource);
    }

    [Fact]
    public async Task WrongProviderGenerationRequiresReconciliationAndDoesNotCommit()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);

        var mismatched = await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            new(ProviderSeam.Storage, "azure-files", "resource-1", 2),
            null,
            effectVerified: true,
            CancellationToken.None);
        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, mismatched.TransitionState);
        Assert.Null(mismatched.ExpectedResource);
        Assert.Null(mismatched.TargetResource);

        var unchanged = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Equal(1, unchanged.TransitionRevision);
        Assert.Equal(0, unchanged.ResourceGeneration);
        Assert.Equal(0, unchanged.DataGeneration);
        Assert.Null(unchanged.Resource);

        var busy = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.ReserveWorkspaceVolumeProvisionAsync(
                fence, volumeId, 1, 0, 0, "blocked", CancellationToken.None));
        Assert.Equal("environment_volume_transition_busy", busy.Code);

        await store.MarkWorkspaceVolumeProvisionReconciledAsync(
            provision.OperationId, fence, CancellationToken.None);
        var retry = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision-retry", CancellationToken.None);
        var completed = await store.CompleteWorkspaceVolumeProvisionAsync(
            retry.OperationId,
            fence,
            effectMayHaveApplied: true,
            new(ProviderSeam.Storage, "azure-files", "resource-1", 1),
            Binding(new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1)),
            effectVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, completed.TransitionState);
        Assert.Equal(2, completed.TargetTransitionRevision);
        Assert.Equal(1, completed.TargetResourceGeneration);
        Assert.Equal(0, completed.TargetDataGeneration);
        Assert.Equal(
            new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1),
            completed.TargetResource);
    }

    [Fact]
    public async Task DifferentProviderIdentityAtTheSameGenerationRequiresReconciliation()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            Binding(resource),
            effectVerified: true,
            CancellationToken.None);

        var bind = await store.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, 2, 1, 0, "bind", CancellationToken.None);
        var mismatched = await store.CompleteWorkspaceVolumeBindAsync(
            bind.OperationId,
            fence,
            effectMayHaveApplied: true,
            new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "different-resource", 1),
            effectVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, mismatched.TransitionState);
        Assert.Equal(resource, mismatched.ExpectedResource);
        Assert.Equal(resource, mismatched.TargetResource);
        var unchanged = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(unchanged);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Ready, unchanged.Phase);
        Assert.Equal(2, unchanged.TransitionRevision);
        Assert.Equal(resource, unchanged.Resource);

        await store.MarkWorkspaceVolumeBindReconciledAsync(
            bind.OperationId, fence, CancellationToken.None);
    }

    [Fact]
    public async Task ProviderReferenceSurvivesStoreRestartAndTransitionReplay()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            Binding(resource),
            effectVerified: true,
            CancellationToken.None);

        var restartedStore = fixture.CreateStore();
        var current = await restartedStore.GetWorkspaceVolumeAsync(
            fence, volumeId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(resource, current.Resource);

        var reservation = await restartedStore.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, 2, 1, 0, "bind", CancellationToken.None);
        Assert.Equal(resource, reservation.CurrentResource);
        var replayedReservation = await restartedStore.ReserveWorkspaceVolumeBindAsync(
            fence, volumeId, 2, 1, 0, "bind", CancellationToken.None);
        Assert.True(replayedReservation.Replayed);
        Assert.Equal(reservation.OperationId, replayedReservation.OperationId);
        Assert.Equal(resource, replayedReservation.CurrentResource);

        var completed = await restartedStore.CompleteWorkspaceVolumeBindAsync(
            reservation.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            effectVerified: true,
            CancellationToken.None);
        var replayedCompletion = await restartedStore.CompleteWorkspaceVolumeBindAsync(
            reservation.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            effectVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.Completed, completed.TransitionState);
        Assert.True(replayedCompletion.Replayed);
        Assert.Equal(resource, replayedCompletion.ExpectedResource);
        Assert.Equal(resource, replayedCompletion.TargetResource);
    }

    [Fact]
    public async Task ConcurrentVolumeReservationsAdmitOnlyOneTransition()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);

        var outcomes = await Task.WhenAll(new[] { "provision-a", "provision-b" }.Select(async key =>
        {
            try
            {
                return (Reservation: await store.ReserveWorkspaceVolumeProvisionAsync(
                    fence, volumeId, 1, 0, 0, key, CancellationToken.None), Error: (Exception?)null);
            }
            catch (Exception exception)
            {
                return (Reservation: (EnvironmentWorkspaceVolumeTransitionReservation?)null, Error: exception);
            }
        }));

        Assert.Single(outcomes, outcome => outcome.Reservation is not null);
        var conflict = Assert.IsType<EnvironmentLifecycleException>(
            Assert.Single(outcomes, outcome => outcome.Error is not null).Error);
        Assert.Equal("environment_volume_transition_busy", conflict.Code);
    }

    [Fact]
    public async Task StaleOwnerCompletionDoesNotCommitProviderReferenceAndForeignOwnerCannotReadIt()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files", "resource-1", 1);
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);
        var provision = await store.ReserveWorkspaceVolumeProvisionAsync(
            fence, volumeId, 1, 0, 0, "provision", CancellationToken.None);
        var foreignOwnerFence = (await RegisterAsync(store, NewOwner())).Snapshot.Fence;

        await store.TransitionAsync(
            new(owner, fence.LifecycleGeneration, EnvironmentLifecycleState.Active, "advance"),
            CancellationToken.None);
        var completion = await store.CompleteWorkspaceVolumeProvisionAsync(
            provision.OperationId,
            fence,
            effectMayHaveApplied: true,
            resource,
            Binding(resource),
            effectVerified: true,
            CancellationToken.None);

        Assert.Equal(EnvironmentWorkspaceVolumeTransitionState.ReconciliationRequired, completion.TransitionState);
        Assert.Null(completion.TargetResource);
        var currentFence = new EnvironmentGenerationFence(owner, fence.LifecycleGeneration + 1);
        var current = await store.GetWorkspaceVolumeAsync(currentFence, volumeId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current.Phase);
        Assert.Null(current.Resource);

        Assert.Null(await store.GetWorkspaceVolumeAsync(
            foreignOwnerFence, volumeId, CancellationToken.None));
        var unknownOwner = await Assert.ThrowsAsync<EnvironmentLifecycleException>(() =>
            store.GetWorkspaceVolumeAsync(
                new EnvironmentGenerationFence(NewOwner(), fence.LifecycleGeneration),
                volumeId,
                CancellationToken.None));
        Assert.Equal("environment_unknown", unknownOwner.Code);
    }

    [Fact]
    public async Task TypedReservationsRejectResourceAndDataGenerationMisuse()
    {
        var store = fixture.CreateStore();
        var owner = NewOwner();
        var fence = (await RegisterAsync(store, owner)).Snapshot.Fence;
        const string volumeId = "workspace-volume";
        await store.CreateWorkspaceVolumeAsync(
            fence, volumeId, CreateSpecification(owner, volumeId), "create", CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ReserveWorkspaceVolumeProvisionAsync(
                fence, volumeId, 1, 1, 0, "invalid-provision", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            store.ReserveWorkspaceVolumeReplaceAsync(
                fence, volumeId, 1, 0, 0, "invalid-replace", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.ReserveWorkspaceVolumeFlushAsync(
                fence, volumeId, 1, 1, 0, 0, "invalid-flush", CancellationToken.None));

        var current = await store.GetWorkspaceVolumeAsync(fence, volumeId, CancellationToken.None);
        Assert.NotNull(current);
        Assert.Equal(EnvironmentWorkspaceVolumeState.Requested, current.Phase);
        Assert.Equal(1, current.TransitionRevision);
        Assert.Equal(0, current.ResourceGeneration);
        Assert.Equal(0, current.DataGeneration);
    }

    private static EnvironmentOwnerIdentity NewOwner()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new("tenant-" + suffix, "project-" + suffix, "run-" + suffix, "environment-" + suffix);
    }

    private static JsonElement CreateSpecification(
        EnvironmentOwnerIdentity owner,
        string volumeId,
        WorkspaceVolumeBindingMode bindingMode = WorkspaceVolumeBindingMode.Environment,
        WorkspaceVolumeReclaimPolicy reclaimPolicy = WorkspaceVolumeReclaimPolicy.Delete) =>
        JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            volumeId,
            owner.ProjectId,
            bindingMode == WorkspaceVolumeBindingMode.Environment
                ? new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, owner.RunId)
                : new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Team, "workspace-team"),
            bindingMode == WorkspaceVolumeBindingMode.Environment ? owner.EnvironmentId : null,
            bindingMode,
            WorkspaceVolumeAccessMode.ReadWriteMany,
            8,
            "azure-files",
            WorkspaceVolumeConsistency.Strict,
            reclaimPolicy,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            bindingMode == WorkspaceVolumeBindingMode.Shared ? [owner.EnvironmentId] : []));

    private static WorkspaceVolumeProviderBindingSnapshot Binding(ProviderResourceRef resource) =>
        new WorkspaceVolumeProviderBindingSnapshot(
            resource.ProviderId,
            "1.0.0",
            1,
            "test-options-1",
            JsonSerializer.SerializeToElement(new { endpoint = "test" }),
            JsonSerializer.SerializeToElement(new { resourceId = resource.ResourceId }))
            .ValidateFor(resource);

    private static Task<EnvironmentLifecycleTransitionResult> RegisterAsync(
        EnvironmentLifecycleStore store,
        EnvironmentOwnerIdentity owner) =>
        store.TransitionAsync(
            new(owner, 0, EnvironmentLifecycleState.Active, "register"),
            CancellationToken.None);
}
