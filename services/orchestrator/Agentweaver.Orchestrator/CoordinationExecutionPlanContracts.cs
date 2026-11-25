using System.Collections.Immutable;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

public sealed record CoordinationExecutionPlanSnapshot(
    SessionIdentity Session,
    string AcceptedSelectionHash,
    long ExecutionFence,
    long DecisionStateVersion,
    WorkPlanSnapshot WorkPlan,
    CoordinationSavedPlanFingerprint SavedPlanFingerprint,
    CoordinationExecutionCheckpointSnapshot? Checkpoint,
    ImmutableArray<CoordinationExecutionChildSnapshot> Children,
    CoordinationExecutionRecoverySnapshot? Recovery);

public sealed record CoordinationSavedPlanFingerprint(int Version, string Sha256);

public sealed record CoordinationExecutionCheckpointSnapshot(
    string CheckpointId,
    long Revision,
    string PayloadHash,
    string? ParentCheckpointId,
    bool HasParentMetadata,
    string WorkPlanId,
    long DecisionStateVersion,
    CoordinationExecutionProgressSnapshot Progress);

public sealed record CoordinationExecutionProgressSnapshot(
    ImmutableArray<CoordinationExecutionProgressEntry> WorkItems,
    ImmutableArray<CoordinationExecutionProgressEntry> NonModelSteps,
    ImmutableArray<CoordinationExecutionProgressEntry> FixedWorkItems)
{
    public ImmutableArray<CoordinationExecutionPendingDispatchSnapshot> PendingDispatches { get; init; } = [];
}

public sealed record CoordinationExecutionProgressEntry(string Id, string State);

public sealed record CoordinationExecutionPendingDispatchSnapshot(
    string AssociationId,
    string ChildSessionId,
    Guid MessageId,
    string PromptHash);

public sealed record CoordinationExecutionChildSnapshot(
    string SessionId,
    string? ParentSessionId,
    string? WorkPlanItemId,
    string AssociationId,
    string Lifecycle,
    long ExecutionFence,
    bool Detached,
    Guid DispatchMessageId,
    string PromptHash,
    string StartupBindingHash,
    string? ResultHash,
    string? TurnReceiptHash)
{
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CoordinationCompletedReceiptLink? CompletedReceiptLink { get; init; }
}

public sealed record CoordinationCompletedReceiptLink(Guid MessageId, string ReceiptHash);

public sealed record CoordinationExecutionRecoverySnapshot(
    Guid OperationId,
    string SelectionHash,
    string WorkPlanId,
    string WorkPlanHash,
    string WorkflowHash,
    string SourceCheckpointId,
    long SourceCheckpointRevision,
    string SourceCheckpointHash,
    long SourceExecutionFence,
    long SourceDecisionStateVersion,
    long HoldFence,
    string ManifestHash,
    ImmutableArray<CoordinationExecutionRecoveryMemberSnapshot> Members)
{
    public string Phase { get; init; } = string.Empty;
    public string CurrentStep { get; init; } = string.Empty;
    public bool? DispatchHeld { get; init; }
}

public sealed record CoordinationExecutionRecoveryMemberSnapshot(
    string AssociationId,
    string SessionId,
    string? WorkPlanItemId,
    string WorkflowStepId,
    string AgentId,
    string ModelSelectionReference,
    string IsolationProviderId,
    Guid MessageId,
    string PromptHash,
    string StartupBindingHash,
    string SpawnCommandHash,
    string? MessageStatus,
    string? MessageRequestHash,
    string Progress,
    string Disposition,
    string? ResultHash,
    string? TurnReceiptHash,
    string MemberHash);
