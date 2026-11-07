using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Environment;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class EnvironmentSandboxManagerTests
{
    [Fact]
    public void PlacementProjectionPreservesOwnedCurrentLeaseIdentity()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "aw-claim-original-id");

        var projection = EnvironmentSandboxManager.ProjectCurrentPlacement(owner, fence, lease, now);

        Assert.Equal(1, projection.ContractVersion);
        Assert.Equal(owner.TenantId, projection.TenantId);
        Assert.Equal(owner.ProjectId, projection.ProjectId);
        Assert.Equal(owner.RunId, projection.RunId);
        Assert.Equal(owner.EnvironmentId, projection.EnvironmentId);
        Assert.Equal(fence.LifecycleGeneration, projection.LifecycleGeneration);
        Assert.Equal(lease.CurrentFencingGeneration, projection.CurrentFencingGeneration);
        Assert.Equal(lease.ProviderFencingGeneration, projection.ProviderFencingGeneration);
        Assert.Equal(lease.LeaseRevision, projection.LeaseRevision);
        Assert.Equal(lease.LeaseExpiresAt, projection.LeaseExpiresAt);
        Assert.True(projection.IsCurrent);
        Assert.Equal(SandboxLeaseState.Active, projection.State);
        Assert.Equal(lease.ProvisionedResource!.Resource, projection.Resource);
        Assert.Equal("aw-claim-original-id", projection.Resource.ResourceId);
        Assert.Equal(lease.ProvisionedResource.Endpoint, projection.Endpoint);
        Assert.Equal(lease.ProvisionedResource.Placement, projection.Placement);
        Assert.Null(projection.Image);
        Assert.DoesNotContain("\"image\"", JsonSerializer.Serialize(projection,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void PlacementImageComesFromProvisionedBindingRatherThanCurrentConfiguration()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "sandbox-uid");
        var options = new AgentSandboxOptions(
            2, "image-options-1", "agentweaver", "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('b', 64),
            "linux/amd64", 10_000_000_000, "kata-vm", "kata-qemu",
            "500m", "512Mi", 1, 1, new(30, 30, 30, 30, 30, 120));
        var binding = lease.ProvisionedResource!.ProviderBinding with
        {
            OptionsSchemaVersion = 2,
            OptionsRevision = options.OptionsRevision,
            OptionsSnapshot = JsonSerializer.SerializeToElement(options,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        var pinned = lease with
        {
            ProvisionedResource = lease.ProvisionedResource with { ProviderBinding = binding }
        };
        var projection = EnvironmentSandboxManager.ProjectCurrentPlacement(owner, fence, pinned, now);
        Assert.Equal(new SandboxImageIdentity("sha256:" + new string('b', 64), "linux/amd64", 10_000_000_000),
            projection.Image);
        Assert.Throws<EnvironmentLifecycleException>(() => EnvironmentSandboxManager.ProjectCurrentPlacement(
            owner, fence, pinned with
            {
                ProvisionedResource = pinned.ProvisionedResource! with
                {
                    ProviderBinding = binding with { OptionsRevision = "different-options" }
                }
            }, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnvironmentSandboxManager.ProjectCurrentPlacement(
            owner, fence, pinned with
            {
                ProvisionedResource = pinned.ProvisionedResource! with
                {
                    ProviderBinding = binding with
                    {
                        OptionsSnapshot = JsonSerializer.SerializeToElement(
                            options with { ContainerImageCompressedPullBytes = 0 },
                            new JsonSerializerOptions(JsonSerializerDefaults.Web))
                    }
                }
            }, now));
    }

    [Fact]
    public void PlacementProjectionRejectsExpiredStaleNonCurrentAndForeignLeases()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "sandbox-uid");
        var foreignOwner = new EnvironmentOwnerIdentity("tenant-b", "project-a", "run-a", "environment-a");

        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, fence, lease with { LeaseExpiresAt = now.AddSeconds(-1) }, now));
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, fence, lease with { Fence = new EnvironmentGenerationFence(owner, 5) }, now));
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner, fence, lease with { IsCurrent = false }, now));
        Assert.Throws<EnvironmentLifecycleException>(() =>
            EnvironmentSandboxManager.ProjectCurrentPlacement(
                owner,
                fence,
                lease with { Fence = new EnvironmentGenerationFence(foreignOwner, fence.LifecycleGeneration) },
                now));
    }

    [Fact]
    public void PlacementProjectionDetectsLeaseChangesDuringAuthorization()
    {
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 4);
        var lease = CreateCurrentLease(fence, now, "sandbox-uid");

        Assert.True(EnvironmentSandboxManager.SameCurrentPlacementLease(lease, lease));
        Assert.True(EnvironmentSandboxManager.SameCurrentPlacementLease(null, null));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(lease, null));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { OperationId = Guid.NewGuid() }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { Fence = new EnvironmentGenerationFence(owner, 5) }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { ResourceGeneration = lease.ResourceGeneration + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { LeaseRevision = lease.LeaseRevision + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { CurrentFencingGeneration = lease.CurrentFencingGeneration + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { ProviderFencingGeneration = lease.ProviderFencingGeneration + 1 }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { State = SandboxLeaseState.Releasing }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { IsCurrent = false }));
        Assert.False(EnvironmentSandboxManager.SameCurrentPlacementLease(
            lease,
            lease with { LeaseExpiresAt = now.AddMinutes(2) }));
    }

    [Fact]
    public async Task ProvisionRequiresFreshWriteProjectsAuthorityBeforeSelectionOrProviderAccess()
    {
        var projects = new FakeProjectsConfigClient();
        var provider = new RecordingSandboxProvider();
        var options = new AgentSandboxOptions(
            AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options-1",
            "agentweaver",
            "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "linux/amd64",
            1,
            "kata-vm",
            "kata-qemu",
            "500m",
            "512Mi",
            1,
            1,
            new(30, 30, 30, 30, 30, 120));
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: null!);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore: null!,
            leaseStore: null!,
            provider,
            options,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager);

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ProvisionAsync(
                new CurrentCallerRequest("validated.jwt.token", "tenant-a"),
                "project-a",
                "run-a",
                "environment-a",
                new SandboxProvisionApiRequest(
                    "workspace-a",
                    1,
                    0,
                    "/workspace/agentweaver/project",
                    ReadOnly: false,
                    NetworkPolicyGeneration: 1,
                    IdempotencyKey: "provision-a"),
                CancellationToken.None));

        Assert.Equal("project_write_not_authorized", exception.Code);
        Assert.Equal(0, projects.RunSelectionCalls);
        Assert.Equal(0, provider.ProvisionCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderAttemptReadRechecksAuthorityAfterStoreRead(bool changeAuthorityDuringRead)
    {
        var (manager, projects, leaseStore, fence, operationId) = CreateAuthorityReadManager();
        if (changeAuthorityDuringRead)
            leaseStore.DuringProviderAttemptRead = () => projects.ChangeMembershipRevision();

        if (changeAuthorityDuringRead)
        {
            var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
                manager.GetProviderAttemptsAsync(
                    Caller(), "project-a", "run-a", "environment-a", operationId, CancellationToken.None));

            Assert.Equal("authorization_changed", exception.Code);
        }
        else
        {
            var response = await manager.GetProviderAttemptsAsync(
                Caller(), "project-a", "run-a", "environment-a", operationId, CancellationToken.None);

            Assert.Equal(1, response.ContractVersion);
            Assert.Equal(fence, response.CurrentFence);
            Assert.Equal(operationId, response.OperationId);
            Assert.Empty(response.Attempts);
        }

        Assert.Equal(1, leaseStore.ProviderAttemptReadCalls);
        Assert.Equal(5, projects.AuthorizationReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateCleanupReadRechecksAuthorityAfterStoreRead(bool changeAuthorityDuringRead)
    {
        var (manager, projects, leaseStore, fence, _) = CreateAuthorityReadManager();
        if (changeAuthorityDuringRead)
            leaseStore.DuringLateCleanupRead = () => projects.ChangeMembershipRevision();

        if (changeAuthorityDuringRead)
        {
            var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
                manager.GetLateResourceCleanupsAsync(
                    Caller(), "project-a", "run-a", "environment-a", CancellationToken.None));

            Assert.Equal("authorization_changed", exception.Code);
        }
        else
        {
            var response = await manager.GetLateResourceCleanupsAsync(
                Caller(), "project-a", "run-a", "environment-a", CancellationToken.None);

            Assert.Equal(1, response.ContractVersion);
            Assert.Equal(fence, response.CurrentFence);
            Assert.Empty(response.Items);
            Assert.False(response.HasMore);
        }

        Assert.Equal(1, leaseStore.LateCleanupReadCalls);
        Assert.Equal(5, projects.AuthorizationReads);
    }

    [Fact]
    public async Task LateCleanupFromOlderLifecycleGenerationDoesNotReadOrDetachReplacementWorkspace()
    {
        var (manager, _, leaseStore, lifecycleStore, provider, currentFence, sourceFence) =
            CreateLateCleanupManager();

        var result = await manager.ReconcileAsync(
            Caller(), "project-a", "run-a", "environment-a", null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(sourceFence, leaseStore.ProviderAttemptRequest!.Fence);
        Assert.Equal(currentFence, leaseStore.ProviderAttemptAuthorizationFence);
        Assert.Equal(SandboxProviderEffectKind.LateResourceRelease,
            leaseStore.ProviderAttemptRequest.EffectKind);
        Assert.Equal(1, provider.ReleaseCalls);
        Assert.Equal(1, leaseStore.CompletedCleanupCalls);
        Assert.Equal(0, lifecycleStore.WorkspaceReadCalls);
    }

    [Fact]
    public async Task LateCleanupRechecksAuthorityAfterProviderAttemptReservation()
    {
        var (manager, projects, leaseStore, _, provider, _, _) = CreateLateCleanupManager();
        leaseStore.DuringProviderAttemptReservation = projects.ChangeMembershipRevision;

        var exception = await Assert.ThrowsAsync<ProjectsConfigApiException>(() =>
            manager.ReconcileAsync(
                Caller(), "project-a", "run-a", "environment-a", null, CancellationToken.None));

        Assert.Equal("authorization_changed", exception.Code);
        Assert.Equal(0, provider.ReleaseCalls);
        Assert.Equal(SandboxProviderAttemptState.FailedNoEffect, leaseStore.RecordedAttempt!.State);
        Assert.Equal("late_cleanup_precondition_failed", leaseStore.RecordedAttempt.FailureCode);
        Assert.False(leaseStore.RecordedEffectMayHaveApplied);
        Assert.Equal(0, leaseStore.CompletedCleanupCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UncertainProvisionCleanupStaysPendingWhenOneLateResourceCompletesAndAnotherRemains(
        bool completeLateResourceOnFirstCleanup)
    {
        var (manager, leaseStore, provider, projection, _) = CreateCoordinatorLateCleanupManager();
        leaseStore.ClaimOneRemainingLateCleanup = completeLateResourceOnFirstCleanup;
        provider.AllowRelease = completeLateResourceOnFirstCleanup;

        var result = await manager.AbandonCoordinatorFirstStartAsync(
            Caller(), projection, placement: null, _ => Task.CompletedTask, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(SandboxLeaseState.Releasing, result.State);
        Assert.True(leaseStore.CompetingWorkerOwnsLateCleanup);
        Assert.Equal(1, leaseStore.OutstandingCheckCalls);
        Assert.Equal(completeLateResourceOnFirstCleanup ? 1 : 0, provider.ReleaseCalls);

        if (!completeLateResourceOnFirstCleanup)
        {
            leaseStore.ClaimOneRemainingLateCleanup = true;
            provider.AllowRelease = true;
            var retry = await manager.AbandonCoordinatorFirstStartAsync(
                Caller(), projection, placement: null, _ => Task.CompletedTask, CancellationToken.None);

            Assert.NotNull(retry);
            Assert.Equal(SandboxLeaseState.Releasing, retry.State);
            Assert.Equal(2, leaseStore.OutstandingCheckCalls);
        }
        Assert.Equal(1, provider.ReleaseCalls);
        Assert.Equal(1, leaseStore.CompletedLateCleanupCalls);
        Assert.Equal(1, leaseStore.OutstandingLateCleanupCount);
        Assert.Equal(
            SandboxProviderAttemptRequest.Fingerprint("late-cleanup-1"),
            Assert.Single(leaseStore.OutstandingLateCleanupFingerprints));
    }

    [Fact]
    public async Task FailedBootstrapRetirementWaitsForLateProvisionCleanupReceiptAfterRelease()
    {
        var (manager, leaseStore, provider, _, request) =
            CreateCoordinatorLateCleanupManager(failedBootstrapRetirement: true);
        var released = await manager.RetireCoordinatorFirstStartAsync(
            Caller(), request, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, released.State);
        Assert.Equal(0, leaseStore.OutstandingLateCleanupCount);

        await leaseStore.CompleteLateProvisionAfterRetirementAsync();
        var pending = await manager.RetireCoordinatorFirstStartAsync(
            Caller(), request, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Releasing, pending.State);
        Assert.Equal(request.Placement.SandboxResource, pending.Resource);
        Assert.Equal(1, leaseStore.OutstandingLateCleanupCount);
        Assert.Equal(0, provider.ReleaseCalls);

        leaseStore.ClaimOneRemainingLateCleanup = true;
        provider.AllowRelease = true;
        var completed = await manager.RetireCoordinatorFirstStartAsync(
            Caller(), request, _ => Task.CompletedTask, CancellationToken.None);
        Assert.Equal(SandboxLeaseState.Released, completed.State);
        Assert.Equal(request.Placement.SandboxResource, completed.Resource);
        Assert.Equal(0, leaseStore.OutstandingLateCleanupCount);
        Assert.Equal(1, leaseStore.CompletedLateCleanupCalls);
        Assert.Equal(1, provider.ReleaseCalls);
    }

    private static (
        EnvironmentSandboxManager Manager,
        CompetingLateCleanupLeaseStore LeaseStore,
        RecoveredSandboxProvider Provider,
        RuntimeRunStartCleanupProjection Projection,
        RuntimeRunStartRetirementRequest RetirementRequest) CreateCoordinatorLateCleanupManager(
            bool failedBootstrapRetirement = false)
    {
        var rootSessionId = "root-session-a";
        var acceptedSelectionHash = new string('a', 64);
        var bootstrapOperationId = Guid.NewGuid();
        var intentId = Guid.NewGuid();
        var environmentId = RuntimeRunStartKeys.EnvironmentId(
            "tenant-a", "project-a", "run-a", rootSessionId, acceptedSelectionHash);
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", environmentId);
        var fence = new EnvironmentGenerationFence(owner, 2);
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var baseLease = CreateCurrentLease(fence, now, "sandbox-uid");
        var sandboxKey = RuntimeRunStartKeys.SandboxIdempotencyKey(
            "tenant-a", "project-a", "run-a", rootSessionId, acceptedSelectionHash, bootstrapOperationId);
        var volumeId = RuntimeRunStartKeys.WorkspaceVolumeId(
            "tenant-a", "project-a", "run-a", rootSessionId, acceptedSelectionHash);
        var plannedResource = SandboxResourceIdentity.CreatePlannedReference(
            baseLease.ProvisionIntent.ProviderId,
            baseLease.ProvisionIntent.OptionsRevision,
            fence,
            baseLease.ResourceGeneration,
            baseLease.ProviderFencingGeneration,
            baseLease.OperationId);
        var provisionedResource = baseLease.ProvisionedResource! with { Resource = plannedResource };
        var storageResource = new ProviderResourceRef(
            ProviderSeam.Storage, "storage-provider", "storage-claim", 1);
        var selection = EnvironmentEgressManagerTests.Selection() with
        {
            ProjectId = owner.ProjectId,
            RunId = owner.RunId
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var provisionRequest = new SandboxProvisionApiRequest(
            volumeId, 1, 0, "/workspace", ReadOnly: false, NetworkPolicyGeneration: 1, IdempotencyKey: sandboxKey);
        var negotiation = new WorkspaceVolumeAttachmentNegotiation(
            owner.ProjectId,
            owner.EnvironmentId,
            owner.RunId,
            new WorkspaceVolumeReference(owner.ProjectId, volumeId, 1),
            plannedResource,
            storageResource,
            fence,
            0,
            "/workspace",
            ReadOnly: false,
            Protocol: WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        var recovery = new EnvironmentSandboxManager.SandboxProviderRecoveryIntent(
            1,
            provisionRequest,
            new SandboxWorkspaceAttachment(
                negotiation, JsonSerializer.SerializeToElement(new { }, jsonOptions)),
            ImmutableDictionary<string, string>.Empty,
            WorkspaceAttachmentTransitionRevision: 2);
        var lease = baseLease with
        {
            State = failedBootstrapRetirement ? SandboxLeaseState.Released : SandboxLeaseState.Releasing,
            ProvisionIntent = baseLease.ProvisionIntent with
            {
                SelectionSnapshot = JsonSerializer.SerializeToElement(selection, jsonOptions),
                ProviderRequest = JsonSerializer.SerializeToElement(recovery, jsonOptions)
            },
            ProvisionedResource = failedBootstrapRetirement ? provisionedResource : null,
            CurrentFencingGeneration = baseLease.ProviderFencingGeneration + 1,
            RetirementReason = SandboxRetirementReason.AuthorizedAbandon,
            ReleaseIdempotencyKey = failedBootstrapRetirement
                ? $"coordinator-bootstrap-retire-{bootstrapOperationId:N}"
                : $"coordinator-release-{intentId:N}",
            IsCurrent = false,
            ProviderRequestFingerprint = SandboxProviderAttemptRequest.Fingerprint("uncertain-provision")
        };
        var lifecycleStore = failedBootstrapRetirement
            ? new PartialReleaseLifecycleStore(fence, negotiation.Volume, storageResource)
            : new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
        var lateResource = failedBootstrapRetirement
            ? provisionedResource with
            {
                Resource = provisionedResource.Resource with { ResourceId = "sandbox-late-uid" }
            }
            : provisionedResource;
        var leaseStore = new CompetingLateCleanupLeaseStore(lease, lateResource, sandboxKey);
        var authorization = new ProjectAuthorizationContextResponse(
            1,
            "https://projects.example",
            "actor-a",
            owner.TenantId,
            3,
            failedBootstrapRetirement ? owner.ProjectId : null,
            failedBootstrapRetirement ? owner.RunId : null,
            [
                new(
                    ProjectAuthorityResourceType.Project,
                    owner.ProjectId,
                    [new(failedBootstrapRetirement
                        ? ProjectAuthorizationPermission.ReadRunSelection
                        : ProjectAuthorizationPermission.CancelRun, 1)])
            ]);
        var projects = new MutableProjectsConfigClient(authorization, selection);
        var provider = new RecoveredSandboxProvider(lease, lateResource, now);
        var egressManager = new EnvironmentEgressManager(
            projects, cilium: null!, providerOptions: null!, lifecycleStore);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore,
            leaseStore,
            provider,
            sandboxOptions: null!,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager);
        var projection = new RuntimeRunStartCleanupProjection(
            1,
            owner.TenantId,
            owner.ProjectId,
            owner.RunId,
            rootSessionId,
            acceptedSelectionHash,
            "cancelled",
            2,
            intentId,
            1,
            1,
            bootstrapOperationId,
            environmentId,
            "cleanup_pending",
            JsonSerializer.SerializeToElement(new { }, jsonOptions));

        var runtimeOwner = new RuntimeOwnerContext(
            1, authorization.Issuer, authorization.ActorId, owner.TenantId,
            owner.ProjectId, owner.RunId, rootSessionId, RuntimeActionContract.CoordinatorRootStepId,
            "platform-model", "first-turn", selection.ProjectRevision,
            selection.ProjectConfigurationRevision, selection.PlatformRuntimeRevision,
            selection.ContextRevision, acceptedSelectionHash, 1, 0, 1, 1);
        var placement = new RuntimeRunStartPlacementReceipt(
            1, environmentId, "runtime-profile", volumeId, sandboxKey, 1, 0, 1,
            lease.OperationId, plannedResource, fence.LifecycleGeneration, baseLease.LeaseRevision,
            lease.ProviderFencingGeneration, lease.ProviderFencingGeneration, now.AddMinutes(10));
        var retirement = new RuntimeRunStartRetirementRequest(
            1, runtimeOwner, bootstrapOperationId, Guid.NewGuid(), 1, placement);
        return (manager, leaseStore, provider, projection, retirement);
    }

    [Theory]
    [InlineData(true, SandboxLeaseState.Releasing)]
    [InlineData(false, SandboxLeaseState.Released)]
    public async Task PartialReleaseWaitsForOutstandingExactLateCleanup(
        bool outstanding, SandboxLeaseState expected)
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 1);
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var original = CreateCurrentLease(fence, now, "sandbox-uid");
        var planned = SandboxResourceIdentity.CreatePlannedReference(
            original.ProvisionIntent.ProviderId, original.ProvisionIntent.OptionsRevision,
            fence, original.ResourceGeneration, original.ProviderFencingGeneration, original.OperationId);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "storage-provider", "storage-claim", 1);
        var volume = new WorkspaceVolumeReference(owner.ProjectId, "workspace", 1);
        var negotiation = new WorkspaceVolumeAttachmentNegotiation(
            owner.ProjectId, owner.EnvironmentId, owner.RunId, volume, planned, storage, fence,
            0, "/workspace", false, WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);
        var recovery = new EnvironmentSandboxManager.SandboxProviderRecoveryIntent(
            1, new SandboxProvisionApiRequest("workspace", 1, 0, "/workspace", false, 1, "provision"),
            new SandboxWorkspaceAttachment(negotiation, JsonSerializer.SerializeToElement(new { })),
            ImmutableDictionary<string, string>.Empty, 2);
        var lease = original with
        {
            State = SandboxLeaseState.Releasing,
            ProvisionedResource = null,
            CurrentFencingGeneration = original.ProviderFencingGeneration + 1,
            RetirementReason = SandboxRetirementReason.AuthorizedAbandon,
            ReleaseIdempotencyKey = "release",
            ProviderRequestFingerprint = SandboxProviderAttemptRequest.Fingerprint("uncertain-provision"),
            ProvisionIntent = original.ProvisionIntent with
            {
                ProviderRequest = JsonSerializer.SerializeToElement(recovery, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            }
        };
        var lifecycle = new PartialReleaseLifecycleStore(fence, volume, storage);
        var leases = new PartialReleaseLeaseStore(lease, outstanding);
        var provider = new PartialReleaseProvider();
        var projects = new MutableProjectsConfigClient(CreateReadAuthorization(), EnvironmentEgressManagerTests.Selection());
        var egress = new EnvironmentEgressManager(projects, null!, null!, lifecycle);
        var manager = new EnvironmentSandboxManager(projects, lifecycle, leases, provider, null!, null!, null!, egress);

        var result = await manager.ReconcileAsync(
            Caller(), owner.ProjectId, owner.RunId, owner.EnvironmentId, null, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expected, result.State);
        Assert.Equal(1, provider.PartialReleaseCalls);
        Assert.Equal(1, leases.OutstandingCheckCalls);
        Assert.True(leases.Completed);
    }

    private static (
        EnvironmentSandboxManager Manager,
        MutableProjectsConfigClient Projects,
        LateCleanupLeaseStore LeaseStore,
        WorkspaceAccessTrackingLifecycleStore LifecycleStore,
        RecordingSandboxProvider Provider,
        EnvironmentGenerationFence CurrentFence,
        EnvironmentGenerationFence SourceFence) CreateLateCleanupManager()
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var currentFence = new EnvironmentGenerationFence(owner, 2);
        var sourceFence = new EnvironmentGenerationFence(owner, 1);
        var now = DateTimeOffset.Parse("2026-10-06T12:00:00Z");
        var sourceLease = CreateCurrentLease(sourceFence, now, "old-sandbox") with
        {
            State = SandboxLeaseState.Released,
            IsCurrent = false,
            LeaseRevision = 2,
            LeaseExpiresAt = null
        };
        var cleanup = new SandboxLateResourceCleanupClaim(
            sourceLease,
            sourceLease.ProvisionedResource!,
            "late-cleanup-1",
            Guid.NewGuid(),
            now.AddMinutes(1))
        {
            ResourceFingerprint = SandboxProviderAttemptRequest.Fingerprint("late-cleanup")
        }.Validate();
        var projects = new MutableProjectsConfigClient(
            CreateReadAuthorization(),
            EnvironmentEgressManagerTests.Selection());
        var lifecycleStore = new WorkspaceAccessTrackingLifecycleStore(currentFence);
        var leaseStore = new LateCleanupLeaseStore(currentFence, cleanup);
        var provider = new RecordingSandboxProvider();
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: lifecycleStore);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore,
            leaseStore,
            provider,
            sandboxOptions: null!,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager: egressManager);

        return (manager, projects, leaseStore, lifecycleStore, provider, currentFence, sourceFence);
    }

    private static (
        EnvironmentSandboxManager Manager,
        MutableProjectsConfigClient Projects,
        AuthorityReadLeaseStore LeaseStore,
        EnvironmentGenerationFence Fence,
        Guid OperationId) CreateAuthorityReadManager()
    {
        var owner = new EnvironmentOwnerIdentity("tenant-a", "project-a", "run-a", "environment-a");
        var fence = new EnvironmentGenerationFence(owner, 1);
        var projects = new MutableProjectsConfigClient(
            CreateReadAuthorization(),
            EnvironmentEgressManagerTests.Selection());
        var lifecycleStore = new EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence);
        var leaseStore = new AuthorityReadLeaseStore(
            CreateCurrentLease(fence, DateTimeOffset.Parse("2026-10-06T12:00:00Z"), "sandbox-uid"));
        var egressManager = new EnvironmentEgressManager(
            projects,
            cilium: null!,
            providerOptions: null!,
            lifecycleStore: lifecycleStore);
        var manager = new EnvironmentSandboxManager(
            projects,
            lifecycleStore,
            leaseStore,
            sandboxProvider: null!,
            sandboxOptions: null!,
            kubernetesClient: null!,
            ciliumOptions: null!,
            egressManager: egressManager);

        return (manager, projects, leaseStore, fence, Guid.NewGuid());
    }

    private static ProjectAuthorizationContextResponse CreateReadAuthorization() =>
        new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://projects.example",
            "actor-a",
            "tenant-a",
            3,
            "project-a",
            "run-a",
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    [
                        new(ProjectAuthorizationPermission.WriteProjects, 1),
                        new(ProjectAuthorizationPermission.ReadProjects, 1),
                        new(ProjectAuthorizationPermission.ReadRunSelection, 1)
                    ])
            ]);

    private static CurrentCallerRequest Caller() => new("validated.jwt.token", "tenant-a");

    private static SandboxLeaseSnapshot CreateCurrentLease(
        EnvironmentGenerationFence fence,
        DateTimeOffset now,
        string resourceId)
    {
        using var json = JsonDocument.Parse("{}");
        var binding = new SandboxProviderBindingSnapshot(
            "agent-sandbox",
            "1.0.0",
            1,
            "options-1",
            json.RootElement.Clone(),
            json.RootElement.Clone());
        var intent = new SandboxLeaseProvisionIntent(
            "agent-sandbox",
            "1.0.0",
            1,
            "options-1",
            json.RootElement.Clone(),
            json.RootElement.Clone(),
            json.RootElement.Clone());
        var resource = new SandboxProvisionedResource(
            new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", resourceId, 1),
            new SandboxEndpointReference(Guid.NewGuid()),
            new SandboxPlacementReference("placement-a"),
            ImmutableHashSet.Create(StringComparer.Ordinal, SandboxCapabilities.VmIsolation),
            [],
            binding);
        return new SandboxLeaseSnapshot(
            fence,
            1,
            1,
            1,
            Guid.NewGuid(),
            SandboxLeaseState.Active,
            intent,
            resource,
            null,
            null,
            null,
            null,
            null,
            IsCurrent: true,
            now)
        {
            LeaseRevision = 1,
            LeaseExpiresAt = now.AddMinutes(1),
            CreatedAt = now.AddMinutes(-1)
        };
    }

    private sealed class MutableProjectsConfigClient(
        ProjectAuthorizationContextResponse authorization,
        EffectiveNetworkPolicySelection selection) : IProjectsConfigClient
    {
        public ProjectAuthorizationContextResponse CurrentAuthorization { get; private set; } = authorization;
        public int AuthorizationReads { get; private set; }

        public void ChangeMembershipRevision() =>
            CurrentAuthorization = CurrentAuthorization with
            {
                MembershipRevision = CurrentAuthorization.MembershipRevision + 1
            };

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken)
        {
            AuthorizationReads++;
            return Task.FromResult(CurrentAuthorization);
        }

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken) =>
            Task.FromResult(selection);
    }

    private sealed class AuthorityReadLeaseStore : RuntimeResumeBootstrapTests.SourceLeases
    {
        private readonly EnvironmentGenerationFence _fence;

        public AuthorityReadLeaseStore(SandboxLeaseSnapshot lease) : base(lease) =>
            _fence = lease.Fence;

        public int ProviderAttemptReadCalls { get; private set; }
        public int LateCleanupReadCalls { get; private set; }
        public Action? DuringProviderAttemptRead { get; set; }
        public Action? DuringLateCleanupRead { get; set; }

        public override Task<ImmutableArray<SandboxProviderAttemptSnapshot>> GetProviderAttemptsAsync(
            EnvironmentGenerationFence currentFence,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_fence, currentFence);
            Assert.NotEqual(Guid.Empty, operationId);
            ProviderAttemptReadCalls++;
            DuringProviderAttemptRead?.Invoke();
            return Task.FromResult(ImmutableArray<SandboxProviderAttemptSnapshot>.Empty);
        }

        public override Task<(ImmutableArray<SandboxLateResourceCleanupRecord> Items, bool HasMore)>
            GetLateResourceCleanupRecordsAsync(
                EnvironmentGenerationFence currentFence,
                CancellationToken cancellationToken)
        {
            Assert.Equal(_fence, currentFence);
            LateCleanupReadCalls++;
            DuringLateCleanupRead?.Invoke();
            return Task.FromResult((ImmutableArray<SandboxLateResourceCleanupRecord>.Empty, false));
        }
    }

    private sealed class LateCleanupLeaseStore(
        EnvironmentGenerationFence currentFence,
        SandboxLateResourceCleanupClaim cleanup) : RuntimeResumeBootstrapTests.SourceLeases(cleanup.Lease)
    {
        private SandboxProviderAttemptSnapshot? _attempt;

        public SandboxProviderAttemptRequest? ProviderAttemptRequest { get; private set; }
        public EnvironmentGenerationFence? ProviderAttemptAuthorizationFence { get; private set; }
        public Action? DuringProviderAttemptReservation { get; set; }
        public SandboxProviderAttemptSnapshot? RecordedAttempt { get; private set; }
        public bool RecordedEffectMayHaveApplied { get; private set; }
        public int CompletedCleanupCalls { get; private set; }

        public override Task<SandboxLeaseSnapshot?> GetCurrentAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentFence, fence);
            return Task.FromResult<SandboxLeaseSnapshot?>(null);
        }

        public override Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentFence, fence);
            return Task.FromResult<SandboxLateResourceCleanupClaim?>(cleanup);
        }

        public override Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken,
            Guid? operationId,
            long? resourceGeneration)
        {
            Assert.Equal(currentFence, fence);
            if (operationId is { } expectedOperationId && expectedOperationId != cleanup.Lease.OperationId ||
                resourceGeneration is { } expectedGeneration &&
                    expectedGeneration != cleanup.Lease.ResourceGeneration)
                return Task.FromResult<SandboxLateResourceCleanupClaim?>(null);
            return Task.FromResult<SandboxLateResourceCleanupClaim?>(cleanup);
        }

        public override Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest request,
            EnvironmentGenerationFence authorizationFence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(currentFence, authorizationFence);
            Assert.Equal(cleanup.Lease.Fence, request.Fence);
            ProviderAttemptRequest = request;
            ProviderAttemptAuthorizationFence = authorizationFence;
            DuringProviderAttemptReservation?.Invoke();
            _attempt = new SandboxProviderAttemptSnapshot(
                Guid.NewGuid(),
                request,
                SandboxProviderAttemptState.InProgress,
                Outcome: null,
                FailureCode: null,
                DateTimeOffset.UtcNow,
                OutcomeRecordedAt: null,
                ResolvedAt: null).Validate();
            return Task.FromResult(new SandboxProviderAttemptReservation(_attempt, Replayed: false));
        }

        public override Task<SandboxProviderAttemptSnapshot> RecordProviderAttemptOutcomeAsync(
            Guid attemptId,
            EnvironmentGenerationFence fence,
            JsonElement outcome,
            bool effectMayHaveApplied,
            string? failureCode,
            CancellationToken cancellationToken)
        {
            Assert.Equal(cleanup.Lease.Fence, fence);
            Assert.Equal(_attempt!.AttemptId, attemptId);
            RecordedEffectMayHaveApplied = effectMayHaveApplied;
            var recordedAt = DateTimeOffset.UtcNow;
            RecordedAttempt = _attempt with
            {
                State = effectMayHaveApplied
                    ? SandboxProviderAttemptState.ResultRecorded
                    : SandboxProviderAttemptState.FailedNoEffect,
                Outcome = outcome.Clone(),
                FailureCode = failureCode,
                OutcomeRecordedAt = recordedAt,
                ResolvedAt = effectMayHaveApplied ? null : recordedAt
            };
            _attempt = RecordedAttempt.Validate();
            return Task.FromResult(_attempt);
        }

        public override Task CompleteLateResourceCleanupAsync(
            EnvironmentGenerationFence authorizationFence,
            SandboxLateResourceCleanupClaim claim,
            SandboxReleaseReceipt receipt,
            CancellationToken cancellationToken,
            Guid? providerAttemptId = null)
        {
            Assert.Equal(currentFence, authorizationFence);
            Assert.Equal(cleanup, claim);
            Assert.Equal(_attempt!.AttemptId, providerAttemptId);
            Assert.Equal(SandboxProviderAttemptState.ResultRecorded, _attempt.State);
            Assert.Equal(cleanup.ProvisionedResource.Resource, receipt.Resource);
            Assert.Equal(cleanup.IdempotencyKey, receipt.IdempotencyKey);
            CompletedCleanupCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class PartialReleaseLeaseStore : RuntimeResumeBootstrapTests.SourceLeases, ISandboxLeaseStore
    {
        private readonly SandboxLeaseSnapshot _lease;
        private readonly bool _outstanding;
        private SandboxProviderAttemptSnapshot? _attempt;

        public PartialReleaseLeaseStore(SandboxLeaseSnapshot lease, bool outstanding) : base(lease)
        {
            _lease = lease;
            _outstanding = outstanding;
        }

        public int OutstandingCheckCalls { get; private set; }
        public bool Completed { get; private set; }

        public override Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest request, CancellationToken cancellationToken)
        {
            _attempt = new SandboxProviderAttemptSnapshot(
                Guid.NewGuid(), request, SandboxProviderAttemptState.InProgress,
                null, null, DateTimeOffset.UtcNow, null, null).Validate();
            return Task.FromResult(new SandboxProviderAttemptReservation(_attempt, false));
        }

        public override Task<SandboxProviderAttemptSnapshot> RecordProviderAttemptOutcomeAsync(
            Guid attemptId, EnvironmentGenerationFence fence, JsonElement outcome,
            bool effectMayHaveApplied, string? failureCode, CancellationToken cancellationToken)
        {
            Assert.Equal(_attempt!.AttemptId, attemptId);
            _attempt = _attempt with
            {
                State = SandboxProviderAttemptState.ResultRecorded,
                Outcome = outcome.Clone(),
                OutcomeRecordedAt = DateTimeOffset.UtcNow
            };
            return Task.FromResult(_attempt.Validate());
        }

        public new Task<SandboxLeaseSnapshot> CompletePartialReleaseAsync(
            SandboxPartialReleaseCompletionRequest request, CancellationToken cancellationToken)
        {
            Assert.Equal(_attempt!.AttemptId, request.ProviderAttemptId);
            Completed = true;
            return Task.FromResult(_lease with { State = SandboxLeaseState.Released, IsCurrent = false });
        }

        public Task<bool> HasOutstandingLateResourceCleanupAsync(
            EnvironmentGenerationFence fence, Guid operationId, long resourceGeneration,
            CancellationToken cancellationToken)
        {
            Assert.True(Completed);
            Assert.Equal(_lease.Fence, fence);
            Assert.Equal(_lease.OperationId, operationId);
            Assert.Equal(_lease.ResourceGeneration, resourceGeneration);
            OutstandingCheckCalls++;
            return Task.FromResult(_outstanding);
        }
    }

    private sealed class PartialReleaseLifecycleStore(
        EnvironmentGenerationFence fence, WorkspaceVolumeReference volume, ProviderResourceRef storage) :
        EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence), IEnvironmentLifecycleStore
    {
        private readonly JsonElement _empty = JsonSerializer.SerializeToElement(new { });
        private readonly Guid _operation = Guid.NewGuid();

        private WorkspaceVolumeProviderBindingSnapshot Binding =>
            new(storage.ProviderId, "1.0.0", 1, "options-1", _empty, _empty);

        public override Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, CancellationToken cancellationToken) =>
            Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(new(
                fence, volume.VolumeId, 2, 1, 0, EnvironmentWorkspaceVolumeState.Attached,
                EnvironmentWorkspaceVolumeOperation.Attach, _empty, storage, Binding));

        public Task<WorkspaceVolumeFlushEvidence?> GetWorkspaceVolumeFlushEvidenceAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, long targetTransitionRevision,
            CancellationToken cancellationToken) => Task.FromResult<WorkspaceVolumeFlushEvidence?>(null);

        public override Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, long expectedTransitionRevision,
            long expectedResourceGeneration, long expectedDataGeneration, string idempotencyKey,
            CancellationToken cancellationToken) => Task.FromResult(new EnvironmentWorkspaceVolumeTransitionReservation(
                _operation, fence, volume.VolumeId, 2, 1, 0, storage, Binding, 3, 1, 0,
                EnvironmentWorkspaceVolumeState.Bound, EnvironmentWorkspaceVolumeOperation.Detach,
                EnvironmentWorkspaceVolumeTransitionState.Reserved, false));

        public override Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
            Guid operationId, EnvironmentGenerationFence environmentFence, bool effectMayHaveApplied,
            ProviderResourceRef? providerResource, bool effectVerified, CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null) =>
            Task.FromResult(new EnvironmentWorkspaceVolumeTransitionResult(
                _operation, fence, volume.VolumeId, 2, 1, 0, storage, 3, 1, 0, storage,
                EnvironmentWorkspaceVolumeState.Bound, EnvironmentWorkspaceVolumeOperation.Detach,
                EnvironmentWorkspaceVolumeTransitionState.Completed, false));
    }

    private sealed class PartialReleaseProvider : ISandboxProvider
    {
        public int PartialReleaseCalls { get; private set; }
        public Task<SandboxProvisionedResource> ProvisionAsync(SandboxProvisionRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SandboxObservation> DescribeAsync(SandboxDescribeRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(SandboxListOwnedRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SandboxObservation>>([]);
        public Task<SandboxReleaseReceipt> ReleaseAsync(SandboxReleaseRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
            SandboxPartialReleaseRequest request, CancellationToken cancellationToken = default)
        {
            PartialReleaseCalls++;
            var lease = request.Lease;
            var planned = SandboxResourceIdentity.CreatePlannedReference(
                lease.ProvisionIntent.ProviderId, lease.ProvisionIntent.OptionsRevision, lease.Fence,
                lease.ResourceGeneration, lease.ProviderFencingGeneration, lease.OperationId);
            var suffix = planned.ResourceId["aw-claim-".Length..];
            return Task.FromResult(new SandboxPartialReleaseReceipt(
                lease.OperationId, lease.ResourceGeneration, lease.ProviderFencingGeneration,
                lease.CurrentFencingGeneration, lease.LeaseRevision, lease.ReleaseIdempotencyKey!,
                "sandbox-system", planned.ResourceId, $"aw-template-{suffix}", null,
                $"aw-pool-{suffix}", null, true, true, true, SandboxReleaseDisposition.KnownOwnedAbsent));
        }
    }

    private sealed class CompetingLateCleanupLeaseStore :
        RuntimeResumeBootstrapTests.SourceLeases, ISandboxLeaseStore
    {
        private readonly SandboxProvisionedResource _provisionedResource;
        private readonly SandboxProviderAttemptSnapshot _provisionAttempt;
        private readonly string _idempotencyKey;
        private SandboxLeaseSnapshot _lease;

        public CompetingLateCleanupLeaseStore(
            SandboxLeaseSnapshot lease,
            SandboxProvisionedResource provisionedResource,
            string idempotencyKey) : base(lease)
        {
            _lease = lease;
            _provisionedResource = provisionedResource;
            _idempotencyKey = idempotencyKey;
            var request = SandboxProviderAttemptRequest.ForProvision(lease);
            _provisionAttempt = new SandboxProviderAttemptSnapshot(
                Guid.NewGuid(),
                request,
                SandboxProviderAttemptState.ResultRecorded,
                JsonSerializer.SerializeToElement(new { effectMayHaveApplied = true }),
                "provider_response_lost",
                lease.CreatedAt!.Value,
                lease.CreatedAt.Value,
                ResolvedAt: null).Validate();
        }

        public bool CompetingWorkerOwnsLateCleanup { get; private set; }
        public int OutstandingCheckCalls { get; private set; }
        public bool ClaimOneRemainingLateCleanup { get; set; }
        public int OutstandingLateCleanupCount => _outstandingLateCleanupFingerprints.Count;
        public IReadOnlyCollection<string> OutstandingLateCleanupFingerprints =>
            _outstandingLateCleanupFingerprints;
        public int CompletedLateCleanupCalls { get; private set; }
        private SandboxLateResourceCleanupClaim? _lateCleanup;
        private SandboxProviderAttemptSnapshot? _lateAttempt;
        private readonly HashSet<string> _outstandingLateCleanupFingerprints = [];

        public async Task CompleteLateProvisionAfterRetirementAsync()
        {
            Assert.Equal(SandboxLeaseState.Released, _lease.State);
            Assert.NotEqual(_lease.ProvisionedResource!.Resource, _provisionedResource.Resource);
            await CompleteProvisionAsync(
                _lease.OperationId, _lease.Fence, _provisionedResource,
                effectMayHaveApplied: true, CancellationToken.None,
                providerAttemptId: _provisionAttempt.AttemptId);
        }

        public new Task<SandboxLeaseSnapshot?> GetAsync(
            EnvironmentGenerationFence fence, long resourceGeneration, CancellationToken cancellationToken)
        {
            Assert.Equal(_lease.Fence, fence);
            Assert.Equal(_lease.ResourceGeneration, resourceGeneration);
            return Task.FromResult<SandboxLeaseSnapshot?>(_lease);
        }

        public Task<SandboxLeaseSnapshot?> GetByProvisionIdempotencyKeyAsync(
            EnvironmentGenerationFence fence,
            string idempotencyKey,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_lease.Fence, fence);
            Assert.Equal(_idempotencyKey, idempotencyKey);
            return Task.FromResult<SandboxLeaseSnapshot?>(_lease);
        }

        public override Task<SandboxLeaseSnapshot?> GetCurrentAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_lease.Fence, fence);
            return Task.FromResult<SandboxLeaseSnapshot?>(_lease);
        }

        public override Task<ImmutableArray<SandboxProviderAttemptSnapshot>> GetProviderAttemptsAsync(
            EnvironmentGenerationFence currentFence,
            Guid operationId,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_lease.Fence, currentFence);
            Assert.Equal(_lease.OperationId, operationId);
            return Task.FromResult(ImmutableArray.Create(_provisionAttempt));
        }

        public override Task<SandboxLeaseSnapshot> CompleteProvisionAsync(
            Guid operationId,
            EnvironmentGenerationFence fence,
            SandboxProvisionedResource? provisionedResource,
            bool effectMayHaveApplied,
            CancellationToken cancellationToken,
            Guid? providerAttemptId = null,
            string? providerErrorCode = null)
        {
            Assert.Equal(_lease.OperationId, operationId);
            Assert.Equal(_lease.Fence, fence);
            Assert.Equal(_provisionAttempt.AttemptId, providerAttemptId);
            Assert.Equal(_provisionedResource, provisionedResource);
            Assert.True(effectMayHaveApplied);
            var alreadyReleased = _lease.State == SandboxLeaseState.Released;
            if (!alreadyReleased)
            {
                _lease = _lease with
                {
                    State = SandboxLeaseState.Released,
                    ProvisionedResource = _provisionedResource,
                    LeaseRevision = _lease.LeaseRevision + 1,
                    LeaseExpiresAt = null
                };
            }
            CompetingWorkerOwnsLateCleanup = true;
            if (!alreadyReleased)
                _outstandingLateCleanupFingerprints.Add(
                    SandboxProviderAttemptRequest.Fingerprint("late-cleanup-1"));
            _outstandingLateCleanupFingerprints.Add(
                SandboxProviderAttemptRequest.Fingerprint("late-cleanup-2"));
            return Task.FromResult(_lease);
        }

        public override Task<SandboxLateResourceCleanupClaim?> ClaimNextLateResourceCleanupAsync(
            EnvironmentGenerationFence fence,
            CancellationToken cancellationToken,
            Guid? operationId,
            long? resourceGeneration)
        {
            Assert.Equal(_lease.Fence, fence);
            Assert.Equal(_lease.OperationId, operationId);
            Assert.Equal(_lease.ResourceGeneration, resourceGeneration);
            if (!ClaimOneRemainingLateCleanup ||
                !_outstandingLateCleanupFingerprints.Contains(
                    SandboxProviderAttemptRequest.Fingerprint("late-cleanup-2")))
                return Task.FromResult<SandboxLateResourceCleanupClaim?>(null);
            var sameFence = _lease.ReleaseIdempotencyKey!.StartsWith(
                "coordinator-bootstrap-retire-", StringComparison.Ordinal);
            var sourceFence = sameFence ? _lease.Fence : new EnvironmentGenerationFence(
                _lease.Fence.Owner, _lease.Fence.LifecycleGeneration - 1);
            var sourceLease = _lease with { Fence = sourceFence, IsCurrent = false, LeaseExpiresAt = null };
            var sourceResource = _provisionedResource with
            {
                Resource = sameFence ? _provisionedResource.Resource : SandboxResourceIdentity.CreatePlannedReference(
                    sourceLease.ProvisionIntent.ProviderId,
                    sourceLease.ProvisionIntent.OptionsRevision,
                    sourceFence,
                    sourceLease.ResourceGeneration,
                    sourceLease.ProviderFencingGeneration,
                    sourceLease.OperationId)
            };
            _lateCleanup ??= new SandboxLateResourceCleanupClaim(
                sourceLease,
                sourceResource,
                "late-cleanup-2",
                Guid.NewGuid(),
                _lease.CreatedAt!.Value.AddMinutes(2))
            {
                ResourceFingerprint = SandboxProviderAttemptRequest.Fingerprint("late-cleanup-2")
            }.Validate();
            return Task.FromResult<SandboxLateResourceCleanupClaim?>(_lateCleanup);
        }

        public override Task<SandboxProviderAttemptReservation> BeginProviderAttemptAsync(
            SandboxProviderAttemptRequest request,
            EnvironmentGenerationFence currentFence,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_lease.Fence, currentFence);
            _lateAttempt = new SandboxProviderAttemptSnapshot(
                Guid.NewGuid(),
                request,
                SandboxProviderAttemptState.InProgress,
                Outcome: null,
                FailureCode: null,
                DateTimeOffset.UtcNow,
                OutcomeRecordedAt: null,
                ResolvedAt: null).Validate();
            return Task.FromResult(new SandboxProviderAttemptReservation(_lateAttempt, Replayed: false));
        }

        public override Task<SandboxProviderAttemptSnapshot> RecordProviderAttemptOutcomeAsync(
            Guid attemptId,
            EnvironmentGenerationFence fence,
            JsonElement outcome,
            bool effectMayHaveApplied,
            string? failureCode,
            CancellationToken cancellationToken)
        {
            Assert.Equal(_lateAttempt!.AttemptId, attemptId);
            Assert.Equal(_lateCleanup!.Lease.Fence, fence);
            var recordedAt = DateTimeOffset.UtcNow;
            _lateAttempt = _lateAttempt with
            {
                State = effectMayHaveApplied
                    ? SandboxProviderAttemptState.ResultRecorded
                    : SandboxProviderAttemptState.FailedNoEffect,
                Outcome = outcome.Clone(),
                FailureCode = failureCode,
                OutcomeRecordedAt = recordedAt,
                ResolvedAt = effectMayHaveApplied ? null : recordedAt
            };
            return Task.FromResult(_lateAttempt.Validate());
        }

        public override Task CompleteLateResourceCleanupAsync(
            EnvironmentGenerationFence authorizationFence,
            SandboxLateResourceCleanupClaim claim,
            SandboxReleaseReceipt receipt,
            CancellationToken cancellationToken,
            Guid? providerAttemptId = null)
        {
            Assert.Equal(_lease.Fence, authorizationFence);
            Assert.Equal(_lateCleanup, claim);
            Assert.Equal(_lateAttempt!.AttemptId, providerAttemptId);
            Assert.Equal(SandboxProviderAttemptState.ResultRecorded, _lateAttempt.State);
            Assert.Equal(claim.ProvisionedResource.Resource, receipt.Resource);
            Assert.Equal(claim.IdempotencyKey, receipt.IdempotencyKey);
            Assert.True(_outstandingLateCleanupFingerprints.Remove(claim.ResourceFingerprint));
            CompletedLateCleanupCalls++;
            return Task.CompletedTask;
        }

        public Task<bool> HasOutstandingLateResourceCleanupAsync(
            EnvironmentGenerationFence fence,
            Guid operationId,
            long resourceGeneration,
            CancellationToken cancellationToken)
        {
            OutstandingCheckCalls++;
            Assert.Equal(_lease.Fence, fence);
            Assert.Equal(_lease.OperationId, operationId);
            Assert.Equal(_lease.ResourceGeneration, resourceGeneration);
            return Task.FromResult(OutstandingLateCleanupCount > 0);
        }
    }

    private sealed class RecoveredSandboxProvider(
        SandboxLeaseSnapshot lease,
        SandboxProvisionedResource resource,
        DateTimeOffset observedAt) : ISandboxProvider
    {
        public int ReleaseCalls { get; private set; }
        public bool AllowRelease { get; set; }

        public Task<SandboxProvisionedResource> ProvisionAsync(
            SandboxProvisionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Uncertain provision recovery must not provision again.");

        public Task<SandboxObservation> DescribeAsync(
            SandboxDescribeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Uncertain provision recovery must list owned resources.");

        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
            SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(lease.Fence, request.Fence);
            IReadOnlyList<SandboxObservation> observations =
            [
                new(
                    resource.Resource,
                    SandboxObservedState.Ready,
                    lease.ProviderFencingGeneration,
                    VmIsolationVerified: true,
                    WorkspaceAttachmentVerified: true,
                    VerifiedNetworkGeneration: 1,
                    [
                        new(SandboxStartupPhase.Configured, 1, observedAt),
                        new(SandboxStartupPhase.Ready, 1, observedAt)
                    ],
                    ProvisionOperationId: lease.OperationId,
                    ProvisionedResource: resource)
            ];
            return Task.FromResult(observations);
        }

        public Task<SandboxReleaseReceipt> ReleaseAsync(
            SandboxReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            if (AllowRelease)
                return Task.FromResult(new SandboxReleaseReceipt(
                    request.Resource,
                    request.IdempotencyKey,
                    SandboxReleaseDisposition.Released));
            throw new InvalidOperationException("The competing worker holds the late-resource claim.");
        }

        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
            SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Partial release is not part of this test.");
    }

    private sealed class WorkspaceAccessTrackingLifecycleStore : EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore
    {
        public WorkspaceAccessTrackingLifecycleStore(EnvironmentGenerationFence currentFence)
            : base(currentFence)
        {
        }

        public int WorkspaceReadCalls { get; private set; }

        public override Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence,
            string volumeId,
            CancellationToken cancellationToken)
        {
            WorkspaceReadCalls++;
            return Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(null);
        }
    }

    private sealed class FakeProjectsConfigClient : IProjectsConfigClient
    {
        private static readonly ProjectAuthorizationContextResponse Context = new(
            ProjectAuthorizationContextContract.CurrentVersion,
            "https://projects.example",
            "actor-a",
            "tenant-a",
            3,
            "project-a",
            "run-a",
            [
                new EffectiveProjectAuthorization(
                    ProjectAuthorityResourceType.Project,
                    "project-a",
                    [new ProjectAuthorizationPermissionGrant(ProjectAuthorizationPermission.ReadProjects, 1)])
            ]);

        public int RunSelectionCalls { get; private set; }

        public Task<ProjectAuthorizationContextResponse> GetAuthorizationContextAsync(
            CurrentCallerRequest caller,
            CancellationToken cancellationToken) =>
            Task.FromResult(Context);

        public Task<EffectiveNetworkPolicySelection> GetRunSelectionAsync(
            CurrentCallerRequest caller,
            string projectId,
            string runId,
            CancellationToken cancellationToken)
        {
            RunSelectionCalls++;
            return Task.FromResult(new EffectiveNetworkPolicySelection(
                projectId,
                runId,
                1,
                1,
                1,
                "selection-1",
                [],
                [],
                null,
                [],
                []));
        }
    }

    private sealed class RecordingSandboxProvider : ISandboxProvider
    {
        public int ProvisionCalls { get; private set; }
        public int ReleaseCalls { get; private set; }

        public Task<SandboxProvisionedResource> ProvisionAsync(
            SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            ProvisionCalls++;
            throw new InvalidOperationException("The provider must not be reached without WriteProjects.");
        }

        public Task<SandboxObservation> DescribeAsync(
            SandboxDescribeRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Describe is not part of an unauthorized provision.");

        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(
            SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ListOwned is not part of an unauthorized provision.");

        public Task<SandboxReleaseReceipt> ReleaseAsync(
            SandboxReleaseRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleaseCalls++;
            return Task.FromResult(new SandboxReleaseReceipt(
                request.Resource,
                request.IdempotencyKey,
                SandboxReleaseDisposition.Released));
        }

        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(
            SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Partial release is not part of an unauthorized provision.");
    }
}
