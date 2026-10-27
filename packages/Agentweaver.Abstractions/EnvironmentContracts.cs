using System.Text.Json;

namespace Agentweaver.Abstractions;

/// <summary>Tenant, project, run, and Environment identity owned by the Environment service.</summary>
public sealed record EnvironmentOwnerIdentity
{
    /// <summary>Creates an owner identity with bounded nonempty identifiers.</summary>
    /// <exception cref="ArgumentException">An identifier is blank, too long, or contains control characters.</exception>
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

    /// <summary>Checks one owner identifier before it becomes part of an Environment fence.</summary>
    private static string Validate(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Environment owner identifiers must be bounded and contain no control characters.", name);
        return value;
    }
}

/// <summary>Lifecycle generation fence used to reject operations from replaced Environment owners.</summary>
public sealed record EnvironmentGenerationFence
{
    /// <summary>Creates a fence for a specific owner and positive lifecycle generation.</summary>
    /// <exception cref="ArgumentNullException">The owner identity is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The generation is not positive.</exception>
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

/// <summary>Whether an Environment owner is active or released.</summary>
public enum EnvironmentLifecycleState
{
    Active,
    Released
}

/// <summary>Current Environment lifecycle state and its generation fence.</summary>
public sealed record EnvironmentLifecycleSnapshot(
    EnvironmentGenerationFence Fence,
    EnvironmentLifecycleState State);

/// <summary>
/// Compare-and-swap request for Environment lifecycle registration or transition.
/// Generation zero is reserved for initial activation; later changes use the expected generation.
/// </summary>
public sealed record EnvironmentLifecycleTransitionRequest
{
    /// <summary>Creates a lifecycle transition with a bounded idempotency key and valid state.</summary>
    /// <exception cref="ArgumentException">The owner, transition, or idempotency key is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The expected generation or state is invalid.</exception>
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

/// <summary>New lifecycle snapshot and whether the request replayed an earlier transition.</summary>
public sealed record EnvironmentLifecycleTransitionResult(
    EnvironmentLifecycleSnapshot Snapshot,
    bool Replayed)
{
    /// <summary>Exact persisted lifecycle operation ID, when returned by an owner that retains it.</summary>
    public Guid? OperationId { get; init; }
}

/// <summary>Operations that change a workspace volume's provider resource or attachment state.</summary>
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

/// <summary>Committed lifecycle phase of an Environment-owned workspace volume.</summary>
public enum EnvironmentWorkspaceVolumeState
{
    Requested,
    Ready,
    Bound,
    Attached,
    Released
}

/// <summary>Whether a reserved provider effect is pending, verified, uncertain, or stale.</summary>
public enum EnvironmentWorkspaceVolumeTransitionState
{
    Reserved,
    Completed,
    ReconciliationRequired,
    Reconciled,
    Failed,
    Stale
}

/// <summary>
/// Owner snapshot of one workspace volume. Transition revision fences metadata changes;
/// resource generation identifies provider replacement; data generation advances only
/// when durable workspace contents are flushed.
/// </summary>
public sealed record EnvironmentWorkspaceVolumeSnapshot
{
    /// <summary>Creates a volume snapshot and checks phase, provider resource, and binding consistency.</summary>
    /// <exception cref="ArgumentException">The specification or provider/resource binding conflicts with the phase.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A revision, generation, or enum value is invalid.</exception>
    public EnvironmentWorkspaceVolumeSnapshot(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long transitionRevision,
        long resourceGeneration,
        long dataGeneration,
        EnvironmentWorkspaceVolumeState phase,
        EnvironmentWorkspaceVolumeOperation lastOperation,
        JsonElement specification,
        ProviderResourceRef? resource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding)
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
        if ((resource is null) != (providerBinding is null))
            throw new ArgumentException(
                "A pinned provider binding must be present exactly when the active provider resource is present.",
                nameof(providerBinding));
        if (providerBinding is not null)
            _ = providerBinding.ValidateFor(resource!);
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
        ProviderBinding = providerBinding;
    }

