using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Identity;
using Agentweaver.Providers.Sandbox.AgentSandbox;
using Agentweaver.Providers.Storage.AzureFiles;

namespace Agentweaver.Environment.Tests;

public sealed partial class RuntimeResumeBootstrapTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task ResumeAuthorityLossAfterReservationPreservesOnlyEffectsThatActuallyStarted(int denyAt)
    {
        var resume = Create().Resume with { PhaseVersion = 2, RestoredWorkspace = null };
        var fence = new EnvironmentGenerationFence(resume.SourceManifest.EnvironmentFence!.Owner, 2);
        var options = new AgentSandboxOptions(AgentSandboxOptions.CurrentOptionsSchemaVersion,
            "sandbox-options", "agentweaver", "azure-files-csi",
            "ghcr.io/agentweaver/agenthost@sha256:" + new string('a', 64),
            "linux/amd64", 1, "kata-vm", "kata-qemu", "500m", "512Mi", 1, 1,
            new(30, 90, 30, 30, 30, 180));
        var storageOptions = new AzureFilesCsiOptions(1, "storage-options", "agentweaver", "azure-files", 8, 30, 30);
        var selection = EnvironmentEgressManagerTests.Selection();
        selection = selection with
        {
            ProjectId = fence.Owner.ProjectId,
            RunId = fence.Owner.RunId,
            Providers = selection.Providers
                .Add(Exclusive(AgentSandboxProviderMetadata.CreateRegistration(options)))
                .Add(Exclusive(AzureFilesCsiProviderMetadata.CreateRegistration(storageOptions)))
        };
        var authorization = new ProjectAuthorizationContextResponse(1, "https://issuer.test/", "actor",
            fence.Owner.TenantId, 1, fence.Owner.ProjectId, fence.Owner.RunId,
            [new(ProjectAuthorityResourceType.Project, fence.Owner.ProjectId,
                [new(ProjectAuthorizationPermission.ReadRunSelection, 1),
                 new(ProjectAuthorizationPermission.WriteProjects, 1)])]);
        var projects = new EnvironmentEgressManagerTests.FakeProjectsConfigClient(authorization, selection);
        using var handler = new Handler(new { });
        using var http = new HttpClient(handler) { BaseAddress = new("https://kubernetes.test/") };
        var kubernetes = new KubernetesAgentSandboxClient(http);
        var storage = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-1", 1);
        var specification = JsonSerializer.SerializeToElement(new WorkspaceVolumeSpec(
            "workspace", fence.Owner.ProjectId, new(WorkspaceVolumeOwnerKind.Run, fence.Owner.RunId),
            fence.Owner.EnvironmentId, WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeAccessMode.ReadWriteMany, 8, storage.ProviderId, WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Delete, WorkspaceVolumeOwnerDeletionPolicy.Retain, []), JsonOptions);
        var volume = new EnvironmentWorkspaceVolumeSnapshot(fence, "workspace", 6, 1, 8,
            EnvironmentWorkspaceVolumeState.Bound, EnvironmentWorkspaceVolumeOperation.Detach, specification, storage,
            new(storage.ProviderId, "1.0.0", 1, storageOptions.OptionsRevision,
                JsonSerializer.SerializeToElement(storageOptions, JsonOptions),
                JsonSerializer.SerializeToElement(new
                {
                    @namespace = "agentweaver", claimName = "claim-1", claimUid = "claim-uid",
                    clusterIdentity = kubernetes.ClusterIdentity
                }, JsonOptions)));
        var lifecycle = new ProvisionLifecycle(fence, volume);
        var egress = EnvironmentEgressManagerTests.Manager(projects,
            new EnvironmentEgressManagerTests.FakeCiliumPolicyResourceStore(), lifecycle);
        var caller = new CurrentCallerRequest("actor-token", fence.Owner.TenantId);
        await egress.ApplyAndVerifyAsync(caller, new(fence, 1, 0, "network-1"), CancellationToken.None);
        await egress.ApplyAndVerifyAsync(caller, new(fence, 2, 1, "network-2"), CancellationToken.None);
        var template = RuntimeSuspendPlacementReleaseTests.Create().Lease;
        var leases = new ProvisionLeases(template with
        {
            Fence = fence, ResourceGeneration = 2, CurrentFencingGeneration = 3, ProviderFencingGeneration = 3,
            State = SandboxLeaseState.Provisioning, ProvisionedResource = null, RetirementReason = null
        });
        var provider = new ProvisionSandbox(options);
        var manager = new EnvironmentSandboxManager(projects, lifecycle, leases, provider, options,
            kubernetes, EnvironmentEgressManagerTests.Options(), egress);
        var guardCalls = 0;
        var failure = await Assert.ThrowsAsync<RuntimeAuthorizationException>(() => manager.ProvisionResumeAsync(
            caller, new("workspace", 1, 8, "/workspace/agentweaver/workspace", false, 2, "resume-placement"),
            resume, _ =>
            {
                if (++guardCalls == denyAt)
                    throw new RuntimeAuthorizationException("resume_authority_lost");
                return Task.CompletedTask;
            }, CancellationToken.None));
        Assert.Equal("resume_authority_lost", failure.Code);
        Assert.Equal(denyAt, guardCalls);
        Assert.Equal(1, leases.Reservations);
        Assert.Equal(1, leases.Completions);
        Assert.Equal(denyAt >= 4 ? 1 : 0, leases.SavedRequests);
        Assert.Equal(denyAt == 6 ? 1 : 0, provider.Calls);
        Assert.Equal(denyAt == 5 ? 1 : 0, lifecycle.DetachCalls);
        Assert.Equal(denyAt == 6, leases.EffectMayHaveApplied);
        Assert.Equal(denyAt == 6 ? SandboxLeaseState.Active : SandboxLeaseState.Released, leases.Current.State);
        Assert.Equal(denyAt == 6 ? EnvironmentWorkspaceVolumeState.Attached : EnvironmentWorkspaceVolumeState.Bound,
            lifecycle.Current.Phase);
        Assert.Equal(storage, lifecycle.Current.Resource);
        Assert.Equal(8, lifecycle.Current.DataGeneration);
        Assert.Equal(denyAt == 6 ? provider.Resource : null, leases.Current.ProvisionedResource);
        Assert.Null(handler.Path);
    }

    private static EffectiveProviderSelection Exclusive(ProviderRegistration registration)
    {
        var descriptor = registration.Descriptor;
        return new(ProviderCardinality.Exclusive, descriptor.Seam,
            [new(descriptor.Seam, descriptor.Id, descriptor.AdapterVersion.ToString(),
                registration.OptionsSchemaVersion, registration.OptionsRevision, descriptor.Hosting,
                descriptor.AdvertisedCapabilities.ToImmutableArray(), [])]);
    }

    private sealed class ProvisionLeases(SandboxLeaseSnapshot template) : SourceLeases(template)
    {
        public SandboxLeaseSnapshot Current { get; private set; } = template;
        public int Reservations { get; private set; }
        public int SavedRequests { get; private set; }
        public int Completions { get; private set; }
        public bool EffectMayHaveApplied { get; private set; }

        public override Task<SandboxLeaseReservation> ReserveProvisionAsync(EnvironmentGenerationFence fence,
            string idempotencyKey, SandboxLeaseProvisionIntent intent, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.Fence, fence);
            Reservations++;
            Current = Current with { ProvisionIntent = intent };
            return Task.FromResult(new SandboxLeaseReservation(Current, false));
        }

        public override Task<SandboxLeaseSnapshot> SaveProviderRequestAsync(Guid operationId,
            EnvironmentGenerationFence fence, JsonElement providerRequest, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.OperationId, operationId);
            Assert.Equal(Current.Fence, fence);
            SavedRequests++;
            Assert.Equal(2, providerRequest.GetProperty("runtimeResume").GetProperty("phaseVersion").GetInt64());
            Current = Current with { ProvisionIntent = Current.ProvisionIntent with { ProviderRequest = providerRequest } };
            return Task.FromResult(Current);
        }

        public override Task<SandboxLeaseSnapshot> CompleteProvisionAsync(Guid operationId,
            EnvironmentGenerationFence fence, SandboxProvisionedResource? provisionedResource,
            bool effectMayHaveApplied, CancellationToken cancellationToken)
        {
            Assert.Equal(Current.OperationId, operationId);
            Assert.Equal(Current.Fence, fence);
            Completions++;
            EffectMayHaveApplied = effectMayHaveApplied;
            Current = Current with
            {
                ProvisionedResource = provisionedResource,
                State = provisionedResource is null ? SandboxLeaseState.Released : SandboxLeaseState.Active
            };
            return Task.FromResult(Current);
        }
    }

    private sealed class ProvisionLifecycle(EnvironmentGenerationFence fence, EnvironmentWorkspaceVolumeSnapshot volume) :
        EnvironmentEgressManagerTests.FakeEnvironmentLifecycleStore(fence)
    {
        private readonly Dictionary<Guid, EnvironmentWorkspaceVolumeTransitionReservation> _reservations = [];
        public EnvironmentWorkspaceVolumeSnapshot Current { get; private set; } = volume;
        public int DetachCalls { get; private set; }

        public override Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, CancellationToken cancellationToken) =>
            Task.FromResult<EnvironmentWorkspaceVolumeSnapshot?>(Current);

        public override Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeAttachAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, long expectedTransitionRevision,
            long expectedResourceGeneration, long expectedDataGeneration, string idempotencyKey,
            CancellationToken cancellationToken) =>
            Reserve(environmentFence, volumeId, expectedTransitionRevision, expectedResourceGeneration,
                expectedDataGeneration, EnvironmentWorkspaceVolumeOperation.Attach);

        public override Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
            EnvironmentGenerationFence environmentFence, string volumeId, long expectedTransitionRevision,
            long expectedResourceGeneration, long expectedDataGeneration, string idempotencyKey,
            CancellationToken cancellationToken)
        {
            DetachCalls++;
            return Reserve(environmentFence, volumeId, expectedTransitionRevision, expectedResourceGeneration,
                expectedDataGeneration, EnvironmentWorkspaceVolumeOperation.Detach);
        }

        private Task<EnvironmentWorkspaceVolumeTransitionReservation> Reserve(EnvironmentGenerationFence environmentFence,
            string volumeId, long revision, long resourceGeneration, long dataGeneration,
            EnvironmentWorkspaceVolumeOperation operation)
        {
            Assert.Equal(fence, environmentFence);
            Assert.Equal(Current.VolumeId, volumeId);
            Assert.Equal(Current.ResourceGeneration, resourceGeneration);
            Assert.Equal(Current.DataGeneration, dataGeneration);
            var reservation = new EnvironmentWorkspaceVolumeTransitionReservation(Guid.NewGuid(), fence,
                volumeId, revision, resourceGeneration, dataGeneration, Current.Resource, Current.ProviderBinding,
                revision + 1, resourceGeneration, dataGeneration,
                operation == EnvironmentWorkspaceVolumeOperation.Attach
                    ? EnvironmentWorkspaceVolumeState.Attached : EnvironmentWorkspaceVolumeState.Bound,
                operation, EnvironmentWorkspaceVolumeTransitionState.Reserved, false);
            _reservations.Add(reservation.OperationId, reservation);
            return Task.FromResult(reservation);
        }

        public override Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeAttachAsync(
            Guid operationId, EnvironmentGenerationFence environmentFence, bool effectMayHaveApplied,
            ProviderResourceRef? providerResource, bool effectVerified, CancellationToken cancellationToken) =>
            Complete(operationId, environmentFence, providerResource);

        public override Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
            Guid operationId, EnvironmentGenerationFence environmentFence, bool effectMayHaveApplied,
            ProviderResourceRef? providerResource, bool effectVerified, CancellationToken cancellationToken,
            Func<CancellationToken, Task>? beforeCommitCheck = null) =>
            Complete(operationId, environmentFence, providerResource);

        private Task<EnvironmentWorkspaceVolumeTransitionResult> Complete(Guid operationId,
            EnvironmentGenerationFence environmentFence, ProviderResourceRef? resource)
        {
            var reservation = _reservations[operationId];
            Assert.Equal(fence, environmentFence);
            Assert.Equal(Current.Resource, resource);
            Current = new(fence, Current.VolumeId, reservation.TargetTransitionRevision,
                Current.ResourceGeneration, Current.DataGeneration, reservation.TargetPhase, reservation.Operation,
                Current.Specification, Current.Resource, Current.ProviderBinding);
            return Task.FromResult(new EnvironmentWorkspaceVolumeTransitionResult(operationId, fence, Current.VolumeId,
                reservation.ExpectedTransitionRevision, Current.ResourceGeneration, Current.DataGeneration, resource,
                reservation.TargetTransitionRevision, Current.ResourceGeneration, Current.DataGeneration, resource,
                reservation.TargetPhase, reservation.Operation, EnvironmentWorkspaceVolumeTransitionState.Completed, false));
        }
    }

    private sealed class ProvisionSandbox(AgentSandboxOptions options) : ISandboxProvider
    {
        public int Calls { get; private set; }
        public SandboxProvisionedResource? Resource { get; private set; }
        public Task<SandboxProvisionedResource> ProvisionAsync(SandboxProvisionRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            Resource = new(request.Workspace.Negotiation.SandboxResource, new(Guid.NewGuid()), new("cluster"),
                ImmutableHashSet.Create(SandboxCapabilities.VmIsolation, SandboxCapabilities.WorkspacePersistentVolumeClaim),
                [], new("agent-sandbox", "1.0.0", options.OptionsSchemaVersion, options.OptionsRevision,
                    JsonSerializer.SerializeToElement(options, JsonOptions), JsonSerializer.SerializeToElement(new { })));
            return Task.FromResult(Resource);
        }
        public Task<SandboxObservation> DescribeAsync(SandboxDescribeRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SandboxObservation>> ListOwnedAsync(SandboxListOwnedRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxReleaseReceipt> ReleaseAsync(SandboxReleaseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<SandboxPartialReleaseReceipt> ReleasePartialAsync(SandboxPartialReleaseRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
