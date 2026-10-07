using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Storage.AzureFiles;

public sealed class AzureFilesCsiWorkspaceVolumeProvider : IWorkspaceVolumeProvider
{
    private static readonly JsonSerializerOptions BindingJsonOptions = new(JsonSerializerDefaults.Web);

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
        if (!string.Equals(spec.StorageClass, _options.StorageClassName, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "storage_class_mismatch",
                "The requested workspace volume StorageClass differs from the configured Azure Files class.",
                effectMayHaveApplied: false);
        if (spec.Consistency != WorkspaceVolumeConsistency.Strict)
            throw new AzureFilesCsiException(
                "consistency_unsupported",
                "Azure Files CSI is configured only for strict workspace consistency.",
                effectMayHaveApplied: false);
        if (spec.CapacityGiB > _options.MaximumCapacityGiB)
            throw new AzureFilesCsiException(
                "capacity_exceeded",
                "Workspace volume capacity exceeds the configured provider limit.",
                effectMayHaveApplied: false);

        AzureFilesStorageClassSnapshot? storageClass;
        try
        {
            storageClass = await _client.GetStorageClassAsync(
                spec.StorageClass,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AzureFilesCsiException(
                "storage_class_unavailable",
                "The Azure Files StorageClass could not be verified before claim creation.",
                effectMayHaveApplied: false,
                exception);
        }
        if (storageClass is null)
            throw new AzureFilesCsiException(
                "storage_class_missing",
                $"Azure Files StorageClass '{spec.StorageClass}' does not exist.",
                effectMayHaveApplied: false);
        ValidateStorageClass(storageClass, spec.StorageClass);

        var claimName = GetClaimName(
            spec.ProjectId, spec.VolumeId, request.ResourceGeneration, spec.EnvironmentId);
        var claimRequest = new AzureFilesClaimRequest(
            _options.Namespace,
            claimName,
            spec.ProjectId,
            spec.VolumeId,
            request.ResourceGeneration,
            spec.Owner,
            spec.StorageClass,
            spec.CapacityGiB,
            spec.AccessMode)
        {
            EnvironmentId = spec.EnvironmentId
        };
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

        var effectiveReleasePolicy = spec.GetEffectiveReleasePolicy();
        var expectedReclaimPolicy = ToKubernetesReclaimPolicy(effectiveReleasePolicy);
        if (!string.Equals(persistentVolume.ReclaimPolicy, expectedReclaimPolicy, StringComparison.Ordinal))
        {
            await _client.SetPersistentVolumeReclaimPolicyAsync(
                persistentVolume.Name,
                persistentVolume.Uid,
                effectiveReleasePolicy,
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
                    "The Azure Files persistent volume did not retain the effective release policy.");
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
            capabilities.ToImmutable(),
            new WorkspaceVolumeProviderBindingSnapshot(
                AzureFilesCsiProviderMetadata.ProviderId,
                AzureFilesCsiProviderMetadata.AdapterVersion.ToString(),
                _options.OptionsSchemaVersion,
                _options.OptionsRevision,
                JsonSerializer.SerializeToElement(_options, BindingJsonOptions),
                JsonSerializer.SerializeToElement(
                    new AzureFilesReleaseDescriptor(
                        _options.Namespace,
                        claim.Name,
                        claim.Uid,
                        persistentVolume.Name,
                        persistentVolume.Uid,
                        spec.EnvironmentId,
                        _client.ClusterIdentity),
                    BindingJsonOptions))).Validate();
    }

    public async Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
        WorkspaceVolumeReleaseRequest request,
        CancellationToken cancellationToken = default)
    {
        request = (request ?? throw new ArgumentNullException(nameof(request))).Validate();
        var descriptor = ReadReleaseBinding(request);
        if (!string.Equals(descriptor.ClusterIdentity, _client.ClusterIdentity, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "cluster_identity_mismatch",
                "The configured Kubernetes API target differs from the target pinned to this volume generation.",
                effectMayHaveApplied: false);
        var reference = request.Volume;
        var claim = await _client.GetClaimAsync(
            descriptor.Namespace,
            descriptor.ClaimName,
            cancellationToken).ConfigureAwait(false);
        if (claim is null)
        {
            var absentClaimPv = await _client.GetPersistentVolumeAsync(
                descriptor.PersistentVolumeName,
                cancellationToken).ConfigureAwait(false);
            if (absentClaimPv is null)
            {
                if (request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete)
                    return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.Released);
                throw new AzureFilesCsiException(
                    "retained_volume_missing",
                    "The exact Azure Files persistent volume is absent and cannot be confirmed as retained.");
            }
            ValidatePersistentVolumeDescriptor(absentClaimPv, descriptor, reference);
            if (request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete)
                throw new AzureFilesCsiException(
                    "persistent_volume_release_unconfirmed",
                    "The exact Azure Files persistent volume still exists after its claim disappeared.");
            await EnsureRetainedAsync(absentClaimPv, descriptor, cancellationToken).ConfigureAwait(false);
            return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.Retained);
        }

