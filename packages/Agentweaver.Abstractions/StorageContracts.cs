using System.Collections.Immutable;

namespace Agentweaver.Abstractions;

public enum WorkspaceVolumeBindingMode
{
    Environment,
    Shared
}

public enum WorkspaceVolumeAccessMode
{
    ReadWriteOnce,
    ReadWriteMany,
    ReadOnlyMany
}

public enum WorkspaceVolumeConsistency
{
    Strict,
    Eventual
}

public enum WorkspaceVolumeReclaimPolicy
{
    Delete,
    Retain
}

public enum WorkspaceVolumeOwnerDeletionPolicy
{
    Delete,
    Retain,
    Unbind
}

public enum WorkspaceVolumeOwnerKind
{
    Run,
    Agent,
    Team
}

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

public static class WorkspaceVolumeTransitionRules
{
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
    public long TargetResourceGeneration =>
        WorkspaceVolumeTransitionRules.GetTargetResourceGeneration(Operation, ExpectedResourceGeneration);

    public long TargetDataGeneration =>
        WorkspaceVolumeTransitionRules.GetTargetDataGeneration(
            Operation, ExpectedDataGeneration, NextDataGeneration);

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

public sealed record WorkspaceVolumeOwner
{
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

public sealed record WorkspaceVolumeSpec
{
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
    public long CapacityGiB { get; }
    public string StorageClass { get; }
    public WorkspaceVolumeConsistency Consistency { get; }
    public WorkspaceVolumeReclaimPolicy ReclaimPolicy { get; }
    public WorkspaceVolumeOwnerDeletionPolicy OwnerDeletionPolicy { get; }
    public ImmutableArray<string> AuthorizedEnvironmentIds { get; }

    public bool AllowsEnvironment(string projectId, string environmentId) =>
        string.Equals(ProjectId, projectId, StringComparison.Ordinal) &&
        (BindingMode == WorkspaceVolumeBindingMode.Environment
            ? string.Equals(EnvironmentId, environmentId, StringComparison.Ordinal)
            : AuthorizedEnvironmentIds.Contains(environmentId, StringComparer.Ordinal));
}

public sealed record WorkspaceVolumeCondition(
    string Type,
    bool Status,
    string Reason,
    string Message,
    DateTimeOffset LastTransitionAt);

/// <summary>
/// Tracks the owner transition revision, provider resource generation, and workspace data generation
/// independently.
/// </summary>
public sealed record WorkspaceVolumeStatus(
    WorkspaceVolumePhase Phase,
    long TransitionRevision,
    long ResourceGeneration,
    long DataGeneration,
    ProviderResourceRef? Resource,
    ImmutableArray<WorkspaceVolumeCondition> Conditions)
{
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

public sealed record WorkspaceVolumeRecord(WorkspaceVolumeSpec Spec, WorkspaceVolumeStatus Status)
{
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
    public WorkspaceVolumeReference Validate()
    {
        _ = WorkspaceVolumeIdentity.Validate(ProjectId, nameof(ProjectId));
        _ = WorkspaceVolumeIdentity.Validate(VolumeId, nameof(VolumeId));
        if (ResourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(ResourceGeneration));
        return this;
    }
}

public sealed record WorkspaceVolumeMountDeclaration(
    WorkspaceVolumeReference Volume,
    string MountPath,
    bool ReadOnly);

public sealed record WorkspaceVolumeMountManifest(ImmutableArray<WorkspaceVolumeMountDeclaration> Mounts)
{
    private const string WorkspaceRoot = "/workspace/agentweaver";

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

public enum WorkspaceVolumeAttachmentProtocol
{
    PersistentVolumeClaim,
    FileMaterialization
}

public sealed record WorkspaceSandboxAttachmentProfile(
    ProviderResourceRef Resource,
    ImmutableHashSet<WorkspaceVolumeAccessMode> SupportedAccessModes,
    ImmutableHashSet<WorkspaceVolumeAttachmentProtocol> SupportedProtocols,
    ImmutableHashSet<string> SupportedStorageProviderIds,
    bool SupportsReadOnlyMounts)
{
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

public static class WorkspaceVolumeAttachmentNegotiator
{
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

public sealed record WorkspaceVolumeBindingRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long DataGeneration,
    string IdempotencyKey)
{
    public WorkspaceVolumeBindingRequest Validate()
    {
        WorkspaceVolumeOperationValidation.Validate(
            Spec, ResourceGeneration, EnvironmentFence, Resource, DataGeneration, IdempotencyKey);
        return this;
    }
}

public sealed record WorkspaceVolumeUnbindRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long DataGeneration,
    string IdempotencyKey)
{
    public WorkspaceVolumeUnbindRequest Validate()
    {
        WorkspaceVolumeOperationValidation.Validate(
            Spec, ResourceGeneration, EnvironmentFence, Resource, DataGeneration, IdempotencyKey);
        return this;
    }
}

public sealed record WorkspaceVolumeAttachRequest(
    WorkspaceVolumeAttachmentNegotiation Negotiation,
    string IdempotencyKey)
{
    public WorkspaceVolumeAttachRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Negotiation);
        Negotiation.Validate();
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }
}

public sealed record WorkspaceVolumeFlushRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    EnvironmentGenerationFence EnvironmentFence,
    ProviderResourceRef Resource,
    long ExpectedDataGeneration,
    string IdempotencyKey)
{
    public WorkspaceVolumeFlushRequest Validate()
    {
        WorkspaceVolumeOperationValidation.Validate(
            Spec, ResourceGeneration, EnvironmentFence, Resource, ExpectedDataGeneration, IdempotencyKey);
        return this;
    }
}

public sealed record WorkspaceVolumeProvisionRequest(
    WorkspaceVolumeSpec Spec,
    long ResourceGeneration,
    string IdempotencyKey)
{
    public WorkspaceVolumeProvisionRequest Validate()
    {
        ArgumentNullException.ThrowIfNull(Spec);
        if (ResourceGeneration < 1)
            throw new ArgumentOutOfRangeException(nameof(ResourceGeneration));
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }
}

public sealed record WorkspaceVolumeReleaseRequest(
    WorkspaceVolumeReference Volume,
    ProviderResourceRef Resource,
    WorkspaceVolumeReclaimPolicy ReclaimPolicy,
    string IdempotencyKey)
{
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
        if (!Enum.IsDefined(ReclaimPolicy))
            throw new ArgumentOutOfRangeException(nameof(ReclaimPolicy));
        _ = WorkspaceVolumeIdentity.Validate(IdempotencyKey, nameof(IdempotencyKey));
        return this;
    }
}

