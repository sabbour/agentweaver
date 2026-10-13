using System.Text.Json.Serialization;

namespace Agentweaver.Identity;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeExecutionTaskStatus>))]
public enum RuntimeExecutionTaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Indeterminate
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionPendingDispatch(Guid MessageId, string PromptSha256);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeExecutionResultDigest(string Sha256, long Utf8ByteLength);

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
