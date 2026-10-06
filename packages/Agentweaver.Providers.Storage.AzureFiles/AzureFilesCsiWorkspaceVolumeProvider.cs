using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Storage.AzureFiles;

public sealed class AzureFilesCsiWorkspaceVolumeProvider : IWorkspaceVolumeProvider
{
    private static readonly ImmutableHashSet<string> RequiredMountOptions =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "dir_mode=0755",
            "file_mode=0644",
            "uid=1000",
            "gid=1000",
            "mfsymlinks",
            "cache=strict",
            "actimeo=30");

    private readonly AzureFilesCsiOptions _options;
    private readonly IAzureFilesCsiClient _client;
    private readonly TimeProvider _timeProvider;

    public AzureFilesCsiWorkspaceVolumeProvider(
        AzureFilesCsiOptions options,
        IAzureFilesCsiClient client,
        TimeProvider? timeProvider = null)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WorkspaceVolumeResource> ProvisionAsync(
        WorkspaceVolumeProvisionRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var spec = request.Spec;
        if (spec.Consistency != WorkspaceVolumeConsistency.Strict)
            throw new AzureFilesCsiException(
                "consistency_unsupported",
                "Azure Files CSI is configured only for strict workspace consistency.");
        if (spec.CapacityGiB > _options.MaximumCapacityGiB)
            throw new AzureFilesCsiException(
                "capacity_exceeded",
                "Workspace volume capacity exceeds the configured provider limit.");

        var storageClass = await _client.GetStorageClassAsync(
            _options.StorageClassName,
            cancellationToken).ConfigureAwait(false);
        if (storageClass is null)
            throw new AzureFilesCsiException(
                "storage_class_missing",
                $"Azure Files StorageClass '{_options.StorageClassName}' does not exist.");
        ValidateStorageClass(storageClass, _options.StorageClassName);

        var claimName = GetClaimName(spec.ProjectId, spec.VolumeId, request.ResourceGeneration);
        var claimRequest = new AzureFilesClaimRequest(
            _options.Namespace,
            claimName,
            spec.ProjectId,
            spec.VolumeId,
            request.ResourceGeneration,
            spec.Owner,
            _options.StorageClassName,
            spec.CapacityGiB,
            spec.AccessMode);
        var claim = await _client.EnsureClaimAsync(claimRequest, cancellationToken).ConfigureAwait(false);
        ValidateClaim(claim, claimRequest);
        claim = await WaitForBoundClaimAsync(claimRequest, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(claim.VolumeName))
            throw new AzureFilesCsiException(
                "persistent_volume_missing",
                "The bound Azure Files claim has no persistent volume identity.");

        var persistentVolume = await _client.GetPersistentVolumeAsync(
            claim.VolumeName,
            cancellationToken).ConfigureAwait(false);
        if (persistentVolume is null)
            throw new AzureFilesCsiException(
                "persistent_volume_missing",
                "The Azure Files claim is bound but its persistent volume cannot be read.");
        ValidatePersistentVolume(persistentVolume, claimRequest, claim);

        var expectedReclaimPolicy = ToKubernetesReclaimPolicy(spec.ReclaimPolicy);
        if (!string.Equals(persistentVolume.ReclaimPolicy, expectedReclaimPolicy, StringComparison.Ordinal))
        {
            await _client.SetPersistentVolumeReclaimPolicyAsync(
                persistentVolume.Name,
                persistentVolume.Uid,
                spec.ReclaimPolicy,
                cancellationToken).ConfigureAwait(false);
            persistentVolume = await _client.GetPersistentVolumeAsync(
                persistentVolume.Name,
                cancellationToken).ConfigureAwait(false)
                ?? throw new AzureFilesCsiException(
                    "persistent_volume_missing",
                    "The Azure Files persistent volume disappeared while applying its reclaim policy.");
            ValidatePersistentVolume(persistentVolume, claimRequest, claim);
            if (!string.Equals(persistentVolume.ReclaimPolicy, expectedReclaimPolicy, StringComparison.Ordinal))
                throw new AzureFilesCsiException(
                    "reclaim_policy_unconfirmed",
                    "The Azure Files persistent volume did not retain the requested reclaim policy.");
        }

        var capabilities = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        capabilities.Add(WorkspaceVolumeCapabilities.ForAccessMode(spec.AccessMode));
        capabilities.Add(WorkspaceVolumeCapabilities.ReadOnlyMount);
        return new WorkspaceVolumeResource(
            new ProviderResourceRef(
                ProviderSeam.Storage,
                AzureFilesCsiProviderMetadata.ProviderId,
                claim.Uid,
                request.ResourceGeneration),
            capabilities.ToImmutable()).Validate();
    }

    public async Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
        WorkspaceVolumeReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        if (!string.Equals(
                request.Resource.ProviderId,
                AzureFilesCsiProviderMetadata.ProviderId,
                StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "claim_resource_mismatch",
                "The pinned Storage provider differs from the Azure Files adapter.");

        var reference = request.Volume;
        var claimName = GetClaimName(reference.ProjectId, reference.VolumeId, reference.ResourceGeneration);
        var claim = await _client.GetClaimAsync(
            _options.Namespace,
            claimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
            return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.AlreadyAbsent);

        if (!string.Equals(request.Resource.ResourceId, claim.Uid, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "claim_resource_mismatch",
                "The Azure Files claim UID differs from the resource pinned to this volume generation.");
        ValidateClaimIdentity(claim, _options.Namespace, claimName, reference);
        if (request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Retain)
        {
            if (string.IsNullOrWhiteSpace(claim.VolumeName))
                throw new AzureFilesCsiException(
                    "retained_volume_unbound",
                    "An unbound Azure Files claim cannot be retained as a ready workspace volume.");
            var persistentVolume = await _client.GetPersistentVolumeAsync(
                claim.VolumeName,
                cancellationToken).ConfigureAwait(false)
                ?? throw new AzureFilesCsiException(
                    "persistent_volume_missing",
                    "The retained Azure Files persistent volume cannot be read.");
            ValidatePersistentVolumeIdentity(persistentVolume, claim);
            if (!string.Equals(persistentVolume.ReclaimPolicy, "Retain", StringComparison.Ordinal))
            {
                await _client.SetPersistentVolumeReclaimPolicyAsync(
                    persistentVolume.Name,
                    persistentVolume.Uid,
                    WorkspaceVolumeReclaimPolicy.Retain,
                    cancellationToken).ConfigureAwait(false);
                persistentVolume = await _client.GetPersistentVolumeAsync(
                    persistentVolume.Name,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new AzureFilesCsiException(
                        "persistent_volume_missing",
                        "The retained Azure Files persistent volume disappeared while applying its reclaim policy.");
                ValidatePersistentVolumeIdentity(persistentVolume, claim);
                if (!string.Equals(persistentVolume.ReclaimPolicy, "Retain", StringComparison.Ordinal))
                    throw new AzureFilesCsiException(
                        "reclaim_policy_unconfirmed",
                        "The Azure Files persistent volume did not retain the requested reclaim policy.");
            }
            return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.Retained);
        }

        if (!string.IsNullOrWhiteSpace(claim.VolumeName))
        {
            var persistentVolume = await _client.GetPersistentVolumeAsync(
                claim.VolumeName,
                cancellationToken).ConfigureAwait(false)
                ?? throw new AzureFilesCsiException(
                    "persistent_volume_missing",
                    "The Azure Files persistent volume cannot be read before deletion.");
            ValidatePersistentVolumeIdentity(persistentVolume, claim);
            if (!string.Equals(persistentVolume.ReclaimPolicy, "Delete", StringComparison.Ordinal))
                throw new AzureFilesCsiException(
                    "reclaim_policy_mismatch",
                    "The Azure Files persistent volume is not configured for the requested Delete policy.");
        }

        await _client.DeleteClaimAsync(
            _options.Namespace,
            claimName,
            claim.Uid,
            cancellationToken).ConfigureAwait(false);
        var remainingClaim = await _client.GetClaimAsync(
            _options.Namespace,
            claimName,
            cancellationToken).ConfigureAwait(false);
        if (remainingClaim is not null)
            throw new AzureFilesCsiException(
                "claim_release_unconfirmed",
                "The Azure Files claim still exists after the UID-guarded delete request.");
        return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.Released);
    }

    private static WorkspaceVolumeReleaseReceipt CreateReleaseReceipt(
        WorkspaceVolumeReleaseRequest request,
        WorkspaceVolumeReleaseDisposition disposition) =>
        new WorkspaceVolumeReleaseReceipt(
            request.Resource,
            request.IdempotencyKey,
            disposition).ValidateFor(request);

    public static string GetClaimName(string projectId, string volumeId, long generation)
    {
        var identity = new WorkspaceVolumeReference(projectId, volumeId, generation).Validate();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity.ProjectId}\0{identity.VolumeId}\0{identity.ResourceGeneration}"));
        return $"aw-{Convert.ToHexString(bytes).ToLowerInvariant()[..40]}";
    }

    private async Task<AzureFilesClaimSnapshot> WaitForBoundClaimAsync(
        AzureFilesClaimRequest request,
        CancellationToken cancellationToken)
    {
        var deadline = _timeProvider.GetUtcNow().AddSeconds(_options.ProvisioningTimeoutSeconds);
        var claim = await _client.GetClaimAsync(request.Namespace, request.Name, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AzureFilesCsiException(
                "claim_missing",
                "The Azure Files claim disappeared before it reached Bound.");

        while (true)
        {
            ValidateClaim(claim, request);
            if (string.Equals(claim.Phase, "Bound", StringComparison.Ordinal))
                return claim;
            if (string.Equals(claim.Phase, "Failed", StringComparison.Ordinal))
                throw new AzureFilesCsiException(
                    "claim_failed",
                    "The Azure Files claim entered the Failed phase.");
            if (!string.Equals(claim.Phase, "Pending", StringComparison.Ordinal))
                throw new AzureFilesCsiException(
                    "claim_phase_invalid",
                    "The Azure Files claim has an unsupported Kubernetes phase.");

            var remaining = deadline - _timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
                throw new AzureFilesCsiException(
                    "claim_timeout",
                    "The Azure Files claim did not reach Bound before the configured timeout.");
            var delay = TimeSpan.FromMilliseconds(_options.PollIntervalMilliseconds);
            await Task.Delay(remaining < delay ? remaining : delay, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            claim = await _client.GetClaimAsync(request.Namespace, request.Name, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new AzureFilesCsiException(
                    "claim_missing",
                    "The Azure Files claim disappeared before it reached Bound.");
        }
    }

    private static void ValidateStorageClass(
        AzureFilesStorageClassSnapshot storageClass,
        string expectedName)
    {
        if (!string.Equals(storageClass.Name, expectedName, StringComparison.Ordinal))
            throw new AzureFilesCsiException("storage_class_invalid", "The Azure Files StorageClass name is invalid.");
        if (!string.Equals(storageClass.Provisioner, "file.csi.azure.com", StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "storage_class_invalid",
                "The configured StorageClass is not provisioned by the Azure Files CSI driver.");
        if (storageClass.MountOptions.IsDefault ||
            storageClass.MountOptions.Distinct(StringComparer.Ordinal).Count() != storageClass.MountOptions.Length ||
            !RequiredMountOptions.SetEquals(storageClass.MountOptions))
            throw new AzureFilesCsiException(
                "mount_options_invalid",
                "The Azure Files StorageClass must use the approved UID, GID, permissions, symlink, and strict-cache mount options.");
    }

    private static void ValidateClaim(AzureFilesClaimSnapshot claim, AzureFilesClaimRequest request)
    {
        ValidateClaimIdentity(
            claim,
            request.Namespace,
            request.Name,
            new WorkspaceVolumeReference(request.ProjectId, request.VolumeId, request.ResourceGeneration));
        if (!string.Equals(claim.StorageClassName, request.StorageClassName, StringComparison.Ordinal) ||
            claim.CapacityGiB != request.CapacityGiB ||
            claim.AccessModes.Length != 1 ||
            claim.AccessModes[0] != request.AccessMode ||
            !HasAnnotation(claim, "agentweaver.dev/owner-kind", request.Owner.Kind.ToString()) ||
            !HasAnnotation(claim, "agentweaver.dev/owner-id", request.Owner.Id))
            throw new AzureFilesCsiException(
                "claim_spec_mismatch",
                "The Azure Files claim does not match the requested storage class, capacity, or access mode.");
    }

    private static void ValidateClaimIdentity(
        AzureFilesClaimSnapshot claim,
        string kubernetesNamespace,
        string claimName,
        WorkspaceVolumeReference reference)
    {
        if (!string.Equals(claim.Namespace, kubernetesNamespace, StringComparison.Ordinal) ||
            !string.Equals(claim.Name, claimName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(claim.Uid) ||
            !HasAnnotation(claim, "agentweaver.dev/project-id", reference.ProjectId) ||
            !HasAnnotation(claim, "agentweaver.dev/volume-id", reference.VolumeId) ||
            !HasAnnotation(claim, "agentweaver.dev/generation", reference.ResourceGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture)))
            throw new AzureFilesCsiException(
                "claim_identity_mismatch",
                "The Kubernetes claim is not owned by the requested workspace volume generation.");
    }

    private static bool HasAnnotation(AzureFilesClaimSnapshot claim, string name, string expected) =>
        claim.Annotations.TryGetValue(name, out var actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    private static void ValidatePersistentVolume(
        AzureFilesPersistentVolumeSnapshot persistentVolume,
        AzureFilesClaimRequest request,
        AzureFilesClaimSnapshot claim)
    {
        ValidatePersistentVolumeIdentity(persistentVolume, claim);
        if (!string.Equals(persistentVolume.StorageClassName, request.StorageClassName, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimNamespace, request.Namespace, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimName, request.Name, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "persistent_volume_mismatch",
                "The bound persistent volume does not match the Azure Files claim.");
    }

    private static void ValidatePersistentVolumeIdentity(
        AzureFilesPersistentVolumeSnapshot persistentVolume,
        AzureFilesClaimSnapshot claim)
    {
        if (string.IsNullOrWhiteSpace(persistentVolume.Name) ||
            string.IsNullOrWhiteSpace(persistentVolume.Uid) ||
            !string.Equals(persistentVolume.ClaimNamespace, claim.Namespace, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimName, claim.Name, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimUid, claim.Uid, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "persistent_volume_mismatch",
                "The persistent volume claim reference does not match the current claim UID.");
    }

    private static string ToKubernetesReclaimPolicy(WorkspaceVolumeReclaimPolicy policy) => policy switch
    {
        WorkspaceVolumeReclaimPolicy.Delete => "Delete",
        WorkspaceVolumeReclaimPolicy.Retain => "Retain",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };
}