public enum WorkspaceVolumeReleaseDisposition
{
    Released,
    AlreadyAbsent,
    Retained
}

public sealed record WorkspaceVolumeReleaseReceipt(
    ProviderResourceRef Resource,
    string IdempotencyKey,
    WorkspaceVolumeReleaseDisposition Disposition)
{
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
                ? Disposition == WorkspaceVolumeReleaseDisposition.Released
                : Disposition == WorkspaceVolumeReleaseDisposition.Retained)
            throw new ArgumentException(
                "Release receipt outcome does not match the requested reclaim policy.",
                nameof(Disposition));
        return this;
    }
}

public sealed record WorkspaceVolumeResource(
    ProviderResourceRef Resource,
    ImmutableHashSet<string> NegotiatedCapabilities)
{
    public WorkspaceVolumeResource Validate()
    {
        ArgumentNullException.ThrowIfNull(Resource);
        ArgumentNullException.ThrowIfNull(NegotiatedCapabilities);
        if (Resource.Seam != ProviderSeam.Storage ||
            string.IsNullOrWhiteSpace(Resource.ProviderId) ||
            string.IsNullOrWhiteSpace(Resource.ResourceId) ||
            Resource.Generation < 1 ||
            NegotiatedCapabilities.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Provisioned workspace volume resource is invalid.");
        return this with
        {
            NegotiatedCapabilities = NegotiatedCapabilities.ToImmutableHashSet(StringComparer.Ordinal)
        };
    }
}

public interface IWorkspaceVolumeProvider
{
    Task<WorkspaceVolumeResource> ProvisionAsync(
        WorkspaceVolumeProvisionRequest request,
        CancellationToken cancellationToken = default);

    Task<WorkspaceVolumeReleaseReceipt> ReleaseAsync(
        WorkspaceVolumeReleaseRequest request,
        CancellationToken cancellationToken = default);
}

public static class WorkspaceVolumeCapabilities
{
    public const string ReadWriteOnce = "workspace.access.rwo";
    public const string ReadWriteMany = "workspace.access.rwx";
    public const string ReadOnlyMany = "workspace.access.rox";
    public const string DurableFlush = "workspace.flush.durable";
    public const string ExistingVolumeAttach = "workspace.attach.existing-volume";
    public const string ReadOnlyMount = "workspace.mount.read-only";

    public static string ForAccessMode(WorkspaceVolumeAccessMode accessMode) => accessMode switch
    {
        WorkspaceVolumeAccessMode.ReadWriteOnce => ReadWriteOnce,
        WorkspaceVolumeAccessMode.ReadWriteMany => ReadWriteMany,
        WorkspaceVolumeAccessMode.ReadOnlyMany => ReadOnlyMany,
        _ => throw new ArgumentOutOfRangeException(nameof(accessMode))
    };
}

internal static class WorkspaceVolumeIdentity
{
    public static string Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 ||
            value.Any(character => !char.IsAsciiLetterOrDigit(character) &&
                character is not ('.' or '_' or '-' or ':')))
            throw new ArgumentException("A valid opaque workspace volume identity is required.", name);
        return value;
    }
}

internal static class WorkspaceVolumeOperationValidation
{
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
