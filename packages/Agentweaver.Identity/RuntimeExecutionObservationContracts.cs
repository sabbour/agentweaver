using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

/// <summary>Execution lifecycle states reported by the workflow owner.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeExecutionTaskStatus>))]
public enum RuntimeExecutionTaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Indeterminate
}

/// <summary>Owner-tracked child dispatch that is pending while the execution is running.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionPendingDispatch(Guid MessageId, string PromptSha256);

/// <summary>Digest and UTF-8 size of a completed execution result.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionResultDigest(string Sha256, long Utf8ByteLength);

/// <summary>
/// Owner observation tying a child execution status to its session, work plan, checkpoint,
/// decision versions, and optional pending-dispatch or completed-result evidence.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionObservation(
    int ContractVersion,
    string RootSessionId,
    string SessionId,
    long ExecutionFence,
    string WorkPlanId,
    string AssociationId,
    long CheckpointRevision,
    string CheckpointId,
    long CheckpointDecisionStateVersion,
    long CurrentDecisionStateVersion,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeExecutionTaskStatus>))]
    RuntimeExecutionTaskStatus Status)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeExecutionPendingDispatch? PendingDispatch { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeExecutionResultDigest? ResultDigest { get; init; }

    /// <summary>Checks version, identifiers, checkpoint ordering, and status-specific evidence.</summary>
    /// <exception cref="RuntimeAuthorizationException">The observation is inconsistent or malformed.</exception>
    public RuntimeExecutionObservation Validate()
    {
        if (ContractVersion != 1 ||
            !ValidMetadata(RootSessionId) ||
            !ValidMetadata(SessionId) ||
            ExecutionFence < 1 ||
            !ValidMetadata(WorkPlanId) ||
            !ValidIdentifier(AssociationId) ||
            CheckpointRevision < 1 ||
            !ValidIdentifier(CheckpointId) ||
            CheckpointDecisionStateVersion < 1 ||
            CheckpointDecisionStateVersion > CurrentDecisionStateVersion ||
            CurrentDecisionStateVersion < 1 ||
            !Enum.IsDefined(Status) ||
            PendingDispatch is not null && ResultDigest is not null)
            throw new RuntimeAuthorizationException("runtime_execution_observation_invalid");

        if (PendingDispatch is { } pending &&
            (pending.MessageId == Guid.Empty || !ValidHash(pending.PromptSha256) ||
             Status != RuntimeExecutionTaskStatus.Running))
            throw new RuntimeAuthorizationException("runtime_execution_observation_invalid");
        if (ResultDigest is { } result &&
            (!ValidHash(result.Sha256) || result.Utf8ByteLength < 0 ||
             Status != RuntimeExecutionTaskStatus.Succeeded))
            throw new RuntimeAuthorizationException("runtime_execution_observation_invalid");
        return this;
    }

    private static bool ValidMetadata(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256 && !value.Any(char.IsControl);

    private static bool ValidIdentifier(string? value) =>
        ValidMetadata(value) && value!.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');

    private static bool ValidHash(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}

/// <summary>Version 2 execution observation with explicit parent-checkpoint metadata.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionObservationV2(
    [property: JsonRequired] int ContractVersion,
    [property: JsonRequired] RuntimeExecutionObservation Observation,
    [property: JsonRequired] RuntimeExecutionCheckpointParent CheckpointParent)
{
    /// <summary>Validates both the nested observation and required parent metadata.</summary>
    public RuntimeExecutionObservationV2 Validate()
    {
        if (ContractVersion != 2 || Observation is null || CheckpointParent is null)
            throw new RuntimeAuthorizationException("runtime_execution_observation_invalid");
        _ = Observation.Validate();
        _ = CheckpointParent.Validate();
        return this;
    }
}

/// <summary>States whether parent-checkpoint metadata exists and, when present, its ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionCheckpointParent(
    [property: JsonRequired] bool HasParentMetadata,
    [property: JsonRequired] string? ParentCheckpointId)
{
    /// <summary>Rejects an ID when parent metadata is absent and validates any supplied ID.</summary>
    public RuntimeExecutionCheckpointParent Validate()
    {
        if (!HasParentMetadata && ParentCheckpointId is not null ||
            ParentCheckpointId is not null &&
                !ValidCheckpointId(ParentCheckpointId))
            throw new RuntimeAuthorizationException("runtime_execution_observation_invalid");
        return this;
    }

    private static bool ValidCheckpointId(string value) =>
        value.Length is > 0 and <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-' or ':');
}
