using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Storage.AzureFiles;

public sealed record AzureFilesCsiOptions(
    int OptionsSchemaVersion,
    string OptionsRevision,
    string Namespace,
    string StorageClassName,
    long MaximumCapacityGiB,
    int ProvisioningTimeoutSeconds,
    int PollIntervalMilliseconds)
{
    public const int CurrentOptionsSchemaVersion = 1;

    public AzureFilesCsiOptions Validate()
    {
        if (OptionsSchemaVersion != CurrentOptionsSchemaVersion)
            throw new ArgumentException("Azure Files options schema version is unsupported.", nameof(OptionsSchemaVersion));
        ValidateOpaque(OptionsRevision, nameof(OptionsRevision));
        ValidateDnsLabel(Namespace, nameof(Namespace));
        ValidateDnsLabel(StorageClassName, nameof(StorageClassName));
        if (MaximumCapacityGiB < 1)
            throw new ArgumentOutOfRangeException(nameof(MaximumCapacityGiB));
        if (ProvisioningTimeoutSeconds is < 1 or > 900)
            throw new ArgumentOutOfRangeException(nameof(ProvisioningTimeoutSeconds));
        if (PollIntervalMilliseconds is < 1 or > 5000)
            throw new ArgumentOutOfRangeException(nameof(PollIntervalMilliseconds));
        return this;
    }

    private static void ValidateOpaque(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque provider options revision is required.", name);
    }

    private static void ValidateDnsLabel(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 63 ||
            !IsDnsLabelCharacter(value[0]) ||
            !IsDnsLabelCharacter(value[^1]) ||
            value.Any(character => !IsDnsLabelCharacter(character) && character != '-'))
            throw new ArgumentException("A valid Kubernetes DNS label is required.", name);
    }

    private static bool IsDnsLabelCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

public static class AzureFilesCsiProviderMetadata
{
    public const string ProviderId = "azure-files-csi";
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            WorkspaceVolumeCapabilities.ReadWriteOnce,
            WorkspaceVolumeCapabilities.ReadWriteMany,
            WorkspaceVolumeCapabilities.ReadOnlyMany,
            WorkspaceVolumeCapabilities.ReadOnlyMount);

    public static ProviderRegistration CreateRegistration(AzureFilesCsiOptions options, bool enabled = true)
    {
        options.Validate();
        return new ProviderRegistration(
            new ProviderDescriptor(
                ProviderSeam.Storage,
                ProviderId,
                AdapterVersion,
                AzureFilesCsiOptions.CurrentOptionsSchemaVersion,
                ProviderHostingPattern.KubernetesController,
                Capabilities),
            enabled,
            options.OptionsRevision,
            options.OptionsSchemaVersion);
    }
}

public sealed record AzureFilesStorageClassSnapshot(
    string Name,
    string Provisioner,
    ImmutableArray<string> MountOptions);

public sealed record AzureFilesClaimRequest(
    string Namespace,
    string Name,
    string ProjectId,
    string VolumeId,
    long ResourceGeneration,
    WorkspaceVolumeOwner Owner,
    string StorageClassName,
    long CapacityGiB,
    WorkspaceVolumeAccessMode AccessMode);

public sealed record AzureFilesClaimSnapshot(
    string Namespace,
    string Name,
    string Uid,
    string Phase,
    string? VolumeName,
    string StorageClassName,
    long CapacityGiB,
    ImmutableArray<WorkspaceVolumeAccessMode> AccessModes,
    ImmutableDictionary<string, string> Annotations);

public sealed record AzureFilesPersistentVolumeSnapshot(
    string Name,
    string Uid,
    string StorageClassName,
    string ReclaimPolicy,
    string ClaimNamespace,
    string ClaimName,
    string ClaimUid);

public interface IAzureFilesCsiClient
{
    Task<AzureFilesStorageClassSnapshot?> GetStorageClassAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<AzureFilesClaimSnapshot> EnsureClaimAsync(
        AzureFilesClaimRequest request,
        CancellationToken cancellationToken = default);

    Task<AzureFilesClaimSnapshot?> GetClaimAsync(
        string kubernetesNamespace,
        string name,
        CancellationToken cancellationToken = default);

    Task<AzureFilesPersistentVolumeSnapshot?> GetPersistentVolumeAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task SetPersistentVolumeReclaimPolicyAsync(
        string name,
        string expectedUid,
        WorkspaceVolumeReclaimPolicy reclaimPolicy,
        CancellationToken cancellationToken = default);

    Task DeleteClaimAsync(
        string kubernetesNamespace,
        string name,
        string expectedUid,
        CancellationToken cancellationToken = default);
}

public sealed class AzureFilesCsiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AzureFilesKubernetesApiException(
    string operation,
    System.Net.HttpStatusCode statusCode) :
    Exception($"Kubernetes operation '{operation}' failed with HTTP {(int)statusCode}.")
{
    public string Operation { get; } = operation;
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
