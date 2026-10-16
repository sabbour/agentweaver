using System.Collections.Immutable;
using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>Whether a workspace volume is private to one Environment or shared through an allowlist.</summary>
public enum WorkspaceVolumeBindingMode
{
    Environment,
    Shared
}

/// <summary>Multi-attach access mode requested from a Storage provider.</summary>
public enum WorkspaceVolumeAccessMode
{
    ReadWriteOnce,
    ReadWriteMany,
    ReadOnlyMany
}

/// <summary>Consistency expectation for consumers of a workspace volume.</summary>
public enum WorkspaceVolumeConsistency
{
    Strict,
    Eventual
}

/// <summary>Whether a released provider volume is deleted or retained.</summary>
public enum WorkspaceVolumeReclaimPolicy
{
    Delete,
    Retain
}

/// <summary>Owner-deletion behavior configured for a workspace volume.</summary>
public enum WorkspaceVolumeOwnerDeletionPolicy
{
    Delete,
    Retain,
    Unbind
}

/// <summary>Kind of Agentweaver entity that owns a workspace volume.</summary>
public enum WorkspaceVolumeOwnerKind
{
    Run,
    Agent,
    Team
}

/// <summary>Owner-visible lifecycle phase of a workspace volume.</summary>
public enum WorkspaceVolumePhase
{
    Requested,
    Provisioning,
    Ready,
    Releasing,
    Released,
    Failed,
    Bound,
    Attached
}

/// <summary>Typed state transition supported by the workspace-volume owner.</summary>
public enum WorkspaceVolumeTransitionKind
{
    Create,
    Provision,
    Replace,
    Bind,
    Unbind,
    Attach,
    Detach,
    Flush,
    Release
}

/// <summary>Computes generation changes and checks completion evidence for volume operations.</summary>
public static class WorkspaceVolumeTransitionRules
{
    /// <summary>Returns the resource generation expected after the requested operation.</summary>
    /// <exception cref="ArgumentException">The operation cannot run at the expected generation.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The operation or generation is invalid.</exception>
    public static long GetTargetResourceGeneration(
        WorkspaceVolumeTransitionKind operation,
        long expectedResourceGeneration)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (expectedResourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedResourceGeneration));
        if (operation == WorkspaceVolumeTransitionKind.Replace &&
            expectedResourceGeneration == long.MaxValue)
            throw new ArgumentOutOfRangeException(
                nameof(expectedResourceGeneration),
                "Replace cannot advance the maximum resource generation.");

        return operation switch
        {
            WorkspaceVolumeTransitionKind.Create when expectedResourceGeneration == 0 => 0,
            WorkspaceVolumeTransitionKind.Create =>
                throw new ArgumentException("Create requires a volume with no resource generation."),
            WorkspaceVolumeTransitionKind.Provision when expectedResourceGeneration == 0 => 1,
            WorkspaceVolumeTransitionKind.Provision =>
                throw new ArgumentException("Provision cannot replace an existing resource; use Replace."),
            WorkspaceVolumeTransitionKind.Replace when expectedResourceGeneration > 0 =>
                checked(expectedResourceGeneration + 1),
            WorkspaceVolumeTransitionKind.Replace =>
                throw new ArgumentException("Replace requires an existing resource generation."),
            WorkspaceVolumeTransitionKind.Bind or
                WorkspaceVolumeTransitionKind.Unbind or
                WorkspaceVolumeTransitionKind.Attach or
                WorkspaceVolumeTransitionKind.Detach or
                WorkspaceVolumeTransitionKind.Flush
                when expectedResourceGeneration == 0 =>
                throw new ArgumentException("This operation requires an existing resource generation."),
            _ => expectedResourceGeneration
        };
    }

    /// <summary>Returns the data generation, allowing only Flush to advance it by exactly one.</summary>
    /// <exception cref="ArgumentException">A non-flush operation requests a new generation or Flush skips a generation.</exception>
    public static long GetTargetDataGeneration(
        WorkspaceVolumeTransitionKind operation,
        long expectedDataGeneration,
        long? nextDataGeneration)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (expectedDataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedDataGeneration));

        if (operation == WorkspaceVolumeTransitionKind.Flush)
        {
            if (expectedDataGeneration == long.MaxValue ||
                nextDataGeneration != expectedDataGeneration + 1)
                throw new ArgumentException(
                    "Flush must request exactly the next data generation.",
                    nameof(nextDataGeneration));
            return nextDataGeneration.Value;
        }

        if (nextDataGeneration is not null)
            throw new ArgumentException(
                "Only Flush may advance the workspace data generation.",
                nameof(nextDataGeneration));
        if (operation == WorkspaceVolumeTransitionKind.Create && expectedDataGeneration != 0)
            throw new ArgumentException("Create must start at data generation zero.");
        return expectedDataGeneration;
    }

    /// <summary>Requires verified provider effect and operation-specific resource or flush evidence.</summary>
    /// <exception cref="InvalidOperationException">The effect or durable flush was not verified.</exception>
    /// <exception cref="ArgumentException">The resource or evidence does not match the operation.</exception>
    public static void ValidateEffectCompletion(
        WorkspaceVolumeTransitionKind operation,
        long targetResourceGeneration,
        ProviderResourceRef? resource,
        bool effectVerified,
        bool durableFlushVerified)
    {
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (targetResourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(targetResourceGeneration));
        if (!effectVerified)
            throw new InvalidOperationException("Unverified provider effects require reconciliation.");
        if (operation == WorkspaceVolumeTransitionKind.Create)
            throw new ArgumentException("Create has no provider effect completion.", nameof(operation));
        if (operation == WorkspaceVolumeTransitionKind.Release)
        {
            if (resource is not null || durableFlushVerified)
                throw new ArgumentException("Release completion must contain no resource or flush proof.");
            return;
        }

        ArgumentNullException.ThrowIfNull(resource);
        if (targetResourceGeneration < 1 ||
            resource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(resource.ProviderId) ||
            string.IsNullOrWhiteSpace(resource.ResourceId) ||
            resource.Generation != targetResourceGeneration)
            throw new ArgumentException(
                "Completion requires the exact Storage resource at the target generation.",
                nameof(resource));
        if (operation == WorkspaceVolumeTransitionKind.Flush)
        {
            if (!durableFlushVerified)
                throw new InvalidOperationException(
                    "Flush cannot advance data generation without verified durability.");
        }
        else if (durableFlushVerified)
        {
            throw new ArgumentException("Only Flush can provide durable flush verification.",
                nameof(durableFlushVerified));
        }
    }
}

