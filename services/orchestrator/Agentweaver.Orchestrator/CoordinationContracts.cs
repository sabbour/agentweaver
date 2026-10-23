using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;
using Agentweaver.Orchestrator.Core;

namespace Agentweaver.Orchestrator;

/// <summary>
/// Holds the issuer, audiences, service addresses, and PostgreSQL schema used by the Orchestrator host.
/// </summary>
public sealed record OrchestratorOptions(
    string Issuer,
    string Audience,
    string ProjectsOwnerBaseAddress,
    string ProjectsAudience,
    string EventsOwnerBaseAddress,
    string EventsAudience,
    string Schema = "orchestrator");

/// <summary>
/// Identifies an authenticated coordination caller by issuer and subject.
/// </summary>
public sealed record CoordinationActor(string Issuer, string Subject);

/// <summary>
/// Identifies the project and run bound to an authenticated coordination request.
/// </summary>
public readonly record struct CoordinationRunScope(string ProjectId, string RunId);

/// <summary>
/// Records one project permission and the role revision at which it was granted.
/// </summary>
public sealed record ProjectsPermissionGrant(string Permission, long RoleRevision);

/// <summary>
/// Represents effective permissions for one Projects-owned resource.
/// </summary>
public sealed record ProjectsAuthority(
    string ResourceType,
    string ResourceId,
    ImmutableArray<ProjectsPermissionGrant> Permissions);

/// <summary>
/// Carries Projects' authenticated actor, tenant, membership revision, and effective resource authority.
/// </summary>
public sealed record ProjectsAuthorizationContext(
    int ContractVersion,
    string Issuer,
    string ActorId,
    string TenantId,
    long MembershipRevision,
    string? BoundProjectId,
    string? BoundRunId,
    ImmutableArray<ProjectsAuthority> EffectiveAuthority);

/// <summary>
/// Captures immutable project, run, configuration, platform, and snapshot revisions accepted for a run.
/// </summary>
public sealed record EffectiveRunSelection(
    string ProjectId,
    string RunId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    JsonElement Snapshot);

/// <summary>
/// Combines an accepted run selection with the Projects authorization that permits its use.
/// </summary>
public sealed record AuthorizedRunSelection(
    EffectiveRunSelection Selection,
    ProjectsAuthorizationContext Authorization);

/// <summary>
/// Request body for accepting a root session for an authorized run.
/// </summary>
public sealed record AcceptRootRequest(string SessionId);

/// <summary>
/// Request body proposing a coordinator outcome against an expected decision version.
/// </summary>
public sealed record ProposeCoordinatorOutcomeRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    CoordinatorOutcomeSpecification Specification);

/// <summary>
/// Request body selecting a registered workflow or proposing a generated definition.
/// </summary>
public sealed record SelectCoordinatorWorkflowRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    string? WorkflowId,
    WorkflowDefinition? ProposedDefinition = null);

/// <summary>
/// Request body proposing a work plan against an expected decision version.
/// </summary>
public sealed record ProposeCoordinatorWorkPlanRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    WorkPlan Plan);

/// <summary>
/// Request body revising the current work plan against an expected decision version.
/// </summary>
public sealed record ReviseCoordinatorWorkPlanRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    WorkPlan RevisedPlan);

/// <summary>
/// Request body for dispatching a confirmed work plan through the MAF executor.
/// </summary>
public sealed record MafExecutionDispatchRequest(long ExpectedStateVersion, string WorkPlanId)
{
    /// <summary>
    /// Gets the explicitly selected BuildTest environment when dispatch includes that platform step.
    /// </summary>
    public string? BuildTestEnvironmentId { get; init; }

    /// <summary>
    /// Gets the work-plan-item to workspace locators used to bind BuildTest execution to Source Control workspaces.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, SourceControlBuildTestWorkspaceLocator>? BuildTestWorkspaces { get; init; }
}

/// <summary>
/// Identifies a Source Control workspace and the immutable base and branch selected for a build-and-test step.
/// </summary>
public sealed record SourceControlBuildTestWorkspaceLocator(
    string WorkspaceId,
    Guid WorkspaceIncarnationId,
    string BaseSha,
    string BranchName);

/// <summary>
/// Reports which plan steps were dispatched, unavailable, failed, or completed.
/// </summary>
public sealed record MafExecutionDispatchResponse(
    string WorkPlanId,
    long DecisionStateVersion,
    long CheckpointRevision,
    ImmutableArray<string> DispatchedAssociationIds,
    ImmutableArray<string> UnavailableExecutorStepIds,
    ImmutableArray<string> FailedDependencyIds,
    bool IsComplete)
{
    /// <summary>
    /// Gets completed execution results keyed by work-plan association identifier.
    /// </summary>
    public ImmutableDictionary<string, string> CompletedResults { get; init; } =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Gets whether the accepted run wall-time limit prevented further dispatch.
    /// </summary>
    public bool WallTimeLimitReached { get; init; }
}

