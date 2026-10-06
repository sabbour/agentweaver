using System.Collections.Immutable;
using System.Text.Json;
using Agentweaver.Abstractions;

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

public sealed record AcceptRootRequest(string SessionId);

public sealed record AcceptedRoot(
    string ProjectId,
    string RunId,
    string RootSessionId,
    long ExecutionFence,
    long StateVersion,
    long LogicalTurnOrdinal,
    string ExecutionState);

public sealed record RegisterChildRequest(string SessionId);

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
    ImmutableArray<ParentNotification> ParentNotifications = default);

public sealed record OwnerRunStatus(
    string ProjectId,
    string RunId,
    string RootSessionId,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    string ExecutionState,
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
