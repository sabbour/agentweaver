using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

public sealed record OrchestratorOptions(
    string Issuer,
    string Audience,
    string ProjectsOwnerBaseAddress,
    string ProjectsAudience,
    string EventsOwnerBaseAddress,
    string EventsAudience,
    string Schema = "orchestrator");

public sealed record CoordinationActor(string Issuer, string Subject);
public readonly record struct CoordinationRunScope(string ProjectId, string RunId);

public sealed record ProjectsPermissionGrant(string Permission, long RoleRevision);

public sealed record ProjectsAuthority(
    string ResourceType,
    string ResourceId,
    ImmutableArray<ProjectsPermissionGrant> Permissions);

public sealed record ProjectsAuthorizationContext(
    int ContractVersion,
    string Issuer,
    string ActorId,
    string TenantId,
    long MembershipRevision,
    string? BoundProjectId,
    string? BoundRunId,
    ImmutableArray<ProjectsAuthority> EffectiveAuthority);

public sealed record EffectiveRunSelection(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    JsonElement Snapshot);

public sealed record AuthorizedRunSelection(
    EffectiveRunSelection Selection,
    ProjectsAuthorizationContext Authorization);

public sealed record AcceptRootRequest(string SessionId);

public sealed record ProposeCoordinatorOutcomeRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    CoordinatorOutcomeSpecification Specification);

public sealed record SelectCoordinatorWorkflowRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    string? WorkflowId,
    WorkflowDefinition? ProposedDefinition = null);

public sealed record ProposeCoordinatorWorkPlanRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    WorkPlan Plan);

public sealed record ReviseCoordinatorWorkPlanRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    WorkPlan RevisedPlan);

public sealed record MafExecutionDispatchRequest(long ExpectedStateVersion, string WorkPlanId)
{
    public string? BuildTestEnvironmentId { get; init; }
}

public sealed record MafExecutionDispatchResponse(
    string WorkPlanId,
    long DecisionStateVersion,
    long CheckpointRevision,
    ImmutableArray<string> DispatchedAssociationIds,
    ImmutableArray<string> UnavailableExecutorStepIds,
    ImmutableArray<string> FailedDependencyIds,
    bool IsComplete)
{
    public ImmutableDictionary<string, string> CompletedResults { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);
    public bool WallTimeLimitReached { get; init; }
}

public sealed record RequestCoordinatorAssemblyRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    CoordinatorAssemblyRequest Request);

public sealed record AnswerCoordinatorGateRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string? ChoiceId,
    string? FreeformAnswer);

public sealed record AskCoordinatorQuestionRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    string QuestionId,
    string Prompt,
    ImmutableArray<string> AllowedChoices,
    bool AllowsFreeform);

public sealed record AskNextOutcomeClarifyingQuestionRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId);

public sealed record RequestCoordinatorApprovalRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    string SubjectId,
    string? Prompt);

public sealed record AcknowledgeCoordinatorGateRequest(
    long ExpectedStateVersion,
    string IdempotencyKey);

public sealed record CoordinatorDecisionOperationResponse(
    Guid DecisionId,
    long StateVersion,
    bool Accepted,
    long ExecutionFence,
    CoordinatorGateRequest? PendingGate,
    ImmutableArray<WorkflowValidationIssue> Issues,
    JsonElement? TransitionValue = null);

public sealed record CoordinatorDecisionStateView(
    long StateVersion,
    long ExecutionFence,
    bool OutcomeConfirmed,
    bool WorkflowConfirmed,
    bool CanDecompose,
    bool CanDispatch,
    CoordinatorGateRequest? PendingGate);

internal static class CoordinatorTypedActionIds
{
    public const string ProposeOutcomeSpec = "propose_outcome_spec";
    public const string SelectWorkflow = "select_workflow";
    public const string ProposeWorkPlan = "propose_work_plan";
    public const string ReviseWorkPlan = "revise_work_plan";
    public const string RequestAssembly = "request_assembly";
    public const string Dispatch = "dispatch";

    public static ImmutableHashSet<string> All { get; } =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            ProposeOutcomeSpec,
            SelectWorkflow,
            ProposeWorkPlan,
            ReviseWorkPlan,
            RequestAssembly,
            Dispatch);
}

public sealed record AcceptedRoot(
    string ProjectId,
    string RunId,
    string RootSessionId,
    long ExecutionFence,
    long StateVersion,
    long LogicalTurnOrdinal,
    string ExecutionState);

public sealed record RegisterChildRequest(string SessionId, string? WorkPlanItemId = null);

