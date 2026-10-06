using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Providers.Tests;

public sealed class WorkspaceVolumeContractsTests
{
    [Fact]
    public void TransitionKindsCoverTheOwnerVolumeEffectLifecycle()
    {
        Assert.Equal(
            [
                WorkspaceVolumeTransitionKind.Create,
                WorkspaceVolumeTransitionKind.Provision,
                WorkspaceVolumeTransitionKind.Replace,
                WorkspaceVolumeTransitionKind.Bind,
                WorkspaceVolumeTransitionKind.Unbind,
                WorkspaceVolumeTransitionKind.Attach,
                WorkspaceVolumeTransitionKind.Detach,
                WorkspaceVolumeTransitionKind.Flush,
                WorkspaceVolumeTransitionKind.Release
            ],
            Enum.GetValues<WorkspaceVolumeTransitionKind>());
    }

    [Fact]
    public void TransitionRulesAdvanceOnlyResourceCreationAndVerifiedFlushTargets()
    {
        Assert.Equal(0, WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Create, 0));
        Assert.Equal(1, WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Provision, 0));
        Assert.Equal(5, WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Replace, 4));
        Assert.Equal(4, WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Attach, 4));
        Assert.Equal(0, WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Release, 0));
        Assert.Equal(4, WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Release, 4));
        Assert.Equal(8, WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            WorkspaceVolumeTransitionKind.Flush, 7, 8));
        Assert.Equal(7, WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            WorkspaceVolumeTransitionKind.Release, 7, null));

        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Provision, 1));
        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Replace, 0));
        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Bind, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(
            WorkspaceVolumeTransitionKind.Replace, long.MaxValue));
        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            WorkspaceVolumeTransitionKind.Flush, 7, 9));
        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            WorkspaceVolumeTransitionKind.Release, 7, 8));
    }

    [Fact]
    public void TransitionRequestValidatesCreateAndKeepsCountersIndependent()
    {
        var create = new WorkspaceVolumeTransitionRequest(
            "volume-1",
            WorkspaceVolumeTransitionKind.Create,
            ExpectedTransitionRevision: 0,
            ExpectedResourceGeneration: 0,
            ExpectedDataGeneration: 0,
            NextDataGeneration: null,
            "create-1",
            Spec()).Validate();
        Assert.Equal(0, create.TargetResourceGeneration);
        Assert.Equal(0, create.TargetDataGeneration);

        var provision = new WorkspaceVolumeTransitionRequest(
            "volume-1",
            WorkspaceVolumeTransitionKind.Provision,
            ExpectedTransitionRevision: 1,
            ExpectedResourceGeneration: 0,
            ExpectedDataGeneration: 0,
            NextDataGeneration: null,
            "provision-1").Validate();
        Assert.Equal(1, provision.TargetResourceGeneration);
        Assert.Equal(0, provision.TargetDataGeneration);
        Assert.Same(
            provision,
            provision.ValidateFor(WorkspaceVolumeTransitionKind.Provision));
        Assert.Throws<ArgumentException>(() =>
            provision.ValidateFor(WorkspaceVolumeTransitionKind.Replace));

        var replace = new WorkspaceVolumeTransitionRequest(
            "volume-1",
            WorkspaceVolumeTransitionKind.Replace,
            ExpectedTransitionRevision: 2,
            ExpectedResourceGeneration: 1,
            ExpectedDataGeneration: 7,
            NextDataGeneration: null,
            "replace-1").Validate();
        Assert.Equal(2, replace.TargetResourceGeneration);
        Assert.Equal(7, replace.TargetDataGeneration);

        var flush = new WorkspaceVolumeTransitionRequest(
            "volume-1",
            WorkspaceVolumeTransitionKind.Flush,
            ExpectedTransitionRevision: 3,
            ExpectedResourceGeneration: 2,
            ExpectedDataGeneration: 7,
            NextDataGeneration: 8,
            "flush-1").Validate();
        Assert.Equal(2, flush.TargetResourceGeneration);
        Assert.Equal(8, flush.TargetDataGeneration);

        var release = new WorkspaceVolumeTransitionRequest(
            "volume-1",
            WorkspaceVolumeTransitionKind.Release,
            ExpectedTransitionRevision: 4,
            ExpectedResourceGeneration: 2,
            ExpectedDataGeneration: 8,
            NextDataGeneration: null,
            "release-1").Validate();
        Assert.Equal(2, release.TargetResourceGeneration);
        Assert.Equal(8, release.TargetDataGeneration);

        Assert.Throws<ArgumentException>(() => new WorkspaceVolumeTransitionRequest(
            "other-volume",
            WorkspaceVolumeTransitionKind.Create,
            0,
            0,
            0,
            null,
            "create-2",
            Spec()).Validate());
        Assert.Throws<ArgumentException>(() => new WorkspaceVolumeTransitionRequest(
            "volume-1",
            WorkspaceVolumeTransitionKind.Replace,
            2,
            1,
            7,
            8,
            "replace-2").Validate());
    }

    [Fact]
    public void EffectCompletionRequiresExactStorageGenerationAndHonestFlushProof()
    {
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid", 2);
        WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
            WorkspaceVolumeTransitionKind.Replace,
            2,
            resource,
            effectVerified: true,
            durableFlushVerified: false);
        WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
            WorkspaceVolumeTransitionKind.Release,
            2,
            resource: null,
            effectVerified: true,
            durableFlushVerified: false);
        WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
            WorkspaceVolumeTransitionKind.Flush,
            2,
            resource,
            effectVerified: true,
            durableFlushVerified: true);

        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
            WorkspaceVolumeTransitionKind.Provision,
            3,
            resource,
            effectVerified: true,
            durableFlushVerified: false));
        Assert.Throws<ArgumentException>(() => WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
            WorkspaceVolumeTransitionKind.Release,
            2,
            resource,
            effectVerified: true,
            durableFlushVerified: false));
        Assert.Throws<InvalidOperationException>(() =>
            WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
                WorkspaceVolumeTransitionKind.Flush,
                2,
                resource,
                effectVerified: true,
                durableFlushVerified: false));
        Assert.Throws<InvalidOperationException>(() =>
            WorkspaceVolumeTransitionRules.ValidateEffectCompletion(
                WorkspaceVolumeTransitionKind.Attach,
                2,
                resource,
                effectVerified: false,
                durableFlushVerified: false));
    }

    [Fact]
    public void EnvironmentVolumeIsProjectAndEnvironmentScoped()
    {
        var volume = Spec(
            WorkspaceVolumeBindingMode.Environment,
            environmentId: "environment-1",
            owner: new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"));

        Assert.True(volume.AllowsEnvironment("project-1", "environment-1"));
        Assert.False(volume.AllowsEnvironment("project-1", "environment-2"));
        Assert.False(volume.AllowsEnvironment("project-2", "environment-1"));
    }

    [Fact]
    public void SharedVolumeRequiresAnExplicitEnvironmentAllowlist()
    {
        var volume = Spec(
            WorkspaceVolumeBindingMode.Shared,
            environmentId: null,
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany,
            authorizedEnvironmentIds: ["environment-2", "environment-1"],
            owner: new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Team, "team-1"));

        Assert.Equal(
            new[] { "environment-1", "environment-2" },
            volume.AuthorizedEnvironmentIds.ToArray());
        var canonical = Spec(
            WorkspaceVolumeBindingMode.Shared,
            environmentId: null,
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany,
            authorizedEnvironmentIds: ["environment-1", "environment-2"],
            owner: new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Team, "team-1"));
        Assert.Equal(
            JsonSerializer.SerializeToElement(volume).GetRawText(),
            JsonSerializer.SerializeToElement(canonical).GetRawText());
        Assert.True(volume.AllowsEnvironment("project-1", "environment-1"));
        Assert.True(volume.AllowsEnvironment("project-1", "environment-2"));
        Assert.False(volume.AllowsEnvironment("project-1", "environment-3"));
        Assert.False(volume.AllowsEnvironment("project-2", "environment-1"));
    }

    [Fact]
    public void MountManifestContainsOnlyDeclaredCanonicalWorkspaceMounts()
    {
        var volume = new WorkspaceVolumeReference("project-1", "volume-1", 2);
        var manifest = new WorkspaceVolumeMountManifest(
        [
            new WorkspaceVolumeMountDeclaration(
                volume,
                "/workspace/agentweaver/shared/team",
                ReadOnly: true)
        ]).Validate();

        Assert.Single(manifest.Mounts);
        Assert.True(manifest.Mounts[0].ReadOnly);
        Assert.Throws<ArgumentException>(() => new WorkspaceVolumeMountManifest(
            [new WorkspaceVolumeMountDeclaration(volume, "/workspace/agentweaver/../secrets", false)]).Validate());
        Assert.Throws<ArgumentException>(() => new WorkspaceVolumeMountManifest(
            [new WorkspaceVolumeMountDeclaration(volume, "/workspace/agentweaver\\shared", false)]).Validate());
        Assert.Throws<ArgumentException>(() => new WorkspaceVolumeMountManifest(
            [
                new WorkspaceVolumeMountDeclaration(volume, "/workspace/agentweaver/team", true),
                new WorkspaceVolumeMountDeclaration(
                    new WorkspaceVolumeReference("project-1", "volume-2", 1),
                    "/workspace/agentweaver/team/data",
                    true)
            ]).Validate());
        Assert.Throws<ArgumentException>(() => new WorkspaceVolumeMountManifest(
            [
                new WorkspaceVolumeMountDeclaration(volume, "/workspace/agentweaver/team-a", true),
                new WorkspaceVolumeMountDeclaration(volume, "/workspace/agentweaver/team-b", false)
            ]).Validate());
    }

    [Fact]
    public void NegotiatesTheActualSandboxAndStorageResourcesBeforeAttachment()
    {
        var volume = Spec(
            bindingMode: WorkspaceVolumeBindingMode.Environment,
            environmentId: "environment-1",
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany);
        var reference = new WorkspaceVolumeReference("project-1", "volume-1", 2);
        var mount = new WorkspaceVolumeMountDeclaration(
            reference,
            "/workspace/agentweaver/repository",
            ReadOnly: true);
        var manifest = new WorkspaceVolumeMountManifest([mount]);
        var sandbox = new ProviderResourceRef(ProviderSeam.Sandbox, "agent-sandbox", "sandbox-uid", 11);
        var storageResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid", 2);
        var storage = new WorkspaceVolumeResource(
            storageResource,
            ImmutableHashSet.Create(
                StringComparer.Ordinal,
                WorkspaceVolumeCapabilities.ReadWriteMany,
                WorkspaceVolumeCapabilities.ReadOnlyMount),
            Binding(storageResource));
        var environmentFence = EnvironmentFence(lifecycleGeneration: 4);
        var profile = new WorkspaceSandboxAttachmentProfile(
            sandbox,
            ImmutableHashSet.Create(WorkspaceVolumeAccessMode.ReadWriteMany),
            ImmutableHashSet.Create(WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim),
            ImmutableHashSet.Create(StringComparer.Ordinal, "azure-files-csi"),
            SupportsReadOnlyMounts: true);

        var negotiation = WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            environmentFence,
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim);

        Assert.Equal("sandbox-uid", negotiation.SandboxResource.ResourceId);
        Assert.Equal("claim-uid", negotiation.StorageResource.ResourceId);
        Assert.Equal(11, negotiation.SandboxResource.Generation);
        Assert.Equal(2, negotiation.StorageResource.Generation);
        Assert.Equal(environmentFence, negotiation.EnvironmentFence);
        Assert.Equal(7, negotiation.DataGeneration);
        Assert.True(negotiation.ReadOnly);
        Assert.Equal(mount.MountPath, negotiation.MountPath);
        var attachRequest = new WorkspaceVolumeAttachRequest(negotiation, "attach-1");
        Assert.Same(attachRequest, attachRequest.Validate());
        Assert.Throws<ArgumentException>(() =>
            (attachRequest with { IdempotencyKey = " " }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (attachRequest with
            {
                Negotiation = negotiation with
                {
                    StorageResource = storage.Resource with { Generation = 3 }
                }
            }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EnvironmentGenerationFence(environmentFence.Owner, 0));
        Assert.Throws<ArgumentException>(() =>
            new EnvironmentOwnerIdentity(
                " ",
                environmentFence.Owner.ProjectId,
                environmentFence.Owner.RunId,
                environmentFence.Owner.EnvironmentId));

        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-2",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            environmentFence,
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            Spec(owner: new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-2")),
            storage,
            manifest,
            mount,
            sandbox,
            environmentFence,
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            EnvironmentFence(runId: "run-2", lifecycleGeneration: 4),
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount with { MountPath = "/workspace/agentweaver/undeclared" },
            sandbox,
            environmentFence,
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            environmentFence,
            7,
            profile with
            {
                SupportedProtocols = ImmutableHashSet<WorkspaceVolumeAttachmentProtocol>.Empty
            },
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            environmentFence,
            7,
            profile with
            {
                Resource = sandbox with { Generation = sandbox.Generation + 1 }
            },
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            EnvironmentFence(environmentId: "environment-2", lifecycleGeneration: 4),
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
        Assert.Throws<InvalidOperationException>(() => WorkspaceVolumeAttachmentNegotiator.Negotiate(
            "project-1",
            "environment-1",
            "run-1",
            volume,
            storage,
            manifest,
            mount,
            sandbox,
            EnvironmentFence(projectId: "project-2", lifecycleGeneration: 4),
            7,
            profile,
            WorkspaceVolumeAttachmentProtocol.PersistentVolumeClaim));
    }

    [Fact]
    public void RejectsInvalidEnvironmentAndSharedScopeCombinations()
    {
        Assert.Throws<ArgumentException>(() => Spec(
            WorkspaceVolumeBindingMode.Environment,
            environmentId: null));
        Assert.Throws<ArgumentException>(() => Spec(
            WorkspaceVolumeBindingMode.Environment,
            environmentId: "environment-1",
            authorizedEnvironmentIds: ["environment-2"]));
        Assert.Throws<ArgumentException>(() => Spec(
            WorkspaceVolumeBindingMode.Shared,
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany));
        Assert.Throws<ArgumentException>(() => Spec(
            WorkspaceVolumeBindingMode.Shared,
            environmentId: "environment-1",
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany,
            authorizedEnvironmentIds: ["environment-1"]));
        Assert.Throws<ArgumentException>(() => Spec(
            WorkspaceVolumeBindingMode.Shared,
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany,
            authorizedEnvironmentIds: ["environment-1", "environment-1"]));
        Assert.Throws<ArgumentException>(() => Spec(
            WorkspaceVolumeBindingMode.Shared,
            accessMode: WorkspaceVolumeAccessMode.ReadWriteOnce,
            authorizedEnvironmentIds: ["environment-1"]));
    }

    [Fact]
    public void StatusSeparatesTransitionResourceAndDataGenerations()
    {
        var status = new WorkspaceVolumeStatus(
            WorkspaceVolumePhase.Ready,
            TransitionRevision: 2,
            ResourceGeneration: 3,
            DataGeneration: 7,
            new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid", 3),
            []);

        Assert.Same(status, status.Validate());
        foreach (var phase in new[] { WorkspaceVolumePhase.Bound, WorkspaceVolumePhase.Attached })
        {
            var ownerPhase = status with { Phase = phase };
            Assert.Same(ownerPhase, ownerPhase.Validate());
        }
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (status with { TransitionRevision = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (status with { ResourceGeneration = -1 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => (status with { DataGeneration = -1 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (status with { ResourceGeneration = 2 }).Validate());
        Assert.Throws<ArgumentException>(() => (status with
        {
            Resource = new ProviderResourceRef(ProviderSeam.ObjectStore, "azure-files-csi", "claim-uid", 3)
        }).Validate());

        var requested = new WorkspaceVolumeStatus(
            WorkspaceVolumePhase.Requested,
            TransitionRevision: 1,
            ResourceGeneration: 0,
            DataGeneration: 0,
            Resource: null,
            []);
        Assert.Same(requested, requested.Validate());
        var record = new WorkspaceVolumeRecord(Spec(), requested);
        Assert.Same(record, record.Validate());
        var released = status with
        {
            Phase = WorkspaceVolumePhase.Released,
            TransitionRevision = 3,
            Resource = null
        };
        Assert.Same(released, released.Validate());
    }

    [Fact]
    public void ProvisionRequestAndResourceRequireGenerationAndStorageSeam()
    {
        var request = new WorkspaceVolumeProvisionRequest(Spec(), 1, "provision-1");
        Assert.Same(request, request.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (request with { ResourceGeneration = 0 }).Validate());
        Assert.Throws<ArgumentException>(() => (request with { IdempotencyKey = " " }).Validate());
        var releaseResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid-1", 1);
        var release = new WorkspaceVolumeReleaseRequest(
            new WorkspaceVolumeReference("project-1", "volume-1", 1),
            releaseResource,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeReclaimPolicy.Retain,
            WorkspaceVolumeOwnerDeletionPolicy.Retain,
            Binding(releaseResource),
            "release-1");
        Assert.Same(release, release.Validate());
        Assert.Equal(WorkspaceVolumeCapabilities.ReadOnlyMany,
            WorkspaceVolumeCapabilities.ForAccessMode(WorkspaceVolumeAccessMode.ReadOnlyMany));

        var volumeResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid", 1);
        var resource = new WorkspaceVolumeResource(
            volumeResource,
            ImmutableHashSet.Create(StringComparer.Ordinal, WorkspaceVolumeCapabilities.ReadWriteMany),
            Binding(volumeResource));
        Assert.Equal(
            ImmutableHashSet.Create(StringComparer.Ordinal, WorkspaceVolumeCapabilities.ReadWriteMany),
            resource.Validate().NegotiatedCapabilities);
        Assert.Throws<ArgumentException>(() => (resource with
        {
            Resource = new ProviderResourceRef(ProviderSeam.Sandbox, "sandbox", "environment-1", 1)
        }).Validate());

        var fence = EnvironmentFence();
        var storageResource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid", 1);
        var binding = new WorkspaceVolumeBindingRequest(
            Spec(),
            1,
            fence,
            storageResource,
            DataGeneration: 0,
            "bind-1");
        Assert.Same(binding, binding.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (binding with
            {
                EnvironmentFence = EnvironmentFence(environmentId: "environment-2")
            }).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            (binding with
            {
                EnvironmentFence = EnvironmentFence(runId: "run-2")
            }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (binding with
            {
                Resource = storageResource with { Generation = 2 }
            }).Validate());
        var sharedSpec = Spec(
            WorkspaceVolumeBindingMode.Shared,
            environmentId: null,
            accessMode: WorkspaceVolumeAccessMode.ReadWriteMany,
            authorizedEnvironmentIds: ["environment-1"]);
        var sharedBinding = new WorkspaceVolumeBindingRequest(
            sharedSpec, 1, fence, storageResource, 0, "bind-shared-1");
        Assert.Same(sharedBinding, sharedBinding.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new WorkspaceVolumeBindingRequest(
                sharedSpec,
                1,
                EnvironmentFence(environmentId: "environment-2"),
                storageResource,
                0,
                "bind-shared-2").Validate());
        var unbind = new WorkspaceVolumeUnbindRequest(
            Spec(),
            1,
            fence,
            storageResource,
            DataGeneration: 0,
            "unbind-1");
        Assert.Same(unbind, unbind.Validate());

        var flush = new WorkspaceVolumeFlushRequest(
            Spec(),
            1,
            fence,
            storageResource,
            ExpectedDataGeneration: 0,
            "flush-1");
        Assert.Same(flush, flush.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (flush with { ExpectedDataGeneration = -1 }).Validate());
    }

    [Fact]
    public void StorageCandidateIsPinnedOnlyToTheNegotiatedPhysicalResource()
    {
        var capabilities = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            WorkspaceVolumeCapabilities.ReadWriteMany,
            WorkspaceVolumeCapabilities.DurableFlush);
        var registration = new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Storage,
                "azure-files-csi",
                new Version(1, 0, 0),
                1,
                ProviderHostingPattern.KubernetesController,
                capabilities),
            Enabled: true,
            OptionsRevision: "azure-files-options-1",
            OptionsSchemaVersion: 1);
        var catalog = Assert.IsType<ProviderCatalog>(ProviderCatalog.Create(
            [registration],
            [new ProviderSelection(ProviderSeam.Storage, "azure-files-csi")],
            []).Value);
        var resolver = new ProviderResolver(catalog);
        var resolved = resolver.Resolve(new ProviderResolutionRequest(
            ProviderSeam.Storage,
            null,
            new Version(1, 0, 0),
            1,
            capabilities));

        Assert.True(resolved.IsSuccess);
        var candidate = resolved.Value!.Candidate!;
        var pin = resolver.Pin(
            "run-1",
            candidate,
            "claim-uid-1",
            new ResourceNegotiation(
                new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid-1", 7),
                capabilities));

        Assert.True(pin.IsSuccess);
        Assert.Equal("azure-files-csi", pin.Value!.ProviderId);
        Assert.Equal("azure-files-options-1", pin.Value.OptionsRevision);
        Assert.Equal("claim-uid-1", pin.Value.Resource.ResourceId);
        Assert.Equal(7, pin.Value.Resource.Generation);
        Assert.Equal(capabilities, pin.Value.NegotiatedCapabilities);
    }

    [Fact]
    public void ReleaseReceiptIsBoundToTheExactResourceKeyAndPolicy()
    {
        var resource = new ProviderResourceRef(ProviderSeam.Storage, "azure-files-csi", "claim-uid-1", 1);
        var request = new WorkspaceVolumeReleaseRequest(
            new WorkspaceVolumeReference("project-1", "volume-1", 1),
            resource,
            WorkspaceVolumeBindingMode.Environment,
            WorkspaceVolumeReclaimPolicy.Delete,
            WorkspaceVolumeOwnerDeletionPolicy.Delete,
            Binding(resource),
            "release-1");
        var released = new WorkspaceVolumeReleaseReceipt(
            request.Resource,
            request.IdempotencyKey,
            WorkspaceVolumeReleaseDisposition.Released);
        var absent = released with { Disposition = WorkspaceVolumeReleaseDisposition.AlreadyAbsent };

        Assert.Same(released, released.ValidateFor(request));
        Assert.Throws<ArgumentException>(() => absent.ValidateFor(request));
        Assert.Throws<ArgumentException>(() =>
            (released with
            {
                Resource = request.Resource with { ResourceId = "other-claim" }
            }).ValidateFor(request));
        Assert.Throws<ArgumentException>(() =>
            (released with { IdempotencyKey = "other-release" }).ValidateFor(request));
        Assert.Throws<ArgumentException>(() =>
            (released with { Disposition = WorkspaceVolumeReleaseDisposition.Retained }).ValidateFor(request));

        var retainedRequest = request with
        {
            ReclaimPolicy = WorkspaceVolumeReclaimPolicy.Retain,
            OwnerDeletionPolicy = WorkspaceVolumeOwnerDeletionPolicy.Retain
        };
        var retained = released with { Disposition = WorkspaceVolumeReleaseDisposition.Retained };
        var retainedAbsent = absent with { Disposition = WorkspaceVolumeReleaseDisposition.AlreadyAbsent };
        Assert.Throws<ArgumentException>(() => released.ValidateFor(retainedRequest));
        Assert.Same(retained, retained.ValidateFor(retainedRequest));
        Assert.Throws<ArgumentException>(() => retainedAbsent.ValidateFor(retainedRequest));
        Assert.Throws<ArgumentException>(() =>
            (request with { OwnerDeletionPolicy = WorkspaceVolumeOwnerDeletionPolicy.Retain }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (request with { BindingMode = WorkspaceVolumeBindingMode.Shared }).Validate());
    }

    private static WorkspaceVolumeProviderBindingSnapshot Binding(ProviderResourceRef resource) =>
        new WorkspaceVolumeProviderBindingSnapshot(
            resource.ProviderId,
            "1.0.0",
            1,
            "test-options-1",
            JsonSerializer.SerializeToElement(new { endpoint = "test" }),
            JsonSerializer.SerializeToElement(new { resourceId = resource.ResourceId }));

    private static EnvironmentGenerationFence EnvironmentFence(
        string tenantId = "tenant-1",
        string projectId = "project-1",
        string environmentId = "environment-1",
        string runId = "run-1",
        long lifecycleGeneration = 3) =>
        new(
            new EnvironmentOwnerIdentity(tenantId, projectId, runId, environmentId),
            lifecycleGeneration);

    private static WorkspaceVolumeSpec Spec(
        WorkspaceVolumeBindingMode bindingMode = WorkspaceVolumeBindingMode.Environment,
        string? environmentId = "environment-1",
        WorkspaceVolumeAccessMode accessMode = WorkspaceVolumeAccessMode.ReadWriteOnce,
        ImmutableArray<string> authorizedEnvironmentIds = default,
        WorkspaceVolumeOwner? owner = null) =>
        new(
            "volume-1",
            "project-1",
            owner ?? new WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind.Run, "run-1"),
            environmentId,
            bindingMode,
            accessMode,
            capacityGiB: 8,
            storageClass: "azure-files-premium",
            WorkspaceVolumeConsistency.Strict,
            WorkspaceVolumeReclaimPolicy.Retain,
            WorkspaceVolumeOwnerDeletionPolicy.Unbind,
            authorizedEnvironmentIds.IsDefault ? [] : authorizedEnvironmentIds);
}
