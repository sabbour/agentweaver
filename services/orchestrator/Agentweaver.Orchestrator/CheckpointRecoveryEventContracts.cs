using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Orchestrator;

public sealed record CheckpointRecoveryEventPage(
    SessionIdentity Session,
    Guid OperationId,
    long Watermark,
    IReadOnlyList<CheckpointRecoveryEvent> Events,
    string? NextCursor,
    bool HasMore);

public sealed record CheckpointRecoveryEvent(
    Guid EventId,
    long Sequence,
    string EventType,
    int EventVersion,
    DateTimeOffset OccurredAt,
    Guid OperationId,
    CheckpointRecoveryEventStatus Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RuntimeEffectsState { get; init; }
}

public sealed record CheckpointRecoveryEventStatus(
    string Phase,
    string SourceCheckpointId,
    long SourceCheckpointRevision,
    long PreviousExecutionFence,
    long ExecutionFence,
    Guid PreviousRuntimeInstanceId,
    long PreviousRegistrationRevision)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentStep { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReasonCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryMaterialMode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundaryKind { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DispatchHeld { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceCheckpointHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryCheckpointId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RecoveryCheckpointRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RecoveryCheckpointHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DecisionStateVersion { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RuntimeInstanceId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? RegistrationRevision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ChildManifestSha256 { get; init; }
}
