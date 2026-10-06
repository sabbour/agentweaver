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

public sealed record EnvironmentWorkspaceVolumeSnapshot(
    EnvironmentGenerationFence EnvironmentFence,
    string VolumeId,
    long TransitionRevision,
    long ResourceGeneration,
    long DataGeneration,
    EnvironmentWorkspaceVolumeState Phase,
    EnvironmentWorkspaceVolumeOperation LastOperation,
    JsonElement Specification);

public sealed record EnvironmentWorkspaceVolumeTransitionReservation(
    Guid OperationId,
    EnvironmentGenerationFence EnvironmentFence,
    string VolumeId,
    long ExpectedTransitionRevision,
    long ExpectedResourceGeneration,
    long ExpectedDataGeneration,
    long TargetTransitionRevision,
    long TargetResourceGeneration,
    long TargetDataGeneration,
    EnvironmentWorkspaceVolumeState TargetPhase,
    EnvironmentWorkspaceVolumeOperation Operation,
    EnvironmentWorkspaceVolumeTransitionState TransitionState,
    bool Replayed);

public sealed record EnvironmentWorkspaceVolumeTransitionResult(
    Guid OperationId,
    EnvironmentGenerationFence EnvironmentFence,
    string VolumeId,
    long ExpectedTransitionRevision,
    long ExpectedResourceGeneration,
    long ExpectedDataGeneration,
    long TargetTransitionRevision,
    long TargetResourceGeneration,
    long TargetDataGeneration,
    EnvironmentWorkspaceVolumeState TargetPhase,
    EnvironmentWorkspaceVolumeOperation Operation,
    EnvironmentWorkspaceVolumeTransitionState TransitionState,
    bool Replayed);

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