/// <summary>
/// Storage transition payload; typed Environment owner calls pass the canonical fence separately.
/// </summary>
public sealed record WorkspaceVolumeTransitionRequest(
    string VolumeId,
    WorkspaceVolumeTransitionKind Operation,
    long ExpectedTransitionRevision,
    long ExpectedResourceGeneration,
    long ExpectedDataGeneration,
    long? NextDataGeneration,
    string IdempotencyKey,
    WorkspaceVolumeSpec? Specification = null)
{
    /// <summary>Resource generation committed by this operation if it completes.</summary>
    public long TargetResourceGeneration =>
        WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(Operation, ExpectedResourceGeneration);

    /// <summary>Data generation committed by this operation; only Flush can advance it.</summary>
    public long TargetDataGeneration =>
        WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            Operation, ExpectedDataGeneration, NextDataGeneration);

    /// <summary>Checks the operation-specific expected revisions, generations, specification, and idempotency key.</summary>
    /// <exception cref="ArgumentException">The operation-specific specification or generation invariants fail.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A revision or generation is invalid.</exception>
    public WorkspaceVolumeTransitionRequest Validate()
    {
        _ = WorkspaceVolumeIdentity.Validate(VolumeId, nameof(VolumeId));
        if (!Enum.IsDefined(Operation))
            throw new ArgumentOutOfRangeException(nameof(Operation));
        if (ExpectedTransitionRevision < 0 ||
            (Operation != WorkspaceVolumeTransitionKind.Create &&
             ExpectedTransitionRevision == long.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(ExpectedTransitionRevision));
        if (Operation == WorkspaceVolumeTransitionKind.Create)
        {
            if (ExpectedTransitionRevision != 0)
                throw new ArgumentException("Create must start without an existing transition revision.");
            ArgumentNullException.ThrowIfNull(Specification);
            if (!string.Equals(Specification.VolumeId, VolumeId, StringComparison.Ordinal))
                throw new ArgumentException("Create specification volume ID does not match the request.",
                    nameof(Specification));
        }
        else
        {
            if (ExpectedTransitionRevision < 1)
                throw new ArgumentOutOfRangeException(
                    nameof(ExpectedTransitionRevision),
                    "Non-create transitions require an existing owner revision.");
            if (Specification is not null)
                throw new ArgumentException(
                    "Only Create carries a volume specification; other operations resolve the owner snapshot.",
                    nameof(Specification));
        }

        _ = WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(Operation, ExpectedResourceGeneration);
        _ = WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            Operation, ExpectedDataGeneration, NextDataGeneration);
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }

    /// <summary>Validates this request and requires it to name the expected transition kind.</summary>
    /// <exception cref="ArgumentException">The request operation differs from the expected operation.</exception>
    public WorkspaceVolumeTransitionRequest ValidateFor(WorkspaceVolumeTransitionKind expectedOperation)
    {
        if (!Enum.IsDefined(expectedOperation))
            throw new ArgumentOutOfRangeException(nameof(expectedOperation));
        if (Operation != expectedOperation)
            throw new ArgumentException(
                $"The request operation must be {expectedOperation}.",
                nameof(expectedOperation));
        return Validate();
    }
}

/// <summary>Opaque owner kind and identity used for volume authorization and deletion policy.</summary>
public sealed record WorkspaceVolumeOwner
{
    /// <summary>Creates an owner identity with a defined kind and valid opaque ID.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The owner kind is unsupported.</exception>
    /// <exception cref="ArgumentException">The owner ID is invalid.</exception>
    public WorkspaceVolumeOwner(WorkspaceVolumeOwnerKind kind, string id)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        Id = WorkspaceVolumeIdentity.Validate(id, nameof(id));
        Kind = kind;
    }

    public WorkspaceVolumeOwnerKind Kind { get; }
    public string Id { get; }
}

/// <summary>
/// Validated workspace volume request. Shared binding requires an explicit consumer allowlist
/// and a multi-reader access mode; Environment binding requires one Environment identity.
/// </summary>
public sealed record WorkspaceVolumeSpec
{
    /// <summary>Creates a volume specification and enforces binding, access, and allowlist rules.</summary>
    /// <exception cref="ArgumentException">The volume settings or shared/environment scope are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Capacity or an enum value is invalid.</exception>
    public WorkspaceVolumeSpec(
        string volumeId,
        string projectId,
        WorkspaceVolumeOwner owner,
        string? environmentId,
        WorkspaceVolumeBindingMode bindingMode,
        WorkspaceVolumeAccessMode accessMode,
        long capacityGiB,
        string storageClass,
        WorkspaceVolumeConsistency consistency,
        WorkspaceVolumeReclaimPolicy reclaimPolicy,
        WorkspaceVolumeOwnerDeletionPolicy ownerDeletionPolicy,
        ImmutableArray<string> authorizedEnvironmentIds)
    {
        VolumeId = WorkspaceVolumeIdentity.Validate(volumeId, nameof(volumeId));
        ProjectId = WorkspaceVolumeIdentity.Validate(projectId, nameof(projectId));
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        EnvironmentId = environmentId is null
            ? null
            : WorkspaceVolumeIdentity.Validate(environmentId, nameof(environmentId));
        if (!Enum.IsDefined(bindingMode) || !Enum.IsDefined(accessMode) ||
            !Enum.IsDefined(consistency) || !Enum.IsDefined(reclaimPolicy) ||
            !Enum.IsDefined(ownerDeletionPolicy))
            throw new ArgumentOutOfRangeException(nameof(bindingMode));
        if (capacityGiB < 1)
            throw new ArgumentOutOfRangeException(nameof(capacityGiB), "Capacity must be positive.");
        StorageClass = WorkspaceVolumeIdentity.Validate(storageClass, nameof(storageClass));
        if (authorizedEnvironmentIds.IsDefault)
            throw new ArgumentException("Authorized environments must be an initialized collection.",
                nameof(authorizedEnvironmentIds));

        var authorized = authorizedEnvironmentIds
            .Select(id => WorkspaceVolumeIdentity.Validate(id, nameof(authorizedEnvironmentIds)))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToImmutableArray();
        if (authorized.Distinct(StringComparer.Ordinal).Count() != authorized.Length)
            throw new ArgumentException("Authorized environments must not contain duplicates.",
                nameof(authorizedEnvironmentIds));
        if (bindingMode == WorkspaceVolumeBindingMode.Shared && authorized.IsEmpty)
            throw new ArgumentException("Shared volumes require an explicit environment allowlist.",
                nameof(authorizedEnvironmentIds));
        if (bindingMode == WorkspaceVolumeBindingMode.Environment && !authorized.IsEmpty)
            throw new ArgumentException("Environment volumes cannot declare shared consumers.",
                nameof(authorizedEnvironmentIds));
        if (bindingMode == WorkspaceVolumeBindingMode.Environment && EnvironmentId is null)
            throw new ArgumentException("Environment volumes require their owning environment identity.",
                nameof(environmentId));
        if (bindingMode == WorkspaceVolumeBindingMode.Shared && EnvironmentId is not null)
            throw new ArgumentException("Shared volumes cannot be scoped to one environment.",
                nameof(environmentId));
        if (bindingMode == WorkspaceVolumeBindingMode.Shared &&
            accessMode == WorkspaceVolumeAccessMode.ReadWriteOnce)
            throw new ArgumentException("Shared volumes require a multi-reader access mode.", nameof(accessMode));

        BindingMode = bindingMode;
        AccessMode = accessMode;
        CapacityGiB = capacityGiB;
        Consistency = consistency;
        ReclaimPolicy = reclaimPolicy;
        OwnerDeletionPolicy = ownerDeletionPolicy;
        AuthorizedEnvironmentIds = authorized;
    }