/// <summary>
/// Request body asking the coordinator to assemble a work-plan result.
/// </summary>
public sealed record RequestCoordinatorAssemblyRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    CoordinatorAssemblyRequest Request);

/// <summary>
/// Request body answering a pending coordinator gate with a choice or freeform response.
/// </summary>
public sealed record AnswerCoordinatorGateRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string? ChoiceId,
    string? FreeformAnswer);

/// <summary>
/// Request body for presenting a question and its permitted response choices.
/// </summary>
public sealed record AskCoordinatorQuestionRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    string QuestionId,
    string Prompt,
    ImmutableArray<string> AllowedChoices,
    bool AllowsFreeform);

/// <summary>
/// Request body requesting the next clarifying question for an unconfirmed outcome.
/// </summary>
public sealed record AskNextOutcomeClarifyingQuestionRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId);

/// <summary>
/// Request body asking for approval of a specific subject.
/// </summary>
public sealed record RequestCoordinatorApprovalRequest(
    long ExpectedStateVersion,
    string IdempotencyKey,
    string RequestId,
    string SubjectId,
    string? Prompt);

/// <summary>
/// Request body acknowledging the currently pending coordinator gate.
/// </summary>
public sealed record AcknowledgeCoordinatorGateRequest(
    long ExpectedStateVersion,
    string IdempotencyKey);

/// <summary>
/// Returns whether a decision operation was accepted and any pending gate or validation issues.
/// </summary>
public sealed record CoordinatorDecisionOperationResponse(
    Guid DecisionId,
    long StateVersion,
    bool Accepted,
    long ExecutionFence,
    CoordinatorGateRequest? PendingGate,
    ImmutableArray<WorkflowValidationIssue> Issues,
    JsonElement? TransitionValue = null);

/// <summary>
/// Summarizes the current coordinator decision state and the operations currently allowed.
/// </summary>
public sealed record CoordinatorDecisionStateView(
    long StateVersion,
    long ExecutionFence,
    bool OutcomeConfirmed,
    bool WorkflowConfirmed,
    bool CanDecompose,
    bool CanDispatch,
    CoordinatorGateRequest? PendingGate);

/// <summary>
/// Defines the stable action identifiers that coordinator decision routes can issue.
/// </summary>
internal static class CoordinatorTypedActionIds
{
    public const string ProposeOutcomeSpec = "propose_outcome_spec";
    public const string SelectWorkflow = "select_workflow";
    public const string ProposeWorkPlan = "propose_work_plan";
    public const string ReviseWorkPlan = "revise_work_plan";
    public const string RequestAssembly = "request_assembly";
    public const string Dispatch = "dispatch";