    public EnvironmentGenerationFence EnvironmentFence { get; }
    public string VolumeId { get; }
    public long TransitionRevision { get; }
    public long ResourceGeneration { get; }
    /// <summary>Data durability generation, distinct from the provider resource generation.</summary>
    public long DataGeneration { get; }
    public EnvironmentWorkspaceVolumeState Phase { get; }
    public EnvironmentWorkspaceVolumeOperation LastOperation { get; }
    public JsonElement Specification { get; }
    public ProviderResourceRef? Resource { get; }
    public WorkspaceVolumeProviderBindingSnapshot? ProviderBinding { get; }

    /// <summary>Checks that an active provider resource is from Storage and matches the supplied generation.</summary>
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

/// <summary>
/// Fenced reservation for a workspace-volume transition. Expected values describe the
/// pre-state; target values are the next transition/data/resource generations.
/// </summary>
public sealed record EnvironmentWorkspaceVolumeTransitionReservation
{
    /// <summary>Creates a reservation and validates its expected and target generation relationship.</summary>
    /// <exception cref="ArgumentException">Current resource and provider binding are incomplete or inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Transition or generation values are invalid.</exception>
    public EnvironmentWorkspaceVolumeTransitionReservation(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        ProviderResourceRef? currentResource,
        WorkspaceVolumeProviderBindingSnapshot? currentProviderBinding,
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
        if ((currentResource is null) != (currentProviderBinding is null))
            throw new ArgumentException(
                "CurrentProviderBinding must be present exactly when CurrentResource is present.",
                nameof(currentProviderBinding));
        if (currentProviderBinding is not null)
            _ = currentProviderBinding.ValidateFor(currentResource!);

        OperationId = operationId;
        EnvironmentFence = environmentFence;
        VolumeId = volumeId;
        ExpectedTransitionRevision = expectedTransitionRevision;
        ExpectedResourceGeneration = expectedResourceGeneration;
        ExpectedDataGeneration = expectedDataGeneration;
        CurrentResource = currentResource;
        CurrentProviderBinding = currentProviderBinding;
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
    public WorkspaceVolumeProviderBindingSnapshot? CurrentProviderBinding { get; }
    public long TargetTransitionRevision { get; }
    public long TargetResourceGeneration { get; }
    public long TargetDataGeneration { get; }
    public EnvironmentWorkspaceVolumeState TargetPhase { get; }
    public EnvironmentWorkspaceVolumeOperation Operation { get; }
    public EnvironmentWorkspaceVolumeTransitionState TransitionState { get; }
    public bool Replayed { get; }
    /// <summary>Verified flush proof attached to this reservation, when the operation is a flush.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WorkspaceVolumeFlushEvidence? FlushEvidence { get; init; }
}

/// <summary>
/// Result of a volume effect, carrying both the expected pre-state and committed target
/// so consumers can detect stale completions and uncertain provider effects.
/// </summary>
public sealed record EnvironmentWorkspaceVolumeTransitionResult
{
    /// <summary>Creates a transition result and checks resource presence for its generations and state.</summary>
    /// <exception cref="ArgumentException">Resource references contradict the expected or completed target state.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Transition or generation values are invalid.</exception>
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
    /// <summary>Durable flush proof associated with this transition, when verified.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public WorkspaceVolumeFlushEvidence? FlushEvidence { get; init; }
}

/// <summary>Effect applied to a network policy resource by the Environment owner.</summary>
public enum EnvironmentNetworkEffectKind
{
    Apply,
    Revoke
}

/// <summary>Lifecycle of a reserved network-policy provider effect.</summary>
public enum EnvironmentNetworkEffectState
{
    Reserved,
    Completed,
    ReconciliationRequired,
    Reconciled,
    Failed,
    Stale
}

/// <summary>Fenced reservation for applying or revoking one network-policy generation.</summary>
public sealed record EnvironmentNetworkEffectReservation(
    Guid OperationId,
    EnvironmentGenerationFence Fence,
    string ResourceId,
    long PolicyGeneration,
    long ExpectedPreviousPolicyGeneration,
    EnvironmentNetworkEffectKind Kind,
    EnvironmentNetworkEffectState State,
    bool Replayed);

/// <summary>Provider observation used to reconcile an uncertain network-policy effect.</summary>
public sealed record EnvironmentNetworkEffectObservation(
    bool ObjectVerified,
    long? AppliedIntentGeneration,
    bool Revoked,
    string? IntentHash);

/// <summary>State of background cleanup for a replaced workspace resource.</summary>
public enum EnvironmentWorkspaceVolumeCleanupState
{
    Pending,
    Leased,
    Blocked,
    Completed
}

/// <summary>Cleanup work identity and current lease state for one retired resource generation.</summary>
public sealed record EnvironmentWorkspaceVolumeCleanupStatus(
    Guid WorkId,
    Guid SourceReplaceOperationId,
    string VolumeId,
    long ResourceGeneration,
    EnvironmentWorkspaceVolumeCleanupState State,
    long LeaseRevision,
    DateTimeOffset? LeaseExpiresAt);

/// <summary>Fenced cleanup lease with the exact prior volume reference and provider release request.</summary>
public sealed record EnvironmentWorkspaceVolumeCleanupLease(
    Guid WorkId,
    EnvironmentGenerationFence Fence,
    Guid SourceReplaceOperationId,
    long SourceLifecycleGeneration,
    string VolumeId,
    long ResourceGeneration,
    WorkspaceVolumeReleaseRequest ReleaseRequest,
    long LeaseRevision,
    Guid LeaseId,
    DateTimeOffset LeaseExpiresAt)
{
    /// <summary>Validates that the lease still identifies the expected retired resource and Environment.</summary>
    /// <exception cref="ArgumentException">The lease identity, release target, generation, or expiry is invalid.</exception>
    public EnvironmentWorkspaceVolumeCleanupLease Validate()
    {
        ArgumentNullException.ThrowIfNull(Fence);
        ArgumentException.ThrowIfNullOrWhiteSpace(VolumeId);
        ArgumentNullException.ThrowIfNull(ReleaseRequest);
        ReleaseRequest.Validate();
        if (WorkId == Guid.Empty ||
            SourceReplaceOperationId == Guid.Empty ||
            SourceLifecycleGeneration < 1 ||
            ResourceGeneration < 1 ||
            ReleaseRequest.Volume.ResourceGeneration != ResourceGeneration ||
            !string.Equals(ReleaseRequest.Volume.VolumeId, VolumeId, StringComparison.Ordinal) ||
            !string.Equals(ReleaseRequest.Volume.ProjectId, Fence.Owner.ProjectId, StringComparison.Ordinal) ||
            LeaseRevision < 1 ||
            LeaseId == Guid.Empty ||
            LeaseExpiresAt <= DateTimeOffset.MinValue)
            throw new ArgumentException("The workspace-volume cleanup lease is invalid.");
        return this;
    }
}

/// <summary>
/// Environment owner persistence boundary for lifecycle fences, provider-effect reservations,
/// workspace-volume generations, durable flush evidence, and cleanup leases.
/// Implementations use the expected revisions/fences to reject stale or replayed effects.
/// </summary>
public interface IEnvironmentLifecycleStore
{
    /// <summary>Reads the lifecycle snapshot for an owner, or null before registration.</summary>
    Task<EnvironmentLifecycleSnapshot?> GetAsync(
        EnvironmentOwnerIdentity owner,
        CancellationToken cancellationToken);