public enum CoordinationSessionKind
{
    Coordinator,
    ChildWork,
    Scribe,
    OperatorChat,
    ChildRun
}

public enum CoordinationActivityState
{
    Busy,
    Idle,
    Unknown
}

public enum CoordinationInterruptionIntentState
{
    None,
    Requested,
    Acknowledged
}

public enum CoordinationLifecycleState
{
    Active,
    Cancelled,
    Completed,
    Archived
}

public enum IdleNotificationMode
{
    Once,
    Always
}

public enum IdleNotificationSourceState
{
    Available,
    Unavailable
}

public enum CoordinationBlockerKind
{
    AwaitingInput,
    AwaitingPlanApproval,
    AwaitingApproval,
    AwaitingOutcomeConfirmation
}

public enum CoordinationSteeringAction
{
    Stop,
    Redirect,
    Amend
}

public sealed record SpawnSessionRequest(
    string SessionId,
    CoordinationSessionKind Kind,
    string IdempotencyKey,
    string Kickoff,
    string? UserQuote = null,
    string? CoordinatorInstructions = null,
    string? WorkPlanItemId = null);

internal sealed record ConfirmedWorkPlanItemAssociation(
    string WorkPlanItemId,
    long DecisionStateVersion,
    string SelectionHash);

public sealed record SessionTreeCommandRequest(long ExecutionFence, string IdempotencyKey);

public sealed record CoordinationSessionForkRequest(
    long ExecutionFence,
    string IdempotencyKey,
    string TargetSessionId,
    Guid SourceEventId,
    string SourceCursor,
    CoordinationSessionKind Kind);

public enum CoordinationForkRegistrationState
{
    RegistrationPending,
    Registered,
    Unregistered
}

public sealed record CoordinationSessionForkResult(
    Guid CommandId,
    SessionIdentity Source,
    string TargetSessionId,
    CoordinationSessionKind Kind,
    long ExecutionFence,
    CoordinationForkRegistrationState RegistrationState,
    SessionTreeNode? Node,
    string? PendingRequestId,
    SessionForkLineage? Lineage,
    string? UnavailableCode,
    bool IsDuplicate);

public sealed record SubscribeToIdleRequest(
    string SubscriberSessionId,
    long ExecutionFence,
    string IdempotencyKey,
    IdleNotificationMode Mode);

public sealed record ResolveCoordinatorGateRequest(
    long ExpectedStateVersion,
    string IdempotencyKey);

public sealed record SteerSessionRequest(
    string RecipientSessionId,
    string IdempotencyKey,
    AddressedMessageDeliveryMode DeliveryMode,
    CoordinationSteeringAction Action,
    JsonElement Instruction,
    string? UserQuote = null,
    string? CoordinatorInstructions = null);

