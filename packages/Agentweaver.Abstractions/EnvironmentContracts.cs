using System.Text.Json;

namespace Agentweaver.Abstractions;

public sealed record EnvironmentOwnerIdentity
{
    public EnvironmentOwnerIdentity(
        string tenantId,
        string projectId,
        string runId,
        string environmentId)
    {
        TenantId = Validate(tenantId, nameof(tenantId));
        ProjectId = Validate(projectId, nameof(projectId));
        RunId = Validate(runId, nameof(runId));
        EnvironmentId = Validate(environmentId, nameof(environmentId));
    }

    public string TenantId { get; }
    public string ProjectId { get; }
    public string RunId { get; }
    public string EnvironmentId { get; }

    private static string Validate(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Environment owner identifiers must be bounded and contain no control characters.", name);
        return value;
    }
}

public sealed record EnvironmentGenerationFence
{
    public EnvironmentGenerationFence(EnvironmentOwnerIdentity owner, long lifecycleGeneration)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (lifecycleGeneration <= 0)
            throw new ArgumentOutOfRangeException(nameof(lifecycleGeneration), "Lifecycle generation must be positive.");
        LifecycleGeneration = lifecycleGeneration;
    }

    public EnvironmentOwnerIdentity Owner { get; }
    public long LifecycleGeneration { get; }
}

public enum EnvironmentLifecycleState
{
    Active,
    Released
}

public sealed record EnvironmentLifecycleSnapshot(
    EnvironmentGenerationFence Fence,
    EnvironmentLifecycleState State);

public sealed record EnvironmentLifecycleTransitionRequest
{
    public EnvironmentLifecycleTransitionRequest(
        EnvironmentOwnerIdentity owner,
        long expectedLifecycleGeneration,
        EnvironmentLifecycleState targetState,
        string idempotencyKey)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (expectedLifecycleGeneration < 0)
            throw new ArgumentOutOfRangeException(
                nameof(expectedLifecycleGeneration),
                "Expected lifecycle generation cannot be negative.");
        if (!Enum.IsDefined(targetState))
            throw new ArgumentOutOfRangeException(nameof(targetState));
        if (expectedLifecycleGeneration == 0 && targetState != EnvironmentLifecycleState.Active)
            throw new ArgumentException(
                "Only an active environment can be explicitly registered.",
                nameof(targetState));
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey, nameof(idempotencyKey));
        if (idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl))
            throw new ArgumentException("Idempotency keys must be bounded and contain no control characters.", nameof(idempotencyKey));

        ExpectedLifecycleGeneration = expectedLifecycleGeneration;
        TargetState = targetState;
        IdempotencyKey = idempotencyKey;
    }

    public EnvironmentOwnerIdentity Owner { get; }
    public long ExpectedLifecycleGeneration { get; }
    public EnvironmentLifecycleState TargetState { get; }
    public string IdempotencyKey { get; }
}

public sealed record EnvironmentLifecycleTransitionResult(
    EnvironmentLifecycleSnapshot Snapshot,
    bool Replayed);

public enum EnvironmentWorkspaceVolumeOperation
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

public enum EnvironmentWorkspaceVolumeState
{
    Requested,
    Ready,
    Bound,
    Attached,
    Released
}

public enum EnvironmentWorkspaceVolumeTransitionState
{
    Reserved,
    Completed,
    ReconciliationRequired,
    Reconciled,
    Failed,
    Stale
}

