using System.Text.Json.Serialization;

namespace Agentweaver.Abstractions;

/// <summary>
/// Authenticated runtime context passed from the run owner to trusted runtime components.
/// Its revisions, hashes, and fences bind work to one actor, accepted selection, and turn.
/// Optional model and budget fields carry the owner-approved runtime limits and credentials
/// by reference; the context does not contain secret values.
/// </summary>
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
    /// <summary>Workflow step bound to this turn, when execution is step-scoped.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowStepId { get; init; }
    /// <summary>Reference to an owner-held model credential; the credential value is not transported here.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecretRef? ModelCredentialReference { get; init; }
    /// <summary>Source mode authorized for the selected model, when explicitly pinned.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModelSourceMode? ModelSourceMode { get; init; }
    /// <summary>Full immutable model binding used to detect a changed runtime configuration.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeModelBindingPin? ModelBindingPin { get; init; }
    /// <summary>Identity of the configured model connection, when selection uses one.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ModelConnectionId { get; init; }
    /// <summary>Resource scope in which the model connection was authorized.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProjectAuthorityResourceType? ModelConnectionScope { get; init; }
    /// <summary>Owner-approved upper bound for model turns in this run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxModelTurns { get; init; }
    /// <summary>Owner-approved upper bound for tool calls in this run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxToolCalls { get; init; }
    /// <summary>Owner-approved prompt token ceiling for this run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPromptTokens { get; init; }
    /// <summary>Owner-approved maximum number of model revision attempts.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxRevisionAttempts { get; init; }
    /// <summary>Soft Copilot credit threshold used for warnings or non-blocking limits.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotSoftCreditLimit { get; init; }
    /// <summary>Hard Copilot credit ceiling after which the owner must stop further work.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CopilotHardCreditLimit { get; init; }
}