    public string VolumeId { get; }
    public string ProjectId { get; }
    public WorkspaceVolumeOwner Owner { get; }
    public string? EnvironmentId { get; }
    public WorkspaceVolumeBindingMode BindingMode { get; }
    public WorkspaceVolumeAccessMode AccessMode { get; }
    /// <summary>Requested capacity in GiB; it must be positive.</summary>
    public long CapacityGiB { get; }
    public string StorageClass { get; }
    public WorkspaceVolumeConsistency Consistency { get; }
    public WorkspaceVolumeReclaimPolicy ReclaimPolicy { get; }
    public WorkspaceVolumeOwnerDeletionPolicy OwnerDeletionPolicy { get; }
    /// <summary>Canonical consumer allowlist required for Shared volumes.</summary>
    public ImmutableArray<string> AuthorizedEnvironmentIds { get; }

    /// <summary>
    /// Returns Delete only for an Environment-bound volume whose reclaim and owner-deletion
    /// policies both request deletion; all other combinations retain the provider data.
    /// </summary>
    public WorkspaceVolumeReclaimPolicy GetEffectiveReleasePolicy() =>
        BindingMode == WorkspaceVolumeBindingMode.Environment &&
        ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete &&
        OwnerDeletionPolicy == WorkspaceVolumeOwnerDeletionPolicy.Delete
            ? WorkspaceVolumeReclaimPolicy.Delete
            : WorkspaceVolumeReclaimPolicy.Retain;

    /// <summary>Returns whether the requested project and Environment may use this volume.</summary>
    public bool AllowsEnvironment(string projectId, string environmentId) =>
        string.Equals(ProjectId, projectId, StringComparison.Ordinal) &&
        (BindingMode == WorkspaceVolumeBindingMode.Environment
            ? string.Equals(EnvironmentId, environmentId, StringComparison.Ordinal)
            : AuthorizedEnvironmentIds.Contains(environmentId, StringComparer.Ordinal));
}

/// <summary>Condition describing one current or historical workspace-volume status fact.</summary>
public sealed record WorkspaceVolumeCondition(
    string Type,
    bool Status,
    string Reason,
    string Message,
    DateTimeOffset LastTransitionAt);

/// <summary>
/// Current owner phase, conditions, and provider resource, tracking transition revision,
/// resource generation, and workspace data generation independently.
/// </summary>
public sealed record WorkspaceVolumeStatus(
    WorkspaceVolumePhase Phase,
    long TransitionRevision,
    long ResourceGeneration,
    long DataGeneration,
    ProviderResourceRef? Resource,
    ImmutableArray<WorkspaceVolumeCondition> Conditions)
{
    /// <summary>Checks generations, phase, conditions, and any pinned Storage resource.</summary>
    /// <exception cref="ArgumentException">The status or provider resource is inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A transition or data/resource generation is invalid.</exception>
    public WorkspaceVolumeStatus Validate()
    {
        if (TransitionRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(TransitionRevision));
        if (ResourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(ResourceGeneration));
        if (DataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(DataGeneration));
        if (!Enum.IsDefined(Phase) || Conditions.IsDefault ||
            Conditions.Any(condition =>
                condition is null ||
                string.IsNullOrWhiteSpace(condition.Type) ||
                string.IsNullOrWhiteSpace(condition.Reason) ||
                string.IsNullOrWhiteSpace(condition.Message) ||
                condition.Type.Any(char.IsControl) ||
                condition.Reason.Any(char.IsControl) ||
                condition.Message.Any(char.IsControl)))
            throw new ArgumentException("Workspace volume status is invalid.", nameof(Conditions));
        if (Resource is { } resource &&
            (resource.Seam != ProviderSeam.Storage ||
             string.IsNullOrWhiteSpace(resource.ProviderId) ||
             string.IsNullOrWhiteSpace(resource.ResourceId) ||
            resource.Generation < 1 ||
            resource.Generation != ResourceGeneration))
            throw new ArgumentException("Workspace volume resource reference is invalid.", nameof(Resource));
        return this;
    }
}

/// <summary>Volume specification paired with its validated owner status.</summary>
public sealed record WorkspaceVolumeRecord(WorkspaceVolumeSpec Spec, WorkspaceVolumeStatus Status)
{
    /// <summary>Requires both specification and status and validates the status before returning this record.</summary>
    /// <exception cref="ArgumentException">A required member is null or status is invalid.</exception>
    public WorkspaceVolumeRecord Validate()
    {
        ArgumentNullException.ThrowIfNull(Spec);
        ArgumentNullException.ThrowIfNull(Status);
        Status.Validate();
        return this;
    }
}

