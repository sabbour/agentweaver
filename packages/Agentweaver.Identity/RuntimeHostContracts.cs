using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agentweaver.Abstractions;

namespace Agentweaver.Identity;

public sealed record EnvironmentRuntimeReadinessContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    DateTimeOffset LeaseCreatedAt,
    SandboxObservation Observation,
    SandboxStartupTimeBudgets StartupBudgets,
    string WorkspaceMountPath);

public sealed record EnvironmentRuntimeWorkspaceContext(
    int ContractVersion,
    EnvironmentRuntimeBootstrapContext Placement,
    WorkspaceVolumeAttachmentNegotiation Workspace,
    long TransitionRevision);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapProfileResolveRequest(int ContractVersion)
{
    public RuntimeBootstrapProfileResolveRequest Validate()
    {
        if (ContractVersion != 1)
            throw new ArgumentException("The Runtime bootstrap profile request is invalid.");
        return this;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeBootstrapProfileResolveReceipt(
    int ContractVersion,
    string SourceProfileId,
    string ProfileId)
{
    public RuntimeBootstrapProfileResolveReceipt ValidateFor(string sourceProfileId)
    {
        if (ContractVersion != 1 ||
            SourceProfileId != sourceProfileId)
            throw new RuntimeAuthorizationException("runtime_bootstrap_profile_receipt_invalid");
        RuntimeContractValidation.ValidateIdentifier(ProfileId);
        return this;
    }
}

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSessionProof(
    int ContractVersion,
    RuntimeRegistration Registration,
    Guid SourceGrantId,
    long SourceGrantRevision,
    [property: JsonConverter(typeof(JsonStringEnumConverter<RuntimeCredentialPurpose>))]
    RuntimeCredentialPurpose Purpose);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostRefreshRequest(RuntimeHostSessionProof Proof, Guid OperationId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostSuspendRequest(
    RuntimeHostSessionProof Proof, Guid OperationId, Guid ManifestId, long PhaseVersion)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkspaceVolumeFlushRequest? WorkspaceFlush { get; init; }
}

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

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeHostRecoveryMode>))]
public enum RuntimeHostRecoveryMode
{
    Fresh,
    NativeCache,
    JournalRebuild
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeHostJournalRecoveryEvidence(long ThroughPosition, string JournalSha256);

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
                material.MaxPromptTokens != Source.MaxPromptTokens)
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

public sealed record RuntimeHostLivenessReceipt(string Status);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ATextPart(string Kind, string Text);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMetadata(RuntimeHostSessionProof Runtime, AddressedMessageDeliveryMode DeliveryMode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2AMessage(
    string Kind,
    Guid MessageId,
    string ContextId,
    string Role,
    ImmutableArray<RuntimeA2ATextPart> Parts,
    RuntimeA2AMetadata Metadata);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record RuntimeA2ASendRequest(RuntimeA2AMessage Message);

public sealed record RuntimeA2AResponse(
    string Kind, Guid MessageId, string ContextId, string Role, ImmutableArray<RuntimeA2ATextPart> Parts);
