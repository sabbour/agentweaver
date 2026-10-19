using System.Collections.Immutable;
using Agentweaver.Abstractions;

namespace Agentweaver.Providers.Storage.AzureFiles;

/// <summary>
/// Validated configuration for the Azure Files CSI workspace-volume provider.
/// The provider uses this revision to constrain the Kubernetes namespace, StorageClass, and resource limits.
/// </summary>
/// <param name="OptionsSchemaVersion">Options contract version; the adapter supports version 1.</param>
/// <param name="OptionsRevision">Opaque revision used to match this configuration with its provider registration.</param>
/// <param name="Namespace">Kubernetes DNS label in which workspace volume claims are created.</param>
/// <param name="StorageClassName">Kubernetes StorageClass DNS label required for workspace claims.</param>
/// <param name="MaximumCapacityGiB">Positive maximum workspace volume capacity in GiB; no upper bound is imposed here.</param>
/// <param name="ProvisioningTimeoutSeconds">Provisioning deadline, from 1 through 900 seconds.</param>
/// <param name="PollIntervalMilliseconds">Provisioning status poll interval, from 1 through 5000 milliseconds.</param>
public sealed record AzureFilesCsiOptions(
    int OptionsSchemaVersion,
    string OptionsRevision,
    string Namespace,
    string StorageClassName,
    long MaximumCapacityGiB,
    int ProvisioningTimeoutSeconds,
    int PollIntervalMilliseconds)
{
    /// <summary>The options contract version supported by this adapter.</summary>
    public const int CurrentOptionsSchemaVersion = 1;

    /// <summary>
    /// Requires schema version 1, a valid opaque revision, Kubernetes DNS labels,
    /// positive capacity, a provisioning timeout of 1 through 900 seconds, and a
    /// polling interval of 1 through 5000 milliseconds.
    /// </summary>
    /// <returns>This options instance when valid.</returns>
    /// <exception cref="ArgumentException">The schema version or a configured name is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity or timeout is outside its supported range.</exception>
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

    /// <summary>Checks the restricted syntax accepted for an opaque provider revision.</summary>
    private static void ValidateOpaque(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque provider options revision is required.", name);
    }

    /// <summary>Checks that a value is one Kubernetes DNS label.</summary>
    private static void ValidateDnsLabel(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 63 ||
            !IsDnsLabelCharacter(value[0]) ||
            !IsDnsLabelCharacter(value[^1]) ||
            value.Any(character => !IsDnsLabelCharacter(character) && character != '-'))
            throw new ArgumentException("A valid Kubernetes DNS label is required.", name);
    }

    /// <summary>Returns whether a character is valid at the start or end of a DNS label.</summary>
    private static bool IsDnsLabelCharacter(char value) =>
        value is >= 'a' and <= 'z' or >= '0' and <= '9';
}

/// <summary>Identifies the Azure Files adapter and creates its provider registration.</summary>
public static class AzureFilesCsiProviderMetadata
{
    /// <summary>The stable identifier used to select this Storage provider.</summary>
    public const string ProviderId = "azure-files-csi";
    /// <summary>The adapter version advertised to provider selection.</summary>
    public static Version AdapterVersion { get; } = new(1, 0, 0);

    private static readonly ImmutableHashSet<string> Capabilities =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            WorkspaceVolumeCapabilities.ReadWriteOnce,
            WorkspaceVolumeCapabilities.ReadWriteMany,
            WorkspaceVolumeCapabilities.ReadOnlyMany,
            WorkspaceVolumeCapabilities.ReadOnlyMount,
            WorkspaceVolumeCapabilities.DurableFlush);

    /// <summary>Creates a registration for the validated options revision.</summary>
    /// <param name="options">The provider configuration to pin.</param>
    /// <param name="enabled">Whether callers may select this registration.</param>
    /// <returns>The Storage provider registration.</returns>
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

/// <summary>StorageClass fields that the provider verifies before creating a workspace claim.</summary>
public sealed record AzureFilesStorageClassSnapshot(
    string Name,
    string Provisioner,
    ImmutableArray<string> MountOptions);