/// <summary>References an exact Storage provider resource generation.</summary>
public sealed record WorkspaceVolumeReference(string ProjectId, string VolumeId, long ResourceGeneration)
{
    /// <summary>Validates project and volume IDs and requires a positive resource generation.</summary>
    /// <exception cref="ArgumentException">An opaque identity is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The resource generation is not positive.</exception>
    public WorkspaceVolumeReference Validate()
    {
        _ = WorkspaceVolumeIdentity.Validate(ProjectId, nameof(ProjectId));
        _ = WorkspaceVolumeIdentity.Validate(VolumeId, nameof(VolumeId));
        if (ResourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(ResourceGeneration));
        return this;
    }
}

/// <summary>Declaration to mount one exact volume generation at a workspace path.</summary>
public sealed record WorkspaceVolumeMountDeclaration(
    WorkspaceVolumeReference Volume,
    string MountPath,
    bool ReadOnly);

/// <summary>
/// Collection of volume mounts under the agent workspace root. It rejects duplicate paths,
/// duplicate volume references, and ancestor/descendant mounts that could shadow each other.
/// </summary>
public sealed record WorkspaceVolumeMountManifest(ImmutableArray<WorkspaceVolumeMountDeclaration> Mounts)
{
    private const string WorkspaceRoot = "/workspace/agentweaver";

    /// <summary>Validates all volume references and non-overlapping paths under the workspace root.</summary>
    /// <exception cref="ArgumentException">A mount path is unsafe, duplicated, or shadows another path.</exception>
    public WorkspaceVolumeMountManifest Validate()
    {
        if (Mounts.IsDefault || Mounts.Any(mount => mount is null))
            throw new ArgumentException("Workspace mount declarations must be initialized and non-null.",
                nameof(Mounts));
        var paths = Mounts.Select(mount =>
        {
            ArgumentNullException.ThrowIfNull(mount.Volume);
            mount.Volume.Validate();
            return ValidateMountPath(mount.MountPath);
        }).ToArray();
        if (Mounts.Select(mount => mount.Volume).Distinct().Count() != Mounts.Length)
            throw new ArgumentException("A volume generation can be declared at only one mount path.",
                nameof(Mounts));
        if (paths.Distinct(StringComparer.Ordinal).Count() != paths.Length)
            throw new ArgumentException("Workspace mount paths must be unique.", nameof(Mounts));
        for (var left = 0; left < paths.Length; left++)
        {
            for (var right = left + 1; right < paths.Length; right++)
            {
                if (paths[left].StartsWith(paths[right] + "/", StringComparison.Ordinal) ||
                    paths[right].StartsWith(paths[left] + "/", StringComparison.Ordinal))
                    throw new ArgumentException("Workspace mount paths cannot shadow one another.", nameof(Mounts));
            }
        }
        return this;
    }

    /// <summary>Requires an absolute normalized path below the allowed workspace root.</summary>
    private static string ValidateMountPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.Any(char.IsControl) || value.Contains('\\') ||
            !value.StartsWith(WorkspaceRoot + "/", StringComparison.Ordinal))
            throw new ArgumentException(
                "Workspace mounts must use an absolute path under /workspace/agentweaver.",
                nameof(value));
        var segments = value.Split('/');
        if (segments.Skip(1).Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Workspace mount paths cannot contain empty or traversal segments.",
                nameof(value));
        return value;
    }
}

/// <summary>Mechanism used by Sandbox to expose an existing Storage volume.</summary>
public enum WorkspaceVolumeAttachmentProtocol
{
    PersistentVolumeClaim,
    FileMaterialization
}

/// <summary>Capabilities of a provisioned Sandbox resource for attaching workspace volumes.</summary>
public sealed record WorkspaceSandboxAttachmentProfile(
    ProviderResourceRef Resource,
    ImmutableHashSet<WorkspaceVolumeAccessMode> SupportedAccessModes,
    ImmutableHashSet<WorkspaceVolumeAttachmentProtocol> SupportedProtocols,
    ImmutableHashSet<string> SupportedStorageProviderIds,
    bool SupportsReadOnlyMounts)
{
    /// <summary>Validates the Sandbox resource and normalizes supported modes and provider IDs.</summary>
    /// <exception cref="ArgumentException">The resource or advertised capability sets are invalid.</exception>
    public WorkspaceSandboxAttachmentProfile Validate()
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(SupportedAccessModes);
        ArgumentNullException.ThrowIfNull(SupportedProtocols);
        ArgumentNullException.ThrowIfNull(SupportedStorageProviderIds);
        if (Resource.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.Generation < 1 ||
            SupportedAccessModes.Any(mode => !Enum.IsDefined(mode)) ||
            SupportedProtocols.Any(protocol => !Enum.IsDefined(protocol)) ||
            SupportedStorageProviderIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Sandbox workspace attachment profile is invalid.");
        return this with
        {
            SupportedAccessModes = SupportedAccessModes.ToImmutableHashSet(),
            SupportedProtocols = SupportedProtocols.ToImmutableHashSet(),
            SupportedStorageProviderIds = SupportedStorageProviderIds.ToImmutableHashSet(StringComparer.Ordinal)
        };
    }
}