    /// <summary>Requires the supplied lifecycle fence to remain current and active.</summary>
    Task<EnvironmentLifecycleSnapshot> RequireActiveAsync(
        EnvironmentGenerationFence fence,
        CancellationToken cancellationToken);

    /// <summary>Registers or transitions lifecycle state using expected-generation and idempotency checks.</summary>
    Task<EnvironmentLifecycleTransitionResult> TransitionAsync(
        EnvironmentLifecycleTransitionRequest request,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null);

    /// <summary>Registers a P1 Environment and binds it to the verified invocation in the same owner transaction.</summary>
    Task<EnvironmentLifecycleTransitionResult> TransitionP1Async(
        EnvironmentLifecycleTransitionRequest request,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> beforeCommitCheck) =>
        throw new NotSupportedException("The Environment lifecycle owner cannot retain P1 creation links.");

    /// <summary>Reserves an apply/revoke effect for an exact network policy generation.</summary>
    Task<EnvironmentNetworkEffectReservation> ReserveNetworkEffectAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        long expectedPreviousPolicyGeneration,
        EnvironmentNetworkEffectKind kind,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Reads a reserved network effect only while the supplied Environment fence is current.</summary>
    Task<EnvironmentNetworkEffectReservation> GetNetworkEffectAsync(
        Guid operationId,
        EnvironmentGenerationFence currentFence,
        CancellationToken cancellationToken);