public sealed record SessionTreeNode(
    SessionIdentity Identity,
    string? ParentSessionId,
    string RootSessionId,
    CoordinationSessionKind Kind,
    bool Detached,
    CoordinationLifecycleState Lifecycle,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long StateVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record SessionTreeSnapshot(
    string RootSessionId,
    ImmutableArray<SessionTreeNode> Nodes);

public sealed record SessionStatusBlocker(
    CoordinationBlockerKind Kind,
    string RequestId,
    ImmutableArray<string> Choices,
    bool AllowsFreeform,
    string? Prompt);

public sealed record SessionStatusSnapshot(
    SessionIdentity Identity,
    string? ParentSessionId,
    string RootSessionId,
    CoordinationSessionKind Kind,
    bool Detached,
    CoordinationActivityState Activity,
    string? ActivityUnavailableCode,
    CoordinationLifecycleState Lifecycle,
    long ExecutionFence,
    long StateVersion,
    ImmutableArray<SessionStatusBlocker> Blockers,
    string RuntimeEffectsState,
    string RuntimeEffectsUnavailableCode,
    SessionInterruptionIntentSnapshot InterruptionIntent,
    OwnerRunExecutionSnapshot RunExecution);

public sealed record OwnerRunExecutionSnapshot(
    string State,
    long StateVersion,
    string? CauseCode,
    string? Reference);

public sealed record SessionInterruptionIntentSnapshot(
    CoordinationInterruptionIntentState State,
    Guid? OwnerMessageId,
    string? CauseCode);

public sealed record CoordinationIdleNotification(
    Guid NotificationId,
    SessionIdentity Target,
    SessionIdentity Subscriber,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long StateVersion);

public sealed record SpawnedSession(
    SessionTreeNode Node,
    string PendingRequestId,
    Guid CommandId,
    string DispatchState);

public sealed record IdleSubscriptionResult(
    Guid SubscriptionId,
    string TargetSessionId,
    string SubscriberSessionId,
    IdleNotificationMode Mode,
    long ExecutionFence,
    bool Active,
    IdleNotificationSourceState NotificationSourceState,
    string? NotificationSourceUnavailableCode);

public sealed record CoordinationTreeCommandResult(
    Guid CommandId,
    string Command,
    string SourceSessionId,
    string? TargetSessionId,
    long ExecutionFence,
    string State);

public sealed record CoordinationSpawnCommand(
    Guid CommandId,
    SessionIdentity Parent,
    SpawnSessionRequest Request,
    string PendingRequestId,
    long ExecutionFence);

public sealed record RegisteredChild(
    SessionIdentity Identity,
    string ParentSessionId,
    string PendingRequestId,
    long ExecutionFence);

public sealed record CoordinationMessageRequest(
    string RecipientSessionId,
    string IdempotencyKey,
    AddressedMessageDeliveryMode DeliveryMode,
    AddressedMessagePurpose Purpose,
    AddressedMessageKind Kind,
    JsonElement Payload,
    Guid? ThreadId = null,
    Guid? ReplyToId = null,
    string? RequestId = null,
    string? ReplyCorrelationId = null,
    string? UserQuote = null,
    string? CoordinatorInstructions = null);

public sealed record CoordinationMessageResult(
    Guid OwnerMessageId,
    string RecipientSessionId,
    string Status,
    string? RequestId);

public sealed record DeliveryIngressRequest(
    Guid OwnerMessageId,
    MessageAdmissionReceipt Admission);

public sealed record CancelRequestResult(string RequestId, string State, Guid? SteeringMessageId);

public enum LogicalTurnCompletion
{
    Idle,
    Blocked,
    Completed
}

public sealed record FinishTurnRequest(
    long ExecutionFence,
    long ExpectedStateVersion,
    LogicalTurnCompletion Completion);

public sealed record TurnBoundaryRequest(long ExecutionFence, long ExpectedStateVersion);

public sealed record TurnBoundaryResult(
    SessionIdentity Session,
    long LogicalTurnOrdinal,
    long StateVersion,
    string ExecutionState,
    bool PendingWake,
    AddressedMessageEnvelope? PresentedMessage,
    ImmutableArray<ParentNotification> ParentNotifications = default,
    string RuntimeTurnId = "",
    long ExecutionFence = 0,
    long RunStateVersion = 0,
    string? CauseCode = null,
    string? Reference = null,
    Guid? OperationId = null,
    bool IsDuplicate = false);

internal sealed record SessionRuntimeOwnerState(
    string RootSessionId,
    string? WorkPlanItemId,
    string TenantId,
    string AcceptedSelectionHash,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long StateVersion,
    string RuntimeTurnId);

public sealed record OwnerRunStatus(
    string ProjectId,
    string RunId,
    string RootSessionId,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    string ExecutionState,
    long StateVersion,
    string? CauseCode = null,
    string? Reference = null);

public enum OwnerRunFailureState
{
    Failed,
    Indeterminate
}

public sealed record ReportRunFailureRequest(
    long ExecutionFence,
    long ExpectedRunStateVersion,
    long ExpectedSessionStateVersion,
    string IdempotencyKey,
    OwnerRunFailureState State,
    string CauseCode,
    string Reference);

public sealed record RecoverRunExecutionRequest(
    long ExecutionFence,
    long ExpectedRunStateVersion,
    string IdempotencyKey,
    string CauseCode,
    string Reference);

public sealed record RunExecutionTransitionResult(
    Guid OperationId,
    SessionIdentity Session,
    string PreviousState,
    string State,
    long PreviousExecutionFence,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long PreviousSessionStateVersion,
    long SessionStateVersion,
    long PreviousRunStateVersion,
    long RunStateVersion,
    string? CauseCode,
    string? Reference,
    string? PreviousCauseCode,
    string? PreviousReference,
    ImmutableArray<SessionExecutionFenceChange> FencedSessions,
    bool IsDuplicate = false);

public sealed record SessionExecutionFenceChange(
    string SessionId,
    string State,
    long StateVersion);

public sealed record ParentNotification(
    Guid NotificationId,
    string ParentSessionId,
    string ChildSessionId,
    Guid MessageId,
    AddressedMessagePurpose Purpose,
    bool WakesParent,
    DateTimeOffset CreatedAt);

public sealed class CoordinationException(string code, int statusCode, Exception? innerException = null)
    : Exception(code, innerException)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