/// <summary>
/// Negotiated attachment tuple binding the project/run/Environment, volume and data generation,
/// Sandbox and Storage resources, mount path, access mode, and selected protocol.
/// </summary>
public sealed record WorkspaceVolumeAttachmentNegotiation(
    string ProjectId,
    string EnvironmentId,
    string RunId,
    WorkspaceVolumeReference Volume,
    ProviderResourceRef SandboxResource,
    ProviderResourceRef StorageResource,
    EnvironmentGenerationFence EnvironmentFence,
    long DataGeneration,
    string MountPath,
    bool ReadOnly,
    WorkspaceVolumeAttachmentProtocol Protocol)
{
    /// <summary>Checks scope, provider resources, mount path, and supported protocol metadata.</summary>
    /// <exception cref="ArgumentException">The scope or resource pins are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Data generation or protocol is invalid.</exception>
    public WorkspaceVolumeAttachmentNegotiation Validate()
    {
        _ = WorkspaceVolumeIdentity.Validate(ProjectId, nameof(ProjectId));
        _ = WorkspaceVolumeIdentity.Validate(EnvironmentId, nameof(EnvironmentId));
        _ = WorkspaceVolumeIdentity.Validate(RunId, nameof(RunId));
        ArgumentNullException.ThrowIfNull(Volume);
        ArgumentNullException.ThrowIfNull(SandboxResource);
        ArgumentNullException.ThrowIfNull(StorageResource);
        ArgumentNullException.ThrowIfNull(EnvironmentFence);
        Volume.Validate();
        if (Volume.ProjectId != ProjectId ||
            EnvironmentFence.Owner.ProjectId != ProjectId ||
            EnvironmentFence.Owner.EnvironmentId != EnvironmentId ||
            EnvironmentFence.Owner.RunId != RunId)
            throw new ArgumentException("Workspace attachment identities do not match.", nameof(Volume));
        if (SandboxResource.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(SandboxResource.ProviderId) ||
            string.IsNullOrWhiteSpace(SandboxResource.ResourceId) ||
            SandboxResource.Generation < 1 ||
            StorageResource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(StorageResource.ProviderId) ||
            string.IsNullOrWhiteSpace(StorageResource.ResourceId) ||
            StorageResource.Generation != Volume.ResourceGeneration)
            throw new ArgumentException("Workspace attachment resources are invalid.");
        if (DataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(DataGeneration));
        if (!Enum.IsDefined(Protocol))
            throw new ArgumentOutOfRangeException(nameof(Protocol));
        new WorkspaceVolumeMountManifest(
            [new WorkspaceVolumeMountDeclaration(Volume, MountPath, ReadOnly)]).Validate();
        return this;
    }
}

/// <summary>Selects whether a volume can attach to a specific Sandbox resource and mount declaration.</summary>
public static class WorkspaceVolumeAttachmentNegotiator
{
    /// <summary>
    /// Verifies Environment and volume authorization, exact resource generations, and both providers'
    /// negotiated capabilities before producing the attachment binding.
    /// </summary>
    /// <exception cref="InvalidOperationException">The owner, mount, resource, or capabilities do not allow attachment.</exception>
    /// <exception cref="ArgumentException">A required input or resource identity is malformed.</exception>
    public static WorkspaceVolumeAttachmentNegotiation Negotiate(
        string projectId,
        string environmentId,
        string runId,
        WorkspaceVolumeSpec volume,
        WorkspaceVolumeResource storageResource,
        WorkspaceVolumeMountManifest mountManifest,
        WorkspaceVolumeMountDeclaration mount,
        ProviderResourceRef sandboxResource,
        EnvironmentGenerationFence environmentFence,
        long dataGeneration,
        WorkspaceSandboxAttachmentProfile sandboxProfile,
        WorkspaceVolumeAttachmentProtocol protocol)
    {
        projectId = WorkspaceVolumeIdentity.Validate(projectId, nameof(projectId));
        environmentId = WorkspaceVolumeIdentity.Validate(environmentId, nameof(environmentId));
        runId = WorkspaceVolumeIdentity.Validate(runId, nameof(runId));
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(storageResource);
        ArgumentNullException.ThrowIfNull(mountManifest);
        ArgumentNullException.ThrowIfNull(mount);
        ArgumentNullException.ThrowIfNull(sandboxResource);
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(sandboxProfile);
        storageResource.Validate();
        mountManifest.Validate();
        sandboxProfile = sandboxProfile.Validate();

        if (!Enum.IsDefined(protocol))
            throw new ArgumentOutOfRangeException(nameof(protocol));
        if (dataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(dataGeneration));
        if (!string.Equals(environmentFence.Owner.ProjectId, projectId, StringComparison.Ordinal) ||
            !string.Equals(environmentFence.Owner.EnvironmentId, environmentId, StringComparison.Ordinal) ||
            !string.Equals(environmentFence.Owner.RunId, runId, StringComparison.Ordinal))
            throw new InvalidOperationException("Environment fence does not match the attachment scope.");
        if (!volume.AllowsEnvironment(projectId, environmentId) ||
            !string.Equals(volume.ProjectId, projectId, StringComparison.Ordinal))
            throw new InvalidOperationException("Workspace volume is not authorized for this project and environment.");
        if (volume.BindingMode == WorkspaceVolumeBindingMode.Environment &&
            volume.Owner.Kind == WorkspaceVolumeOwnerKind.Run &&
            !string.Equals(volume.Owner.Id, runId, StringComparison.Ordinal))
            throw new InvalidOperationException("Run-owned workspace volumes are restricted to their owning run.");
        if (!mountManifest.Mounts.Contains(mount) ||
            mount.Volume.ProjectId != volume.ProjectId ||
            mount.Volume.VolumeId != volume.VolumeId ||
            mount.Volume.ResourceGeneration != storageResource.Resource.Generation)
            throw new InvalidOperationException("The requested attachment is not a declared volume generation.");
        if (sandboxResource.Seam != ProviderSeam.Sandbox ||
            string.IsNullOrWhiteSpace(sandboxResource.ProviderId) ||
            string.IsNullOrWhiteSpace(sandboxResource.ResourceId) ||
            sandboxResource.Generation < 1)
            throw new ArgumentException("A provisioned Sandbox resource is required.", nameof(sandboxResource));
        if (sandboxProfile.Resource != sandboxResource)
            throw new InvalidOperationException("Sandbox attachment profile does not match the provisioned resource.");
        if (!sandboxProfile.SupportedStorageProviderIds.Contains(storageResource.Resource.ProviderId) ||
            !sandboxProfile.SupportedAccessModes.Contains(volume.AccessMode) ||
            !sandboxProfile.SupportedProtocols.Contains(protocol))
            throw new InvalidOperationException("Sandbox cannot attach this Storage provider, access mode, or protocol.");
        if (!storageResource.NegotiatedCapabilities.Contains(
                WorkspaceVolumeCapabilities.ForAccessMode(volume.AccessMode)))
            throw new InvalidOperationException("Provisioned Storage resource lacks the requested access mode.");

        var readOnly = mount.ReadOnly || volume.AccessMode == WorkspaceVolumeAccessMode.ReadOnlyMany;
        if (readOnly &&
            (!sandboxProfile.SupportsReadOnlyMounts ||
             !storageResource.NegotiatedCapabilities.Contains(WorkspaceVolumeCapabilities.ReadOnlyMount)))
            throw new InvalidOperationException("Sandbox does not support read-only workspace mounts.");

        return new WorkspaceVolumeAttachmentNegotiation(
            projectId,
            environmentId,
            runId,
            mount.Volume,
            sandboxResource,
            storageResource.Resource,
            environmentFence,
            dataGeneration,
            mount.MountPath,
            readOnly,
            protocol).Validate();
    }
}