    /// <summary>Requires durable proof that the exact network-policy generation is currently applied.</summary>
    Task RequireVerifiedNetworkPolicyGenerationAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        CancellationToken cancellationToken);

    /// <summary>Reads verification state for the exact current network-policy generation.</summary>
    Task ReadVerifiedNetworkPolicyGenerationAsync(
        EnvironmentGenerationFence fence,
        string resourceId,
        long policyGeneration,
        CancellationToken cancellationToken);

    /// <summary>Records effect completion, retaining reconciliation state if application may be uncertain.</summary>
    Task<EnvironmentNetworkEffectReservation> CompleteNetworkEffectAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        bool effectMayHaveApplied,
        bool exactGenerationVerified,
        CancellationToken cancellationToken);

    /// <summary>Reconciles a network effect against an owner/provider observation.</summary>
    Task<EnvironmentNetworkEffectReservation> MarkNetworkEffectReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence fence,
        EnvironmentNetworkEffectObservation observation,
        CancellationToken cancellationToken);

    /// <summary>Reads a volume snapshot within the supplied Environment generation.</summary>
    Task<EnvironmentWorkspaceVolumeSnapshot?> GetWorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        CancellationToken cancellationToken);

    /// <summary>Creates a volume definition with its initial specification and idempotency key.</summary>
    Task<EnvironmentWorkspaceVolumeSnapshot> CreateWorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        JsonElement specification,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Creates P1 volume metadata and retains its exact binding in the same owner transaction.</summary>
    Task<EnvironmentWorkspaceVolumeSnapshot> CreateP1WorkspaceVolumeAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        JsonElement specification,
        string idempotencyKey,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> beforeCommitCheck);

    /// <summary>Reserves provider provisioning against expected transition, resource, and data generations.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Reserves a P1 volume provision while checking Core again under the Environment owner lock.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveP1WorkspaceVolumeProvisionAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task> beforeCommitCheck) =>
        throw new NotSupportedException("The Environment owner cannot retain P1 Workspace creation links.");

    /// <summary>Completes provisioning only with the effect status and exact negotiated Storage resource.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectVerified,
        CancellationToken cancellationToken);

    /// <summary>Commits a P1 volume resource and its binding link atomically after current-binding revalidation.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteP1WorkspaceVolumeProvisionAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectVerified,
        P1InvocationBinding binding,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null) =>
        throw new NotSupportedException("The Environment owner cannot retain P1 Workspace creation links.");

    /// <summary>Reconciles an uncertain volume-provision operation against current provider state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeProvisionReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reserves replacement of a provider resource at the expected volume generations.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReplaceAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Commits the replacement resource only after its provider effect is verified.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReplaceAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        WorkspaceVolumeProviderBindingSnapshot? providerBinding,
        bool effectVerified,
        CancellationToken cancellationToken);

    /// <summary>Reconciles an uncertain replacement against the provider's current resource.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReplaceReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reserves binding of a ready volume to an Environment consumer.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeBindAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Commits the volume binding only when the provider effect is verified.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeBindAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    /// <summary>Reconciles an uncertain binding operation against provider state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeBindReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reserves unbinding of a volume from its current consumer.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeUnbindAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Commits the volume unbinding only when the provider effect is verified.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeUnbindAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    /// <summary>Reconciles an uncertain unbinding operation against provider state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeUnbindReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reserves attachment of the exact Storage resource to a runtime consumer.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeAttachAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Commits attachment only after the provider reports the expected effect.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeAttachAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken);

    /// <summary>Reconciles an uncertain attachment against the provider's actual state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeAttachReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reserves detachment of a bound volume at its expected generation.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeDetachAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Completes detachment and optionally rechecks authority before committing owner state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeDetachAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null);

    /// <summary>Reconciles an uncertain detachment against provider state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeDetachReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reserves a durable data flush to the explicitly supplied next data generation.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeFlushAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        long nextDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Completes a flush only when effect and durable-flush evidence are verified.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeFlushAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        ProviderResourceRef? providerResource,
        bool effectVerified,
        bool durableFlushVerified,
        CancellationToken cancellationToken);

    /// <summary>Completes a flush with provider-issued durable evidence and a final authority check.</summary>
    /// <remarks>The default implementation rejects this operation so unsupported owners fail closed.</remarks>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeFlushAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        WorkspaceVolumeFlushEvidence? evidence,
        bool effectVerified,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeCommitCheck = null) =>
        throw new NotSupportedException("The Environment owner cannot persist verified workspace flush evidence.");

    /// <summary>Reconciles an uncertain flush without claiming durability unless evidence is available.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeFlushReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Reads durable flush evidence for the exact current volume transition.</summary>
    /// <remarks>The default implementation rejects reads so unsupported owners fail closed.</remarks>
    Task<WorkspaceVolumeFlushEvidence?> GetWorkspaceVolumeFlushEvidenceAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long targetTransitionRevision,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The Environment owner cannot read durable workspace flush evidence.");

    /// <summary>Reads retained flush evidence across a lifecycle replacement using both source and current fences.</summary>
    /// <remarks>The default implementation rejects reads so unsupported owners fail closed.</remarks>
    Task<WorkspaceVolumeFlushEvidence?> GetRetainedWorkspaceVolumeFlushEvidenceAsync(
        EnvironmentGenerationFence currentFence,
        EnvironmentGenerationFence sourceFence,
        string volumeId,
        long targetTransitionRevision,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The Environment owner cannot read retained workspace flush evidence.");

    /// <summary>Reserves release of the exact provider resource at expected transition and data generations.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionReservation> ReserveWorkspaceVolumeReleaseAsync(
        EnvironmentGenerationFence environmentFence,
        string volumeId,
        long expectedTransitionRevision,
        long expectedResourceGeneration,
        long expectedDataGeneration,
        string idempotencyKey,
        CancellationToken cancellationToken);

    /// <summary>Commits release only when the external provider confirms the resource is released.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> CompleteWorkspaceVolumeReleaseAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        bool effectMayHaveApplied,
        bool releaseVerified,
        CancellationToken cancellationToken);

    /// <summary>Reconciles an uncertain release against the provider's current resource state.</summary>
    Task<EnvironmentWorkspaceVolumeTransitionResult> MarkWorkspaceVolumeReleaseReconciledAsync(
        Guid operationId,
        EnvironmentGenerationFence environmentFence,
        CancellationToken cancellationToken);

    /// <summary>Claims pending cleanup work with a bounded lease under the current Environment fence.</summary>
    Task<EnvironmentWorkspaceVolumeCleanupLease?> ClaimWorkspaceVolumeCleanupAsync(
        EnvironmentGenerationFence environmentFence,
        Guid? sourceReplaceOperationId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    /// <summary>Completes cleanup only when its release receipt matches the current lease.</summary>
    Task<EnvironmentWorkspaceVolumeCleanupStatus> CompleteWorkspaceVolumeCleanupAsync(
        EnvironmentWorkspaceVolumeCleanupLease lease,
        WorkspaceVolumeReleaseReceipt receipt,
        CancellationToken cancellationToken);

    /// <summary>Releases the matching cleanup lease without marking the resource deleted.</summary>
    Task ReleaseWorkspaceVolumeCleanupLeaseAsync(
        EnvironmentWorkspaceVolumeCleanupLease lease,
        CancellationToken cancellationToken);

    /// <summary>Reads cleanup status for a resource replaced by the specified operation.</summary>
    Task<EnvironmentWorkspaceVolumeCleanupStatus?> GetWorkspaceVolumeCleanupStatusAsync(
        EnvironmentGenerationFence environmentFence,
        Guid sourceReplaceOperationId,
        CancellationToken cancellationToken);
}