    /// <summary>
    /// Provides the complete ordinal set of registered typed action identifiers.
    /// </summary>
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

/// <summary>
/// Identifies the accepted root session and its current decision, turn, and execution-fence state.
/// </summary>
public sealed record AcceptedRoot(
    string ProjectId,
    string RunId,
    string RootSessionId,
    long ExecutionFence,
    long StateVersion,
    long LogicalTurnOrdinal,
    string ExecutionState);

/// <summary>
/// Request body for registering a child session under a parent session.
/// </summary>
public sealed record RegisterChildRequest(string SessionId, string? WorkPlanItemId = null);

/// <summary>
/// Classifies a session's role in a coordination tree.
/// </summary>
public enum CoordinationSessionKind
{
    Coordinator,
    ChildWork,
    Scribe,
    OperatorChat,
    ChildRun
}

/// <summary>
/// Describes whether a session is running, idle, or could not be observed.
/// </summary>
public enum CoordinationActivityState
{
    Busy,
    Idle,
    Unknown
}

/// <summary>
/// Tracks whether an interruption was not requested, requested, or acknowledged by the owner.
/// </summary>
public enum CoordinationInterruptionIntentState
{
    None,
    Requested,
    Acknowledged
}

/// <summary>
/// Describes the persisted lifecycle of a coordination session.
/// </summary>
public enum CoordinationLifecycleState
{
    Active,
    Cancelled,
    Completed,
    Archived
}

/// <summary>
/// Selects whether an idle subscription sends one notice or continues notifying.
/// </summary>
public enum IdleNotificationMode
{
    Once,
    Always
}

/// <summary>
/// Reports whether the source for idle notifications is available.
/// </summary>
public enum IdleNotificationSourceState
{
    Available,
    Unavailable
}

/// <summary>
/// Classifies the condition currently blocking a session.
/// </summary>
public enum CoordinationBlockerKind
{
    AwaitingInput,
    AwaitingPlanApproval,
    AwaitingApproval,
    AwaitingOutcomeConfirmation
}

/// <summary>
/// Selects the kind of instruction sent when steering a session.
/// </summary>
public enum CoordinationSteeringAction
{
    Stop,
    Redirect,
    Amend
}

/// <summary>
/// Request body for starting a child session with an idempotent owner command.
/// </summary>
public sealed record SpawnSessionRequest(
    string SessionId,
    CoordinationSessionKind Kind,
    string IdempotencyKey,
    string Kickoff,
    string? UserQuote = null,
    string? CoordinatorInstructions = null,
    string? WorkPlanItemId = null);

/// <summary>
/// Binds a confirmed work-plan item to the decision version and accepted selection that approved it.
/// </summary>
internal sealed record ConfirmedWorkPlanItemAssociation(
    string WorkPlanItemId,
    long DecisionStateVersion,
    string SelectionHash);

/// <summary>
/// Request body for a session-tree command guarded by the current execution fence.
/// </summary>
public sealed record SessionTreeCommandRequest(long ExecutionFence, string IdempotencyKey);

/// <summary>
/// Request body describing an explicit session fork from a source event and cursor.
/// </summary>
public sealed record CoordinationSessionForkRequest(
    long ExecutionFence,
    string IdempotencyKey,
    string TargetSessionId,
    Guid SourceEventId,
    string SourceCursor,
    CoordinationSessionKind Kind);

/// <summary>
/// Reports whether a requested fork has been registered, remains pending, or could not be registered.
/// </summary>
public enum CoordinationForkRegistrationState
{
    RegistrationPending,
    Registered,
    Unregistered
}

/// <summary>
/// Returns the fork command result together with its registration state and optional lineage.
/// </summary>
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

/// <summary>
/// Request body for subscribing to a target session's idle notifications.
/// </summary>
public sealed record SubscribeToIdleRequest(
    string SubscriberSessionId,
    long ExecutionFence,
    string IdempotencyKey,
    IdleNotificationMode Mode);

/// <summary>
/// Request body for resolving a pending coordinator gate.
/// </summary>
public sealed record ResolveCoordinatorGateRequest(
    long ExpectedStateVersion,
    string IdempotencyKey);

/// <summary>
/// Request body for steering a recipient with a typed delivery mode and instruction.
/// </summary>
public sealed record SteerSessionRequest(
    string RecipientSessionId,
    string IdempotencyKey,
    AddressedMessageDeliveryMode DeliveryMode,
    CoordinationSteeringAction Action,
    JsonElement Instruction,
    string? UserQuote = null,
    string? CoordinatorInstructions = null);

/// <summary>
/// One session's persisted identity, parent, lifecycle, and concurrency versions in the tree.
/// </summary>
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

/// <summary>
/// Returns the session-tree nodes rooted at one accepted coordinator session.
/// </summary>
public sealed record SessionTreeSnapshot(
    string RootSessionId,
    ImmutableArray<SessionTreeNode> Nodes);

/// <summary>
/// Describes a pending input, approval, or outcome condition that blocks a session.
/// </summary>
public sealed record SessionStatusBlocker(
    CoordinationBlockerKind Kind,
    string RequestId,
    ImmutableArray<string> Choices,
    bool AllowsFreeform,
    string? Prompt);

/// <summary>
/// Combines a session's tree, activity, blockers, runtime effects, and owner run status.
/// </summary>
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

/// <summary>
/// Reports the state and failure reference held by the run owner.
/// </summary>
public sealed record OwnerRunExecutionSnapshot(
    string State,
    long StateVersion,
    string? CauseCode,
    string? Reference);

/// <summary>
/// Reports the persisted interruption intent and its related owner message or cause.
/// </summary>
public sealed record SessionInterruptionIntentSnapshot(
    CoordinationInterruptionIntentState State,
    Guid? OwnerMessageId,
    string? CauseCode);

/// <summary>
/// Identifies an idle notification emitted for a target and subscriber at a specific session version.
/// </summary>
public sealed record CoordinationIdleNotification(
    Guid NotificationId,
    SessionIdentity Target,
    SessionIdentity Subscriber,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long StateVersion);

/// <summary>
/// Returns the child session created by a spawn command and its pending dispatch request.
/// </summary>
public sealed record SpawnedSession(
    SessionTreeNode Node,
    string PendingRequestId,
    Guid CommandId,
    string DispatchState);

/// <summary>
/// Describes an idle subscription and whether its notification source is available.
/// </summary>
public sealed record IdleSubscriptionResult(
    Guid SubscriptionId,
    string TargetSessionId,
    string SubscriberSessionId,
    IdleNotificationMode Mode,
    long ExecutionFence,
    bool Active,
    IdleNotificationSourceState NotificationSourceState,
    string? NotificationSourceUnavailableCode);

/// <summary>
/// Summarizes the outcome of a session-tree command.
/// </summary>
public sealed record CoordinationTreeCommandResult(
    Guid CommandId,
    string Command,
    string SourceSessionId,
    string? TargetSessionId,
    long ExecutionFence,
    string State);

/// <summary>
/// Describes a persisted command that requests a child-session spawn.
/// </summary>
public sealed record CoordinationSpawnCommand(
    Guid CommandId,
    SessionIdentity Parent,
    SpawnSessionRequest Request,
    string PendingRequestId,
    long ExecutionFence);

/// <summary>
/// Identifies a registered child and its parent-side pending request.
/// </summary>
public sealed record RegisteredChild(
    SessionIdentity Identity,
    string ParentSessionId,
    string PendingRequestId,
    long ExecutionFence);

/// <summary>
/// Request body for delivering a typed owner message to a recipient session.
/// </summary>
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

/// <summary>
/// Returns the owner message identifier and delivery status for a coordination message.
/// </summary>
public sealed record CoordinationMessageResult(
    Guid OwnerMessageId,
    string RecipientSessionId,
    string Status,
    string? RequestId);

/// <summary>
/// Carries an owner message and its Events admission receipt into delivery processing.
/// </summary>
public sealed record DeliveryIngressRequest(
    Guid OwnerMessageId,
    MessageAdmissionReceipt Admission);

/// <summary>
/// Reports the state of a cancelled request and any steering message used to wake its owner.
/// </summary>
public sealed record CancelRequestResult(string RequestId, string State, Guid? SteeringMessageId);

/// <summary>
/// Selects the resulting state at the end of a logical session turn.
/// </summary>
public enum LogicalTurnCompletion
{
    Idle,
    Blocked,
    Completed
}

/// <summary>
/// Request body for completing a logical turn under its current session version and execution fence.
/// </summary>
public sealed record FinishTurnRequest(
    long ExecutionFence,
    long ExpectedStateVersion,
    LogicalTurnCompletion Completion);

/// <summary>
/// Request body for advancing a turn boundary under optimistic version checks.
/// </summary>
public sealed record TurnBoundaryRequest(long ExecutionFence, long ExpectedStateVersion);

/// <summary>
/// Returns the next logical turn state, any presented message, notifications, and duplicate status.
/// </summary>
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

/// <summary>
/// Holds local owner metadata used to verify that a runtime session remains bound to its accepted run.
/// </summary>
internal sealed record SessionRuntimeOwnerState(
    string RootSessionId,
    string? WorkPlanItemId,
    string TenantId,
    string AcceptedSelectionHash,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long StateVersion,
    string RuntimeTurnId)
{
    public bool IsCoordinatorRoot { get; init; }
}

/// <summary>
/// Reports the accepted run's current root, fence, turn, and execution state.
/// </summary>
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

/// <summary>
/// Selects whether a reported run failure is definitive or leaves execution indeterminate.
/// </summary>
public enum OwnerRunFailureState
{
    Failed,
    Indeterminate
}

/// <summary>
/// Request body for recording a failed or indeterminate run at expected run and session versions.
/// </summary>
public sealed record ReportRunFailureRequest(
    long ExecutionFence,
    long ExpectedRunStateVersion,
    long ExpectedSessionStateVersion,
    string IdempotencyKey,
    OwnerRunFailureState State,
    string CauseCode,
    string Reference);

/// <summary>
/// Request body for recovering a run execution using its current fence and run-state version.
/// </summary>
public sealed record RecoverRunExecutionRequest(
    long ExecutionFence,
    long ExpectedRunStateVersion,
    string IdempotencyKey,
    string CauseCode,
    string Reference);

/// <summary>
/// Returns the state and version changes caused by a run-execution transition.
/// </summary>
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

/// <summary>
/// Reports one session whose execution fence changed during a run transition.
/// </summary>
public sealed record SessionExecutionFenceChange(
    string SessionId,
    string State,
    long StateVersion);

/// <summary>
/// Describes a parent notification created when a child sends a coordination message.
/// </summary>
public sealed record ParentNotification(
    Guid NotificationId,
    string ParentSessionId,
    string ChildSessionId,
    Guid MessageId,
    AddressedMessagePurpose Purpose,
    bool WakesParent,
    DateTimeOffset CreatedAt);

/// <summary>
/// Represents an HTTP-facing coordination failure with a stable error code and status.
/// </summary>
public sealed class CoordinationException(string code, int statusCode, Exception? innerException = null)
    : Exception(code, innerException)
{
    /// <summary>
    /// Gets the stable machine-readable error code.
    /// </summary>
    public string Code { get; } = code;

    /// <summary>
    /// Gets the HTTP status associated with this coordination failure.
    /// </summary>
    public int StatusCode { get; } = statusCode;
}