/// <summary>Request to bind an exact provisioned volume resource to a fenced Environment.</summary>
public sealed record WorkspaceVolumeBindingRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long DataGeneration,
    string IdempotencyKey)
{
    /// <summary>Validates volume ownership, Environment scope, resource generation, and idempotency key.</summary>
    /// <exception cref="ArgumentException">The resource or idempotency binding is inconsistent.</exception>
    public WorkspaceVolumeBindingRequest Validate()
    {
        WorkspaceVolumeOperationValidation.Validate(
            Spec, ResourceGeneration, EnvironmentFence, Resource, DataGeneration, IdempotencyKey);
        return this;
    }
}

/// <summary>Request to remove a volume's binding from its current Environment consumer.</summary>
public sealed record WorkspaceVolumeUnbindRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long DataGeneration,
    string IdempotencyKey)
{
    /// <summary>Validates the exact volume, Storage resource, generation, and Environment scope.</summary>
    public WorkspaceVolumeUnbindRequest Validate()
    {
        WorkspaceVolumeOperationValidation.Validate(
            Spec, ResourceGeneration, EnvironmentFence, Resource, DataGeneration, IdempotencyKey);
        return this;
    }
}

/// <summary>Request to attach a previously negotiated volume to a Sandbox resource.</summary>
public sealed record WorkspaceVolumeAttachRequest(
    WorkspaceVolumeAttachmentNegotiation Negotiation,
    string IdempotencyKey)
{
    /// <summary>Validates the attachment negotiation and idempotency key.</summary>
    /// <exception cref="ArgumentException">The negotiation or key is invalid.</exception>
    public WorkspaceVolumeAttachRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Negotiation);
        Negotiation.Validate();
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }
}

/// <summary>Request to flush mounted workspace data and advance its data generation.</summary>
public sealed record WorkspaceVolumeFlushRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long ExpectedDataGeneration,
    string IdempotencyKey)
{
    /// <summary>Provider binding pinned when the volume was provisioned; it must match the exact resource.</summary>
    public WorkspaceVolumeProviderBindingSnapshot? ProviderBinding { get; init; }

    /// <summary>Validates Environment scope, Storage resource, and expected data-generation capacity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The data generation cannot be advanced.</exception>
    public WorkspaceVolumeFlushRequest Validate()
    {
        WorkspaceVolumeOperationValidation.Validate(
            Spec, ResourceGeneration, EnvironmentFence, Resource, ExpectedDataGeneration, IdempotencyKey);
        if (ExpectedDataGeneration == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(ExpectedDataGeneration));
        _ = ProviderBinding?.ValidateFor(Resource);
        return this;
    }
}

/// <summary>
/// Storage provider's proof that one exact volume generation was durably flushed to the
/// next data generation, using either a tree digest or provider checkpoint identity.
/// </summary>
public sealed record WorkspaceVolumeFlushReceipt(
    int ContractVersion,
    EnvironmentGenerationFence EnvironmentFence,
    WorkspaceVolumeReference Volume,
    ProviderResourceRef Resource,
    long ExpectedDataGeneration,
    long DataGeneration,
    string IdempotencyKey,
    string? TreeSha256,
    string? ProviderCheckpointId)
{
    /// <summary>Validates the owner fence and requires exactly one supported durability proof.</summary>
    /// <exception cref="ArgumentException">The resource, generation, proof, or owner binding is invalid.</exception>
    public WorkspaceVolumeFlushReceipt Validate()
    {
        ArgumentNullException.ThrowIfNull(EnvironmentFence);
        ArgumentNullException.ThrowIfNull(Volume);
        ArgumentNullException.ThrowIfNull(Resource);
        Volume.Validate();
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        if (ContractVersion != 1 ||
            EnvironmentFence.LifecycleGeneration < 1 ||
            EnvironmentFence.Owner.ProjectId != Volume.ProjectId ||
            Resource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.Generation != Volume.ResourceGeneration ||
            ExpectedDataGeneration < 0 ||
            ExpectedDataGeneration == long.MaxValue ||
            DataGeneration != ExpectedDataGeneration + 1)
            throw new ArgumentException("The workspace flush receipt has invalid owner or generation evidence.");
        if ((TreeSha256 is null) == (ProviderCheckpointId is null))
            throw new ArgumentException("A workspace flush requires exactly one tree hash or provider checkpoint.");
        if (TreeSha256 is { } hash && !SessionSuspendResumeManifestValidation.IsSha256(hash))
            throw new ArgumentException("The workspace flush tree hash must be a lowercase SHA-256 digest.");
        if (ProviderCheckpointId is { } checkpoint &&
            !SessionSuspendResumeManifestValidation.IsIdentifier(checkpoint, 256))
            throw new ArgumentException("The workspace flush provider checkpoint is invalid.");
        return this;
    }

    /// <summary>Requires the flush receipt to match the exact request and idempotency key.</summary>
    public WorkspaceVolumeFlushReceipt ValidateFor(WorkspaceVolumeFlushRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        Validate();
        if (EnvironmentFence != request.EnvironmentFence ||
            Volume.ProjectId != request.Spec.ProjectId ||
            Volume.VolumeId != request.Spec.VolumeId ||
            Volume.ResourceGeneration != request.ResourceGeneration ||
            Resource != request.Resource ||
            ExpectedDataGeneration != request.ExpectedDataGeneration ||
            IdempotencyKey != request.IdempotencyKey)
            throw new ArgumentException("The workspace flush receipt does not match the exact requested operation.");
        return this;
    }
}

/// <summary>Persistable flush proof plus optional bounded runtime-host receipt.</summary>
public sealed record WorkspaceVolumeFlushEvidence(
    WorkspaceVolumeFlushReceipt Receipt,
    JsonElement? RuntimeHostReceipt)
{
    /// <summary>Validates and clones the proof so it does not depend on a caller-owned JSON document.</summary>
    /// <exception cref="ArgumentException">The receipt or host JSON is invalid or too large.</exception>
    public WorkspaceVolumeFlushEvidence Validate()
    {
        ArgumentNullException.ThrowIfNull(Receipt);
        Receipt.Validate();
        if (RuntimeHostReceipt is { } host &&
            (host.ValueKind != JsonValueKind.Object ||
             System.Text.Encoding.UTF8.GetByteCount(host.GetRawText()) > 128 * 1024))
            throw new ArgumentException("Runtime host evidence must be a bounded JSON object.");
        return this with { RuntimeHostReceipt = RuntimeHostReceipt?.Clone() };
    }
}