        if (!string.Equals(claim.Uid, descriptor.ClaimUid, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "claim_resource_mismatch",
                "The Azure Files claim UID differs from the pinned generation descriptor.");
        ValidateClaimIdentity(
            claim, descriptor.Namespace, descriptor.ClaimName, reference, descriptor.EnvironmentId);
        if (!string.Equals(claim.Uid, request.Resource.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(claim.VolumeName, descriptor.PersistentVolumeName, StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "claim_resource_mismatch",
                "The Azure Files claim does not match its pinned claim and persistent volume identities.");
        var persistentVolume = await _client.GetPersistentVolumeAsync(
            descriptor.PersistentVolumeName,
            cancellationToken).ConfigureAwait(false)
            ?? throw new AzureFilesCsiException(
                "persistent_volume_missing",
                "The exact Azure Files persistent volume cannot be read for release.");
        ValidatePersistentVolumeDescriptor(persistentVolume, descriptor, reference);
        if (request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Retain)
        {
            await EnsureRetainedAsync(persistentVolume, descriptor, cancellationToken).ConfigureAwait(false);
            return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.Retained);
        }

        if (!string.Equals(persistentVolume.ReclaimPolicy, "Delete", StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "reclaim_policy_mismatch",
                "The Azure Files persistent volume is not configured for the requested Delete policy.");

        await _client.DeleteClaimAsync(
            descriptor.Namespace,
            descriptor.ClaimName,
            claim.Uid,
            cancellationToken).ConfigureAwait(false);
        var remainingClaim = await _client.GetClaimAsync(
            descriptor.Namespace,
            descriptor.ClaimName,
            cancellationToken).ConfigureAwait(false);
        if (remainingClaim is not null)
            throw new AzureFilesCsiException(
                "claim_release_unconfirmed",
                "The Azure Files claim still exists after the UID-guarded delete request.");
        var remainingPv = await _client.GetPersistentVolumeAsync(
            descriptor.PersistentVolumeName,
            cancellationToken).ConfigureAwait(false);
        if (remainingPv is not null)
        {
            ValidatePersistentVolumeDescriptor(remainingPv, descriptor, reference);
            throw new AzureFilesCsiException(
                "persistent_volume_release_unconfirmed",
                "The exact Azure Files persistent volume still exists after claim deletion.");
        }
        return CreateReleaseReceipt(request, WorkspaceVolumeReleaseDisposition.Released);
    }

    private static AzureFilesReleaseDescriptor ReadReleaseBinding(WorkspaceVolumeReleaseRequest request)
    {
        var binding = request.ProviderBinding;
        if (!string.Equals(
                binding.ProviderId,
                AzureFilesCsiProviderMetadata.ProviderId,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.AdapterVersion,
                AzureFilesCsiProviderMetadata.AdapterVersion.ToString(),
                StringComparison.Ordinal) ||
            binding.OptionsSchemaVersion != AzureFilesCsiOptions.CurrentOptionsSchemaVersion)
            throw new AzureFilesCsiException(
                "provider_binding_unsupported",
                "The pinned Azure Files provider binding is not supported by this adapter version.",
                effectMayHaveApplied: false);

        AzureFilesCsiOptions options;
        AzureFilesReleaseDescriptor descriptor;
        try
        {
            options = (JsonSerializer.Deserialize<AzureFilesCsiOptions>(
                binding.OptionsSnapshot,
                BindingJsonOptions)
                ?? throw new JsonException("Provider options are empty.")).Validate();
            descriptor = JsonSerializer.Deserialize<AzureFilesReleaseDescriptor>(
                binding.ReleaseDescriptor,
                BindingJsonOptions)
                ?? throw new JsonException("The release descriptor is empty.");
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or NotSupportedException)
        {
            throw new AzureFilesCsiException(
                "provider_binding_invalid",
                "The pinned Azure Files provider binding is invalid.",
                effectMayHaveApplied: false,
                exception);
        }

        if (!string.Equals(options.OptionsRevision, binding.OptionsRevision, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(descriptor.Namespace) ||
            string.IsNullOrWhiteSpace(descriptor.ClaimName) ||
            string.IsNullOrWhiteSpace(descriptor.ClaimUid) ||
            string.IsNullOrWhiteSpace(descriptor.PersistentVolumeName) ||
            string.IsNullOrWhiteSpace(descriptor.PersistentVolumeUid) ||
            string.IsNullOrWhiteSpace(descriptor.ClusterIdentity) ||
            request.BindingMode == WorkspaceVolumeBindingMode.Environment &&
                string.IsNullOrWhiteSpace(descriptor.EnvironmentId) ||
            request.BindingMode == WorkspaceVolumeBindingMode.Shared &&
                descriptor.EnvironmentId is not null ||
            !string.Equals(descriptor.Namespace, options.Namespace, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ClaimUid, request.Resource.ResourceId, StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.ClaimName,
                GetClaimName(
                    request.Volume.ProjectId,
                    request.Volume.VolumeId,
                    request.Volume.ResourceGeneration,
                    descriptor.EnvironmentId),
                StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "provider_binding_mismatch",
                "The pinned Azure Files options or release descriptor does not match the exact resource generation.",
                effectMayHaveApplied: false);
        return descriptor;
    }

    private async Task EnsureRetainedAsync(
        AzureFilesPersistentVolumeSnapshot persistentVolume,
        AzureFilesReleaseDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(persistentVolume.ReclaimPolicy, "Retain", StringComparison.Ordinal))
        {
            await _client.SetPersistentVolumeReclaimPolicyAsync(
                persistentVolume.Name,
                persistentVolume.Uid,
                WorkspaceVolumeReclaimPolicy.Retain,
                cancellationToken).ConfigureAwait(false);
            persistentVolume = await _client.GetPersistentVolumeAsync(
                descriptor.PersistentVolumeName,
                cancellationToken).ConfigureAwait(false)
                ?? throw new AzureFilesCsiException(
                    "persistent_volume_missing",
                    "The retained Azure Files persistent volume disappeared while applying its reclaim policy.");
            ValidatePersistentVolumeDescriptor(persistentVolume, descriptor, null);
            if (!string.Equals(persistentVolume.ReclaimPolicy, "Retain", StringComparison.Ordinal))
                throw new AzureFilesCsiException(
                    "reclaim_policy_unconfirmed",
                    "The Azure Files persistent volume did not retain the requested reclaim policy.");
        }
    }

    private static WorkspaceVolumeReleaseReceipt CreateReleaseReceipt(
        WorkspaceVolumeReleaseRequest request,
        WorkspaceVolumeReleaseDisposition disposition) =>
        new WorkspaceVolumeReleaseReceipt(
            request.Resource,
            request.IdempotencyKey,
            disposition).ValidateFor(request);

    public static string GetClaimName(
        string projectId,
        string volumeId,
        long generation,
        string? environmentId = null)
    {
        var identity = new WorkspaceVolumeReference(projectId, volumeId, generation).Validate();
        var scope = environmentId is null
            ? string.Empty
            : $"\0{ValidateEnvironmentId(environmentId)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity.ProjectId}\0{identity.VolumeId}\0{identity.ResourceGeneration}{scope}"));
        return $"aw-{Convert.ToHexString(bytes).ToLowerInvariant()[..40]}";
    }

    private static string ValidateEnvironmentId(string environmentId)
    {
        if (string.IsNullOrWhiteSpace(environmentId) || environmentId.Length > 256 ||
            environmentId.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque workspace volume identity is required.", nameof(environmentId));
        return environmentId;
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
            throw new AzureFilesCsiException(
                "storage_class_invalid",
                "The Azure Files StorageClass name is invalid.",
                effectMayHaveApplied: false);
        if (!string.Equals(storageClass.Provisioner, "file.csi.azure.com", StringComparison.Ordinal))
            throw new AzureFilesCsiException(
                "storage_class_invalid",
                "The configured StorageClass is not provisioned by the Azure Files CSI driver.",
                effectMayHaveApplied: false);
        if (storageClass.MountOptions.IsDefault ||
            storageClass.MountOptions.Distinct(StringComparer.Ordinal).Count() != storageClass.MountOptions.Length ||
            !RequiredMountOptions.SetEquals(storageClass.MountOptions))
            throw new AzureFilesCsiException(
                "mount_options_invalid",
                "The Azure Files StorageClass must use the approved UID, GID, permissions, symlink, and strict-cache mount options.",
                effectMayHaveApplied: false);
    }

    private static void ValidateClaim(AzureFilesClaimSnapshot claim, AzureFilesClaimRequest request)
    {
        ValidateClaimIdentity(
            claim,
            request.Namespace,
            request.Name,
            new WorkspaceVolumeReference(request.ProjectId, request.VolumeId, request.ResourceGeneration),
            request.EnvironmentId);
        if (!string.Equals(claim.StorageClassName, request.StorageClassName, StringComparison.Ordinal) ||
            claim.CapacityGiB != request.CapacityGiB ||
            claim.AccessModes.Length != 1 ||
            claim.AccessModes[0] != request.AccessMode ||
            !HasExpectedEnvironmentAnnotation(claim, request.EnvironmentId) ||
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
        WorkspaceVolumeReference reference,
        string? environmentId)
    {
        if (!string.Equals(claim.Namespace, kubernetesNamespace, StringComparison.Ordinal) ||
            !string.Equals(claim.Name, claimName, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(claim.Uid) ||
            !HasAnnotation(claim, "agentweaver.dev/project-id", reference.ProjectId) ||
            !HasAnnotation(claim, "agentweaver.dev/volume-id", reference.VolumeId) ||
            !HasAnnotation(claim, "agentweaver.dev/generation", reference.ResourceGeneration.ToString(
                System.Globalization.CultureInfo.InvariantCulture)) ||
            !HasExpectedEnvironmentAnnotation(claim, environmentId))
            throw new AzureFilesCsiException(
                "claim_identity_mismatch",
                "The Kubernetes claim is not owned by the requested workspace volume generation.");
    }

    private static bool HasAnnotation(AzureFilesClaimSnapshot claim, string name, string expected) =>
        claim.Annotations.TryGetValue(name, out var actual) &&
        string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool HasExpectedEnvironmentAnnotation(
        AzureFilesClaimSnapshot claim,
        string? environmentId) =>
        environmentId is null
            ? !claim.Annotations.ContainsKey("agentweaver.dev/environment-id")
            : HasAnnotation(claim, "agentweaver.dev/environment-id", environmentId);

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

    private static void ValidatePersistentVolumeDescriptor(
        AzureFilesPersistentVolumeSnapshot persistentVolume,
        AzureFilesReleaseDescriptor descriptor,
        WorkspaceVolumeReference? reference)
    {
        if (!string.Equals(persistentVolume.Name, descriptor.PersistentVolumeName, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.Uid, descriptor.PersistentVolumeUid, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimNamespace, descriptor.Namespace, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimName, descriptor.ClaimName, StringComparison.Ordinal) ||
            !string.Equals(persistentVolume.ClaimUid, descriptor.ClaimUid, StringComparison.Ordinal) ||
            (reference is not null &&
             (!string.Equals(
                  descriptor.ClaimName,
                  GetClaimName(
                      reference.ProjectId,
                      reference.VolumeId,
                      reference.ResourceGeneration,
                      descriptor.EnvironmentId),
                  StringComparison.Ordinal) ||
              !string.Equals(descriptor.ClaimUid, persistentVolume.ClaimUid, StringComparison.Ordinal))))
            throw new AzureFilesCsiException(
                "persistent_volume_mismatch",
                "The persistent volume identity differs from the exact saved generation descriptor.");
    }

    private static string ToKubernetesReclaimPolicy(WorkspaceVolumeReclaimPolicy policy) => policy switch
    {
        WorkspaceVolumeReclaimPolicy.Delete => "Delete",
        WorkspaceVolumeReclaimPolicy.Retain => "Retain",
        _ => throw new ArgumentOutOfRangeException(nameof(policy))
    };

    private sealed record AzureFilesReleaseDescriptor(
        string Namespace,
        string ClaimName,
        string ClaimUid,
        string PersistentVolumeName,
        string PersistentVolumeUid,
        string? EnvironmentId,
        string ClusterIdentity);
}
