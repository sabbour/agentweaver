using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

/// <summary>Environment-owner evidence that the registered placement is ready for runtime execution.</summary>
public sealed record EnvironmentRuntimeReadinessContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    DateTimeOffset LeaseCreatedAt,
    SandboxObservation Observation,
    SandboxStartupTimeBudgets StartupBudgets,
    string WorkspaceMountPath);

/// <summary>Environment-owner workspace attachment and transition context for a runtime.</summary>
public sealed record EnvironmentRuntimeWorkspaceContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    WorkspaceVolumeAttachmentNegotiation Workspace,
    long TransitionRevision);

/// <summary>Versioned request to resolve an environment profile for runtime bootstrap.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapProfileResolveRequest(int ContractVersion)
{
    /// <summary>Requires the supported profile-resolution contract version.</summary>
    public RuntimeBootstrapProfileResolveRequest Validate()
    {
        if (ContractVersion != 1)
            throw new ArgumentException("The Runtime bootstrap profile request is invalid.");
        return this;
    }
}

/// <summary>Receipt linking the requested profile to the profile selected by the owner.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapProfileResolveReceipt(
    int ContractVersion,
    string SourceProfileId,
    string ProfileId)
{
    /// <summary>Checks the receipt against the profile requested by the caller.</summary>
    public RuntimeBootstrapProfileResolveReceipt ValidateFor(string sourceProfileId)
    {
        if (ContractVersion != 1 ||
            SourceProfileId != sourceProfileId)
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_receipt_invalid");
        RuntimeContractValidation.ValidateIdentifier(ProfileId);
        return this;
    }
}

/// <summary>Request to configure the runtime with a registration, operation IDs, and settings payload.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostConfigureRequest(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid ConsumeOperationId,
    Guid ExchangeOperationId,
    JsonElement Configuration)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeMcpServerEndpoint? McpServer { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeResumeBootstrapRequest? ResumeBootstrap { get; init; }
}

/// <summary>Proof that identifies the runtime registration and source grant for an owner operation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSessionProof(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid SourceGrantId,
    long SourceGrantRevision,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeCredentialPurpose>))]
    RuntimeCredentialPurpose Purpose);

/// <summary>Request to refresh a source grant using a session proof and idempotent operation ID.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostRefreshRequest(RuntimeHostSessionProof Proof, Guid OperationId);

/// <summary>Request to suspend a runtime and persist its manifest and optional workspace flush evidence.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSuspendRequest(
    RuntimeHostSessionProof Proof, Guid OperationId, Guid ManifestId, long PhaseVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceVolumeFlushRequest? WorkspaceFlush { get; init; }
}

/// <summary>Owner receipt for a suspended runtime, durable turn, cache, and optional flushed workspace.</summary>
public sealed record RuntimeHostSuspendReceipt(
    int ContractVersion,
    Guid OperationId,
    Guid ManifestId,
    RuntimeRegistration Registration,
    RuntimeGrantReceipt SourceGrant,
    RuntimeNativeTurnRecordedReceipt NativeTurn,
    SessionMaterialAcknowledgment CacheAcknowledgment)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceVolumeFlushReceipt? WorkspaceFlush { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionMaterialAcknowledgment? ConfigurationAcknowledgment { get; init; }
}

/// <summary>Receipt confirming image and startup observations for one registered runtime.</summary>
public sealed record RuntimeHostReadinessReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    SandboxImageIdentity Image,
    ImmutableArray<SandboxStartupPhaseObservation> StartupPhases,
    RuntimeGrantReceipt SourceGrant)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostResumeCacheReceipt? ResumeCache { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostRecoveryReceipt? Recovery { get; init; }
}

/// <summary>Recovery source used to recreate a native SDK conversation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RuntimeHostRecoveryMode>))]
public enum RuntimeHostRecoveryMode
{
    Fresh,
    NativeCache,
    JournalRebuild
}

/// <summary>Hash and journal position evidence used when rebuilding native context from owner history.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostJournalRecoveryEvidence(long ThroughPosition, string JournalSha256);

