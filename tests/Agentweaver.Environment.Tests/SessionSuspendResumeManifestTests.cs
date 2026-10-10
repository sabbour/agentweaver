using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Xunit;

namespace Agentweaver.Environment.Tests;

public sealed class SessionSuspendResumeManifestTests
{
    [Fact]
    public void CompleteManifestUsesOwnerScopedCheckpointAndExistingCacheAcknowledgment()
    {
        var manifest = SuspendedManifest();

        Assert.Same(manifest, manifest.Validate());
        Assert.Equal(0, manifest.FlushedJournalPosition);
        Assert.Equal(manifest.Identity, manifest.CacheAcknowledgment!.Identity);
        Assert.Equal(SessionMaterialKind.SdkCache,
            manifest.CacheAcknowledgment.Reference.Material!.Kind);
        Assert.Equal(
            SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName,
            manifest.Checkpoint!.StoreName);
        Assert.Equal(manifest.Identity, manifest.Checkpoint.Identity);

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Contains("\"cacheAcknowledgment\"", json, StringComparison.Ordinal);
        Assert.Contains("\"sdkVersion\"", json, StringComparison.Ordinal);
        Assert.Contains("\"modelId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"workflowPosition\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"progress\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"sdkVersion\":", JsonSerializer.Serialize(
            manifest with { CacheAcknowledgment = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void InterruptedManifestRequiresGapsButCanOmitUnresolvedEvidence()
    {
        var interrupted = SuspendedManifest() with
        {
            State = SessionSuspendResumeManifestState.Interrupted,
            EnvironmentFence = null,
            CoreExecutionFence = null,
            CacheAcknowledgment = null,
            Checkpoint = null,
            FlushedJournalPosition = null,
            WorkspaceVolume = null,
            WorkspaceResource = null,
            WorkspaceDataGeneration = null,
            WorkspaceTreeSha256 = null,
            WorkspaceProviderCheckpointId = null,
            NetworkIntentGeneration = null,
            MissingEvidence = ["workspace-flush", "core-checkpoint", "network-intent-generation"],
        };

        Assert.Same(interrupted, interrupted.Validate());
        Assert.Throws<ArgumentException>(() => (interrupted with { MissingEvidence = [] }).Validate());
        Assert.Throws<ArgumentException>(() => (interrupted with { MissingEvidence = default }).Validate());
    }

    [Fact]
    public void SuspendedManifestRequiresResolvedFenceCheckpointCacheJournalAndWorkspace()
    {
        var manifest = SuspendedManifest();

        Assert.Throws<ArgumentException>(() => (manifest with { EnvironmentFence = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { CoreExecutionFence = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { Checkpoint = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { CacheAcknowledgment = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { FlushedJournalPosition = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { WorkspaceVolume = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { NetworkIntentGeneration = null }).Validate());
        Assert.Throws<ArgumentException>(() => (manifest with { MissingEvidence = ["journal"] }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (manifest with { CoreExecutionFence = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (manifest with { FlushedJournalPosition = -1 }).Validate());
    }

    [Fact]
    public void CacheAndCheckpointReferencesCannotCrossSessionOwners()
    {
        var manifest = SuspendedManifest();
        var otherIdentity = new SessionIdentity("project-1", "run-1", "session-2");

        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                CacheAcknowledgment = manifest.CacheAcknowledgment! with { Identity = otherIdentity },
            }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                Checkpoint = manifest.Checkpoint! with { Identity = otherIdentity },
            }).Validate());
    }

    [Fact]
    public void WorkspaceContentIsAnExclusiveOneOfBoundToTheSameStorageGeneration()
    {
        var manifest = SuspendedManifest();
        Assert.Throws<ArgumentException>(() =>
            (manifest with { WorkspaceProviderCheckpointId = "provider-checkpoint-1" }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with { WorkspaceTreeSha256 = null, WorkspaceProviderCheckpointId = null }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with { WorkspaceTreeSha256 = new string('A', 64) }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                WorkspaceResource = manifest.WorkspaceResource! with { Generation = 4 },
            }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                WorkspaceResource = manifest.WorkspaceResource! with { Seam = ProviderSeam.Sandbox },
            }).Validate());

        var providerCheckpoint = manifest with
        {
            WorkspaceTreeSha256 = null,
            WorkspaceProviderCheckpointId = "provider-checkpoint-1",
        };
        Assert.Same(providerCheckpoint, providerCheckpoint.Validate());
    }

    [Fact]
    public void WorkspaceDataGenerationAllowsZeroButSuspendedStateStillRequiresContentProof()
    {
        var manifest = SuspendedManifest() with { WorkspaceDataGeneration = 0 };
        Assert.Same(manifest, manifest.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (manifest with { WorkspaceDataGeneration = -1 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                WorkspaceTreeSha256 = null,
                WorkspaceProviderCheckpointId = null,
            }).Validate());
    }

    [Fact]
    public void GuestSnapshotEvidenceIsOptionalButMustBePairedAndPositive()
    {
        var manifest = SuspendedManifest();
        var withGuestSnapshot = manifest with
        {
            GuestSnapshotResource = new ProviderResourceRef(
                ProviderSeam.Snapshots, "snapshots-a", "snapshot-1", 8),
            GuestSnapshotLifecycleGeneration = 3,
        };
        Assert.Same(withGuestSnapshot, withGuestSnapshot.Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with { GuestSnapshotResource = new ProviderResourceRef(
                ProviderSeam.Snapshots, "snapshots-a", "snapshot-1", 8) }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (manifest with
            {
                GuestSnapshotResource = new ProviderResourceRef(
                    ProviderSeam.Snapshots, "snapshots-a", "snapshot-1", 8),
                GuestSnapshotLifecycleGeneration = 0,
            }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                GuestSnapshotResource = new ProviderResourceRef(
                    ProviderSeam.Storage, "snapshots-a", "snapshot-1", 8),
                GuestSnapshotLifecycleGeneration = 3,
            }).Validate());
    }

    [Fact]
    public void PresentNullableGenerationsAndFencesMustBePositiveAndOwnerBound()
    {
        var manifest = SuspendedManifest();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (manifest with { NetworkIntentGeneration = 0 }).Validate());
        Assert.Throws<ArgumentException>(() =>
            (manifest with
            {
                EnvironmentFence = new EnvironmentGenerationFence(
                    new EnvironmentOwnerIdentity("tenant-1", "project-2", "run-1", "environment-1"), 4),
            }).Validate());

        var interrupted = manifest with
        {
            State = SessionSuspendResumeManifestState.Interrupted,
            EnvironmentFence = null,
            CoreExecutionFence = null,
            MissingEvidence = ["core-fence"],
        };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (interrupted with { NetworkIntentGeneration = -1 }).Validate());
    }

    [Fact]
    public void CheckpointReferenceDoesNotDuplicateWorkflowProgress()
    {
        var properties = typeof(SessionSuspendResumeCheckpointReference)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            new[] { "Identity", "StoreName", "CheckpointId", "Revision", "WorkPlanId", "DecisionStateVersion" }
                .ToHashSet(StringComparer.Ordinal),
            properties);
    }

    private static SessionSuspendResumeManifest SuspendedManifest()
    {
        var identity = new SessionIdentity("project-1", "run-1", "session-1");
        var cacheMaterial = new SessionMaterialBinding(
            1,
            SessionMaterialKind.SdkCache,
            "tenant-1",
            new string('b', 64),
            Guid.NewGuid(),
            1,
            1,
            new string('c', 64),
            "sdk-v1",
            "runtime-v1",
            "model-selection-1",
            "model-1");
        var cacheReference = new SessionObjectReference(
            new ObjectKey("cache/runtime/sha256"),
            SessionMaterialValidation.Purpose(SessionMaterialKind.SdkCache),
            512)
        {
            Material = cacheMaterial,
        };

        return new SessionSuspendResumeManifest(
            SessionSuspendResumeContractVersions.CurrentManifestVersion,
            Guid.NewGuid(),
            identity,
            SessionSuspendResumeManifestState.Suspended,
            new EnvironmentGenerationFence(
                new EnvironmentOwnerIdentity("tenant-1", identity.ProjectId, identity.RunId, "environment-1"), 4),
            CoreExecutionFence: 7,
            new SessionMaterialAcknowledgment(1, identity, Guid.NewGuid(), 5, cacheReference),
            new SessionSuspendResumeCheckpointReference(
                identity,
                SessionSuspendResumeContractVersions.CoordinatorCheckpointStoreName,
                "checkpoint-1",
                3,
                "work-plan-1",
                4),
            FlushedJournalPosition: 0,
            new WorkspaceVolumeReference(identity.ProjectId, "volume-1", 9),
            new ProviderResourceRef(ProviderSeam.Storage, "storage-a", "volume-1", 9),
            WorkspaceDataGeneration: 12,
            WorkspaceTreeSha256: new string('a', 64),
            WorkspaceProviderCheckpointId: null,
            GuestSnapshotResource: null,
            GuestSnapshotLifecycleGeneration: null,
            NetworkIntentGeneration: 2,
            MissingEvidence: ImmutableArray<string>.Empty);
    }
}