public sealed record EnvironmentWorkspaceVolumeSnapshot
{
    public EnvironmentWorkspaceVolumeSnapshot(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long transitionRevision,
        long resourceGeneration,
        long dataGeneration,
        EnvironmentWorkspaceVolumeState phase,
        EnvironmentWorkspaceVolumeOperation lastOperation,
        JsonElement specification,
        ProviderResourceRef? resource)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        if (transitionRevision < 1)
            throw new ArgumentOutOfRangeException(nameof(transitionRevision));
        if (resourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(resourceGeneration));
        if (dataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(dataGeneration));
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (!Enum.IsDefined(lastOperation))
            throw new ArgumentOutOfRangeException(nameof(lastOperation));
        if (specification.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Workspace-volume specifications must be JSON objects.", nameof(specification));
        if ((phase is EnvironmentWorkspaceVolumeState.Ready or
            EnvironmentWorkspaceVolumeState.Bound or
            EnvironmentWorkspaceVolumeState.Attached) &&
            resourceGeneration == 0)
            throw new ArgumentException(
                "A provider-backed workspace-volume phase requires a positive resource generation.",
                nameof(resourceGeneration));
        ValidateStorageResource(resource, resourceGeneration, "resource");
        if ((resourceGeneration == 0 || phase == EnvironmentWorkspaceVolumeState.Released) && resource is not null)
            throw new ArgumentException(
                "A requested or released volume cannot expose a provider resource.",
                nameof(resource));
        if (resourceGeneration > 0 && phase != EnvironmentWorkspaceVolumeState.Released && resource is null)
            throw new ArgumentException(
                "An active provider-backed volume must expose its pinned resource.",
                nameof(resource));

        EnvironmentFence = environmentFence;
        VolumeId = volumeId;
        TransitionRevision = transitionRevision;
        ResourceGeneration = resourceGeneration;
        DataGeneration = dataGeneration;
        Phase = phase;
        LastOperation = lastOperation;
        Specification = specification.Clone();
        Resource = resource;
    }

    public EnvironmentGenerationFence EnvironmentFence { get; }
    public string VolumeId { get; }
    public long TransitionRevision { get; }
    public long ResourceGeneration { get; }
    public long DataGeneration { get; }
    public EnvironmentWorkspaceVolumeState Phase { get; }
    public EnvironmentWorkspaceVolumeOperation LastOperation { get; }
    public JsonElement Specification { get; }
    public ProviderResourceRef? Resource { get; }

    internal static void ValidateStorageResource(
        ProviderResourceRef? resource,
        long generation,
        string parameterName)
    {
        if (resource is null)
            return;
        if (resource.Seam != ProviderSeam.Storage || resource.Generation != generation)
            throw new ArgumentException(
                "A workspace-volume provider resource must use the Storage seam and match ResourceGeneration.",
                parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ProviderId, parameterName);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource.ResourceId, parameterName);
        if (resource.ProviderId.Length > 256 || resource.ResourceId.Length > 512 ||
            resource.ProviderId.Any(char.IsControl) || resource.ResourceId.Any(char.IsControl))
            throw new ArgumentException(
                "Workspace-volume provider resource identifiers must be bounded and contain no control characters.",
                parameterName);
    }
}