/// <summary>Request to provision a new Storage resource generation.</summary>
public sealed record WorkspaceVolumeProvisionRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    string IdempotencyKey)
{
    /// <summary>Requires a valid volume specification, positive generation, and idempotency key.</summary>
    public WorkspaceVolumeProvisionRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Spec);
        if (ResourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(ResourceGeneration));
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }
}

/// <summary>
/// Non-secret provider/options snapshot and release descriptor pinned with a Storage resource.
/// Validation rejects credential-like field names so persisted release metadata cannot carry secrets.
/// </summary>
public sealed record WorkspaceVolumeProviderBindingSnapshot(
    string ProviderId,
    string AdapterVersion,
    int OptionsSchemaVersion,
    string OptionsRevision,
    JsonElement OptionsSnapshot,
    JsonElement ReleaseDescriptor)
{
    /// <summary>Validates adapter/options pins and returns cloned, secret-field-free JSON objects.</summary>
    /// <exception cref="ArgumentException">The provider differs from the resource or JSON includes secret-bearing fields.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The options schema version is not positive.</exception>
    public WorkspaceVolumeProviderBindingSnapshot ValidateFor(ProviderResourceRef resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (string.IsNullOrWhiteSpace(ProviderId) ||
            ProviderId.Length > 256 ||
            ProviderId.Any(char.IsControl) ||
            !string.Equals(ProviderId, resource.ProviderId, StringComparison.Ordinal))
            throw new ArgumentException("The provider binding does not match its Storage resource.", nameof(ProviderId));
        if (!Version.TryParse(AdapterVersion, out var version) ||
            version.Major < 1 ||
            !string.Equals(version.ToString(), AdapterVersion, StringComparison.Ordinal))
            throw new ArgumentException("The provider binding adapter version must be exact and canonical.", nameof(AdapterVersion));
        if (OptionsSchemaVersion < 1)
            throw new ArgumentOutOfRangeException(nameof(OptionsSchemaVersion));
        if (string.IsNullOrWhiteSpace(OptionsRevision) ||
            OptionsRevision.Length > 128 ||
            OptionsRevision.Any(char.IsControl))
            throw new ArgumentException("A bounded provider options revision is required.", nameof(OptionsRevision));
        ValidateObject(OptionsSnapshot, nameof(OptionsSnapshot));
        ValidateObject(ReleaseDescriptor, nameof(ReleaseDescriptor));
        RejectSecretFields(OptionsSnapshot);
        RejectSecretFields(ReleaseDescriptor);
        return this with
        {
            OptionsSnapshot = OptionsSnapshot.Clone(),
            ReleaseDescriptor = ReleaseDescriptor.Clone()
        };
    }

    /// <summary>Requires a JSON object for a persisted provider snapshot or release descriptor.</summary>
    private static void ValidateObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Provider snapshots must be JSON objects.", name);
    }

    /// <summary>Recursively rejects field names that could store credential material.</summary>
    private static void RejectSecretFields(JsonElement value)
    {
        foreach (var property in value.EnumerateObject())
        {
            var name = property.Name;
            if (name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("privatekey", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("accesskey", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("accountkey", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("sas", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Provider snapshots cannot persist secret-bearing fields.");
            if (property.Value.ValueKind == JsonValueKind.Object)
                RejectSecretFields(property.Value);
            else if (property.Value.ValueKind == JsonValueKind.Array)
                foreach (var item in property.Value.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object)
                        RejectSecretFields(item);
        }
    }
}

/// <summary>Exact request to release or retain one Storage resource after owner lifecycle completion.</summary>
public sealed record WorkspaceVolumeReleaseRequest(
    WorkspaceVolumeReference Volume,
    ProviderResourceRef Resource,
    WorkspaceVolumeBindingMode BindingMode,
    WorkspaceVolumeReclaimPolicy ReclaimPolicy,
    WorkspaceVolumeOwnerDeletionPolicy OwnerDeletionPolicy,
    WorkspaceVolumeProviderBindingSnapshot ProviderBinding,
    string IdempotencyKey)
{
    /// <summary>Validates the Storage resource, provider snapshot, reclaim policy, and exact operation key.</summary>
    /// <exception cref="ArgumentException">The requested deletion conflicts with owner/binding policy.</exception>
    public WorkspaceVolumeReleaseRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Volume);
        Volume.Validate();
        ArgumentNullException.ThrowIfNull(Resource);
        if (Resource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.Generation != Volume.ResourceGeneration)
            throw new ArgumentException(
                "Release requires the exact Storage resource and volume generation.",
                nameof(Resource));
        if (!Enum.IsDefined(BindingMode))
            throw new ArgumentOutOfRangeException(nameof(BindingMode));
        if (!Enum.IsDefined(ReclaimPolicy))
            throw new ArgumentOutOfRangeException(nameof(ReclaimPolicy));
        if (!Enum.IsDefined(OwnerDeletionPolicy))
            throw new ArgumentOutOfRangeException(nameof(OwnerDeletionPolicy));
        if (ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Delete &&
            (BindingMode != WorkspaceVolumeBindingMode.Environment ||
             OwnerDeletionPolicy != WorkspaceVolumeOwnerDeletionPolicy.Delete))
            throw new ArgumentException(
                "Delete requires an Environment-bound volume and an owner deletion policy of Delete.",
                nameof(ReclaimPolicy));
        ArgumentNullException.ThrowIfNull(ProviderBinding);
        _ = ProviderBinding.ValidateFor(Resource);
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }
}

/// <summary>Provider's result when releasing a Storage resource.</summary>
public enum WorkspaceVolumeReleaseDisposition
{
    Released,
    AlreadyAbsent,
    Retained
}

/// <summary>Provider receipt for the exact release request and resulting disposition.</summary>
public sealed record WorkspaceVolumeReleaseReceipt(
    ProviderResourceRef Resource,
    string IdempotencyKey,
    WorkspaceVolumeReleaseDisposition Disposition)
{
    /// <summary>Checks resource, idempotency key, and disposition against the requested reclaim policy.</summary>
    /// <exception cref="ArgumentException">The receipt does not match the exact request or expected disposition.</exception>
    public WorkspaceVolumeReleaseReceipt ValidateFor(WorkspaceVolumeReleaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        ArgumentNullException.ThrowIfNull(Resource);
        if (Resource != request.Resource ||
            !string.Equals(IdempotencyKey, request.IdempotencyKey, StringComparison.Ordinal) ||
            !Enum.IsDefined(Disposition))
            throw new ArgumentException(
                "Release receipt must identify the exact provider resource, idempotency key, and outcome.",
                nameof(request));
        if (request.ReclaimPolicy == WorkspaceVolumeReclaimPolicy.Retain
                ? Disposition != WorkspaceVolumeReleaseDisposition.Retained
                : Disposition != WorkspaceVolumeReleaseDisposition.Released)
            throw new ArgumentException(
                "Release receipt outcome does not match the requested reclaim policy.",
                nameof(Disposition));
        return this;
    }
}

/// <summary>Provisioned Storage resource, negotiated capabilities, and pinned provider metadata.</summary>
public sealed record WorkspaceVolumeResource(
    ProviderResourceRef Resource,
    ImmutableHashSet<string> NegotiatedCapabilities,
    WorkspaceVolumeProviderBindingSnapshot ProviderBinding)
{
    /// <summary>Validates the Storage identity and binding, returning a normalized capability set.</summary>
    /// <exception cref="ArgumentException">The provider resource or binding is invalid.</exception>
    public WorkspaceVolumeResource Validate()
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(NegotiatedCapabilities);
        ArgumentNullException.ThrowIfNull(ProviderBinding);
        if (Resource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.Generation < 1 ||
            NegotiatedCapabilities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Provisioned workspace volume resource is invalid.");
        _ = ProviderBinding.ValidateFor(Resource);
        return this with
        {
            NegotiatedCapabilities = NegotiatedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
        };
    }
}

/// <summary>Storage provider boundary for provisioning, release, and optionally verified durable flush.</summary>
public interface IWorkspaceVolumeProvider
{
    /// <summary>Creates the requested provider resource generation.</summary>
    Task<WorkspaceVolumeResource> ProvisionAsync(
        WorkspaceVolumeProvisionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases or retains the exact resource according to the validated request.</summary>
    Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
        WorkspaceVolumeReleaseRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Flushes through a trusted mounted-workspace callback; unsupported providers reject
    /// the operation rather than claiming durable data.
    /// </summary>
    Task<WorkspaceVolumeFlushReceipt> FlushAsync(
        WorkspaceVolumeFlushRequest request,
        Func<WorkspaceVolumeFlushRequest, CancellationToken, Task<WorkspaceVolumeFlushReceipt>> trustedMountedFlush,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The Storage provider has no verified workspace flush operation.");
}

/// <summary>Capability identifiers negotiated for workspace volume access and durability.</summary>
public static class WorkspaceVolumeCapabilities
{
    /// <summary>Single-node read/write access.</summary>
    public const string ReadWriteOnce = "workspace.access.rwo";
    /// <summary>Multi-node read/write access.</summary>
    public const string ReadWriteMany = "workspace.access.rwx";
    /// <summary>Multi-node read-only access.</summary>
    public const string ReadOnlyMany = "workspace.access.rox";
    /// <summary>Provider guarantees durable flush completion.</summary>
    public const string DurableFlush = "workspace.flush.durable";
    /// <summary>Provider can attach an already provisioned volume.</summary>
    public const string ExistingVolumeAttach = "workspace.attach.existing-volume";
    /// <summary>Provider can mount a volume read-only.</summary>
    public const string ReadOnlyMount = "workspace.mount.read-only";

    /// <summary>Maps an access mode to its provider-negotiated capability ID.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The access mode is unsupported.</exception>
    public static string ForAccessMode(WorkspaceVolumeAccessMode accessMode) => accessMode switch
    {
        WorkspaceVolumeAccessMode.ReadWriteOnce => ReadWriteOnce,
        WorkspaceVolumeAccessMode.ReadWriteMany => ReadWriteMany,
        WorkspaceVolumeAccessMode.ReadOnlyMany => ReadOnlyMany,
        _ => throw new ArgumentOutOfRangeException(nameof(accessMode))
    };
}

/// <summary>Validates opaque volume and operation identities shared by owner contracts.</summary>
internal static class WorkspaceVolumeIdentity
{
    /// <summary>Returns a bounded supported identifier or rejects it.</summary>
    /// <exception cref="ArgumentException">The identity is empty, too long, or has unsupported characters.</exception>
    public static string Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque workspace volume identity is required.", name);
        return value;
    }
}

/// <summary>Checks common ownership and provider pins for bind, unbind, and flush requests.</summary>
internal static class WorkspaceVolumeOperationValidation
{
    /// <summary>Requires an authorized Environment and the exact Storage resource generation.</summary>
    /// <exception cref="InvalidOperationException">The volume is not authorized for this Environment or run.</exception>
    /// <exception cref="ArgumentException">The Storage resource or idempotency key is invalid.</exception>
    public static void Validate(
        WorkspaceVolumeSpec spec,
        long resourceGeneration,
        EnvironmentGenerationFence environmentFence,
        ProviderResourceRef resource,
        long dataGeneration,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentNullException.ThrowIfNull(resource);
        if (resourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(resourceGeneration));
        if (spec.ProjectId != environmentFence.Owner.ProjectId ||
            !spec.AllowsEnvironment(spec.ProjectId, environmentFence.Owner.EnvironmentId))
            throw new InvalidOperationException("Workspace volume is not authorized for this Environment.");
        if (spec.BindingMode == WorkspaceVolumeBindingMode.Environment &&
            spec.Owner.Kind == WorkspaceVolumeOwnerKind.Run &&
            spec.Owner.Id != environmentFence.Owner.RunId)
            throw new InvalidOperationException("Run-owned workspace volumes are restricted to their owning run.");
        if (resource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(resource.ProviderId) ||
            string.IsNullOrWhiteSpace(resource.ResourceId) ||
            resource.Generation != resourceGeneration)
            throw new ArgumentException(
                "Workspace volume operation requires the Storage resource pinned to this generation.",
                nameof(resource));
        if (dataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(dataGeneration));
        _ = WorkspaceVolumeIdentity.Validate(idempotencyKey, nameof(idempotencyKey));
    }
}
