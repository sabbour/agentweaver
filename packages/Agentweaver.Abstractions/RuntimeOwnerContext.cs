using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

public sealed record RuntimeOwnerContext(
    int ContractVersion,
    string ActorIssuer,
    string ActorId,
    string TenantId,
    string ProjectId,
    string RunId,
    string SessionId,
    string AgentId,
    string ModelSelectionReference,
    string TurnId,
    long ProjectRevision,
    long ProjectConfigurationRevision,
    long PlatformRuntimeRevision,
    string ContextRevision,
    string AcceptedSelectionHash,
    long ExecutionFence,
    long LogicalTurnOrdinal,
    long OwnerStateVersion,
    long DecisionStateVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? ModelCredentialReference { get; init; }
}