public sealed record EnvironmentWorkspaceVolumeTransitionReservation
{
    public EnvironmentWorkspaceVolumeTransitionReservation(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        ProviderResourceRef? currentResource,
        long targetTransitionRevision,
        long targetResourceGeneration,
        long targetDataGeneration,
        EnvironmentWorkspaceVolumeState targetPhase,
        EnvironmentWorkspaceVolumeOperation operation,
        EnvironmentWorkspaceVolumeTransitionState transitionState,
        bool replayed)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        if (expectedTransitionRevision < 0 ||
            targetTransitionRevision != expectedTransitionRevision + 1 ||
            (expectedTransitionRevision == 0) != (operation == EnvironmentWorkspaceVolumeOperation.Create))
            throw new ArgumentOutOfRangeException(nameof(expectedTransitionRevision));
        if (expectedResourceGeneration < 0 || targetResourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedResourceGeneration));
        if (expectedDataGeneration < 0 || targetDataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedDataGeneration));
        if (!Enum.IsDefined(targetPhase))
            throw new ArgumentOutOfRangeException(nameof(targetPhase));
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (!Enum.IsDefined(transitionState))
            throw new ArgumentOutOfRangeException(nameof(transitionState));
        EnvironmentWorkspaceVolumeSnapshot.ValidateStorageResource(
            currentResource,
            expectedResourceGeneration,
            nameof(currentResource));
        if ((expectedResourceGeneration == 0) != (currentResource is null))
            throw new ArgumentException(
                "CurrentResource must be present exactly when the volume has a provider resource generation.",
                nameof(currentResource));

        OperationId = operationId;
        EnvironmentFence = environmentFence;
        VolumeId = volumeId;
        ExpectedTransitionRevision = expectedTransitionRevision;
        ExpectedResourceGeneration = expectedResourceGeneration;
        ExpectedDataGeneration = expectedDataGeneration;
        CurrentResource = currentResource;
        TargetTransitionRevision = targetTransitionRevision;
        TargetResourceGeneration = targetResourceGeneration;
        TargetDataGeneration = targetDataGeneration;
        TargetPhase = targetPhase;
        Operation = operation;
        TransitionState = transitionState;
        Replayed = replayed;
    }

    public Guid OperationId { get; }
    public EnvironmentGenerationFence EnvironmentFence { get; }
    public string VolumeId { get; }
    public long ExpectedTransitionRevision { get; }
    public long ExpectedResourceGeneration { get; }
    public long ExpectedDataGeneration { get; }
    public ProviderResourceRef? CurrentResource { get; }
    public long TargetTransitionRevision { get; }
    public long TargetResourceGeneration { get; }
    public long TargetDataGeneration { get; }
    public EnvironmentWorkspaceVolumeState TargetPhase { get; }
    public EnvironmentWorkspaceVolumeOperation Operation { get; }
    public EnvironmentWorkspaceVolumeTransitionState TransitionState { get; }
    public bool Replayed { get; }
}

public sealed record EnvironmentWorkspaceVolumeTransitionResult
{
    public EnvironmentWorkspaceVolumeTransitionResult(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        ProviderResourceRef? expectedResource,
        long targetTransitionRevision,
        long targetResourceGeneration,
        long targetDataGeneration,
        ProviderResourceRef? targetResource,
        EnvironmentWorkspaceVolumeState targetPhase,
        EnvironmentWorkspaceVolumeOperation operation,
        EnvironmentWorkspaceVolumeTransitionState transitionState,
        bool replayed)
    {
        ArgumentNullException.ThrowIfNull(environmentFence);
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeId);
        if (expectedTransitionRevision < 0 || targetTransitionRevision != expectedTransitionRevision + 1)
            throw new ArgumentOutOfRangeException(nameof(expectedTransitionRevision));
        if (expectedResourceGeneration < 0 || targetResourceGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedResourceGeneration));
        if (expectedDataGeneration < 0 || targetDataGeneration < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedDataGeneration));
        if (!Enum.IsDefined(targetPhase))
            throw new ArgumentOutOfRangeException(nameof(targetPhase));
        if (!Enum.IsDefined(operation))
            throw new ArgumentOutOfRangeException(nameof(operation));
        if (!Enum.IsDefined(transitionState))
            throw new ArgumentOutOfRangeException(nameof(transitionState));
        EnvironmentWorkspaceVolumeSnapshot.ValidateStorageResource(
            expectedResource,
            expectedResourceGeneration,
            nameof(expectedResource));
        EnvironmentWorkspaceVolumeSnapshot.ValidateStorageResource(
            targetResource,
            targetResourceGeneration,
            nameof(targetResource));
        if ((expectedResourceGeneration == 0) != (expectedResource is null))
            throw new ArgumentException(
                "ExpectedResource must be present exactly when the volume has a provider resource generation.",
                nameof(expectedResource));
        if (transitionState == EnvironmentWorkspaceVolumeTransitionState.Completed &&
            ((targetResourceGeneration == 0 || targetPhase == EnvironmentWorkspaceVolumeState.Released)
                ? targetResource is not null
                : targetResource is null))
            throw new ArgumentException(
                "A completed volume transition must expose exactly its committed provider resource, if any.",
                nameof(targetResource));

        OperationId = operationId;
        EnvironmentFence = environmentFence;
        VolumeId = volumeId;
        ExpectedTransitionRevision = expectedTransitionRevision;
        ExpectedResourceGeneration = expectedResourceGeneration;
        ExpectedDataGeneration = expectedDataGeneration;
        ExpectedResource = expectedResource;
        TargetTransitionRevision = targetTransitionRevision;
        TargetResourceGeneration = targetResourceGeneration;
        TargetDataGeneration = targetDataGeneration;
        TargetResource = targetResource;
        TargetPhase = targetPhase;
        Operation = operation;
        TransitionState = transitionState;
        Replayed = replayed;
    }

    public Guid OperationId { get; }
    public EnvironmentGenerationFence EnvironmentFence { get; }
    public string VolumeId { get; }
    public long ExpectedTransitionRevision { get; }
    public long ExpectedResourceGeneration { get; }
    public long ExpectedDataGeneration { get; }
    public ProviderResourceRef? ExpectedResource { get; }
    public long TargetTransitionRevision { get; }
    public long TargetResourceGeneration { get; }
    public long TargetDataGeneration { get; }
    public ProviderResourceRef? TargetResource { get; }
    public EnvironmentWorkspaceVolumeState TargetPhase { get; }
    public EnvironmentWorkspaceVolumeOperation Operation { get; }
    public EnvironmentWorkspaceVolumeTransitionState TransitionState { get; }
    public bool Replayed { get; }
}