/// <summary>Authenticated evidence describing how the runtime recovered its native SDK session.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostRecoveryReceipt(
    int ContractVersion,
    Guid RuntimeInstanceId,
    long RegistrationRevision,
    long ExecutionFence,
    RuntimeHostRecoveryMode RecoveryMode,
    SdkSessionFacts Source)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionMaterialAcknowledgment? NativeCacheAcknowledgment { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuntimeHostJournalRecoveryEvidence? Journal { get; init; }

    /// <summary>Validates recovery evidence against the registration and matching SDK source.</summary>
    public RuntimeHostRecoveryReceipt ValidateFor(RuntimeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        RuntimeUsageSourceReceiptContract.ValidateSource(registration, Source);
        var binding = registration.Binding;
        if (ContractVersion != 1 ||
            RuntimeInstanceId != registration.RuntimeInstanceId ||
            RegistrationRevision != registration.Revision ||
            ExecutionFence != binding.ExecutionFence ||
            Source.SdkSessionId != RuntimeContractValidation.NativeSessionId(binding) ||
            !Enum.IsDefined(RecoveryMode))
            throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");

        if (RecoveryMode == RuntimeHostRecoveryMode.NativeCache)
        {
            var cache = NativeCacheAcknowledgment
                ?? throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
            SessionMaterialValidation.Validate(cache.Reference);
            var material = cache.Reference.Material!;
            if (Journal is not null ||
                cache.ContractVersion != 1 ||
                cache.Identity != new SessionIdentity(binding.ProjectId, binding.RunId, binding.SessionId) ||
                cache.EventId == Guid.Empty || cache.Position < 1 ||
                material.Kind != SessionMaterialKind.SdkCache ||
                material.TenantId != binding.TenantId ||
                material.AcceptedSelectionHash != binding.AcceptedSelectionHash ||
                material.ModelSelectionReference != Source.ModelSelectionReference ||
                material.SdkVersion != Source.SdkVersion ||
                material.RuntimeVersion != Source.RuntimeVersion ||
                material.ModelId != Source.ModelId ||
                material.MaxPromptTokens != Source.MaxPromptTokens ||
                material.ReasoningEffort != Source.ReasoningEffort ||
                material.ModelBindingPin != Source.ModelBindingPin ||
                material.ResolvedModelBindingPin != Source.ResolvedModelBindingPin)
                throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
        }
        else if (RecoveryMode == RuntimeHostRecoveryMode.JournalRebuild)
        {
            if (NativeCacheAcknowledgment is not null ||
                Journal is not { ThroughPosition: > 0, JournalSha256: { Length: 64 } hash } ||
                !hash.All(Uri.IsHexDigit))
                throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");
        }
        else if (NativeCacheAcknowledgment is not null || Journal is not null)
            throw new RuntimeAuthorizationException("runtime_recovery_receipt_invalid");

        return this;
    }
}

/// <summary>Small health response describing the runtime host status.</summary>
public sealed record RuntimeHostLivenessReceipt(string Status);

/// <summary>Text content part in the runtime's A2A message representation.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ATextPart(string Kind, string Text);

/// <summary>Runtime proof and delivery mode attached to an A2A message.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMetadata(
    RuntimeHostSessionProof Runtime,
    AddressedMessageDeliveryMode DeliveryMode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ContinueAfterCallerDisconnect = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    bool ReadOnlyToolsOnly = false);

/// <summary>Validated A2A text message shape sent to the runtime.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMessage(
    string Kind,
    Guid MessageId,
    string ContextId,
    string Role,
    ImmutableArray<RuntimeA2ATextPart> Parts,
    RuntimeA2AMetadata Metadata);

/// <summary>Envelope for submitting one A2A message to a runtime host.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ASendRequest(RuntimeA2AMessage Message);

/// <summary>Runtime response containing the resulting A2A message content.</summary>
public sealed record RuntimeA2AResponse(
    string Kind, Guid MessageId, string ContextId, string Role, ImmutableArray<RuntimeA2ATextPart> Parts);