/// <summary>Exact claim inputs, including ownership and workspace-generation metadata.</summary>
public sealed record AzureFilesClaimRequest(
    string Namespace,
    string Name,
    string ProjectId,
    string VolumeId,
    long ResourceGeneration,
    WorkspaceVolumeOwner Owner,
    string StorageClassName,
    long CapacityGiB,
    WorkspaceVolumeAccessMode AccessMode)
{
    /// <summary>Optional owner Environment ID; absent for a shared workspace.</summary>
    public string? EnvironmentId { get; init; }
}

/// <summary>Current claim identity, binding state, capacity, access modes, and owner annotations.</summary>
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

/// <summary>Current PV identity, reclaim policy, and claim reference.</summary>
public sealed record AzureFilesPersistentVolumeSnapshot(
    string Name,
    string Uid,
    string StorageClassName,
    string ReclaimPolicy,
    string ClaimNamespace,
    string ClaimName,
    string ClaimUid);

/// <summary>
/// Kubernetes operations required by the Azure Files provider.
/// Implementations must preserve UID preconditions for destructive changes.
/// </summary>
public interface IAzureFilesCsiClient
{
    /// <summary>Derives a stable cluster identity from the trusted Kubernetes API CA bundle.</summary>
    Task<string> GetClusterIdentityAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a StorageClass, or returns <see langword="null"/> when it is absent.</summary>
    Task<AzureFilesStorageClassSnapshot?> GetStorageClassAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the existing exact claim or creates it with the requested ownership annotations.</summary>
    Task<AzureFilesClaimSnapshot> EnsureClaimAsync(
        AzureFilesClaimRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a claim, or returns <see langword="null"/> when it is absent.</summary>
    Task<AzureFilesClaimSnapshot?> GetClaimAsync(
        string kubernetesNamespace,
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a persistent volume, or returns <see langword="null"/> when it is absent.</summary>
    Task<AzureFilesPersistentVolumeSnapshot?> GetPersistentVolumeAsync(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>Changes a PV reclaim policy only if its UID still matches the expected identity.</summary>
    Task SetPersistentVolumeReclaimPolicyAsync(
        string name,
        string expectedUid,
        WorkspaceVolumeReclaimPolicy reclaimPolicy,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a claim only if its UID still matches the expected identity.</summary>
    Task DeleteClaimAsync(
        string kubernetesNamespace,
        string name,
        string expectedUid,
        CancellationToken cancellationToken = default);
}

/// <summary>Provider failure with a stable code and an indication that the attempted effect may have applied.</summary>
public sealed class AzureFilesCsiException : Exception
{
    /// <summary>Creates a typed provider failure.</summary>
    /// <param name="code">Stable machine-readable failure code.</param>
    /// <param name="message">Safe diagnostic message.</param>
    /// <param name="effectMayHaveApplied">Whether the failed operation may still have changed Kubernetes state.</param>
    /// <param name="innerException">Optional underlying exception.</param>
    public AzureFilesCsiException(
        string code,
        string message,
        bool effectMayHaveApplied = true,
        Exception? innerException = null) : base(message, innerException)
    {
        Code = code;
        EffectMayHaveApplied = effectMayHaveApplied;
    }

    /// <summary>Gets the stable provider failure code.</summary>
    public string Code { get; }
    /// <summary>Gets whether the attempted side effect may have applied.</summary>
    public bool EffectMayHaveApplied { get; }
}

/// <summary>Failure returned for a non-success Kubernetes API response.</summary>
public sealed class AzureFilesKubernetesApiException(
    string operation,
    System.Net.HttpStatusCode statusCode) :
    Exception($"Kubernetes operation '{operation}' failed with HTTP {(int)statusCode}.")
{
    /// <summary>Gets the provider operation name associated with the response.</summary>
    public string Operation { get; } = operation;
    /// <summary>Gets the HTTP status returned by Kubernetes.</summary>
    public System.Net.HttpStatusCode StatusCode { get; } = statusCode;
}