public enum EnvironmentNetworkEffectKind
{
    Apply,
    Revoke
}

public enum EnvironmentNetworkEffectState
{
    Reserved,
    Completed,
    ReconciliationRequired,
    Reconciled,
    Failed,
    Stale
}

public sealed record EnvironmentNetworkEffectReservation(
    Guid OperationId,
    EnvironmentGenerationFence Fence,
    string ResourceId,
    long PolicyGeneration,
    long ExpectedPreviousPolicyGeneration,
    EnvironmentNetworkEffectKind Kind,
    EnvironmentNetworkEffectState State,
    bool Replayed);

public sealed record EnvironmentNetworkEffectObservation(
    bool ObjectVerified,
    long? AppliedIntentGeneration,
    bool Revoked,
    string? IntentHash);

public interface IEnvironmentLifecycleStore
{
    Task<EnvironmentLifecycleSnapshot?> GetAsync(
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken);

    Task<EnvironmentLifecycleSnapshot> RequireActiveAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken);

    Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentLifecycleTransitionRequest request,
        CancellationToken cancellationToken);

    Task<EnvironmentNetworkEffectReservation> ReserveNetworkEffectAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        long expectedPreviousPolicyGeneration,
        EnvironmentNetworkEffectKind kind,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentNetworkEffectReservation> GetNetworkEffectAsync(
        Guid operationId,
        EnvironmentGenerationFence currentFence,
        CancellationToken cancellationToken);

    Task RequireVerifiedNetworkPolicyGenerationAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        CancellationToken cancellationToken);

    Task<EnvironmentNetworkEffectReservation> CompleteNetworkEffectAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        bool effectMayHaveApplied,
        bool exactGenerationVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentNetworkEffectReservation> MarkNetworkEffectReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        EnvironmentNetworkEffectObservation observation,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeSnapshot> CreateWorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        JsonElement specification,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeProvisionReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReplaceAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReplaceAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReplaceReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeBindAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeBindAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeBindReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeUnbindAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeUnbindAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeUnbindReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeAttachAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeAttachAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeAttachReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeDetachReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeFlushAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        long nextDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeFlushAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        bool durableFlushVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeFlushReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReleaseAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        bool releaseVerified,
        CancellationToken cancellationToken);

    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReleaseReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);
}
